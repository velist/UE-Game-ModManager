using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class ProfileLockOverrideTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ProfileLockOverrides", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportThenExport_PreservesConflictOverrideInActiveProfile()
    {
        var (profiles, repository) = await CreateAsync();
        var service = new ProfileLockService(NullLogger<ProfileLockService>.Instance, profiles, repository);
        var lockFile = new ProfileLock
        {
            Host = new ProfileLockHost { GameName = "Game" },
            Profile = new ProfileLockProfile { Name = "Shared" },
            ConflictOverrides = new Dictionary<string, string> { ["Engine/Config/Game.ini"] = "winner" }
        };

        var imported = await service.ApplyImportAsync(lockFile);
        Assert.Equal(imported.Id, profiles.CurrentProfile!.Id);
        var outputPath = Path.Combine(_root, "roundtrip.lock.json");
        await service.ExportAsync(outputPath);
        var exported = JsonSerializer.Deserialize<ProfileLock>(await File.ReadAllTextAsync(outputPath))!;

        Assert.Equal("winner", Assert.Single(exported.ConflictOverrides).Value);
    }

    [Fact]
    public async Task CloneAndSwitch_KeepIndependentRulesAfterRestart()
    {
        var (profiles, _) = await CreateAsync();
        var source = profiles.CurrentProfile!;
        await profiles.SetConflictOverrideAsync(source.Id, "@game/Config/settings.ini", "first");
        var clone = await profiles.CloneProfileAsync(source.Id, "Clone");
        Assert.NotSame(source.ConflictOverrides, clone.ConflictOverrides);
        Assert.Equal("first", clone.ConflictOverrides["@game/Config/settings.ini"]);
        await profiles.SetConflictOverrideAsync(clone.Id, "@game/config/SETTINGS.ini", "second");
        await profiles.SwitchProfileAsync(clone.Id);
        Assert.Equal("first", source.ConflictOverrides["@game/Config/settings.ini"]);

        var restarted = NewProfileService();
        await restarted.SetCurrentGameAsync("Game");
        Assert.Equal(clone.Id, restarted.CurrentProfile!.Id);
        Assert.Equal("second", Assert.Single(restarted.CurrentProfile.ConflictOverrides).Value);
        await restarted.SwitchProfileAsync(source.Id);
        Assert.Equal("first", Assert.Single(restarted.CurrentProfile.ConflictOverrides).Value);
    }

    [Fact]
    public async Task LegacyGlobalRules_AreCopiedOnceToExistingProfilesAndNeverResurrect()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await WriteLegacyProfilesAsync(first, second);
        var legacyPath = Path.Combine(_root, "data", "Game_conflict_overrides.json");
        var legacyJson = "{\"C:/Game/Config/settings.ini\":\"legacy\"}";
        await File.WriteAllTextAsync(legacyPath, legacyJson);
        var profiles = NewProfileService();
        await profiles.SetCurrentGameAsync("Game");

        Assert.All(profiles.GetProfiles(), p => Assert.Equal("legacy", Assert.Single(p.ConflictOverrides).Value));
        Assert.NotSame(profiles.FindProfile(first)!.ConflictOverrides, profiles.FindProfile(second)!.ConflictOverrides);
        await profiles.ReplaceConflictOverridesAsync(first, new Dictionary<string, string>());
        var empty = await profiles.CreateProfileAsync("Empty");
        Assert.Empty(empty.ConflictOverrides);

        var restarted = NewProfileService();
        await restarted.SetCurrentGameAsync("Game");
        Assert.Empty(restarted.FindProfile(first)!.ConflictOverrides);
        Assert.Empty(restarted.FindProfile(empty.Id)!.ConflictOverrides);
        Assert.Equal("legacy", Assert.Single(restarted.FindProfile(second)!.ConflictOverrides).Value);
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(legacyPath));
    }

    [Fact]
    public async Task LegacyRulesWithoutProfiles_ArePreservedByDefaultProfileCreation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        await File.WriteAllTextAsync(Path.Combine(_root, "data", "Game_conflict_overrides.json"),
            "{\"C:/Game/file.pak\":\"legacy\"}");
        var profiles = NewProfileService();
        await profiles.SetCurrentGameAsync("Game");
        Assert.Equal("legacy", Assert.Single(profiles.CurrentProfile!.ConflictOverrides).Value);
    }

    [Fact]
    public async Task LegacyMigration_PersistFailureLeavesOldFilesAndRetriesSuccessfully()
    {
        var id = Guid.NewGuid();
        var original = await WriteLegacyProfilesAsync(id);
        var profilePath = Path.Combine(_root, "data", "Game_profiles.json");
        var legacyPath = Path.Combine(_root, "data", "Game_conflict_overrides.json");
        await File.WriteAllTextAsync(legacyPath, "{\"C:/Game/file.pak\":\"legacy\"}");
        var profiles = NewProfileService();
        using (var held = File.Open(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => profiles.SetCurrentGameAsync("Game"));
            Assert.Empty(profiles.FindProfile(id)!.ConflictOverrides);
            Assert.Equal(original, await File.ReadAllTextAsync(profilePath));
            Assert.True(File.Exists(legacyPath));
        }

        await profiles.SetCurrentGameAsync("Game");
        Assert.Equal("legacy", Assert.Single(profiles.CurrentProfile!.ConflictOverrides).Value);
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        Assert.True(persisted.RootElement[0].TryGetProperty("conflictOverrides", out _));
    }

    [Fact]
    public async Task LegacyMigration_CorruptLegacyRulesDoNotOverwriteHealthyProfiles()
    {
        var original = await WriteLegacyProfilesAsync(Guid.NewGuid());
        await File.WriteAllTextAsync(Path.Combine(_root, "data", "Game_conflict_overrides.json"), "invalid json");
        var profiles = NewProfileService();

        await Assert.ThrowsAsync<JsonException>(() => profiles.SetCurrentGameAsync("Game"));

        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_root, "data", "Game_profiles.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data"), "*.corrupt-*"));
    }

    [Fact]
    public async Task OverrideWriteFailure_RestoresPreviousRuleAndPropagatesError()
    {
        var (profiles, _) = await CreateAsync();
        var profile = profiles.CurrentProfile!;
        await profiles.SetConflictOverrideAsync(profile.Id, "@game/file.ini", "original");
        var profilePath = Path.Combine(_root, "data", "Game_profiles.json");
        using var held = File.Open(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            profiles.SetConflictOverrideAsync(profile.Id, "@game/file.ini", "replacement"));

        Assert.Equal("original", Assert.Single(profile.ConflictOverrides).Value);
    }

    [Fact]
    public async Task LegacyAbsoluteLock_RebasesUniqueWinnerArtifactToLocalRoot()
    {
        var (profiles, repository) = await CreateAsync();
        await repository.RegisterPackageAsync(new Package
        {
            PackageKey = "cfg", DisplayName = "Config", HostGameName = "Game", Kind = PackageKind.Config,
            TargetRootPath = "Engine/Config",
            Artifacts = [new PackageArtifact { RelativeTargetPath = "settings.ini", FileName = "settings.ini" }]
        });
        var service = new ProfileLockService(NullLogger<ProfileLockService>.Instance, profiles, repository);
        var imported = await service.ApplyImportAsync(new ProfileLock
        {
            Host = new ProfileLockHost { GameName = "Game" },
            Packages = [new ProfileLockPackage { PackageKey = "cfg", Kind = "Config", IsEnabled = true }],
            ConflictOverrides = new() { ["Z:/previous-install/Engine/Config/settings.ini"] = "cfg" }
        });

        Assert.Equal("cfg", imported.ConflictOverrides["@game/Engine/Config/settings.ini"]);
    }

    [Fact]
    public async Task InvalidPortableLockPath_IsRejectedBeforeCreatingProfile()
    {
        var (profiles, repository) = await CreateAsync();
        var service = new ProfileLockService(NullLogger<ProfileLockService>.Instance, profiles, repository);
        var count = profiles.GetProfiles().Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyImportAsync(new ProfileLock
        {
            Host = new ProfileLockHost { GameName = "Game" },
            ConflictOverrides = new() { ["@game/../../outside.ini"] = "cfg" }
        }));

        Assert.Equal(count, profiles.GetProfiles().Count);
    }

    [Fact]
    public async Task ConcurrentConflictEdits_AreAllPersistedAndSnapshotsAreDetached()
    {
        var (profiles, repository) = await CreateAsync();
        var config = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(_root, "config.json"));
        config.Config.GamePath = Path.Combine(_root, "game");
        config.Config.ModPath = Path.Combine(_root, "game", "mods");
        var analyzer = new ConflictAnalyzer(NullLogger<ConflictAnalyzer>.Instance, repository, profiles, config);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => analyzer.SetOverrideAsync(
            Path.Combine(config.CurrentGamePath, "Config", $"{i}.ini"), "cfg")));

        Assert.Equal(20, analyzer.GetOverrides().Count);
        var snapshot = Assert.IsType<Dictionary<string, string>>(analyzer.GetOverrides());
        snapshot.Clear();
        Assert.Equal(20, analyzer.GetOverrides().Count);
        var restarted = NewProfileService();
        await restarted.SetCurrentGameAsync("Game");
        Assert.Equal(20, restarted.CurrentProfile!.ConflictOverrides.Count);
    }

    private ProfileService NewProfileService()
        => new(NullLogger<ProfileService>.Instance, Path.Combine(_root, "data"));

    private async Task<string> WriteLegacyProfilesAsync(params Guid[] ids)
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        var json = JsonSerializer.Serialize(ids.Select((id, i) => new
        {
            id, hostGameName = "Game", name = $"Legacy {i}", isActive = i == 0,
            packages = Array.Empty<ProfilePackageEntry>()
        }));
        await File.WriteAllTextAsync(Path.Combine(_root, "data", "Game_profiles.json"), json);
        return json;
    }

    private async Task<(ProfileService Profiles, PackageRepository Repository)> CreateAsync()
    {
        var data = Path.Combine(_root, "data");
        var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(_root, "repository"));
        var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
        await repository.SetCurrentGameAsync("Game");
        var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
        await profiles.SetCurrentGameAsync("Game");
        return (profiles, repository);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
