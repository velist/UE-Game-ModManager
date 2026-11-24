using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Health;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;
using UEModManager.Services.InstallLayout;

namespace UEModManager.Tests.Services;

/// <summary>
/// 影之刃零 UE4SS 脚本 MOD 的安装端到端验证，不 mock 任何一层。
///
/// 来源是两位 2.1.3 玩家的诊断包：导入、部署都显示成功，游戏里不生效。游戏路径记的是启动壳目录
/// <c>PBZ\steam</c>，而 UE4SS 装在本体旁边的 <c>PBZ\platform\steam\ue4ss</c>；插件一律以游戏路径为基准，
/// 于是文件被装进了 <c>PBZ\steam\PBZ\platform\steam\ue4ss\…</c>、<c>PBZ\steam\steam\ue4ss\…</c>、
/// <c>PBZ\steam\platform\steam\ue4ss\…</c>、<c>PBZ\steam\AMDFidelityOptimizer\…</c>——没有一次落在游戏读取的位置。
/// 夹具的文件结构取自诊断包 packages.json 的 Artifacts 路径（路径已换成临时目录）。
/// </summary>
public sealed class PhantomBladeZeroUe4ssInstallEndToEndTests : IDisposable
{
    private const string GameName = "影之刃零";

    private static readonly string[] CompassMapFiles =
    [
        "enabled.txt", "minimap_mask.png", "points.txt", "rematch_logo.png",
        @"compass\arrow.png", @"compass\band.png", @"compass\dot.png", @"compass\drop.png",
        @"compass\glyph_E.png", @"compass\glyph_N.png", @"compass\glyph_S.png", @"compass\glyph_W.png", @"compass\seal.png",
        @"Scripts\bossplaces.lua", @"Scripts\lang_en.lua", @"Scripts\lang_ja.lua", @"Scripts\lang_zh.lua",
        @"Scripts\main.lua", @"Scripts\questdetails.lua", @"Scripts\roadbook.lua", @"Scripts\roadbook_en.lua",
        @"Scripts\roadbook_ja.lua", @"Scripts\roadbook_zh.lua",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "PbzUe4ss", Guid.NewGuid().ToString("N"));

    private string GameRoot => Path.Combine(_root, "steamapps", "common", "PBZ");
    private string LauncherDir => Path.Combine(GameRoot, "steam");
    private string Ue4ssMods => Path.Combine(GameRoot, "platform", "steam", "ue4ss", "Mods");
    private string ModPath => Path.Combine(GameRoot, "ProjectPBZ", "Content", "Paks", "~mods");

    // ─── 新导入 ───

    [Fact]
    public async Task Nexus压缩包_顶层带游戏安装目录名_落到本体旁的UE4SS目录_停用后清干净()
    {
        var f = await OpenAsync();
        // PBZ_AIO_1.8.2.zip 内：PBZ/platform/steam/ue4ss/Mods/PBZCompassMap/…
        var zip = CreateZip("PBZ_AIO_1.8.2", CompassMapFiles.Select(p => @"PBZ\platform\steam\ue4ss\Mods\PBZCompassMap\" + p));

        var package = await ImportOneAsync(f, zip);
        Assert.Equal(PackageKind.Plugin, package.Kind);
        Assert.All(package.Artifacts, a => Assert.StartsWith(@"ue4ss\Mods\PBZCompassMap\", a.RelativeTargetPath));

        await EnableAndDeployAsync(f, package);

        AssertCompassMapInstalled();
        AssertLauncherDirectoryUntouched();

        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, false);
        Assert.Equal(DeploymentStatus.Committed, (await f.DeployAsync()).Status);
        Assert.False(Directory.Exists(Path.Combine(Ue4ssMods, "PBZCompassMap")), "停用后 MOD 文件夹应被清掉");
    }

    /// <summary>
    /// 玩家解压后拖进来的文件夹层级各不相同（诊断包里三种都出现过），结果必须一样。
    /// </summary>
    [Theory]
    [InlineData(@"PBZ_AIO_1.8.2", @"PBZ\platform\steam\ue4ss\Mods\")] // 拖最外层：里面是 PBZ\platform\…
    [InlineData(@"PBZ", @"platform\steam\ue4ss\Mods\")]               // 拖 PBZ：里面是 platform\…
    [InlineData(@"platform", @"steam\ue4ss\Mods\")]                   // 拖 platform：里面是 steam\ue4ss\…
    [InlineData(@"ue4ss", @"Mods\")]                                  // 拖 ue4ss
    [InlineData(@"PBZCompassMap_wrap", @"")]                          // 只有 MOD 文件夹，没有任何锚点
    public async Task 拖入不同层级的文件夹_都落到同一个位置(string folderName, string inner)
    {
        var f = await OpenAsync();
        var folder = CreateFolder(folderName, CompassMapFiles.Select(p => inner + @"PBZCompassMap\" + p));

        var package = await ImportOneAsync(f, folder);
        await EnableAndDeployAsync(f, package);

        AssertCompassMapInstalled();
        AssertLauncherDirectoryUntouched();
    }

    [Fact]
    public async Task 不带锚点的散装UE4SS_MOD文件夹_收进UE4SS的Mods目录()
    {
        var f = await OpenAsync();
        // 「2. UE4SS Companion Mod」：顶层直接是 AMDFidelityOptimizer\enabled.txt + Scripts\main.lua
        var zip = CreateZip("2. UE4SS Companion Mod (In-Memory Stutter Fix)",
            [@"AMDFidelityOptimizer\enabled.txt", @"AMDFidelityOptimizer\Scripts\main.lua"]);

        var package = await ImportOneAsync(f, zip);
        await EnableAndDeployAsync(f, package);

        Assert.True(File.Exists(Path.Combine(Ue4ssMods, "AMDFidelityOptimizer", "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(Ue4ssMods, "AMDFidelityOptimizer", "enabled.txt")));
        AssertLauncherDirectoryUntouched();
    }

    [Fact]
    public async Task UE4SS运行时_代理DLL落在游戏本体旁边()
    {
        var f = await OpenAsync();
        var zip = CreateZip("UE4SS For Phantom Blade Zero",
            ["dwmapi.dll", @"ue4ss\UE4SS.dll", @"ue4ss\UE4SS-settings.ini", @"ue4ss\Mods\mods.txt",
             @"ue4ss\Mods\BPModLoaderMod\Scripts\main.lua"]);

        var package = await ImportOneAsync(f, zip);
        await EnableAndDeployAsync(f, package);

        var binaryDir = Path.Combine(GameRoot, "platform", "steam");
        Assert.True(File.Exists(Path.Combine(binaryDir, "dwmapi.dll")));
        Assert.True(File.Exists(Path.Combine(binaryDir, "ue4ss", "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(Ue4ssMods, "BPModLoaderMod", "Scripts", "main.lua")));
        // 游戏本体一个字节都不能动
        Assert.Equal(@"ORIGINAL:platform\steam\PBZ.exe", File.ReadAllText(Path.Combine(binaryDir, "PBZ.exe")));
        AssertLauncherDirectoryUntouched();
    }

    // ─── 存量用户 ───

    /// <summary>
    /// 2.1.3 留下的现场：包以老形状入库（<c>platform\steam\ue4ss\…</c>），文件装在
    /// <c>PBZ\steam\platform\steam\ue4ss\…</c>，部署清单记着它们。升级后体检必须报出来，
    /// 重新部署一次（界面上的「立即修复」）就把旧文件撤掉、按正确位置装好，包不需要重新导入。
    /// </summary>
    [Fact]
    public async Task 老版本装错位置的现场_体检能发现_重新部署一次即修复()
    {
        var f = await OpenAsync();
        var (package, misplaced) = await RecreateLegacyInstallAsync(f);
        var health = f.CreateHealthCheck();

        var before = await health.CheckInstallPlacementAsync();
        Assert.True(before.Drift.HasDrift);
        Assert.Equal(CompassMapFiles.Length, before.Drift.Misplaced.Count);
        Assert.Equal([package.PackageKey], before.Drift.PackageKeys);
        var item = (await health.CheckAsync()).Checks.Single(c => c.Name == HealthCheckService.InstallPlacementCheckName);
        Assert.Equal(HealthStatus.Warning, item.Status);

        // 一键修复 = 按当前方案重新部署
        var repaired = await f.DeployAsync();
        Assert.Equal(DeploymentStatus.Committed, repaired.Status);

        AssertCompassMapInstalled();
        Assert.All(misplaced, path => Assert.False(File.Exists(path), $"装错位置的旧文件没有撤掉: {path}"));
        AssertLauncherDirectoryUntouched();

        var after = await health.CheckInstallPlacementAsync();
        Assert.False(after.Drift.HasDrift);
        Assert.Equal(HealthStatus.Ok,
            (await health.CheckAsync()).Checks.Single(c => c.Name == HealthCheckService.InstallPlacementCheckName).Status);

        // 修复后的事务可以整体回滚：旧现场原样回来，说明新基准下回滚与崩溃恢复用的仍是同一份清单
        var rollback = await f.Deployment.RollbackAsync(repaired);
        Assert.True(rollback.Succeeded, rollback.SkipReason);
        Assert.All(misplaced, path => Assert.True(File.Exists(path), $"回滚后应恢复: {path}"));
        Assert.False(Directory.Exists(Path.Combine(Ue4ssMods, "PBZCompassMap")));
    }

    [Fact]
    public async Task 老版本装错位置的文件被改过_不会被悄悄删掉()
    {
        var f = await OpenAsync();
        var (_, misplaced) = await RecreateLegacyInstallAsync(f);
        File.WriteAllText(misplaced[0], "玩家自己改过的内容");

        var view = await f.Views.BuildAsync();
        await Assert.ThrowsAsync<ExternallyModifiedFilesException>(() => f.Planner.CreatePlanForViewAsync(view));

        Assert.Equal("玩家自己改过的内容", File.ReadAllText(misplaced[0]));
    }

    [Fact]
    public async Task 位置正确的安装_体检不报()
    {
        var f = await OpenAsync();
        var zip = CreateZip("PBZ_AIO_1.8.2", CompassMapFiles.Select(p => @"PBZ\platform\steam\ue4ss\Mods\PBZCompassMap\" + p));
        await EnableAndDeployAsync(f, await ImportOneAsync(f, zip));

        var report = await f.CreateHealthCheck().CheckInstallPlacementAsync();

        Assert.False(report.Drift.HasDrift);
        Assert.Empty(report.Issues);
    }

    // ─── 装不到有效位置的包 ───

    [Fact]
    public async Task 多选一的配置方案_导入时明说_不铺进游戏目录_体检列出()
    {
        var f = await OpenAsync();
        var results = await f.Imports.ImportSingleAsync(CreateOptimizerArchive());
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        var profile = results.Single(r => r.Package!.PackageKey.StartsWith("1. "));
        var companion = results.Single(r => r.Package!.PackageKey.StartsWith("2. "));

        Assert.Equal(PackageKind.Config, profile.Package!.Kind);
        Assert.NotNull(profile.Warning);
        Assert.Contains("Tier 1", profile.Warning);
        Assert.Null(companion.Warning);

        await EnableAndDeployAsync(f, profile.Package);
        await EnableAndDeployAsync(f, companion.Package!);

        // 同一个压缩包里的脚本 MOD 照常装到位，多选一的配置一个文件都不铺
        Assert.True(File.Exists(Path.Combine(Ue4ssMods, "AMDFidelityOptimizer", "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(Ue4ssMods, "AMDFidelityOptimizer", "enabled.txt")));
        Assert.Empty(Directory.EnumerateDirectories(GameRoot, "Tier*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(GameRoot, "Engine.ini", SearchOption.AllDirectories));
        AssertLauncherDirectoryUntouched();

        var issue = Assert.Single((await f.CreateHealthCheck().CheckInstallPlacementAsync()).Issues);
        Assert.Equal(PlacementIssueKind.ChoiceVariants, issue.Kind);
        Assert.Equal(profile.Package.PackageKey, issue.PackageKey);
        Assert.Equal(HealthStatus.Warning, (await f.CreateHealthCheck().CheckAsync()).Checks
            .Single(c => c.Name == HealthCheckService.InstallPlacementCheckName).Status);
    }

    [Fact]
    public async Task 老版本铺进游戏目录的多选一配置与散装脚本MOD_重新部署后各归其位()
    {
        var f = await OpenAsync();
        var results = await f.Imports.ImportSingleAsync(CreateOptimizerArchive());
        var profile = results.Single(r => r.Package!.PackageKey.StartsWith("1. ")).Package!;
        var companion = results.Single(r => r.Package!.PackageKey.StartsWith("2. ")).Package!;
        // 把脚本 MOD 的入库路径改回老版本的形状（AMDFidelityOptimizer\…，没有 ue4ss\Mods）
        companion.Artifacts = companion.Artifacts.Select(a => new PackageArtifact
        {
            Id = a.Id, PackageId = a.PackageId, RelativeSourcePath = a.RelativeSourcePath,
            RelativeTargetPath = Path.GetRelativePath(Path.Combine("ue4ss", "Mods"), a.RelativeTargetPath),
            FileName = a.FileName, FileSize = a.FileSize, FileHash = a.FileHash, ArtifactType = a.ArtifactType
        }).ToList();
        await f.Repository.UpdatePackageAsync(companion);
        await f.Profiles.AddPackagesToCurrentProfileAsync([profile, companion]);
        await f.Profiles.SetPackageEnabledFlagAsync(profile.PackageKey, true);
        await f.Profiles.SetPackageEnabledFlagAsync(companion.PackageKey, true);
        // 2.1.3 的现场：Tier N\ 与 AMDFidelityOptimizer\ 被原样铺在启动壳目录下
        var stray = await f.PlantLegacyFilesAsync([profile, companion], a => Path.Combine(LauncherDir, a.RelativeTargetPath));
        Assert.Contains(Path.Combine(LauncherDir, "AMDFidelityOptimizer", "Scripts", "main.lua"), stray);
        Assert.Equal(8, stray.Count);

        var drift = (await f.CreateHealthCheck().CheckInstallPlacementAsync()).Drift;
        Assert.Equal([companion.PackageKey], drift.PackageKeys);

        Assert.Equal(DeploymentStatus.Committed, (await f.DeployAsync()).Status);

        Assert.All(stray, path => Assert.False(File.Exists(path), $"没用的旧文件没有撤掉: {path}"));
        Assert.True(File.Exists(Path.Combine(Ue4ssMods, "AMDFidelityOptimizer", "Scripts", "main.lua")));
        AssertLauncherDirectoryUntouched();
    }

    [Fact]
    public async Task 只拖了Scripts文件夹的零散脚本_导入时提醒()
    {
        var f = await OpenAsync();
        var folder = CreateFolder("Scripts", ["main.lua", "bossplaces.lua"]);

        var result = Assert.Single(await f.Imports.ImportSingleAsync(folder));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Warning);
    }

    // ─── 其它游戏不受影响 ───

    [Fact]
    public void 没有启动壳的UE游戏_插件根就是游戏路径()
    {
        var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(_root, "other.json"));
        game.Config.GameName = "黑神话·悟空";
        game.Config.GamePath = Path.Combine(_root, "BlackMythWukong", "b1", "Binaries", "Win64");

        Assert.Equal(game.CurrentGamePath, game.CurrentPluginRootPath);
        Assert.Empty(game.CurrentInstallLayoutRules.BinaryAnchors);
        Assert.Contains("ue4ss", game.CurrentInstallLayoutRules.LoaderDirectories);
    }

    [Theory]
    [InlineData("黑神话·悟空")]
    [InlineData("剑星")]
    [InlineData("剑星 (CNS)")]
    [InlineData("光与影：33号远征队")]
    [InlineData("明末·渊虚之羽")]
    [InlineData("无主之地4")]
    [InlineData("暗黑破坏神4")]
    [InlineData("生化危机9")]
    [InlineData("识质存在")]
    [InlineData("死亡搁浅2")]
    [InlineData("杀戮尖塔2")]
    public void 其它内置游戏没有启动壳约定(string gameName)
        => Assert.Null(GameConfigService.GetInstallLayout(gameName));

    [Fact]
    public void 影之刃零_游戏路径是本体目录时也不改道()
    {
        CreateMirroredLayout();
        var binaryDir = Path.Combine(GameRoot, "platform", "steam");

        Assert.Equal(binaryDir, GameConfigService.ResolvePluginRoot(GameName, binaryDir).PluginRoot);
        Assert.Equal(binaryDir, GameConfigService.ResolvePluginRoot(GameName, LauncherDir).PluginRoot);
    }

    // ─── 场景搭建 ───

    private void AssertCompassMapInstalled()
    {
        foreach (var file in CompassMapFiles)
        {
            var path = Path.Combine(Ue4ssMods, "PBZCompassMap", file);
            Assert.True(File.Exists(path), $"没有落到游戏读取的位置: {path}");
            Assert.EndsWith(@"PBZCompassMap\" + file, File.ReadAllText(path));
        }
    }

    /// <summary>启动壳目录里除了 PBZ.exe 不该多出任何东西——那里的文件游戏不会读。</summary>
    private void AssertLauncherDirectoryUntouched()
        => Assert.Equal(
            [Path.Combine(LauncherDir, "PBZ.exe")],
            Directory.EnumerateFileSystemEntries(LauncherDir, "*", SearchOption.AllDirectories)
                .Where(p => File.Exists(p) || Directory.EnumerateFileSystemEntries(p).Any()));

    /// <summary>内容带上完整相对路径：三套方案的同名 ini 内容各不相同，与真实包一致。</summary>
    private static string Content(string relativePath) => "PBZ-UE4SS:" + relativePath;

    private async Task<Package> ImportOneAsync(Fixture f, string source)
    {
        var results = await f.Imports.ImportSingleAsync(source);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        return Assert.Single(results).Package!;
    }

    private static async Task EnableAndDeployAsync(Fixture f, Package package)
    {
        await f.Profiles.AddPackagesToCurrentProfileAsync([package]);
        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, true);
        Assert.Equal(DeploymentStatus.Committed, (await f.DeployAsync()).Status);
    }

    /// <summary>
    /// 复刻 2.1.3 的现场：包按老形状入库（玩家拖的是 PBZ 文件夹，入库路径 platform\steam\ue4ss\…），
    /// 文件装在 游戏路径\platform\steam\ue4ss\…，并由部署清单托管。
    /// </summary>
    private async Task<(Package Package, List<string> Misplaced)> RecreateLegacyInstallAsync(Fixture f)
    {
        var folder = CreateFolder("PBZ", CompassMapFiles.Select(p => @"platform\steam\ue4ss\Mods\PBZCompassMap\" + p));
        var package = await ImportOneAsync(f, folder);

        // 把入库路径改回老版本的形状（当时没有归位这一步）
        package.Artifacts = package.Artifacts.Select(a => new PackageArtifact
        {
            Id = a.Id, PackageId = a.PackageId, RelativeSourcePath = a.RelativeSourcePath,
            RelativeTargetPath = Path.Combine("platform", "steam", a.RelativeTargetPath),
            FileName = a.FileName, FileSize = a.FileSize, FileHash = a.FileHash, ArtifactType = a.ArtifactType
        }).ToList();
        await f.Repository.UpdatePackageAsync(package);

        await f.Profiles.AddPackagesToCurrentProfileAsync([package]);
        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, true);
        var misplaced = await f.PlantLegacyFilesAsync([package], a => Path.Combine(LauncherDir, a.RelativeTargetPath));
        Assert.Contains(Path.Combine(LauncherDir, "platform", "steam", "ue4ss", "Mods", "PBZCompassMap", "Scripts", "main.lua"), misplaced);
        return (package, misplaced);
    }

    private void CreateMirroredLayout()
    {
        foreach (var rel in new[]
                 {
                     @"steam\PBZ.exe",
                     @"platform\steam\PBZ.exe",
                     @"ProjectPBZ\Binaries\Win64\CheckEnv.exe",
                     @"ProjectPBZ\Content\Paks\ProjectPBZ-Windows.pak",
                 })
        {
            var full = Path.Combine(GameRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "ORIGINAL:" + rel);
        }
    }

    /// <summary>
    /// 「PBZ AMD Radeon 120FPS Fidelity Optimizer」的真实结构：顶层两个文件夹，
    /// 一个是三套互斥的 Engine.ini / Scalability.ini，一个是不带锚点的 UE4SS 脚本 MOD。
    /// </summary>
    private string CreateOptimizerArchive()
    {
        string[] tiers =
        [
            "Tier 1 - 8GB VRAM (RX 6600, 6650 XT, 7600)",
            "Tier 2 - 12GB VRAM (RX 6700, 6750 XT, 7700 XT)",
            "Tier 3 - 16GB+ VRAM (RX 6800, 6900, 7800, 7900 XT-XTX)",
        ];
        return CreateZip("PBZ AMD Radeon 120FPS Fidelity Optimizer V1.1",
            tiers.SelectMany(t => new[] { t + @"\Engine.ini", t + @"\Scalability.ini" })
                .Select(p => @"1. Choose Your GPU Profile\" + p)
                .Concat(
                [
                    @"2. UE4SS Companion Mod (In-Memory Stutter Fix)\AMDFidelityOptimizer\enabled.txt",
                    @"2. UE4SS Companion Mod (In-Memory Stutter Fix)\AMDFidelityOptimizer\Scripts\main.lua",
                ]));
    }

    private string CreateFolder(string name, IEnumerable<string> relativeFiles)
    {
        var folder = Path.Combine(_root, "import", Guid.NewGuid().ToString("N"), name);
        foreach (var rel in relativeFiles)
        {
            var full = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Content(rel));
        }
        return folder;
    }

    private string CreateZip(string name, IEnumerable<string> relativeFiles)
    {
        var staging = CreateFolder(name, relativeFiles);
        var zip = Path.Combine(Path.GetDirectoryName(staging)!, name + ".zip");
        ZipFile.CreateFromDirectory(staging, zip);
        return zip;
    }

    private async Task<Fixture> OpenAsync()
    {
        CreateMirroredLayout();
        // 与「自动检测」一致：游戏路径 = 启动壳所在目录
        return await Fixture.OpenAsync(_root, LauncherDir, ModPath);
    }

    private sealed record Fixture(
        GameConfigService Game, PackageRepository Repository, ProfileService Profiles, PackageImportService Imports,
        ResolvedViewBuilder Views, DeploymentPlanner Planner, DeploymentService Deployment, DeploymentStateStore State)
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

            return new Fixture(game, repository, profiles, imports, views,
                new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
                    repository, store, profiles, game, views, state),
                new DeploymentService(NullLogger<DeploymentService>.Instance,
                    [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state,
                    Path.Combine(root, "Backups")),
                state);
        }

        public HealthCheckService CreateHealthCheck()
            => new(NullLogger<HealthCheckService>.Instance, Game, Profiles, Repository, Views, State);

        public async Task<DeploymentTransaction> DeployAsync()
        {
            var view = await Views.BuildAsync();
            var plan = await Planner.CreatePlanForViewAsync(view);
            return await Deployment.ExecuteAsync(plan);
        }

        /// <summary>
        /// 按老版本的落盘位置把包的文件放进游戏目录，并写一份托管它们的部署清单
        /// （清单里没有插件根字段，与 2.1.3 写出的一致）。
        /// </summary>
        public async Task<List<string>> PlantLegacyFilesAsync(
            IReadOnlyList<Package> packages, Func<PackageArtifact, string> legacyTarget)
        {
            var files = new List<ManagedDeploymentFile>();
            foreach (var (package, artifact) in packages.SelectMany(p => p.Artifacts.Select(a => (p, a))))
            {
                // 老版本把安装树里的图片记成预览图、不部署
                if (artifact.ArtifactType == ArtifactType.PreviewImage) continue;
                var source = Path.Combine(Repository.Store.RepositoryRoot, artifact.RelativeSourcePath);
                var target = legacyTarget(artifact);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                files.Add(new ManagedDeploymentFile
                {
                    TargetPath = target, RelativeTargetPath = Path.GetRelativePath(Game.CurrentGamePath, target),
                    PackageKey = package.PackageKey, PackageDisplayName = package.DisplayName, Kind = package.Kind,
                    FileHash = await ObjectStore.ComputeFileHashAsync(target), FileSize = new FileInfo(target).Length
                });
            }
            await State.WriteAsync(new DeploymentState
            {
                HostGameName = GameName,
                GameRootPath = DeploymentStateStore.NormalizeRoot(Game.CurrentGamePath),
                ModRootPath = DeploymentStateStore.NormalizeRoot(Game.CurrentModPath),
                Revision = Guid.NewGuid(), Files = files
            });
            return files.Select(file => file.TargetPath).ToList();
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
