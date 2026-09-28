using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;
using UEModManager.Services.DeploymentPlanning;

namespace UEModManager.Tests.Services;

/// <summary>
/// T1 的端到端验证：**真实的 zip 导入 → 方案 → 冲突分析 → 视图 → 部署计划 → 写盘**，
/// 全程走生产链路上的真实服务（不 mock 任何一层），最后断言磁盘布局。
///
/// 现场症状：用户的游戏目录里 UE4SS 的 <c>dwmapi.dll</c> 与 CSharpLoader 的
/// <c>version.dll</c> 落在包子目录（<c>.../Win64/超级筋斗云/dwmapi.dll</c>）里，
/// 而 Windows 加载器只与 <c>b1-Win64-Shipping.exe</c> 同目录找代理 DLL，
/// 于是三个插件 MOD 一个都没挂上。等价布局复刻如下（压缩包顶层是 MOD 名，
/// 内部自带 <c>b1/Binaries/Win64</c> 层级）。
///
/// 本用例的价值在于跨越"纯函数单测"与"实机 GUI"之间的空档：
/// 它把 <c>ArchiveTargetPathResolver</c> → <c>PackageImportService</c> →
    /// <c>ConflictModuleService</c> → <c>ResolvedViewBuilder</c> → <c>DeploymentPlanner</c> →
/// <c>DeploymentService</c> 串成一条链，验证的判据是**游戏目录里的最终文件名与位置**。
/// </summary>
public sealed class ProxyDllDeploymentEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ProxyDllE2E", Guid.NewGuid().ToString("N"));

    private const string ProxyDllUtf8 = "PROXY-DLL-BYTES";

    /// <summary>
    /// 用户的真实布局：两个 MOD 各带一份内容相同的 UE4SS 代理 DLL，
    /// 自动闪避 MOD 带一份 CSharpLoader 的 <c>version.dll</c>。
    /// 部署后两个代理 DLL 必须与游戏 exe 同目录。
    /// </summary>
    [Fact]
    public async Task RealZipImport_DeploysProxyDllsToGameRoot_NotIntoPackageSubdirectories()
    {
        var f = await Fixture.OpenAsync(_root);

        // 造一个与 0916.zip 同形状的压缩包：顶层是 MOD 名，内部自带 b1/Binaries/Win64。
        var zip = CreateArchive(
            ("超级筋斗云", [
                ("b1/Binaries/Win64/dwmapi.dll", ProxyDllUtf8),
                ("b1/Binaries/Win64/ue4ss/Mods/SomersaultCloudAnywhere/Scripts/main.lua", "return {}"),
            ]),
            ("超级识破", [
                ("b1/Binaries/Win64/dwmapi.dll", ProxyDllUtf8),
                ("b1/Binaries/Win64/ue4ss/Mods/BPML.lua", "return {}"),
            ]),
            ("黑神话26年8月最新版自动闪避MOD", [
                ("b1/Binaries/Win64/version.dll", "VERSION-DLL-BYTES"),
                ("b1/Binaries/Win64/CSharpLoader/Mods/PerfectDodgeV2.4/config.json", "{}"),
            ]));

        // ── ① 真实导入：走 PackageImportService（会真正解压 + 清洗路径 + 建包） ──
        var results = await f.Imports.ImportSingleAsync(zip);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        var packages = results.Select(r => r.Package!).ToList();
        Assert.Equal(3, packages.Count);

        // 三个包都是"目录结构 = 安装位置"的插件包。
        Assert.All(packages, p => Assert.True(p.PreservesInstallLayout, p.PackageKey));

        // 代理 DLL 的 RelativeTargetPath 被剥成裸文件名（b1/Binaries/Win64 是游戏根锚点）。
        foreach (var key in new[] { "超级筋斗云", "超级识破" })
        {
            var dll = packages.Single(p => p.PackageKey == key)
                .Artifacts.Single(a => a.FileName == "dwmapi.dll");
            Assert.Equal("dwmapi.dll", dll.RelativeTargetPath);
        }
        var versionDll = packages.Single(p => p.PackageKey == "黑神话26年8月最新版自动闪避MOD")
            .Artifacts.Single(a => a.FileName == "version.dll");
        Assert.Equal("version.dll", versionDll.RelativeTargetPath);

        // ── ② 全部启用（等价于实机里"全部启用并部署"） ──
        await f.EnableAllAsync(packages);

        // ── ③ 冲突分析：两份 dwmapi.dll 属"共享组件"，自动裁决、不打扰用户 ──
        var gameRoot = f.Config.Config.GamePath!;
        var conflicts = await f.Conflicts.AnalyzeAsync();
        Assert.Empty(conflicts.Groups);
        // 代理 DLL 是"带一份就够"的运行时文件，归共享组件而不是让用户选。
        Assert.Contains(conflicts.SharedComponents, c =>
            c.Providers.Any(p => p.PackageKey == "超级筋斗云")
            && c.Providers.Any(p => p.PackageKey == "超级识破"));

        // ── ④ 视图 + 部署计划 + 真实写盘 ──
        var view = await f.Views.BuildAsync();
        var plan = await f.Planner.CreatePlanForViewAsync(view);
        var outcome = await f.Deployment.ExecuteAsync(plan);
        Assert.Equal(DeploymentStatus.Committed, outcome.Status);

        // ── ⑤ 判据：游戏目录的最终布局 ──
        Assert.True(File.Exists(Path.Combine(gameRoot, "dwmapi.dll")),
            "dwmapi.dll 必须与游戏 exe 同目录，否则 UE4SS 不会被加载");
        Assert.True(File.Exists(Path.Combine(gameRoot, "version.dll")),
            "version.dll 必须与游戏 exe 同目录，否则 CSharpLoader 不会被加载");

        // 三个插件包的包目录不得出现在游戏根下 —— 它们正是修复前关住代理 DLL 的地方。
        foreach (var key in new[] { "超级筋斗云", "超级识破", "黑神话26年8月最新版自动闪避MOD" })
            Assert.False(Directory.Exists(Path.Combine(gameRoot, key)),
                $"包目录 {key} 不该出现在游戏根下");

        // 带层级的 UE4SS / CSharpLoader 载荷也要到位。
        Assert.True(File.Exists(Path.Combine(gameRoot, "ue4ss", "Mods", "BPML.lua")));
        Assert.True(File.Exists(Path.Combine(gameRoot, "ue4ss", "Mods", "SomersaultCloudAnywhere",
            "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(gameRoot, "CSharpLoader", "Mods",
            "PerfectDodgeV2.4", "config.json")));

        // 内容确实写进去了（不是空文件）。
        Assert.Equal(ProxyDllUtf8, await File.ReadAllTextAsync(Path.Combine(gameRoot, "dwmapi.dll")));
    }

    /// <summary>
    /// 反向对照：手动导入的单个插件文件（没有"路径即安装位置"的声明）仍走包目录隔离。
    /// 两个同名 dll 因此不会互相覆盖 —— 这正是当初加隔离要解决的问题，
    /// 本次修复不能把它一起弄丢。
    /// </summary>
    [Fact]
    public async Task ManuallyImportedSinglePluginFiles_StillIsolatedByPackageDirectory()
    {
        var f = await Fixture.OpenAsync(_root);

        var a = Path.Combine(_root, "source-a", "alpha.dll");
        var b = Path.Combine(_root, "source-b", "beta.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        await File.WriteAllTextAsync(a, "ALPHA");
        await File.WriteAllTextAsync(b, "BETA");

        var resultA = await f.Imports.ImportSingleAsync(a);
        var resultB = await f.Imports.ImportSingleAsync(b);
        Assert.All(resultA, r => Assert.True(r.Success, r.ErrorMessage));
        Assert.All(resultB, r => Assert.True(r.Success, r.ErrorMessage));

        var packages = resultA.Concat(resultB).Select(r => r.Package!).ToList();
        Assert.All(packages, p => Assert.False(p.PreservesInstallLayout));
        Assert.All(packages, p => Assert.All(p.Artifacts, art =>
        {
            var target = DeploymentTargetPathBuilder.ComputeTargetPath(
                art, p, entry: null, f.Config.Config.ModPath!, f.Config.Config.GamePath!);
            Assert.Contains(p.PackageKey, target);
        }));
    }

    // ─── 场景搭建 ───

    private string CreateArchive(params (string Folder, (string Path, string Content)[] Files)[] folders)
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        foreach (var (folder, files) in folders)
            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(staging, folder,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

        var zip = Path.Combine(_root, "0916-shape-" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(staging, zip);
        return zip;
    }

    private sealed record Fixture(
        GameConfigService Config, PackageImportService Imports,
        ProfileService Profiles, ConflictModuleService Conflicts,
        ResolvedViewBuilder Views, DeploymentPlanner Planner, DeploymentService Deployment)
    {
        public static async Task<Fixture> OpenAsync(string root)
        {
            var data = Path.Combine(root, "Data");
            // 游戏根 = .../b1/Binaries/Win64 —— 与实机一致，代理 DLL 必须落在这里。
            var gameRoot = Path.Combine(root, "game", "b1", "Binaries", "Win64");
            Directory.CreateDirectory(gameRoot);

            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "Repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
            var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            game.Config.GameName = "黑神话·悟空";
            game.Config.GamePath = gameRoot;
            game.Config.ModPath = Path.Combine(root, "game", "b1", "Content", "Paks", "~mods");

            await repository.SetCurrentGameAsync("黑神话·悟空");
            await profiles.SetCurrentGameAsync("黑神话·悟空");

            var overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, repository, null!,
                data, Path.Combine(root, "Overwrites"));
            await overwrites.SetCurrentGameAsync("黑神话·悟空");

            var imports = new PackageImportService(NullLogger<PackageImportService>.Instance,
                repository, store, game,
                new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
                new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data));
            var conflicts = new ConflictModuleService(
                NullLogger<ConflictModuleService>.Instance, repository, profiles, game,
                new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data));
            await conflicts.SetCurrentGameAsync("黑神话·悟空");
            var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
                new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), overwrites, game);
            var state = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
                Path.Combine(root, "DeploymentState"));

            return new Fixture(game, imports, profiles, conflicts, views,
                new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
                    repository, store, profiles, game, views, state),
                new DeploymentService(NullLogger<DeploymentService>.Instance,
                    [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state,
                    Path.Combine(root, "Backups")));
        }

        /// <summary>把导入的包全部加进当前方案并启用 —— 等价于实机里"全部启用并部署"。</summary>
        public async Task EnableAllAsync(IEnumerable<Package> packages)
        {
            var list = packages.ToList();
            await Profiles.AddPackagesToCurrentProfileAsync(list);
            foreach (var package in list)
                await Profiles.SetPackageEnabledFlagAsync(package.PackageKey, true);
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
