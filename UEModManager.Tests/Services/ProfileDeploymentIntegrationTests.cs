using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;

namespace UEModManager.Tests.Services;

/// <summary>Crosses lock import, profile-scoped conflict selection, merged output and actual deployment.</summary>
public sealed class ProfileDeploymentIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ProfileDeployment", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportedConflictSelection_DrivesDeploymentAcrossRoots_SurvivesRestartAndRollback()
    {
        var source = await Fixture.OpenAsync(Path.Combine(_root, "Source"));
        await source.AddConfigPackagesAsync();
        await source.Conflicts.SetOverrideAsync(source.ConfigTarget, "cfg-b");
        var lockPath = Path.Combine(_root, "portable.lock.json");
        await source.Locks.ExportAsync(lockPath);

        var destinationRoot = Path.Combine(_root, "Destination");
        var destination = await Fixture.OpenAsync(destinationRoot);
        await destination.AddConfigPackagesAsync();
        var originalProfileId = destination.Profiles.CurrentProfile!.Id;
        await destination.Conflicts.SetOverrideAsync(destination.ConfigTarget, "cfg-a");

        var preview = await destination.Locks.PreviewImportAsync(lockPath);
        Assert.Equal("cfg-b", preview.lockFile.ConflictOverrides["@game/Config/settings.ini"]);
        var imported = await destination.Locks.ApplyImportAsync(preview.lockFile);
        Assert.NotEqual(originalProfileId, imported.Id);
        Assert.Equal(imported.Id, destination.Profiles.CurrentProfile!.Id);
        await AssertWinnerAsync(destination, "cfg-b");

        var view = await destination.Views.BuildAsync();
        var merged = Assert.Single(view.ConfigMergeResults);
        Assert.True(merged.Success);
        AssertConfig(merged.MergedContent!, "B");
        var plan = await destination.Planner.CreatePlanForViewAsync(view);
        var install = Assert.Single(plan.Operations);
        Assert.Equal(destination.ConfigTarget, install.TargetPath, ignoreCase: true);
        var deployed = await destination.Deployment.ExecuteAsync(plan);
        Assert.Equal(DeploymentStatus.Committed, deployed.Status);
        AssertConfig(await File.ReadAllTextAsync(destination.ConfigTarget), "B");
        Assert.Empty((await destination.Planner.CreatePlanAsync()).Operations);

        // The same lock must not change a pre-existing profile's winner.
        await destination.Profiles.SwitchProfileAsync(originalProfileId);
        await AssertWinnerAsync(destination, "cfg-a");
        var oldProfilePlan = await destination.Planner.CreatePlanAsync();
        var replacement = Assert.Single(oldProfilePlan.Operations);
        Assert.Equal(DeploymentOperationType.Replace, replacement.Type);
        Assert.Equal(new FileInfo(destination.ConfigTarget).Length, new FileInfo(replacement.SourcePath!).Length);
        var changed = await destination.Deployment.ExecuteAsync(oldProfilePlan);
        Assert.Equal(DeploymentStatus.Committed, changed.Status);
        AssertConfig(await File.ReadAllTextAsync(destination.ConfigTarget), "A");
        Assert.True((await destination.Deployment.RollbackAsync(changed)).Succeeded);
        AssertConfig(await File.ReadAllTextAsync(destination.ConfigTarget), "B");

        await destination.Profiles.SwitchProfileAsync(imported.Id);
        var restarted = await Fixture.OpenAsync(destinationRoot);
        Assert.Equal(imported.Id, restarted.Profiles.CurrentProfile!.Id);
        await AssertWinnerAsync(restarted, "cfg-b");
        Assert.Empty((await restarted.Planner.CreatePlanAsync()).Operations);

        // The generated merge is managed too: an empty profile removes it, preserving other files.
        var unmanaged = Path.Combine(Path.GetDirectoryName(restarted.ConfigTarget)!, "user-notes.txt");
        await File.WriteAllTextAsync(unmanaged, "leave this file alone");
        var empty = await restarted.Profiles.CreateProfileAsync("Empty");
        await restarted.Profiles.SwitchProfileAsync(empty.Id);
        var removePlan = await restarted.Planner.CreatePlanAsync();
        var removal = Assert.Single(removePlan.Operations);
        Assert.Equal(DeploymentOperationType.Remove, removal.Type);
        Assert.Equal(restarted.ConfigTarget, removal.TargetPath, ignoreCase: true);
        var removed = await restarted.Deployment.ExecuteAsync(removePlan);
        Assert.Equal(DeploymentStatus.Committed, removed.Status);
        Assert.False(File.Exists(restarted.ConfigTarget));
        Assert.Equal("leave this file alone", await File.ReadAllTextAsync(unmanaged));
        Assert.True((await restarted.Deployment.RollbackAsync(removed)).Succeeded);
        AssertConfig(await File.ReadAllTextAsync(restarted.ConfigTarget), "B");
        Assert.Equal("leave this file alone", await File.ReadAllTextAsync(unmanaged));
    }

    private static async Task AssertWinnerAsync(Fixture fixture, string expected)
    {
        var analysis = await fixture.Conflicts.AnalyzeAsync();
        var conflict = Assert.Single(analysis.Conflicts);
        Assert.Equal(expected, conflict.WinnerPackageKey);
        Assert.True(conflict.IsUserOverride);
    }

    private static void AssertConfig(string content, string mode)
    {
        var entries = new IniParser().Parse(content)
            .Where(entry => !string.IsNullOrEmpty(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(mode, entries["Mode"]);
        Assert.Equal("one", entries["OnlyA"]);
        Assert.Equal("two", entries["OnlyB"]);
    }

    private sealed class Fixture
    {
        private string Root { get; init; } = "";
        private ObjectStore Store { get; init; } = null!;
        private PackageRepository Repository { get; init; } = null!;
        public ProfileService Profiles { get; init; } = null!;
        public ConflictAnalyzer Conflicts { get; init; } = null!;
        public ProfileLockService Locks { get; init; } = null!;
        public ResolvedViewBuilder Views { get; init; } = null!;
        public DeploymentPlanner Planner { get; init; } = null!;
        public DeploymentService Deployment { get; init; } = null!;
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
            var conflicts = new ConflictAnalyzer(NullLogger<ConflictAnalyzer>.Instance, repository, profiles, game);
            await conflicts.SetCurrentGameAsync("Integration");
            var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
                new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), overwrites, game);
            var state = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
                Path.Combine(root, "DeploymentState"));
            return new Fixture
            {
                Root = root, Store = store, Repository = repository, Profiles = profiles, Conflicts = conflicts,
                Locks = new ProfileLockService(NullLogger<ProfileLockService>.Instance, profiles, repository, conflicts),
                Views = views,
                Planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
                    repository, store, profiles, game, views, state),
                Deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
                    [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state, Path.Combine(root, "Backups"))
            };
        }

        public async Task AddConfigPackagesAsync()
        {
            await AddAsync("cfg-a", "[Settings]\nMode=A\nOnlyA=one\n");
            await AddAsync("cfg-b", "[Settings]\nMode=B\nOnlyB=two\n");
        }

        private async Task AddAsync(string key, string content)
        {
            var source = Path.Combine(Store.GetPackageFilesDirectory(key), "settings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, content);
            var hash = await ObjectStore.ComputeFileHashAsync(source);
            var length = new FileInfo(source).Length;
            var package = new Package
            {
                PackageKey = key, DisplayName = key, HostGameName = "Integration",
                Kind = PackageKind.Config, TargetRootPath = "Config", ContentHash = hash, TotalSize = length,
                Artifacts = [new PackageArtifact
                {
                    FileName = "settings.ini", ArtifactType = ArtifactType.ConfigFile,
                    RelativeSourcePath = $"{key}/files/settings.ini", RelativeTargetPath = "settings.ini",
                    FileSize = length, FileHash = hash
                }]
            };
            await Repository.RegisterPackageAsync(package);
            await Profiles.AddPackagesToCurrentProfileAsync([package]);
            await Profiles.SetPackageEnabledFlagAsync(key, true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
