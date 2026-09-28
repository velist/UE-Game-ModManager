using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Conflict;
using UEModManager.Services.DeploymentPlanning;

namespace UEModManager.Tests.Services;

/// <summary>
/// 目录导入回归。
///
/// 现场症状：RAR 提示"请先手动解压"后，用户照做，把解压出来的文件夹拖进来却毫无反应。
/// 原因是 ImportSingleAsync 用 File.Exists 判定，文件夹被当成"文件不存在"，
/// 而唯一支持目录的 ImportPluginAsync 在生产链路上零调用方。
/// </summary>
public sealed class FolderImportIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "FolderImport", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FolderWithPak_ImportsAsModPackagePreservingBareFileName()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "mod"));
        var folder = Path.Combine(_root, "解压出来的MOD");
        WriteFile(folder, Path.Combine("SomeMod", "SomeMod_P.pak"), "PAK");
        WriteFile(folder, "readme.txt", "notes");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        var package = Assert.Single(results, r => r.Package!.Kind == PackageKind.Mod).Package!;
        Assert.Equal(PackageKind.Mod, package.Kind);
        // ~mods 不递归子目录：pak 必须以裸文件名登记，否则游戏扫不到。
        var artifact = Assert.Single(package.Artifacts);
        Assert.Equal("SomeMod_P.pak", artifact.RelativeTargetPath);
        // 同目录的 readme.txt 是说明文件：不部署，也不单独成包。
        // （旧行为是把它做成一个插件包，结果是游戏目录根上多出一堆
        //  "说明与事项.txt" 这类和游戏无关的文件。）
        Assert.Single(results);
    }

    [Fact]
    public async Task FolderWithoutPak_ImportsAsPluginKeepingDirectoryStructure()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "plugin"));
        var folder = Path.Combine(_root, "ue4ss");
        WriteFile(folder, Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json"), "{}");
        WriteFile(folder, Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "BPML.lua"), "return {}");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.ErrorMessage);
        var package = result.Package!;
        // 游戏根已经是 .../b1/Binaries/Win64，锚点必须剥掉，否则会多叠一层 Binaries/Win64。
        Assert.All(package.Artifacts, a =>
            Assert.StartsWith($"ue4ss{Path.DirectorySeparatorChar}Mods", a.RelativeTargetPath));
        Assert.Equal(2, package.Artifacts.Count);
        Assert.Equal(Path.Combine("ue4ss", "Mods", "mods.json"),
            package.Artifacts.Single(a => a.FileName == "mods.json").RelativeTargetPath);
    }

    [Fact]
    public async Task FolderImport_SkipsDevelopmentResidue()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "junk"));
        var folder = Path.Combine(_root, "with-junk");
        WriteFile(folder, Path.Combine("ue4ss", "Mods", "mods.json"), "{}");
        WriteFile(folder, Path.Combine("ue4ss", "Mods", "debug.log"), "noise");
        WriteFile(folder, Path.Combine("ue4ss", "Mods", "symbols.pdb"), "noise");

        var result = Assert.Single(await fixture.Imports.ImportSingleAsync(folder));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("mods.json", Assert.Single(result.Package!.Artifacts).FileName);
    }

    [Fact]
    public async Task MissingPath_ReportsFolderOrFileMissing()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "missing"));

        var result = Assert.Single(await fixture.Imports.ImportSingleAsync(Path.Combine(_root, "nope")));

        Assert.False(result.Success);
        Assert.Contains("不存在", result.ErrorMessage);
    }

    [Fact]
    public async Task MultiModFolder_SplitsIntoOnePackagePerTopLevelFolder()
    {
        // 现场症状：把 0916.zip 解压出来丢进程序后，界面里只有一个名叫 0916 的条目，
        // 冲突检测报 0 个冲突。根因是整包被并成一个包：顶层 MOD 文件夹的边界被丢掉，
        // 所有文件共用一个 PackageKey，冲突检测按包去重后自然什么都检测不到。
        //
        // 形状照抄 0916.zip：超级筋斗云 = 一个 LogicMods pak + 一整套 UE4SS 载荷；
        // 超级识破 = 只有 UE4SS 载荷，且与前者带同名的 shared.lua（真实冲突）。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "multi"));
        var folder = Path.Combine(_root, "解压出来的合集");

        WriteFile(folder, Path.Combine("超级筋斗云", "筋斗云MOD加强版", "b1", "Content", "Paks", "LogicMods", "SomersaultCloudAnywhere.pak"), "PAK-A");
        WriteFile(folder, Path.Combine("超级筋斗云", "筋斗云MOD加强版", "b1", "Binaries", "Win64", "ue4ss", "Mods", "shared.lua"), "LUA-A");
        WriteFile(folder, Path.Combine("超级识破", "b1", "Binaries", "Win64", "ue4ss", "Mods", "shared.lua"), "LUA-B");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        var packages = results.Select(r => r.Package!).ToList();

        // 两个顶层目录 → 不再是"整包一个条目"，也不再出现以合集名命名的包。
        Assert.DoesNotContain(packages, p => p.PackageKey == "解压出来的合集");
        Assert.Contains(packages, p => p.PackageKey == "超级筋斗云");
        Assert.Contains(packages, p => p.PackageKey == "超级识破");

        // 两个包各自登记同名 ue4ss 载荷 —— 这正是冲突检测赖以成立的前提。
        foreach (var key in new[] { "超级筋斗云", "超级识破" })
            Assert.Contains(Assert.Single(packages, p => p.PackageKey == key).Artifacts,
                a => a.RelativeTargetPath.EndsWith("shared.lua", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultiModFolder_ModPaksKeepBareFileNames()
    {
        // ~mods 不递归子目录，pak 必须以裸文件名登记，否则游戏扫不到。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "bare"));
        var folder = Path.Combine(_root, "合集");
        WriteFile(folder, Path.Combine("甲", "b1", "Content", "Paks", "~mods", "甲.pak"), "PAK-A");
        WriteFile(folder, Path.Combine("乙", "b1", "Content", "Paks", "~mods", "乙.pak"), "PAK-B");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        var mods = results.Select(r => r.Package!).Where(p => p.Kind == PackageKind.Mod).ToList();
        Assert.Equal(2, mods.Count);
        Assert.All(mods, p => Assert.DoesNotContain(Path.DirectorySeparatorChar, Assert.Single(p.Artifacts).RelativeTargetPath));
    }

    [Fact]
    public async Task MultiModFolder_PackagesFromOneArchive_ProduceDetectedConflicts()
    {
        // 现场症状：第一次装检测出了冲突，重装同一个合集却报 0 个冲突。
        // 根因不是检测器退化，而是整包被并成一个包：所有文件同属一个 PackageKey，
        // ConflictDetector 按包去重后只剩一个声称者，"两个包抢同一路径"永远不成立。
        // 这个用例把"多 MOD 合集 → 包 → 冲突"整条链钉住。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "conflicts"));
        var folder = Path.Combine(_root, "合集");
        var shared = Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json");
        WriteFile(folder, Path.Combine("超级筋斗云", shared), "{ \"mod\": \"筋斗云\" }");
        WriteFile(folder, Path.Combine("超级识破", shared), "{ \"mod\": \"识破\" }");

        var results = await fixture.Imports.ImportSingleAsync(folder);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));

        var profile = new InstanceProfile { HostGameName = "Game", Name = "合集" };
        var packagesByKey = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        var priority = 0;
        foreach (var package in results.Select(r => r.Package!))
        {
            packagesByKey[package.PackageKey] = package;
            profile.Packages.Add(new ProfilePackageEntry
            {
                PackageKey = package.PackageKey,
                IsEnabled = true,
                Priority = priority++,
                Kind = package.Kind
            });
        }

        var conflicts = ConflictDetector.DetectConflicts(
            profile, packagesByKey, fixture.Config.Config.ModPath!, fixture.Config.Config.GamePath!);

        // 两份 mods.json 内容不同 → 至少一条 Warning，说明"内容真实分歧"能被看见，
        // 而不是被并包降级成 0 条。
        Assert.Contains(conflicts, c => c.Severity == ConflictSeverity.Warning);
        Assert.All(conflicts, c => Assert.Equal(2, c.Losers.Count + 1));
    }
    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ─── T1：代理 DLL 必须落在游戏根，不能被关进包子目录 ───

    [Fact]
    public async Task FolderWithProxyDll_KeepsBareDllAtGameRoot()
    {
        // 现场症状：UE4SS 靠 dwmapi.dll 代理注入、CSharpLoader 靠 version.dll，
        // 两者必须与 b1-Win64-Shipping.exe 同目录才会被 Windows 加载器拾取。
        // 旧行为把裸文件名套进包目录（.../Win64/超级筋斗云/dwmapi.dll），
        // 于是三个插件 MOD 一个都没挂上。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "proxy"));
        var folder = Path.Combine(_root, "超级筋斗云");
        WriteFile(folder, Path.Combine("b1", "Binaries", "Win64", "dwmapi.dll"), "DLL");
        WriteFile(folder, Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "main.lua"), "LUA");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.ErrorMessage);
        var package = result.Package!;

        // 目录导入的产物：RelativeTargetPath 就是相对游戏根的安装位置。
        Assert.True(package.PreservesInstallLayout);
        var dll = package.Artifacts.Single(a => a.FileName == "dwmapi.dll");
        Assert.Equal("dwmapi.dll", dll.RelativeTargetPath);

        // 部署目标 = <gamePath>/dwmapi.dll，不含包名段。
        var target = DeploymentTargetPathBuilder.ComputeTargetPath(
            dll, package, entry: null,
            modPath: fixture.Config.Config.ModPath!, gamePath: fixture.Config.Config.GamePath!);
        Assert.Equal(
            Path.Combine(fixture.Config.Config.GamePath!, "dwmapi.dll"),
            target);
        Assert.DoesNotContain(package.PackageKey, target);

        // 带层级的 UE4SS 载荷同样落在游戏根下。
        var lua = package.Artifacts.Single(a => a.FileName == "main.lua");
        Assert.Equal(
            Path.Combine(fixture.Config.Config.GamePath!, "ue4ss", "Mods", "main.lua"),
            DeploymentTargetPathBuilder.ComputeTargetPath(
                lua, package, entry: null,
                modPath: fixture.Config.Config.ModPath!, gamePath: fixture.Config.Config.GamePath!));
    }

    [Fact]
    public async Task FolderWithPak_ModPackageDoesNotClaimInstallLayout()
    {
        // Mod 包的路径语义完全不同（~mods 不递归，pak 用裸文件名 + 包目录），
        // 不能被误标成"自带安装布局"。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "pakflag"));
        var folder = Path.Combine(_root, "pakmod");
        WriteFile(folder, Path.Combine("甲", "b1", "Content", "Paks", "~mods", "甲.pak"), "PAK");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        var mod = Assert.Single(results, r => r.Package!.Kind == PackageKind.Mod).Package!;
        Assert.False(mod.PreservesInstallLayout);
    }

    // ─── T2：单 MOD 无包装目录时用文件夹名，而不是 b1 ───

    [Fact]
    public async Task FolderWithProxyDllAndTopLevelReadme_LeavesNoModNamedDirectoryAtGameRoot()
    {
        // 现场症状：游戏根下留下一个以 MOD 命名的目录，里面只有一个说明文档。
        // 压缩包的真实形状是
        // 「MOD名/自动闪避/b1/Binaries/Win64/version.dll」+「MOD名/说明与事项.txt」，
        // 命中锚点的文件被剥成裸文件名，没命中锚点的说明文档因此算不出共有前缀，
        // 于是带着 MOD 名目录一起落进游戏根。
        //
        // 后续重做换了个更彻底的解法：说明文档根本不进包（见
        // SharedComponentClassifier.IsAttachment 与 ImportUnitAsync）。
        // 于是"游戏根下不会多出 MOD 名目录"这个结论依然成立，而且更稳 ——
        // 不再依赖"共有前缀算得对不对"。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "readme"));
        // 顶层一个 MOD 目录（= 一个导入单元），内部同时有"命中锚点的 DLL"和"没命中锚点的说明文档"。
        // 这正是 0916.zip 里 黑神话26年8月最新版自动闪避MOD 那一份的形状。
        var folder = Path.Combine(_root, "合集");
        WriteFile(folder, Path.Combine("MOD名", "自动闪避", "b1", "Binaries", "Win64", "version.dll"), "DLL");
        WriteFile(folder, Path.Combine("MOD名", "说明与事项.txt"), "notes");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.ErrorMessage);
        var package = result.Package!;
        Assert.Equal("MOD名", package.PackageKey);
        var gamePath = fixture.Config.Config.GamePath!;

        // 代理 DLL 仍必须落在游戏根。
        var dll = package.Artifacts.Single(a => a.FileName == "version.dll");
        Assert.Equal(Path.Combine(gamePath, "version.dll"),
            DeploymentTargetPathBuilder.ComputeTargetPath(dll, package, entry: null,
                modPath: fixture.Config.Config.ModPath!, gamePath: gamePath));

        // 说明文档不部署：它和游戏毫无关系，复制进游戏根只会留下一堆杂项文件。
        Assert.DoesNotContain(package.Artifacts, a => a.FileName == "说明与事项.txt");

        // 没有任何 artifact 的部署目标落在以包名命名的目录里。
        Assert.All(package.Artifacts, a => Assert.DoesNotContain(
            Path.DirectorySeparatorChar + package.PackageKey + Path.DirectorySeparatorChar,
            DeploymentTargetPathBuilder.ComputeTargetPath(a, package, entry: null,
                modPath: fixture.Config.Config.ModPath!, gamePath: gamePath)));
    }

    [Fact]
    public async Task SingleModFolder_WithoutWrapperDirectory_UsesFolderNameNotB1()
    {
        // 用户单独把解压后的「超级识破」拖进来，文件夹内部直接是 b1\Binaries\Win64\...
        // 旧行为以顶层第一段当包名 → 包叫 b1，第二个这样的 MOD 变成 b1_2。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "single"));
        var folder = Path.Combine(_root, "超级识破");
        WriteFile(folder, Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "x.lua"), "LUA");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("超级识破", result.Package!.PackageKey);
        Assert.DoesNotContain(results, r => r.Package!.PackageKey == "b1");
    }

    [Fact]
    public async Task SingleModArchive_WithoutWrapperDirectory_UsesArchiveName()
    {
        // 压缩包内容直接是 b1\Binaries\...（没有外层 MOD 名目录）时，
        // 包名应回落到压缩包名。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "zip"));
        var staging = Path.Combine(_root, "zip-staging");
        WriteFile(staging, Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "y.lua"), "LUA");
        var zip = Path.Combine(_root, "超级识破.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);

        var results = await fixture.Imports.ImportSingleAsync(zip);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("超级识破", result.Package!.PackageKey);
        Assert.DoesNotContain(results, r => r.Package!.PackageKey == "b1");
    }

    private sealed record Fixture(ObjectStore Store, PackageImportService Imports, GameConfigService Config)
    {
        public static async Task<Fixture> CreateAsync(string root)
        {
            var data = Path.Combine(root, "data");
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            await repository.SetCurrentGameAsync("Game");
            var config = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            config.Config.GameName = "Game";
            config.Config.GamePath = Path.Combine(root, "game", "b1", "Binaries", "Win64");
            config.Config.ModPath = Path.Combine(root, "game", "b1", "Content", "Paks", "~mods");
            var imports = new PackageImportService(NullLogger<PackageImportService>.Instance, repository,
                store, config,
                new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
                new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data));
            return new Fixture(store, imports, config);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
