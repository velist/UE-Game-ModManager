using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Health;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;
using UEModManager.Services.Loaders;

namespace UEModManager.Tests.Services;

/// <summary>
/// UE4SS 的检测与脚本 MOD 落位的端到端验证：<b>导入 → 部署 → 检测 → 体检</b> 全走真实服务，
/// 不联网，游戏目录是临时目录里搭的。（查新、下载与一键安装的用例在 2.2.0 线的同名文件里。）
///
/// 压缩包形状照 2026-11-25 实际下载的官方发布包：v4.0.0（<c>dwmapi.dll</c> + <c>ue4ss\</c>）
/// 与 v3.0.1（平铺：<c>dwmapi.dll</c>、<c>UE4SS.dll</c>、<c>Mods\</c> 都在 exe 旁）。
/// </summary>
public sealed class Ue4ssInstallEndToEndTests : IDisposable
{
    private const string GameName = "剑星";

    /// <summary>玩家自己的 mods.txt（官方 v3.0.1 包里的原文节选）：管理器一个字节都不许动。</summary>
    private const string PlayerModsTxt =
        "CheatManagerEnablerMod : 1\r\nConsoleCommandsMod : 1\r\nMyOwnMod : 0\r\n\r\n; Built-in keybinds, do not move up!\r\nKeybinds : 1\r\n";

    private static readonly string[] V4Files =
    [
        "dwmapi.dll", @"ue4ss\LICENSE", @"ue4ss\UE4SS-settings.ini", @"ue4ss\UE4SS.dll", @"ue4ss\Mods\mods.txt",
        @"ue4ss\Mods\BPModLoaderMod\Scripts\main.lua", @"ue4ss\Mods\BPModLoaderMod\load_order.txt",
        @"ue4ss\Mods\Keybinds\Scripts\main.lua", @"ue4ss\Mods\shared\UEHelpers\UEHelpers.lua",
        @"ue4ss\UE4SS_SDK_Backends\UE4SS.json",
    ];

    private static readonly string[] V3Files =
    [
        "Changelog.md", "dwmapi.dll", "README.md", "UE4SS-settings.ini", "UE4SS.dll", @"Mods\mods.txt",
        @"Mods\BPModLoaderMod\Scripts\main.lua", @"Mods\Keybinds\Scripts\main.lua", @"Mods\shared\UEHelpers\UEHelpers.lua",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "Ue4ssE2E", Guid.NewGuid().ToString("N"));

    private string GameRoot => Path.Combine(_root, "steamapps", "common", "StellarBlade");
    private string BinaryDir => Path.Combine(GameRoot, "SB", "Binaries", "Win64");
    private string ModPath => Path.Combine(GameRoot, "SB", "Content", "Paks", "~mods");

    // ─── 检测 + 落位 ───

    [Fact]
    public async Task 没装UE4SS_脚本MOD按现行结构落位_缺的enabled补上_停用后清干净()
    {
        var f = await OpenAsync();
        Assert.Equal(Ue4ssInstallState.NotInstalled, f.Loader.Detect()!.State);
        Assert.Empty(f.Loader.EnabledScriptModNames());

        // 很多 MOD 只带脚本，指望玩家自己去改 mods.txt
        var package = await ImportOneAsync(f, CreateZip("CoolMod", [@"CoolMod\Scripts\main.lua", @"CoolMod\Scripts\util.lua"]));
        await EnableAndDeployAsync(f, package);

        var modDir = Path.Combine(BinaryDir, "ue4ss", "Mods", "CoolMod");
        Assert.True(File.Exists(Path.Combine(modDir, "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(modDir, "enabled.txt")), "缺 enabled.txt 的脚本 MOD 应由管理器补上，否则 UE4SS 不会启动它");
        Assert.Equal(0, new FileInfo(Path.Combine(modDir, "enabled.txt")).Length);
        Assert.Equal(["CoolMod"], f.Loader.EnabledScriptModNames());
        // 装了脚本 MOD 不等于装了 UE4SS
        Assert.Equal(Ue4ssInstallState.NotInstalled, f.Loader.Detect()!.State);

        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, false);
        Assert.Equal(DeploymentStatus.Committed, (await f.DeployAsync()).Status);
        Assert.False(Directory.Exists(modDir), "停用后 MOD 文件夹（连同补的 enabled.txt）应被清掉");
        Assert.Empty(f.Loader.EnabledScriptModNames());
    }

    [Fact]
    public async Task 玩家自己装的老式UE4SS_识别为已安装_脚本MOD装进exe旁的Mods_不碰玩家的mods列表()
    {
        var f = await OpenAsync();
        PlantManualInstall(V3Files);
        var modsTxt = Path.Combine(BinaryDir, "Mods", "mods.txt");
        File.WriteAllText(modsTxt, PlayerModsTxt);

        var detection = f.Loader.Detect()!;
        Assert.Equal(Ue4ssInstallState.Installed, detection.State);
        Assert.Equal(Ue4ssLayout.Flat, detection.Layout);
        Assert.Null(f.Loader.FindManagedPackage()); // 不是管理器装的，不接管

        // MOD 作者按现行结构打的包
        var package = await ImportOneAsync(f, CreateZip("CoolMod",
            [@"ue4ss\Mods\CoolMod\Scripts\main.lua", @"ue4ss\Mods\CoolMod\enabled.txt"]));
        await EnableAndDeployAsync(f, package);

        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "CoolMod", "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "CoolMod", "enabled.txt")));
        Assert.False(Directory.Exists(Path.Combine(BinaryDir, "ue4ss")), "老式结构的 UE4SS 不读 ue4ss\\Mods，不该往那里装");
        Assert.Equal(PlayerModsTxt, File.ReadAllText(modsTxt));

        var report = await f.CreateHealthCheck().CheckInstallPlacementAsync();
        Assert.False(report.Drift.HasDrift);

        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, false);
        Assert.Equal(DeploymentStatus.Committed, (await f.DeployAsync()).Status);
        Assert.False(Directory.Exists(Path.Combine(BinaryDir, "Mods", "CoolMod")));
        // 玩家自己装的东西一样不少
        Assert.Equal(PlayerModsTxt, File.ReadAllText(modsTxt));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "Keybinds", "Scripts", "main.lua")));
    }

    [Fact]
    public async Task 先装了脚本MOD_后来玩家手动装了老式UE4SS_体检报装错位置_重新部署一次即挪过去()
    {
        var f = await OpenAsync();
        var package = await ImportOneAsync(f, CreateZip("CoolMod", [@"CoolMod\Scripts\main.lua", @"CoolMod\enabled.txt"]));
        await EnableAndDeployAsync(f, package);
        var modern = Path.Combine(BinaryDir, "ue4ss", "Mods", "CoolMod", "Scripts", "main.lua");
        Assert.True(File.Exists(modern));
        Assert.False((await f.CreateHealthCheck().CheckInstallPlacementAsync()).Drift.HasDrift);

        PlantManualInstall(V3Files);

        var report = await f.CreateHealthCheck().CheckInstallPlacementAsync();
        Assert.True(report.Drift.HasDrift, "UE4SS 读的是 Mods\\，装在 ue4ss\\Mods 的脚本 MOD 应报成装错位置");
        Assert.Contains(modern, report.Drift.Misplaced);
        Assert.Equal([package.PackageKey], report.Drift.PackageKeys);

        // 界面上的「立即修复」就是按当前方案重新部署一次
        Assert.Equal(DeploymentStatus.Committed, (await f.DeployAsync()).Status);

        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "CoolMod", "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "CoolMod", "enabled.txt")));
        Assert.False(File.Exists(modern));
        Assert.False((await f.CreateHealthCheck().CheckInstallPlacementAsync()).Drift.HasDrift);
    }

    [Fact]
    public async Task 拖入官方v4压缩包_识别为UE4SS本体_整包装到插件根_可停用()
    {
        var f = await OpenAsync();

        var package = await ImportOneAsync(f, CreateZip("UE4SS_v4.0.0", V4Files));
        Assert.NotEqual(PackageKind.Mod, package.Kind);
        await EnableAndDeployAsync(f, package);

        Assert.True(File.Exists(Path.Combine(BinaryDir, "dwmapi.dll")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "ue4ss", "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "ue4ss", "UE4SS-settings.ini")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "ue4ss", "UE4SS_SDK_Backends", "UE4SS.json")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "ue4ss", "Mods", "mods.txt")), "mods.txt 是 UE4SS 的运行时文件，不能当说明丢掉");
        Assert.True(File.Exists(Path.Combine(BinaryDir, "ue4ss", "Mods", "shared", "UEHelpers", "UEHelpers.lua")));
        // 自带的示例 MOD 开不开由它的 mods.txt 定，不替它加 enabled.txt
        Assert.False(File.Exists(Path.Combine(BinaryDir, "ue4ss", "Mods", "BPModLoaderMod", "enabled.txt")));
        Assert.False(Directory.Exists(ModPath) && Directory.EnumerateFiles(ModPath, "*", SearchOption.AllDirectories).Any(),
            "UE4SS 的文件不该进 pak 的 MOD 目录");

        var detection = f.Loader.Detect()!;
        Assert.Equal(Ue4ssInstallState.Installed, detection.State);
        Assert.Equal(Ue4ssLayout.Subfolder, detection.Layout);
        Assert.Equal(package.PackageKey, f.Loader.FindManagedPackage()?.PackageKey);
        // 本体自带的示例 MOD 不算「玩家装了脚本 MOD」
        Assert.Empty(f.Loader.EnabledScriptModNames());
        // 游戏本体一个字节都不能动
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(BinaryDir, "SB-Win64-Shipping.exe")));

        await f.Profiles.SetPackageEnabledFlagAsync(package.PackageKey, false);
        Assert.Equal(DeploymentStatus.Committed, (await f.DeployAsync()).Status);
        Assert.Equal(Ue4ssInstallState.NotInstalled, f.Loader.Detect()!.State);
        Assert.False(File.Exists(Path.Combine(BinaryDir, "dwmapi.dll")));
    }

    [Fact]
    public async Task 拖入官方v3平铺包_已启用的脚本MOD在同一次部署里改落到Mods()
    {
        var f = await OpenAsync();
        var mod = await ImportOneAsync(f, CreateZip("CoolMod", [@"CoolMod\Scripts\main.lua"]));
        await EnableAndDeployAsync(f, mod);
        Assert.True(File.Exists(Path.Combine(BinaryDir, "ue4ss", "Mods", "CoolMod", "Scripts", "main.lua")));

        var runtime = await ImportOneAsync(f, CreateZip("UE4SS_v3.0.1", V3Files));
        // 这一次部署开始时磁盘上还没有 UE4SS：结构必须按「即将装上的这一份」算
        await EnableAndDeployAsync(f, runtime);

        Assert.True(File.Exists(Path.Combine(BinaryDir, "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "mods.txt")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "Keybinds", "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "CoolMod", "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(BinaryDir, "Mods", "CoolMod", "enabled.txt")));
        Assert.False(Directory.Exists(Path.Combine(BinaryDir, "ue4ss")), "ue4ss\\Mods 里不该留下这份 UE4SS 不读的 MOD");

        Assert.Equal(Ue4ssLayout.Flat, f.Loader.Detect()!.Layout);
        Assert.Equal(runtime.PackageKey, f.Loader.FindManagedPackage()?.PackageKey);
        Assert.False((await f.CreateHealthCheck().CheckInstallPlacementAsync()).Drift.HasDrift);
    }

    [Fact]
    public async Task 残缺安装_只剩代理DLL与设置_报残缺而不是已安装()
    {
        var f = await OpenAsync();
        PlantManualInstall(["dwmapi.dll", @"ue4ss\UE4SS-settings.ini", @"ue4ss\Mods\mods.txt"]); // UE4SS.dll 被杀软删了

        var detection = f.Loader.Detect()!;
        Assert.Equal(Ue4ssInstallState.Incomplete, detection.State);
        Assert.Equal(Ue4ssProblem.MissingCore, detection.Problem);
    }

    [Fact]
    public async Task 代理DLL被作者改了名_按内容认出_不误报成残缺()
    {
        var f = await OpenAsync();
        PlantManualInstall(["UE4SS.dll", "UE4SS-settings.ini", @"Mods\mods.txt", "unrelated.dll"]);
        Assert.Equal(Ue4ssProblem.MissingProxy, f.Loader.Detect()!.Problem);

        // 官方 proxy_generator 生成的代理里带着宽字符的 UE4SS.dll
        File.WriteAllBytes(Path.Combine(BinaryDir, "bink2w64.dll"),
            [0x4D, 0x5A, .. System.Text.Encoding.Unicode.GetBytes("UE4SS.dll"), 0, 0]);

        var detection = f.Loader.Detect()!;
        Assert.Equal(Ue4ssInstallState.Installed, detection.State);
        Assert.Equal("bink2w64.dll", detection.ProxyFileName);
    }

    [Fact]
    public async Task 非UE游戏_不涉及UE4SS()
    {
        var f = await OpenAsync(gameName: "生化危机9");

        Assert.False(f.Loader.IsRelevant);
        Assert.Null(f.Loader.Detect());
        Assert.Empty(f.Loader.EnabledScriptModNames());
    }

    // ─── 体检 ───

    [Fact]
    public async Task 体检_有启用的脚本MOD而没装UE4SS_给警告_装上后转正常()
    {
        var f = await OpenAsync();
        var health = f.CreateHealthCheck();
        // 没有脚本 MOD 的玩家：不报警告
        Assert.Equal(HealthStatus.Ok, (await health.CheckAsync()).Checks.Single(c => c.Name == HealthCheckService.Ue4ssCheckName).Status);

        var mod = await ImportOneAsync(f, CreateZip("CoolMod", [@"CoolMod\Scripts\main.lua"]));
        await EnableAndDeployAsync(f, mod);
        var missing = (await health.CheckAsync()).Checks.Single(c => c.Name == HealthCheckService.Ue4ssCheckName);
        Assert.Equal(HealthStatus.Warning, missing.Status);

        var runtime = await ImportOneAsync(f, CreateZip("UE4SS_v4.0.0", V4Files));
        await EnableAndDeployAsync(f, runtime);
        Assert.Equal(HealthStatus.Ok, (await health.CheckAsync()).Checks.Single(c => c.Name == HealthCheckService.Ue4ssCheckName).Status);
    }

    // ─── 场景搭建 ───

    private void PlantManualInstall(IEnumerable<string> relativeFiles)
    {
        foreach (var rel in relativeFiles)
        {
            var full = Path.Combine(BinaryDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "MANUAL:" + rel);
        }
    }

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

    private string CreateZip(string name, IEnumerable<string> relativeFiles)
    {
        var dir = Path.Combine(_root, "import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, name + ".zip");
        File.WriteAllBytes(zip, BuildZipBytes(relativeFiles));
        return zip;
    }

    private static byte[] BuildZipBytes(IEnumerable<string> relativeFiles)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var rel in relativeFiles)
            {
                using var writer = new StreamWriter(zip.CreateEntry(rel.Replace('\\', '/')).Open());
                writer.Write("UE4SS-FIXTURE:" + rel);
            }
        }
        return buffer.ToArray();
    }

    private async Task<Fixture> OpenAsync(string gameName = GameName)
    {
        Directory.CreateDirectory(BinaryDir);
        File.WriteAllText(Path.Combine(BinaryDir, "SB-Win64-Shipping.exe"), "ORIGINAL");
        return await Fixture.OpenAsync(Path.Combine(_root, "host-" + Guid.NewGuid().ToString("N")[..8]),
            gameName, BinaryDir, ModPath);
    }

    private sealed record Fixture(
        GameConfigService Game, PackageRepository Repository, ProfileService Profiles, PackageImportService Imports,
        ResolvedViewBuilder Views, DeploymentPlanner Planner, DeploymentService Deployment, DeploymentStateStore State,
        Ue4ssService Loader)
    {
        public static async Task<Fixture> OpenAsync(string root, string gameName, string gamePath, string modPath)
        {
            var data = Path.Combine(root, "Data");
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "Repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
            var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            game.Config.GameName = gameName;
            game.Config.GamePath = gamePath;
            game.Config.ModPath = modPath;

            await repository.SetCurrentGameAsync(gameName);
            await profiles.SetCurrentGameAsync(gameName);

            var overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, repository, null!,
                data, Path.Combine(root, "Overwrites"));
            await overwrites.SetCurrentGameAsync(gameName);

            var indexes = new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data);
            var imports = new PackageImportService(NullLogger<PackageImportService>.Instance,
                repository, store, game,
                new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
                indexes);
            var conflicts = new ConflictModuleService(NullLogger<ConflictModuleService>.Instance,
                repository, profiles, game, indexes);
            await conflicts.SetCurrentGameAsync(gameName);

            var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
                new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), overwrites, game);
            var state = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
                Path.Combine(root, "DeploymentState"));
            var planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
                repository, store, profiles, game, views, state);
            var deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
                [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state,
                Path.Combine(root, "Backups"));
            var loader = new Ue4ssService(NullLogger<Ue4ssService>.Instance, game, repository, profiles);
            return new Fixture(game, repository, profiles, imports, views, planner, deployment, state, loader);
        }

        public HealthCheckService CreateHealthCheck()
            => new(NullLogger<HealthCheckService>.Instance, Game, Profiles, Repository, Views, State, ue4ss: Loader);

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
