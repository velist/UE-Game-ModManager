using UEModManager.Models;
using UEModManager.Services.Lock;

namespace UEModManager.Core.Tests.Services.Lock;

public class ProfileLockBuilderTests
{
    private static InstanceProfile MakeProfile(
        string name = "Demo",
        params (string key, int prio, bool enabled)[] entries)
        => new()
        {
            Name = name,
            HostGameName = "demo-game",
            Description = "test profile",
            BackendType = DeploymentBackendType.HardLink,
            Packages = entries.Select(e => new ProfilePackageEntry
            {
                PackageKey = e.key,
                Priority = e.prio,
                IsEnabled = e.enabled,
                Kind = PackageKind.Mod,
            }).ToList(),
        };

    private static Package MakePkg(
        string key, string display, string version = "1.0",
        string? hash = null, PackageKind kind = PackageKind.Mod)
        => new()
        {
            PackageKey = key,
            DisplayName = display,
            Version = version,
            ContentHash = hash,
            Kind = kind,
            HostGameName = "demo-game",
        };

    // ─── Build ───

    [Fact]
    public void Build_BasicProfile_ProducesLockWithMatchingShape()
    {
        var profile = MakeProfile("MyMix", ("a", 0, true), ("b", 1, false));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakePkg("a", "Mod A", "2.1", "hash-a"),
            ["b"] = MakePkg("b", "Mod B", "1.0", "hash-b"),
        };

        var lockFile = ProfileLockBuilder.Build(profile, packages);

        Assert.Equal(ProfileLockSchema.CurrentVersion, lockFile.LockVersion);
        Assert.Equal("MyMix", lockFile.Profile.Name);
        Assert.Equal("test profile", lockFile.Profile.Description);
        Assert.Equal("HardLink", lockFile.Profile.BackendType);
        Assert.Equal("demo-game", lockFile.Host.GameName);
        Assert.Equal(2, lockFile.Packages.Count);
        Assert.Contains(lockFile.Packages, p => p.PackageKey == "a" && p.IsEnabled && p.Priority == 0);
        Assert.Contains(lockFile.Packages, p => p.PackageKey == "b" && !p.IsEnabled && p.Priority == 1);
    }

    [Fact]
    public void Build_PreservesPackageMetadataFromRepo()
    {
        var profile = MakeProfile(entries: ("k", 0, true));
        var packages = new Dictionary<string, Package>
        {
            ["k"] = MakePkg("k", "Display Name", "3.0", "abc"),
        };

        var lockFile = ProfileLockBuilder.Build(profile, packages);

        var pkg = Assert.Single(lockFile.Packages);
        Assert.Equal("Display Name", pkg.DisplayName);
        Assert.Equal("3.0", pkg.Version);
        Assert.Equal("abc", pkg.ContentHash);
        Assert.Equal("Mod", pkg.Kind);
    }

    [Fact]
    public void Build_OrphanedEntry_FallsBackToKeyAsDisplayName()
    {
        // Profile 引用了 ghost-pkg，但仓库里找不到
        var profile = MakeProfile(entries: ("ghost-pkg", 0, true));
        var packages = new Dictionary<string, Package>();

        var lockFile = ProfileLockBuilder.Build(profile, packages);

        var pkg = Assert.Single(lockFile.Packages);
        Assert.Equal("ghost-pkg", pkg.PackageKey);
        Assert.Equal("ghost-pkg", pkg.DisplayName);  // fallback
        Assert.Null(pkg.ContentHash);
    }

    [Fact]
    public void Build_ConflictOverrides_PreservedCaseInsensitive()
    {
        var profile = MakeProfile();
        var overrides = new Dictionary<string, string>
        {
            ["Engine.ini"] = "winner-pkg",
            ["DefaultGame.ini"] = "another-pkg",
        };

        var lockFile = ProfileLockBuilder.Build(profile, new Dictionary<string, Package>(), overrides);

        Assert.Equal(2, lockFile.ConflictOverrides.Count);
        Assert.Equal("winner-pkg", lockFile.ConflictOverrides["engine.ini"]);  // 大小写不敏感
    }

    [Fact]
    public void Build_NullProfile_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProfileLockBuilder.Build(null!, new Dictionary<string, Package>()));
    }
}

public class ProfileLockComparatorTests
{
    private static ProfileLock MakeLock(params ProfileLockPackage[] pkgs)
        => new() { Packages = pkgs.ToList() };

    [Fact]
    public void Compare_AllMatched_DiffIsAllOk()
    {
        var lockFile = MakeLock(
            new ProfileLockPackage { PackageKey = "a", DisplayName = "A", ContentHash = "h1" },
            new ProfileLockPackage { PackageKey = "b", DisplayName = "B", ContentHash = "h2" });

        var local = new Dictionary<string, Package>
        {
            ["a"] = new() { PackageKey = "a", DisplayName = "A", ContentHash = "h1", HostGameName = "x" },
            ["b"] = new() { PackageKey = "b", DisplayName = "B", ContentHash = "h2", HostGameName = "x" },
        };

        var diff = ProfileLockComparator.Compare(lockFile, local);

        Assert.True(diff.CanImportFully);
        Assert.Equal(2, diff.MatchedCount);
        Assert.Equal(0, diff.MissingCount);
    }

    [Fact]
    public void Compare_MissingLocalPackage_ReportedAsMissing()
    {
        var lockFile = MakeLock(
            new ProfileLockPackage { PackageKey = "have", DisplayName = "Have" },
            new ProfileLockPackage { PackageKey = "ghost", DisplayName = "Ghost" });

        var local = new Dictionary<string, Package>
        {
            ["have"] = new() { PackageKey = "have", DisplayName = "Have", HostGameName = "x" },
        };

        var diff = ProfileLockComparator.Compare(lockFile, local);

        Assert.False(diff.CanImportFully);
        Assert.Equal(1, diff.MissingCount);
        Assert.Equal(1, diff.MatchedCount);
        Assert.Contains(diff.PackageDiffs, d =>
            d.PackageKey == "ghost" && d.Status == LockPackageImportStatus.Missing);
    }

    [Fact]
    public void Compare_DifferentHash_ReportedAsHashMismatch()
    {
        var lockFile = MakeLock(
            new ProfileLockPackage { PackageKey = "a", DisplayName = "A", ContentHash = "old-hash" });

        var local = new Dictionary<string, Package>
        {
            ["a"] = new() { PackageKey = "a", DisplayName = "A", ContentHash = "new-hash", HostGameName = "x" },
        };

        var diff = ProfileLockComparator.Compare(lockFile, local);

        Assert.False(diff.CanImportFully);
        Assert.Equal(1, diff.HashMismatchCount);
        var entry = Assert.Single(diff.PackageDiffs);
        Assert.Equal("old-hash", entry.LockedHash);
        Assert.Equal("new-hash", entry.LocalHash);
    }

    [Fact]
    public void Compare_MissingHashInEither_DoesNotFlagMismatch()
    {
        // 当 lock 或 local 任一没 hash，不应误报 mismatch
        var lockFile = MakeLock(
            new ProfileLockPackage { PackageKey = "a", DisplayName = "A", ContentHash = null },
            new ProfileLockPackage { PackageKey = "b", DisplayName = "B", ContentHash = "h" });

        var local = new Dictionary<string, Package>
        {
            ["a"] = new() { PackageKey = "a", DisplayName = "A", ContentHash = "anything", HostGameName = "x" },
            ["b"] = new() { PackageKey = "b", DisplayName = "B", ContentHash = null, HostGameName = "x" },
        };

        var diff = ProfileLockComparator.Compare(lockFile, local);

        Assert.True(diff.CanImportFully);
        Assert.Equal(2, diff.MatchedCount);
    }

    [Fact]
    public void Compare_NullInputs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProfileLockComparator.Compare(null!, new Dictionary<string, Package>()));
        Assert.Throws<ArgumentNullException>(() =>
            ProfileLockComparator.Compare(new ProfileLock(), null!));
    }
}
