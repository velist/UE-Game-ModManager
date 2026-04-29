using UEModManager.Models;
using UEModManager.Services.Profile;

namespace UEModManager.Core.Tests.Services.Profile;

public class ProfileSyncPlannerTests
{
    private static InstanceProfile MakeProfile(params (string key, bool enabled, int priority, PackageKind kind)[] entries)
        => new()
        {
            Name = "test",
            HostGameName = "demo",
            Packages = entries.Select(e => new ProfilePackageEntry
            {
                PackageKey = e.key,
                IsEnabled = e.enabled,
                Priority = e.priority,
                Kind = e.kind,
            }).ToList(),
        };

    private static LegacyModEntry Mod(string name, bool enabled = false, bool isPlugin = false)
        => new(name, enabled, isPlugin, null);

    [Fact]
    public void Empty_ProfileAndScan_ReturnsEmpty()
    {
        var profile = MakeProfile();
        var result = ProfileSyncPlanner.ComputeSync(profile, Array.Empty<LegacyModEntry>());

        Assert.Empty(result.Packages);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Updated);
    }

    [Fact]
    public void EmptyProfile_AllScanned_AllAdded()
    {
        var profile = MakeProfile();
        var scan = new[] { Mod("a", true), Mod("b", false) };

        var result = ProfileSyncPlanner.ComputeSync(profile, scan);

        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(new[] { 0, 1 }, result.Packages.Select(p => p.Priority));
    }

    [Fact]
    public void NonEmptyProfile_ScanEmpty_AllRemoved()
    {
        var profile = MakeProfile(("a", true, 0, PackageKind.Mod), ("b", false, 1, PackageKind.Mod));

        var result = ProfileSyncPlanner.ComputeSync(profile, Array.Empty<LegacyModEntry>());

        Assert.Empty(result.Packages);
        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.Added);
    }

    [Fact]
    public void EnabledFlip_CountsAsUpdate()
    {
        var profile = MakeProfile(("a", false, 0, PackageKind.Mod));
        var scan = new[] { Mod("a", enabled: true) };

        var result = ProfileSyncPlanner.ComputeSync(profile, scan);

        var entry = Assert.Single(result.Packages);
        Assert.True(entry.IsEnabled);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void KindChange_CountsAsUpdate()
    {
        // 旧条目 Kind=Mod，但新扫描的是 plugin
        var profile = MakeProfile(("plug.dll", false, 0, PackageKind.Mod));
        var scan = new[] { Mod("plug.dll", false, isPlugin: true) };

        var result = ProfileSyncPlanner.ComputeSync(profile, scan);

        Assert.Equal(PackageKind.Plugin, result.Packages[0].Kind);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void NoChange_ZeroUpdated()
    {
        var profile = MakeProfile(("a", true, 0, PackageKind.Mod));
        var scan = new[] { Mod("a", enabled: true) };

        var result = ProfileSyncPlanner.ComputeSync(profile, scan);

        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Mixed_AddRemoveUpdate()
    {
        var profile = MakeProfile(
            ("keep", false, 0, PackageKind.Mod),
            ("toRemove", true, 1, PackageKind.Mod));
        var scan = new[]
        {
            Mod("keep", enabled: true),    // updated (enable flip)
            Mod("brandNew", false),        // added
        };

        var result = ProfileSyncPlanner.ComputeSync(profile, scan);

        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Updated);
        Assert.DoesNotContain(result.Packages, p => p.PackageKey == "toRemove");
        Assert.Contains(result.Packages, p => p.PackageKey == "keep" && p.IsEnabled);
        Assert.Contains(result.Packages, p => p.PackageKey == "brandNew");
    }

    [Fact]
    public void NewEntries_PriorityContinuesFromMax()
    {
        var profile = MakeProfile(
            ("a", false, 5, PackageKind.Mod),
            ("b", false, 10, PackageKind.Mod));
        var scan = new[] { Mod("a"), Mod("b"), Mod("new1"), Mod("new2") };

        var result = ProfileSyncPlanner.ComputeSync(profile, scan);

        var newEntries = result.Packages
            .Where(p => p.PackageKey is "new1" or "new2")
            .OrderBy(p => p.Priority)
            .ToList();
        Assert.Equal(11, newEntries[0].Priority); // max + 1
        Assert.Equal(12, newEntries[1].Priority);
    }

    [Fact]
    public void Order_ExistingKeptInOriginalOrder_NewAppended()
    {
        var profile = MakeProfile(
            ("b", false, 0, PackageKind.Mod),
            ("a", false, 1, PackageKind.Mod));  // 注意：原顺序是 b, a
        var scan = new[] { Mod("a"), Mod("b"), Mod("c") };

        var result = ProfileSyncPlanner.ComputeSync(profile, scan);

        Assert.Equal(new[] { "b", "a", "c" }, result.Packages.Select(p => p.PackageKey));
    }

    [Fact]
    public void NullProfile_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ProfileSyncPlanner.ComputeSync(null!, Array.Empty<LegacyModEntry>()));

    [Fact]
    public void NullScan_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ProfileSyncPlanner.ComputeSync(MakeProfile(), null!));
}
