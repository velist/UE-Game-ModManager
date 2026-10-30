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
/// 影之刃零的安装效果端到端验证：<b>真实 zip 导入 → 启用 → 部署写盘 → 禁用 → 清理</b>，
/// 链路与 <see cref="ProxyDllDeploymentEndToEndTests"/> 相同，不 mock 任何一层。
///
/// 压缩包形状照 Nexus「Easier or Harder Parry - PAK Version」（phantombladezero/mods/24）：
/// zip 顶层直接是 <c>&lt;名字&gt;_P.pak/.utoc/.ucas</c> 三件套，作者要求放进
/// <c>PBZ\ProjectPBZ\Content\Paks\~mods</c>。
///
/// 默认在临时目录里复刻 Steam 实装布局（启动壳 steam\PBZ.exe、本体 platform\steam\PBZ.exe、
/// 原版 ProjectPBZ-Windows.* 封包）。设置环境变量 <c>PBZ_REAL_ROOT</c> 为真实游戏根目录
/// （如 <c>D:\s\Steam\steamapps\common\PBZ</c>）时改为对真实目录部署并在禁用后核对清理干净；
/// 真实目录下原本就存在的 ~mods 内容不会被触碰，测试只断言自己写入的文件。
/// </summary>
public sealed class PhantomBladeZeroInstallEndToEndTests : IDisposable
{
    private const string GameName = "影之刃零";
    private const string ModBase = "PBZ_DeflectWindow_Easier33_P";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "PbzE2E", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NexusShapedPakMod_DeploysIntoProjectPbzMods_AndDisableCleansUp()
    {
        var realRoot = Environment.GetEnvironmentVariable("PBZ_REAL_ROOT");
        var gameRoot = string.IsNullOrWhiteSpace(realRoot) ? CreateMirroredLayout() : realRoot;
        Assert.True(Directory.Exists(Path.Combine(gameRoot, "ProjectPBZ", "Content", "Paks")),
            $"不是影之刃零的游戏根目录: {gameRoot}");

        // ① 与「自动检测」一致：游戏路径 = 启动壳所在目录，MOD 路径 = ProjectPBZ\Content\Paks\~mods
        var detector = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(_root, "probe.json"));
        var exe = detector.AutoDetectExecutablePath(gameRoot, GameName);
        Assert.Equal(Path.Combine(gameRoot, "steam", "PBZ.exe"), exe);

        var modPath = Path.Combine(gameRoot, "ProjectPBZ", "Content", "Paks", "~mods");
        var f = await Fixture.OpenAsync(_root, Path.GetDirectoryName(exe)!, modPath);
        var originalPaks = SnapshotOriginalPaks(gameRoot);

        // ② 真实导入
        var zip = CreateNexusShapedArchive();
        var results = await f.Imports.ImportSingleAsync(zip);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        var package = Assert.Single(results).Package!;
        Assert.Equal(3, package.Artifacts.Count);
        Assert.Equal(PackageKind.Mod, package.Kind);

        // ③ 启用并部署
        await f.Profiles.AddPackagesToCurrentProfileAsync([package]);
        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, true);
        var deployed = await f.DeployAsync();
        Assert.Equal(DeploymentStatus.Committed, deployed.Status);

        var written = new[] { ".pak", ".utoc", ".ucas" }
            .Select(ext => FindUnder(modPath, ModBase + ext))
            .ToList();
        Assert.All(written, path => Assert.NotNull(path));
        foreach (var path in written)
            Assert.Equal(ModContent(Path.GetExtension(path!)), await File.ReadAllTextAsync(path!));

        // 原版封包一个字节都不能动
        Assert.Equal(originalPaks, SnapshotOriginalPaks(gameRoot));

        // ④ 禁用后重新部署：三件套全部移除
        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, false);
        var removed = await f.DeployAsync();
        Assert.Equal(DeploymentStatus.Committed, removed.Status);
        Assert.All(written, path => Assert.False(File.Exists(path), $"禁用后仍残留: {path}"));
        Assert.Equal(originalPaks, SnapshotOriginalPaks(gameRoot));
    }

    // ─── 场景搭建 ───

    private static string ModContent(string extension) => "PBZ-TEST-MOD" + extension;

    private string CreateMirroredLayout()
    {
        var root = Path.Combine(_root, "steamapps", "common", "PBZ");
        foreach (var rel in new[]
                 {
                     @"steam\PBZ.exe",
                     @"platform\steam\PBZ.exe",
                     @"Engine\Binaries\Win64\EpicWebHelper.exe",
                     @"ProjectPBZ\Binaries\Win64\CheckEnv.exe",
                     @"ProjectPBZ\Content\Paks\ProjectPBZ-Windows.pak",
                     @"ProjectPBZ\Content\Paks\ProjectPBZ-Windows.utoc",
                     @"ProjectPBZ\Content\Paks\ProjectPBZ-Windows.ucas",
                     @"ProjectPBZ\Content\Paks\global.utoc",
                     @"ProjectPBZ\Content\Paks\global.ucas"
                 })
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "ORIGINAL:" + rel);
        }
        return root;
    }

    private string CreateNexusShapedArchive()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        foreach (var ext in new[] { ".pak", ".utoc", ".ucas" })
            File.WriteAllText(Path.Combine(staging, ModBase + ext), ModContent(ext));

        var zip = Path.Combine(_root, "PBZ DeflectWindow 3 Easier 33.zip");
        ZipFile.CreateFromDirectory(staging, zip);
        return zip;
    }

    private static string? FindUnder(string dir, string fileName)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary>原版封包（Paks 顶层，不含 ~mods）的文件名与大小/时间，用来证明部署没碰它们。</summary>
    private static string SnapshotOriginalPaks(string gameRoot)
    {
        var paks = Path.Combine(gameRoot, "ProjectPBZ", "Content", "Paks");
        return string.Join("\n", Directory.GetFiles(paks, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new FileInfo(p))
            .Select(i => $"{i.Name}|{i.Length}|{i.LastWriteTimeUtc.Ticks}"));
    }

    private sealed record Fixture(
        ProfileService Profiles, PackageImportService Imports,
        ResolvedViewBuilder Views, DeploymentPlanner Planner, DeploymentService Deployment)
    {
        public static async Task<Fixture> OpenAsync(string root, string gamePath, string modPath)
        {
            var data = Path.Combine(root, "Data");
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "Repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
            var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            game.Config.GameName = GameName;
            game.Config.GamePath = gamePath;
            game.Config.ModPath = modPath;

            await repository.SetCurrentGameAsync(GameName);
            await profiles.SetCurrentGameAsync(GameName);

            var overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, repository, null!,
                data, Path.Combine(root, "Overwrites"));
            await overwrites.SetCurrentGameAsync(GameName);

            var imports = new PackageImportService(NullLogger<PackageImportService>.Instance,
                repository, store, game,
                new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
                new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data));
            var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
                new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), overwrites, game);
            var state = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
                Path.Combine(root, "DeploymentState"));

            return new Fixture(profiles, imports, views,
                new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
                    repository, store, profiles, game, views, state),
                new DeploymentService(NullLogger<DeploymentService>.Instance,
                    [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state,
                    Path.Combine(root, "Backups")));
        }

        public async Task<DeploymentTransaction> DeployAsync()
        {
            var view = await Views.BuildAsync();
            var plan = await Planner.CreatePlanForViewAsync(view);
            return await Deployment.ExecuteAsync(plan);
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
