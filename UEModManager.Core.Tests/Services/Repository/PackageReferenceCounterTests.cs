using UEModManager.Models;
using UEModManager.Services.Repository;

namespace UEModManager.Core.Tests.Services.Repository;

public class PackageReferenceCounterTests
{
    private static InstanceProfile MakeProfile(string game, params (string key, bool enabled)[] entries)
    {
        var p = new InstanceProfile { HostGameName = game };
        foreach (var (key, enabled) in entries)
            p.Packages.Add(new ProfilePackageEntry { PackageKey = key, IsEnabled = enabled });
        return p;
    }

    [Fact]
    public void Count_NotReferenced_ReportsZero()
    {
        var profiles = new[] { MakeProfile("g1", ("OtherKey", true)) };

        var report = PackageReferenceCounter.Count("MyMod", profiles);

        Assert.Equal("MyMod", report.PackageKey);
        Assert.Equal(0, report.ProfileReferenceCount);
        Assert.Equal(0, report.EnabledReferenceCount);
        Assert.False(report.IsReferenced);
        Assert.False(report.IsEnabledAnywhere);
    }

    [Fact]
    public void Count_ReferencedInOneProfile_Enabled_ReportsBoth()
    {
        var p = MakeProfile("g1", ("MyMod", true));
        var report = PackageReferenceCounter.Count("MyMod", new[] { p });

        Assert.Equal(1, report.ProfileReferenceCount);
        Assert.Equal(1, report.EnabledReferenceCount);
        Assert.True(report.IsReferenced);
        Assert.True(report.IsEnabledAnywhere);
        Assert.Equal(p.Id, report.ReferencingProfileIds.Single());
        Assert.Equal(p.Id, report.EnabledInProfileIds.Single());
    }

    [Fact]
    public void Count_ReferencedButDisabled_ReportsRefButNotEnabled()
    {
        var p = MakeProfile("g1", ("MyMod", false));
        var report = PackageReferenceCounter.Count("MyMod", new[] { p });

        Assert.Equal(1, report.ProfileReferenceCount);
        Assert.Equal(0, report.EnabledReferenceCount);
        Assert.True(report.IsReferenced);
        Assert.False(report.IsEnabledAnywhere);
    }

    [Fact]
    public void Count_MultipleProfiles_MixedStates_CountsCorrectly()
    {
        var p1 = MakeProfile("g1", ("MyMod", true));
        var p2 = MakeProfile("g1", ("MyMod", false));
        var p3 = MakeProfile("g1", ("MyMod", true));
        var p4 = MakeProfile("g1", ("OtherMod", true)); // 不引用

        var report = PackageReferenceCounter.Count("MyMod", new[] { p1, p2, p3, p4 });

        Assert.Equal(3, report.ProfileReferenceCount);
        Assert.Equal(2, report.EnabledReferenceCount);
        Assert.Contains(p1.Id, report.EnabledInProfileIds);
        Assert.Contains(p3.Id, report.EnabledInProfileIds);
        Assert.DoesNotContain(p2.Id, report.EnabledInProfileIds);
    }

    [Fact]
    public void Count_CaseInsensitive_PackageKey()
    {
        var p = MakeProfile("g1", ("MyMod", true));
        var report = PackageReferenceCounter.Count("MYMOD", new[] { p });

        Assert.Equal(1, report.ProfileReferenceCount);
    }

    [Fact]
    public void Count_NullKey_Throws()
        => Assert.Throws<ArgumentException>(() =>
            PackageReferenceCounter.Count(null!, Array.Empty<InstanceProfile>()));

    [Fact]
    public void Count_EmptyKey_Throws()
        => Assert.Throws<ArgumentException>(() =>
            PackageReferenceCounter.Count("", Array.Empty<InstanceProfile>()));

    [Fact]
    public void Count_NullProfiles_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            PackageReferenceCounter.Count("Key", null!));

    [Fact]
    public void Count_HandlesNullProfileEntry_Gracefully()
    {
        // 有时反序列化会出现 null 项；不能让 Count 崩
        var profiles = new InstanceProfile?[] { null, MakeProfile("g1", ("MyMod", true)) };

        var report = PackageReferenceCounter.Count("MyMod", profiles!);

        Assert.Equal(1, report.ProfileReferenceCount);
    }

    [Fact]
    public void CountAll_ReturnsReportForEachUniqueKey()
    {
        var p1 = MakeProfile("g1", ("ModA", true), ("ModB", false));
        var p2 = MakeProfile("g1", ("ModB", true), ("ModC", true));

        var all = PackageReferenceCounter.CountAll(new[] { p1, p2 });

        Assert.Equal(3, all.Count);
        Assert.Equal(1, all["ModA"].ProfileReferenceCount);
        Assert.Equal(2, all["ModB"].ProfileReferenceCount);
        Assert.Equal(1, all["ModB"].EnabledReferenceCount);
        Assert.Equal(1, all["ModC"].ProfileReferenceCount);
    }

    [Fact]
    public void CountAll_NoProfiles_ReturnsEmpty()
    {
        var all = PackageReferenceCounter.CountAll(Array.Empty<InstanceProfile>());
        Assert.Empty(all);
    }

    [Fact]
    public void CountAll_SkipsEmptyKeys()
    {
        // 防御：空 PackageKey 不应被纳入结果（数据脏）
        var p = new InstanceProfile { HostGameName = "g" };
        p.Packages.Add(new ProfilePackageEntry { PackageKey = "", IsEnabled = true });
        p.Packages.Add(new ProfilePackageEntry { PackageKey = "ValidKey", IsEnabled = false });

        var all = PackageReferenceCounter.CountAll(new[] { p });

        Assert.Single(all);
        Assert.True(all.ContainsKey("ValidKey"));
    }
}
