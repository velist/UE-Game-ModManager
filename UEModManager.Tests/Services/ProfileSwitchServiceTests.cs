using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;

namespace UEModManager.Tests.Services;

/// <summary>
/// 切换方案 = 先部署目标方案，成功才切换。
/// 以前只改"当前方案"，游戏目录里还是旧方案的文件，界面却显示新方案。
/// </summary>
public sealed class ProfileSwitchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ProfileSwitch", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 切换时立即部署目标方案()
    {
        var f = await Fixture.OpenAsync(_root);
        await f.DeployCurrentAsync();
        Assert.True(File.Exists(f.ConfigTarget));
        var empty = await f.Profiles.CreateProfileAsync("纯净");

        var result = await f.Switcher.SwitchAsync(empty.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(empty.Id, f.Profiles.CurrentProfile!.Id);
        Assert.False(File.Exists(f.ConfigTarget));   // 纯净方案没有这个配置，已从游戏目录移除
    }

    [Fact]
    public async Task 部署失败时留在原方案且游戏文件不变()
    {
        var f = await Fixture.OpenAsync(_root);
        await f.DeployCurrentAsync();
        var original = f.Profiles.CurrentProfile!.Id;
        var empty = await f.Profiles.CreateProfileAsync("纯净");

        OperationResult result;
        // 模拟游戏正在运行、文件被独占：移除这个文件必然失败
        using (new FileStream(f.ConfigTarget, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await f.Switcher.SwitchAsync(empty.Id);

        Assert.False(result.Success);
        Assert.Equal(original, f.Profiles.CurrentProfile!.Id);
        Assert.True(File.Exists(f.ConfigTarget));
        Assert.Contains("[Settings]", await File.ReadAllTextAsync(f.ConfigTarget));
    }

    [Fact]
    public async Task 未配置MOD目录时直接切换()
    {
        var f = await Fixture.OpenAsync(_root);
        f.Game.Config.ModPath = null;
        var empty = await f.Profiles.CreateProfileAsync("纯净");

        var result = await f.Switcher.SwitchAsync(empty.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(empty.Id, f.Profiles.CurrentProfile!.Id);
    }

    private sealed class Fixture
    {
        private string Root { get; init; } = "";
        public ProfileService Profiles { get; init; } = null!;
        public GameConfigService Game { get; init; } = null!;
        public DeploymentPlanner Planner { get; init; } = null!;
        public DeploymentService Deployment { get; init; } = null!;
        public ProfileSwitchService Switcher { get; init; } = null!;
        public string ConfigTarget => Path.Combine(Root, "Game", "Config", "settings.ini");

        public static async Task<Fixture> OpenAsync(string root)
        {
            var data = Path.Combine(root, "Data");
            var gameRoot = Path.Combine(root, "Game");
            Directory.CreateDirectory(Path.Combine(gameRoot, "Mods"));
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "Repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
            var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            game.Config.GameName = "Integration";
            game.Config.GamePath = gameRoot;
            game.Config.ModPath = Path.Combine(gameRoot, "Mods");
            await repository.SetCurrentGameAsync("Integration");
            await profiles.SetCurrentGameAsync("Integration");
            var overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, repository, null!,
                data, Path.Combine(root, "Overwrites"));
            await overwrites.SetCurrentGameAsync("Integration");
            var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
                new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), overwrites, game);
            var state = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
                Path.Combine(root, "DeploymentState"));
            var planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
                repository, store, profiles, game, views, state);
            var deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
                [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state, Path.Combine(root, "Backups"));

            // 当前方案里放一个已启用的配置包
            var source = Path.Combine(store.GetPackageFilesDirectory("cfg"), "settings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, "[Settings]\nMode=A\n");
            var hash = await ObjectStore.ComputeFileHashAsync(source);
            var length = new FileInfo(source).Length;
            var package = new Package
            {
                PackageKey = "cfg", DisplayName = "cfg", HostGameName = "Integration",
                Kind = PackageKind.Config, TargetRootPath = "Config", ContentHash = hash, TotalSize = length,
                Artifacts = [new PackageArtifact
                {
                    FileName = "settings.ini", ArtifactType = ArtifactType.ConfigFile,
                    RelativeSourcePath = "cfg/files/settings.ini", RelativeTargetPath = "settings.ini",
                    FileSize = length, FileHash = hash
                }]
            };
            await repository.RegisterPackageAsync(package);
            await profiles.AddPackagesToCurrentProfileAsync([package]);
            await profiles.SetPackageEnabledFlagAsync("cfg", true);

            return new Fixture
            {
                Root = root, Profiles = profiles, Game = game, Planner = planner, Deployment = deployment,
                Switcher = new ProfileSwitchService(profiles, planner, deployment, game,
                    NullLogger<ProfileSwitchService>.Instance)
            };
        }

        public async Task DeployCurrentAsync()
        {
            var tx = await Deployment.ExecuteAsync(await Planner.CreatePlanAsync());
            Assert.Equal(DeploymentStatus.Committed, tx.Status);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
