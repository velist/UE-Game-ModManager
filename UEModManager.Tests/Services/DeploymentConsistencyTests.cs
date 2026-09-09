using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using System.Text.Json;

namespace UEModManager.Tests.Services;

public sealed class DeploymentConsistencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UEModManager.Tests", "deploy-" + Guid.NewGuid().ToString("N"));
    private ObjectStore _store = null!;
    private PackageRepository _repository = null!;
    private ProfileService _profiles = null!;
    private GameConfigService _game = null!;
    private OverwriteStore _overwrites = null!;
    private DeploymentPlanner _planner = null!;
    private DeploymentService _deployment = null!;
    private ResolvedViewBuilder _views = null!;
    private DeploymentStateStore _states = null!;

    private async Task InitializeAsync()
    {
        if (_deployment != null) await _deployment.PendingBackupCleanup;
        var data = Path.Combine(_root, "Data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(_root, "Game", "Mods"));
        _store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(_root, "Repository"));
        _repository = new PackageRepository(NullLogger<PackageRepository>.Instance, _store, data);
        _profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
        _game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(_root, "config.json"));
        _game.Config.GameName = "Test";
        _game.Config.GamePath = Path.Combine(_root, "Game");
        _game.Config.ModPath = Path.Combine(_root, "Game", "Mods");
        await _repository.SetCurrentGameAsync("Test");
        await _profiles.SetCurrentGameAsync("Test");
        _overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, _repository, null!, data, Path.Combine(_root, "Overwrites"));
        await _overwrites.SetCurrentGameAsync("Test");
        _views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, _repository, _profiles,
            new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), _overwrites, _game);
        _states = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance, Path.Combine(_root, "State"), Path.Combine(_root, "Backups"));
        _planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance, _repository, _store, _profiles, _game, _views, _states);
        _deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
            [new CopyBackend(NullLogger<CopyBackend>.Instance)], _overwrites, _states, Path.Combine(_root, "Backups"));
    }

    [Fact]
    public async Task SwitchingToEmptyProfile_RemovesPreviouslyDeployedFiles_PreservesUnmanagedSiblings()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("mod-a", "payload.pak", "AAAA");
        var target = Path.Combine(_game.CurrentModPath, package.PackageKey, "payload.pak");
        var deployed = await _deployment.ExecuteAsync(await _planner.CreatePlanAsync());
        Assert.Equal(DeploymentStatus.Committed, deployed.Status);
        Assert.True(File.Exists(target));
        var unmanaged = Path.Combine(Path.GetDirectoryName(target)!, "my-own.pak");
        await File.WriteAllTextAsync(unmanaged, "USER");

        var empty = await _profiles.CreateProfileAsync("Empty");
        await _profiles.SwitchProfileAsync(empty.Id);
        var plan = await _planner.CreatePlanAsync();
        Assert.Contains(plan.Operations, op => op.Type == DeploymentOperationType.Remove && op.TargetPath == target);
        var removed = await _deployment.ExecuteAsync(plan);
        Assert.Equal(DeploymentStatus.Committed, removed.Status);
        Assert.False(File.Exists(target));
        Assert.Equal("USER", await File.ReadAllTextAsync(unmanaged));

        Assert.True((await _deployment.RollbackAsync(removed)).Succeeded);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(target));
        Assert.Equal("USER", await File.ReadAllTextAsync(unmanaged));
        Assert.Contains((await _planner.CreatePlanAsync()).Operations, op => op.Type == DeploymentOperationType.Remove && op.TargetPath == target);
    }

    [Fact]
    public async Task SameSizeDifferentContent_IsReplacedByRepositoryContent()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("mod-a", "payload.pak", "AAAA");
        var target = Path.Combine(_game.CurrentModPath, package.PackageKey, "payload.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "BBBB");

        var plan = await _planner.CreatePlanAsync();
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(DeploymentOperationType.Replace, operation.Type);
        var transaction = await _deployment.ExecuteAsync(plan);
        Assert.Equal(DeploymentStatus.Committed, transaction.Status);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(target));
        Assert.Empty((await _planner.CreatePlanAsync()).Operations);
        Assert.True((await _deployment.RollbackAsync(transaction)).Succeeded);
        Assert.Equal("BBBB", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task TwoConfigPackages_ProduceMergedViewWithBothKeys()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("cfg-a", "settings.ini", "[Settings]\nA=1\n", PackageKind.Config, ArtifactType.ConfigFile);
        await AddEnabledPackageAsync("cfg-b", "settings.ini", "[Settings]\nB=2\n", PackageKind.Config, ArtifactType.ConfigFile);

        var view = await _views.BuildAsync();
        var merged = Assert.Single(view.ConfigMergeResults);
        Assert.True(merged.Success);
        Assert.Contains("A=1", merged.MergedContent);
        Assert.Contains("B=2", merged.MergedContent);
        var transaction = await _deployment.ExecuteAsync(await _planner.CreatePlanForViewAsync(view));
        Assert.Equal(DeploymentStatus.Committed, transaction.Status);
        var installed = await File.ReadAllTextAsync(Path.Combine(_game.CurrentGamePath, "Config", "settings.ini"));
        Assert.Contains("A=1", installed);
        Assert.Contains("B=2", installed);
        Assert.False(Directory.Exists(Path.Combine(_game.CurrentGamePath, "Config", "cfg-a")));
    }

    [Fact]
    public async Task ProfileAtoBtoA_AfterRestart_UsesPersistedExactOwnership()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var a = _profiles.CurrentProfile!;
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        var b = await _profiles.CreateProfileAsync("B");
        await _profiles.SwitchProfileAsync(b.Id);
        await AddEnabledPackageAsync("b", "b.pak", "BBBB");
        var targetA = Path.Combine(_game.CurrentModPath, "a", "a.pak");
        var targetB = Path.Combine(_game.CurrentModPath, "b", "b.pak");
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        Assert.False(File.Exists(targetA));
        Assert.True(File.Exists(targetB));

        await InitializeAsync(); // Re-read repository, profiles, and state from the same isolated on-disk installation.
        await _profiles.SwitchProfileAsync(a.Id);
        var plan = await _planner.CreatePlanAsync();
        Assert.Equal(1, plan.AddCount);
        Assert.Equal(1, plan.RemoveCount);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(plan)).Status);
        Assert.True(File.Exists(targetA));
        Assert.False(File.Exists(targetB));
        Assert.Equal(targetA, Assert.Single((await ReadStateAsync()).Files).TargetPath);
    }

    [Fact]
    public async Task UnmanagedFileMatchingPackagePathAndBytes_IsNeverClaimedBySimilarity()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var target = Path.Combine(_game.CurrentModPath, "a", "a.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "AAAA");
        await SwitchToEmptyAsync();
        var plan = await _planner.CreatePlanAsync();
        Assert.False(plan.HasChanges);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(target));
        Assert.Empty((await ReadStateAsync()).Files);
    }

    [Fact]
    public async Task OldCommittedTransaction_UpgradesOwnershipAndCanRemoveThenRollback()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var legacy = await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Add));
        Assert.Equal(DeploymentStatus.Committed, legacy.Status);
        Assert.Null(legacy.StateAfter);
        await InitializeAsync();
        await SwitchToEmptyAsync();
        var plan = await _planner.CreatePlanAsync();
        var target = Assert.Single(plan.Operations).TargetPath;
        Assert.Equal(DeploymentOperationType.Remove, plan.Operations[0].Type);
        var removed = await _deployment.ExecuteAsync(plan);
        Assert.Equal(DeploymentStatus.Committed, removed.Status);
        Assert.False(File.Exists(target));
        Assert.True((await _deployment.RollbackAsync(removed)).Succeeded);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(target));
        Assert.Equal(target, Assert.Single((await ReadStateAsync()).Files).TargetPath);
    }

    [Theory]
    [InlineData(DeploymentStatus.RolledBack)]
    [InlineData(DeploymentStatus.PartiallyRolledBack)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Dismissed)]
    public async Task UncertainOrRevertedLegacyTransaction_DoesNotClaimMatchingFile(DeploymentStatus status)
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var transaction = await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Add));
        transaction.Status = status;
        await SaveLegacyLogAsync(transaction);
        await SwitchToEmptyAsync();
        Assert.False((await _planner.CreatePlanAsync()).HasChanges);
        Assert.True(File.Exists(transaction.ExecutedOperations[0].TargetPath));
    }

    [Fact]
    public async Task LaterLegacyRemoval_CancelsEarlierProvenance_EvenIfUserRestoresIdenticalBytes()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var add = await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Add));
        var target = add.ExecutedOperations[0].TargetPath;
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Remove))).Status);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "AAAA");
        await SwitchToEmptyAsync();
        Assert.False((await _planner.CreatePlanAsync()).HasChanges);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.PartiallyRolledBack)]
    public async Task LaterUncertainTransaction_InvalidatesEarlierCommittedOwnership(DeploymentStatus status)
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Add))).Status);
        var later = await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Replace));
        later.Status = status;
        await SaveLegacyLogAsync(later);
        await SwitchToEmptyAsync();
        Assert.False((await _planner.CreatePlanAsync()).HasChanges);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(later.ExecutedOperations[0].TargetPath));
    }

    [Fact]
    public async Task LegacyReplace_PreservesOriginalBeforeAdoptingAndRestoresItOnDisable()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var target = Path.Combine(_game.CurrentModPath, "a", "a.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "USER");
        var legacy = await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Replace));
        Assert.Equal(DeploymentStatus.Committed, legacy.Status);
        await SwitchToEmptyAsync();
        var plan = await _planner.CreatePlanAsync();
        Assert.Equal(DeploymentOperationType.Replace, Assert.Single(plan.Operations).Type);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(plan)).Status);
        Assert.Equal("USER", await File.ReadAllTextAsync(target));
        Assert.Empty((await ReadStateAsync()).Files);
    }

    [Fact]
    public async Task CorruptHistory_PreventsClaimingAnOlderMatchingDeployment()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Add))).Status);
        await _deployment.PendingBackupCleanup;
        var invalid = Path.Combine(_root, "Backups", "broken");
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(Path.Combine(invalid, "transaction.json"), "{");
        await SwitchToEmptyAsync();
        Assert.False((await _planner.CreatePlanAsync()).HasChanges);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(Path.Combine(_game.CurrentModPath, "a", "a.pak")));
    }

    [Fact]
    public async Task LegacyProvenance_IsScopedToActualInstallationRoot()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var legacy = await _deployment.ExecuteAsync(LegacyPlan(package, DeploymentOperationType.Add));
        _game.Config.GamePath = Path.Combine(_root, "OtherGame");
        _game.Config.ModPath = Path.Combine(_root, "OtherGame", "Mods");
        var other = Path.Combine(_game.CurrentModPath, "a", "a.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        await File.WriteAllTextAsync(other, "AAAA");
        await SwitchToEmptyAsync();
        Assert.False((await _planner.CreatePlanAsync()).HasChanges);
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(legacy.ExecutedOperations[0].TargetPath));
    }

    [Fact]
    public async Task EmptyProfile_RestoresOriginalConfigEvenAfterTransactionBackupsAreCleaned()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("cfg", "settings.ini", "[Settings]\nMod=1\n", PackageKind.Config, ArtifactType.ConfigFile);
        var target = Path.Combine(_game.CurrentGamePath, "Config", "settings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        const string original = "[Settings]\nUser=keep\n";
        await File.WriteAllTextAsync(target, original);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        var owner = Assert.Single((await ReadStateAsync()).Files);
        Assert.NotNull(owner.OriginalFilePath);
        _deployment.CleanupOldBackups(keepCount: 0);
        Assert.True(File.Exists(owner.OriginalFilePath));
        await SwitchToEmptyAsync();
        var remove = await _deployment.ExecuteAsync(await _planner.CreatePlanAsync());
        Assert.Equal(DeploymentStatus.Committed, remove.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(target));
        Assert.Empty((await ReadStateAsync()).Files);
        Assert.True((await _deployment.RollbackAsync(remove)).Succeeded);
        Assert.Contains("Mod=1", await File.ReadAllTextAsync(target));
        Assert.Single((await ReadStateAsync()).Files);
    }

    [Fact]
    public async Task ToggleConfigContribution_RebuildsMergedOutputWithoutDisabledKeys()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "settings.ini", "[Settings]\nA=1\nShared=A\n", PackageKind.Config, ArtifactType.ConfigFile);
        await AddEnabledPackageAsync("b", "settings.ini", "[Settings]\nB=2\nShared=B\n", PackageKind.Config, ArtifactType.ConfigFile);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        var target = Path.Combine(_game.CurrentGamePath, "Config", "settings.ini");
        var original = await File.ReadAllTextAsync(target);
        var toggle = await _planner.CreateTogglePlanAsync("a", enable: false);
        Assert.True(_profiles.CurrentProfile!.Packages.Single(p => p.PackageKey == "a").IsEnabled);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(toggle)).Status);
        var installed = await File.ReadAllTextAsync(target);
        Assert.DoesNotContain("A=1", installed);
        Assert.Contains("B=2", installed);
        Assert.Contains("Shared=B", installed);
        await _profiles.SetPackageEnabledFlagAsync("a", false);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreateTogglePlanAsync("a", true))).Status);
        Assert.Equal(original, await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task UserFix_IsInstalledAboveMerge_AndIsScopedToItsProfile()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "settings.ini", "[Settings]\nA=1\n", PackageKind.Config, ArtifactType.ConfigFile);
        await AddEnabledPackageAsync("b", "settings.ini", "[Settings]\nB=2\n", PackageKind.Config, ArtifactType.ConfigFile);
        var fix = Path.Combine(_root, "fix.ini");
        await File.WriteAllTextAsync(fix, "[Settings]\nUserFix=1\n");
        await _overwrites.RegisterAsync(fix, GeneratedArtifactType.UserFix, "Fix",
            sourceProfileId: _profiles.CurrentProfile!.Id, relativeTargetPath: "Config/settings.ini");
        var view = await _views.BuildAsync();
        Assert.Equal(ResolvedEntrySource.UserOverride, Assert.Single(view.Entries).Source);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        var target = Path.Combine(_game.CurrentGamePath, "Config", "settings.ini");
        Assert.Equal(await File.ReadAllTextAsync(fix), await File.ReadAllTextAsync(target));
        await SwitchToEmptyAsync();
        Assert.Empty((await _views.BuildAsync()).Entries);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task SameNamedMods_RetainDistinctPhysicalFiles()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "shared.pak", "AAAA");
        await AddEnabledPackageAsync("b", "shared.pak", "BBBB");
        var view = await _views.BuildAsync();
        Assert.Equal(2, view.Entries.Count);
        Assert.Single(view.Conflicts);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(Path.Combine(_game.CurrentModPath, "a", "shared.pak")));
        Assert.Equal("BBBB", await File.ReadAllTextAsync(Path.Combine(_game.CurrentModPath, "b", "shared.pak")));
    }

    [Fact]
    public async Task ConfigsInDifferentTargetRoots_AreNeverMergedTogether()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "settings.ini", "[Settings]\nA=1\n", PackageKind.Config, ArtifactType.ConfigFile);
        await AddEnabledPackageAsync("b", "settings.ini", "[Settings]\nB=2\n", PackageKind.Config, ArtifactType.ConfigFile);
        _profiles.CurrentProfile!.Packages.Single(p => p.PackageKey == "b").TargetRootPath = "OtherConfig";
        var view = await _views.BuildAsync();
        Assert.Empty(view.ConfigMergeResults);
        Assert.Equal(2, view.Entries.Count);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanForViewAsync(view))).Status);
        Assert.Contains("A=1", await File.ReadAllTextAsync(Path.Combine(_game.CurrentGamePath, "Config", "settings.ini")));
        Assert.Contains("B=2", await File.ReadAllTextAsync(Path.Combine(_game.CurrentGamePath, "OtherConfig", "settings.ini")));
    }

    [Theory]
    [InlineData("settings.ini")]
    [InlineData("settings.unknown")]
    public async Task EqualConfigPriorities_UseTheSameWinnerForAnalysisAndMergedBytes(string fileName)
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("z-first", fileName, "[Settings]\nShared=FIRST\n", PackageKind.Config, ArtifactType.ConfigFile);
        await AddEnabledPackageAsync("a-second", fileName, "[Settings]\nShared=SECOND\n", PackageKind.Config, ArtifactType.ConfigFile);
        foreach (var entry in _profiles.CurrentProfile!.Packages) entry.Priority = 0;
        var view = await _views.BuildAsync();
        Assert.Equal("z-first", Assert.Single(view.Conflicts, c => c.Type == ConflictType.LoadOrder).WinnerPackageKey);
        Assert.Equal("z-first", Assert.Single(view.Entries).PackageKey);
        if (fileName.EndsWith(".ini"))
            Assert.Contains("Shared=FIRST", Assert.Single(view.ConfigMergeResults).MergedContent);
        else
            Assert.Empty(view.ConfigMergeResults);
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanForViewAsync(view))).Status);
        Assert.Contains("Shared=FIRST", await File.ReadAllTextAsync(Path.Combine(_game.CurrentGamePath, "Config", fileName)));
    }

    [Fact]
    public async Task MissingSource_AbortsWholePlanInsteadOfRemovingItsInstalledFile()
    {
        await InitializeAsync();
        var package = await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        File.Delete(Path.Combine(_store.RepositoryRoot, package.Artifacts[0].RelativeSourcePath));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _planner.CreatePlanAsync());
        Assert.Equal("AAAA", await File.ReadAllTextAsync(Path.Combine(_game.CurrentModPath, "a", "a.pak")));
    }

    [Fact]
    public async Task ExternallyEditedOwnedFile_IsPreservedAndStopsRemoval()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        var target = Path.Combine(_game.CurrentModPath, "a", "a.pak");
        await File.WriteAllTextAsync(target, "USER");
        await SwitchToEmptyAsync();
        await Assert.ThrowsAsync<IOException>(() => _planner.CreatePlanAsync());
        Assert.Equal("USER", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileChangedAfterPlan_StopsBeforeWriting(bool changeSource)
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var target = Path.Combine(_game.CurrentModPath, "a", "a.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "BBBB");
        var plan = await _planner.CreatePlanAsync();
        await File.WriteAllTextAsync(changeSource ? plan.Operations[0].SourcePath! : target, "USER");
        var transaction = await _deployment.ExecuteAsync(plan);
        Assert.NotEqual(DeploymentStatus.Committed, transaction.Status);
        Assert.Equal(changeSource ? "BBBB" : "USER", await File.ReadAllTextAsync(target));
        Assert.Empty((await ReadStateAsync()).Files);
    }

    [Fact]
    public async Task StalePlan_IsRejectedAfterAnotherTransactionCommits()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var stale = await _planner.CreatePlanAsync();
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _deployment.ExecuteAsync(stale));
        Assert.Equal("AAAA", await File.ReadAllTextAsync(Path.Combine(_game.CurrentModPath, "a", "a.pak")));
    }

    [Fact]
    public async Task CorruptOwnershipManifest_StopsPlanAndPreservesTarget()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(_root, "State"), "state.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(manifest, "{");
        await SwitchToEmptyAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => _planner.CreatePlanAsync());
        Assert.Equal("AAAA", await File.ReadAllTextAsync(Path.Combine(_game.CurrentModPath, "a", "a.pak")));
    }

    [Fact]
    public async Task StateStore_PreservesDriveRootRatherThanDriveRelativePath()
    {
        await InitializeAsync();
        var drive = Path.GetPathRoot(Path.GetFullPath(_root))!;
        var state = await _states.ReadAsync("DriveRoot", drive, drive);
        Assert.Equal(drive, state.GameRootPath);
        Assert.Equal(drive, state.ModRootPath);
        Assert.True(Path.IsPathFullyQualified(state.GameRootPath));
    }

    [Fact]
    public async Task BackendFailureAfterFileWrite_RollsBackBytesAndOwnershipTogether()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var target = Path.Combine(_game.CurrentModPath, "a", "a.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "ORIGINAL");
        var service = new DeploymentService(NullLogger<DeploymentService>.Instance, [new WriteThenFailBackend()],
            _overwrites, _states, Path.Combine(_root, "Backups"));
        var transaction = await service.ExecuteAsync(await _planner.CreatePlanAsync());
        Assert.Equal(DeploymentStatus.RolledBack, transaction.Status);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(target));
        Assert.Empty((await ReadStateAsync()).Files);
    }

    [Fact]
    public async Task CrashRecoveryFromPlannedOperations_RestoresOwnershipAndFileBytes()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        var transaction = await _deployment.ExecuteAsync(await _planner.CreatePlanAsync());
        // Simulate a crash after files/state were written, before the commit log was recorded.
        transaction.Status = DeploymentStatus.InProgress;
        transaction.ExecutedOperations.Clear();
        await SaveLegacyLogAsync(transaction);
        var recovered = JsonSerializer.Deserialize<DeploymentTransaction>(
            await File.ReadAllTextAsync(Path.Combine(transaction.BackupDirectory, "transaction.json")))!;
        Assert.True((await _deployment.RollbackAsync(recovered)).Succeeded);
        Assert.False(File.Exists(Path.Combine(_game.CurrentModPath, "a", "a.pak")));
        Assert.Empty((await ReadStateAsync()).Files);
    }

    [Fact]
    public async Task MissingRollbackBackup_DoesNotFalselyRestoreOwnership()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "a.pak", "AAAA");
        Assert.Equal(DeploymentStatus.Committed, (await _deployment.ExecuteAsync(await _planner.CreatePlanAsync())).Status);
        await SwitchToEmptyAsync();
        var removal = await _deployment.ExecuteAsync(await _planner.CreatePlanAsync());
        Assert.Equal(DeploymentStatus.Committed, removal.Status);
        File.Delete(Assert.Single(removal.ExecutedOperations).BackupPath!);
        var result = await _deployment.RollbackAsync(removal);
        Assert.False(result.Succeeded);
        Assert.Equal(DeploymentStatus.PartiallyRolledBack, removal.Status);
        Assert.Empty((await ReadStateAsync()).Files);
        Assert.False(File.Exists(Path.Combine(_game.CurrentModPath, "a", "a.pak")));
    }

    [Fact]
    public async Task LaunchDeployment_ConsumesTheBuiltMergedView_BeforeProcessStage()
    {
        await InitializeAsync();
        await AddEnabledPackageAsync("a", "settings.ini", "[Settings]\nA=1\n", PackageKind.Config, ArtifactType.ConfigFile);
        await AddEnabledPackageAsync("b", "settings.ini", "[Settings]\nB=2\n", PackageKind.Config, ArtifactType.ConfigFile);
        var executable = Path.Combine(_root, "test.exe");
        await File.WriteAllBytesAsync(executable, []);
        // A missing conflict analyzer deliberately terminates after deployment, before any process can start.
        var launch = new LaunchOrchestrator(NullLogger<LaunchOrchestrator>.Instance, _game, _profiles, _views,
            _planner, _deployment, null!, Path.Combine(_root, "Sessions"));
        var session = await launch.LaunchAsync(new LaunchContext
        {
            GameName = "Test", GameRootPath = _game.CurrentGamePath, ExecutablePath = executable,
            WorkingDirectory = _game.CurrentGamePath
        });
        Assert.Equal(LaunchStepStatus.Passed, Assert.Single(session.Steps, s => s.Type == LaunchStepType.Deploy).Status);
        Assert.Equal(LaunchStepStatus.Pending, Assert.Single(session.Steps, s => s.Type == LaunchStepType.LaunchProcess).Status);
        var installed = await File.ReadAllTextAsync(Path.Combine(_game.CurrentGamePath, "Config", "settings.ini"));
        Assert.Contains("A=1", installed);
        Assert.Contains("B=2", installed);
        Assert.Equal((await _views.BuildAsync()).ViewHash, session.ViewHash);
    }

    private async Task SwitchToEmptyAsync()
    {
        var empty = await _profiles.CreateProfileAsync("Empty");
        await _profiles.SwitchProfileAsync(empty.Id);
    }

    private Task<DeploymentState> ReadStateAsync() => _states.ReadAsync("Test", _game.CurrentGamePath, _game.CurrentModPath);

    private async Task SaveLegacyLogAsync(DeploymentTransaction transaction)
    {
        await _deployment.PendingBackupCleanup;
        await File.WriteAllTextAsync(Path.Combine(transaction.BackupDirectory, "transaction.json"), JsonSerializer.Serialize(transaction));
    }

    private DeploymentPlan LegacyPlan(Package package, DeploymentOperationType type) => new()
    {
        HostGameName = "Test", ProfileId = _profiles.CurrentProfile!.Id, BackendType = DeploymentBackendType.Copy,
        Operations = [new DeploymentOperation
        {
            Type = type, PackageKey = package.PackageKey, PackageDisplayName = package.DisplayName,
            PackageKind = package.Kind, FileHash = package.Artifacts[0].FileHash, FileSize = package.Artifacts[0].FileSize,
            SourcePath = type == DeploymentOperationType.Remove ? null : Path.Combine(_store.RepositoryRoot, package.Artifacts[0].RelativeSourcePath),
            TargetPath = Path.Combine(_game.CurrentModPath, package.PackageKey, package.Artifacts[0].RelativeTargetPath),
            RelativeTargetPath = Path.Combine(package.PackageKey, package.Artifacts[0].RelativeTargetPath)
        }]
    };

    private sealed class WriteThenFailBackend : IDeploymentBackend
    {
        public DeploymentBackendType Type => DeploymentBackendType.Copy;
        public string DisplayName => "Test fault";
        public Task<bool> CanUseAsync() => Task.FromResult(true);
        public async Task DeployFileAsync(string sourcePath, string targetPath)
        {
            await new CopyBackend(NullLogger<CopyBackend>.Instance).DeployFileAsync(sourcePath, targetPath);
            throw new IOException("Test fault after a real write");
        }
        public Task RemoveFileAsync(string targetPath) => throw new IOException("Test fault");
    }

    private async Task<Package> AddEnabledPackageAsync(string key, string fileName, string content,
        PackageKind kind = PackageKind.Mod, ArtifactType artifactType = ArtifactType.ModFile)
    {
        var source = Path.Combine(_store.GetPackageFilesDirectory(key), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, content);
        var hash = await ObjectStore.ComputeFileHashAsync(source);
        var size = new FileInfo(source).Length;
        var package = new Package
        {
            PackageKey = key, DisplayName = key, HostGameName = "Test", Kind = kind,
            TargetRootPath = kind == PackageKind.Mod ? null : "Config", ContentHash = hash, TotalSize = size,
            Artifacts = [new PackageArtifact
            {
                FileName = fileName, ArtifactType = artifactType, RelativeSourcePath = $"{key}/files/{fileName}",
                RelativeTargetPath = fileName, FileHash = hash, FileSize = size
            }]
        };
        await _repository.RegisterPackageAsync(package);
        await _profiles.AddPackagesToCurrentProfileAsync([package]);
        await _profiles.SetPackageEnabledFlagAsync(key, true);
        return package;
    }

    public void Dispose()
    {
        _deployment?.PendingBackupCleanup.GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
