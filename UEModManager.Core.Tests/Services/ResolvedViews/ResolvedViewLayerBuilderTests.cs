using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.ResolvedViews;

namespace UEModManager.Core.Tests.Services.ResolvedViews;

public class ResolvedViewLayerBuilderTests
{
    private sealed class FakeObjectStore : IObjectStoreQuery
    {
        public string RepositoryRoot => "/repo";
        public string GetPackageDirectory(string key) => $"/repo/{key}";
        public string GetPackageFilesDirectory(string key) => $"/repo/{key}/files";
        public string GetManifestPath(string key) => $"/repo/{key}/manifest.json";
        public string? GetPreviewImagePath(string key) => null;
        public List<string> GetPackageFiles(string key) => [];
        public bool PackageExists(string key) => true;
        public List<string> GetAllPackageKeys() => [];
        public long GetTotalSize() => 0;
    }

    private static readonly IObjectStoreQuery Store = new FakeObjectStore();

    private static Package MakeMod(string key, params (string rel, string? hash, ArtifactType type)[] artifacts)
        => new()
        {
            PackageKey = key,
            DisplayName = key.ToUpperInvariant(),
            Kind = PackageKind.Mod,
            HostGameName = "demo",
            Artifacts = artifacts.Select(a => new PackageArtifact
            {
                RelativeSourcePath = a.rel,
                RelativeTargetPath = a.rel,
                ArtifactType = a.type,
                FileHash = a.hash,
                FileSize = 100,
            }).ToList(),
        };

    private static InstanceProfile MakeProfile(params (string key, int prio, bool enabled)[] entries)
        => new()
        {
            Name = "Test", HostGameName = "demo",
            Packages = entries.Select(e => new ProfilePackageEntry
            {
                PackageKey = e.key, Priority = e.prio, IsEnabled = e.enabled,
            }).ToList(),
        };

    [Fact]
    public void BuildPackageLayer_NoEnabledPackages_ReturnsEmpty()
    {
        var profile = MakeProfile(("a", 0, false));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", ("file.pak", "h", ArtifactType.ModFile))
        };

        var (entries, conflicts) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        Assert.Empty(entries);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void BuildPackageLayer_NoOverlap_AllEntriesAreSinglePackage()
    {
        var profile = MakeProfile(("a", 0, true), ("b", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", ("a.pak", "ha", ArtifactType.ModFile)),
            ["b"] = MakeMod("b", ("b.pak", "hb", ArtifactType.ModFile)),
        };

        var (entries, conflicts) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        Assert.Equal(2, entries.Count);
        Assert.Empty(conflicts);
        Assert.All(entries, e => Assert.False(e.IsConflictWinner));
    }

    [Fact]
    public void BuildPackageLayer_TwoPackagesSamePath_HighPriorityWins()
    {
        var profile = MakeProfile(("low", 10, true), ("high", 0, true));
        var packages = new Dictionary<string, Package>
        {
            ["low"]  = MakeMod("low",  ("shared.pak", "h-low",  ArtifactType.ModFile)),
            ["high"] = MakeMod("high", ("shared.pak", "h-high", ArtifactType.ModFile)),
        };

        var (entries, conflicts) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        var winnerEntry = Assert.Single(entries);
        Assert.True(winnerEntry.IsConflictWinner);
        Assert.Equal("high", winnerEntry.PackageKey);
        Assert.Contains("low", winnerEntry.OverriddenPackageKeys);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("high", conflict.WinnerPackageKey);
        Assert.Equal(ConflictType.LoadOrder, conflict.Type);
        Assert.Single(conflict.Losers);
    }

    [Fact]
    public void BuildPackageLayer_PreviewImage_IsSkipped()
    {
        var profile = MakeProfile(("a", 0, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a",
                ("p.png", "hp", ArtifactType.PreviewImage),
                ("real.pak", "hr", ArtifactType.ModFile))
        };

        var (entries, _) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        var entry = Assert.Single(entries);
        Assert.Equal("real.pak", entry.TargetRelativePath);
    }

    [Fact]
    public void BuildPackageLayer_SourceAbsolutePath_UsesObjectStoreFilesDir()
    {
        var profile = MakeProfile(("foo", 0, true));
        var packages = new Dictionary<string, Package>
        {
            ["foo"] = MakeMod("foo", ("sub/file.pak", "h", ArtifactType.ModFile))
        };

        var (entries, _) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        var entry = Assert.Single(entries);
        Assert.Contains("/repo/foo/files", entry.SourceAbsolutePath.Replace('\\', '/'));
        Assert.EndsWith("file.pak", entry.SourceAbsolutePath.Replace('\\', '/'));
    }

    [Fact]
    public void BuildPackageLayer_MissingPackageInDict_IsSkipped()
    {
        var profile = MakeProfile(("ghost", 0, true), ("a", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", ("file.pak", "h", ArtifactType.ModFile)),
        };

        var (entries, conflicts) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        Assert.Single(entries);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void BuildPackageLayer_ConflictRecord_HasProfileContext()
    {
        var profile = MakeProfile(("a", 0, true), ("b", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", ("p.pak", "h1", ArtifactType.ModFile)),
            ["b"] = MakeMod("b", ("p.pak", "h2", ArtifactType.ModFile)),
        };

        var (_, conflicts) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        var c = Assert.Single(conflicts);
        Assert.Equal(profile.Id, c.ProfileId);
        Assert.Equal("demo", c.HostGameName);
    }

    [Fact]
    public void BuildPackageLayer_ThreeWayConflict_OnlyOneEntryPerPath()
    {
        var profile = MakeProfile(("a", 5, true), ("b", 0, true), ("c", 10, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", ("shared.pak", "ha", ArtifactType.ModFile)),
            ["b"] = MakeMod("b", ("shared.pak", "hb", ArtifactType.ModFile)),
            ["c"] = MakeMod("c", ("shared.pak", "hc", ArtifactType.ModFile)),
        };

        var (entries, conflicts) = ResolvedViewLayerBuilder.BuildPackageLayer(profile, packages, Store);

        Assert.Single(entries);
        Assert.Equal("b", entries[0].PackageKey);  // priority 0 胜
        Assert.Equal(2, entries[0].OverriddenPackageKeys.Count);

        var c = Assert.Single(conflicts);
        Assert.Equal(2, c.Losers.Count);
    }

    [Fact]
    public void BuildPackageLayer_NullProfile_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResolvedViewLayerBuilder.BuildPackageLayer(null!, new Dictionary<string, Package>(), Store));
    }

    [Fact]
    public void BuildPackageLayer_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResolvedViewLayerBuilder.BuildPackageLayer(MakeProfile(), new Dictionary<string, Package>(), null!));
    }

    // ─── BuildConfigMergePlans ───

    private static ResolvedEntry MakeConfigEntry(string targetPath, string pkg, int priority)
        => new()
        {
            TargetRelativePath = targetPath,
            SourceAbsolutePath = $"/repo/{pkg}/files/{targetPath}",
            Source = ResolvedEntrySource.Package,
            PackageKey = pkg,
            PackageDisplayName = pkg.ToUpperInvariant(),
            Priority = priority,
            ArtifactType = ArtifactType.ConfigFile,
        };

    [Fact]
    public void BuildConfigMergePlans_Empty_ReturnsEmpty()
    {
        var plans = ResolvedViewLayerBuilder.BuildConfigMergePlans(Array.Empty<ResolvedEntry>());
        Assert.Empty(plans);
    }

    [Fact]
    public void BuildConfigMergePlans_SingleEntry_NotIncluded()
    {
        var entries = new[] { MakeConfigEntry("Engine.ini", "a", 0) };
        var plans = ResolvedViewLayerBuilder.BuildConfigMergePlans(entries);
        Assert.Empty(plans);
    }

    [Fact]
    public void BuildConfigMergePlans_TwoEntriesSamePath_OnePlan()
    {
        var entries = new[]
        {
            MakeConfigEntry("Engine.ini", "high", 0),
            MakeConfigEntry("Engine.ini", "low", 10),
        };

        var plans = ResolvedViewLayerBuilder.BuildConfigMergePlans(entries);

        var plan = Assert.Single(plans);
        Assert.Equal("Engine.ini", plan.TargetRelativePath);
        Assert.Equal(ConfigFormat.Ini, plan.Format);
        Assert.Equal(ConfigMergeStrategy.MergeByKey, plan.Strategy);
        Assert.Equal(2, plan.Sources.Count);
        Assert.Equal("high", plan.Sources[0].PackageKey); // priority asc
        Assert.Equal(0, plan.Sources[0].Priority);
        Assert.Equal("low", plan.Sources[1].PackageKey);
    }

    [Fact]
    public void BuildConfigMergePlans_NonConfigArtifactType_Excluded()
    {
        var entries = new[]
        {
            MakeConfigEntry("Engine.ini", "a", 0),
            MakeConfigEntry("Engine.ini", "b", 1),
            new ResolvedEntry
            {
                TargetRelativePath = "Mod.pak",
                Source = ResolvedEntrySource.Package,
                PackageKey = "a",
                ArtifactType = ArtifactType.ModFile,
            },
            new ResolvedEntry
            {
                TargetRelativePath = "Mod.pak",
                Source = ResolvedEntrySource.Package,
                PackageKey = "b",
                ArtifactType = ArtifactType.ModFile,
            },
        };

        var plans = ResolvedViewLayerBuilder.BuildConfigMergePlans(entries);

        var plan = Assert.Single(plans);
        Assert.Equal("Engine.ini", plan.TargetRelativePath);
    }

    [Fact]
    public void BuildConfigMergePlans_UnknownExtension_Skipped()
    {
        var entries = new[]
        {
            MakeConfigEntry("data.xyz", "a", 0),
            MakeConfigEntry("data.xyz", "b", 1),
        };

        var plans = ResolvedViewLayerBuilder.BuildConfigMergePlans(entries);

        Assert.Empty(plans);
    }

    [Fact]
    public void BuildConfigMergePlans_MultiplePaths_SeparatePlans()
    {
        var entries = new[]
        {
            MakeConfigEntry("Engine.ini", "a", 0),
            MakeConfigEntry("Engine.ini", "b", 1),
            MakeConfigEntry("Game.json", "a", 0),
            MakeConfigEntry("Game.json", "b", 1),
        };

        var plans = ResolvedViewLayerBuilder.BuildConfigMergePlans(entries);

        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, p => p.TargetRelativePath == "Engine.ini" && p.Format == ConfigFormat.Ini);
        Assert.Contains(plans, p => p.TargetRelativePath == "Game.json" && p.Format == ConfigFormat.Json);
    }

    [Fact]
    public void BuildConfigMergePlans_PathCaseInsensitive()
    {
        var entries = new[]
        {
            MakeConfigEntry("Engine.ini", "a", 0),
            MakeConfigEntry("ENGINE.INI", "b", 1),
        };

        var plans = ResolvedViewLayerBuilder.BuildConfigMergePlans(entries);

        var plan = Assert.Single(plans);
        Assert.Equal(2, plan.Sources.Count);
    }

    [Fact]
    public void BuildConfigMergePlans_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResolvedViewLayerBuilder.BuildConfigMergePlans(null!));
    }

    // ─── TranslateConfigKeyConflicts ───

    [Fact]
    public void TranslateConfigKeyConflicts_Empty_ReturnsEmpty()
    {
        var profileId = Guid.NewGuid();
        var records = ResolvedViewLayerBuilder.TranslateConfigKeyConflicts(
            "Engine.ini", Array.Empty<ConfigKeyConflict>(), "demo", profileId);

        Assert.Empty(records);
    }

    [Fact]
    public void TranslateConfigKeyConflicts_OneConflict_OneRecordWithEmbeddedKey()
    {
        var profileId = Guid.NewGuid();
        var conflicts = new[]
        {
            new ConfigKeyConflict
            {
                Section = "Audio",
                Key = "Volume",
                WinnerPackageKey = "winner",
                WinnerValue = "100",
                Losers =
                {
                    new ConfigKeyConflictLoser
                    {
                        PackageKey = "loser",
                        DisplayName = "LOSER",
                        Value = "50",
                        Priority = 5,
                    },
                },
            },
        };

        var records = ResolvedViewLayerBuilder.TranslateConfigKeyConflicts(
            "Engine.ini", conflicts, "demo", profileId);

        var record = Assert.Single(records);
        Assert.Equal("Engine.ini [Audio.Volume]", record.TargetPath);
        Assert.Equal(ConflictType.ConfigKey, record.Type);
        Assert.Equal("winner", record.WinnerPackageKey);
        Assert.Equal(ResolutionMethod.Priority, record.Resolution);
        Assert.Equal(ConflictSeverity.Info, record.Severity);
        Assert.Equal("demo", record.HostGameName);
        Assert.Equal(profileId, record.ProfileId);

        var loser = Assert.Single(record.Losers);
        Assert.Equal("loser", loser.PackageKey);
        Assert.Equal(5, loser.Priority);
    }

    [Fact]
    public void TranslateConfigKeyConflicts_NullKeyConflicts_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResolvedViewLayerBuilder.TranslateConfigKeyConflicts("p", null!, "g", Guid.Empty));
    }

    // ─── BuildOverwriteEntry ───

    [Fact]
    public void BuildOverwriteEntry_NullTargetPath_ReturnsNull()
    {
        var artifact = new GeneratedArtifact
        {
            RelativePath = "fix.pak",
            RelativeTargetPath = null,
            Type = GeneratedArtifactType.UserFix,
        };

        Assert.Null(ResolvedViewLayerBuilder.BuildOverwriteEntry(artifact, "/abs/fix.pak"));
    }

    [Fact]
    public void BuildOverwriteEntry_EmptyTargetPath_ReturnsNull()
    {
        var artifact = new GeneratedArtifact
        {
            RelativePath = "fix.pak",
            RelativeTargetPath = "",
            Type = GeneratedArtifactType.UserFix,
        };

        Assert.Null(ResolvedViewLayerBuilder.BuildOverwriteEntry(artifact, "/abs/fix.pak"));
    }

    [Fact]
    public void BuildOverwriteEntry_Valid_ReturnsUserOverrideEntry()
    {
        var artifact = new GeneratedArtifact
        {
            RelativePath = "userfixes/fix.pak",
            RelativeTargetPath = "Content/Paks/fix.pak",
            Type = GeneratedArtifactType.UserFix,
            FileSize = 4096,
            FileHash = "abc123",
        };

        var entry = ResolvedViewLayerBuilder.BuildOverwriteEntry(artifact, "/abs/userfixes/fix.pak");

        Assert.NotNull(entry);
        Assert.Equal("Content/Paks/fix.pak", entry!.TargetRelativePath);
        Assert.Equal("/abs/userfixes/fix.pak", entry.SourceAbsolutePath);
        Assert.Equal(ResolvedEntrySource.UserOverride, entry.Source);
        Assert.Equal(-1, entry.Priority);
        Assert.Equal(4096, entry.FileSize);
        Assert.Equal("abc123", entry.FileHash);
    }

    [Fact]
    public void BuildOverwriteEntry_NullArtifact_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResolvedViewLayerBuilder.BuildOverwriteEntry(null!, "/abs"));
    }

    [Fact]
    public void BuildOverwriteEntry_NullSourcePath_Throws()
    {
        var artifact = new GeneratedArtifact { RelativeTargetPath = "x" };
        Assert.Throws<ArgumentNullException>(() =>
            ResolvedViewLayerBuilder.BuildOverwriteEntry(artifact, null!));
    }
}
