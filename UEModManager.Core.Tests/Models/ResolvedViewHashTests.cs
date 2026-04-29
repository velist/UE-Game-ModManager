using UEModManager.Models;

namespace UEModManager.Core.Tests.Models;

/// <summary>
/// ResolvedView.ComputeViewHash 稳定性测试。
/// 总计划 Phase 8 验收点：相同输入应生成相同 hash；改顺序不应导致 hash 变化（内部 OrderBy）。
/// </summary>
public class ResolvedViewHashTests
{
    private static ResolvedEntry MakeEntry(string path, string? hash, string? pkg = "pkg-a")
        => new()
        {
            TargetRelativePath = path,
            FileHash = hash,
            PackageKey = pkg,
            Source = ResolvedEntrySource.Package,
        };

    [Fact]
    public void EmptyEntries_ProducesStableHash()
    {
        var h1 = ResolvedView.ComputeViewHash(new List<ResolvedEntry>());
        var h2 = ResolvedView.ComputeViewHash(new List<ResolvedEntry>());

        Assert.Equal(h1, h2);
        Assert.Equal(16, h1.Length);
    }

    [Fact]
    public void SameInputs_ProduceSameHash()
    {
        var entries1 = new List<ResolvedEntry>
        {
            MakeEntry("Mods/A.pak", "hash-a"),
            MakeEntry("Mods/B.pak", "hash-b"),
        };
        var entries2 = new List<ResolvedEntry>
        {
            MakeEntry("Mods/A.pak", "hash-a"),
            MakeEntry("Mods/B.pak", "hash-b"),
        };

        Assert.Equal(
            ResolvedView.ComputeViewHash(entries1),
            ResolvedView.ComputeViewHash(entries2));
    }

    [Fact]
    public void EntryOrder_DoesNotAffectHash()
    {
        var ordered = new List<ResolvedEntry>
        {
            MakeEntry("Mods/A.pak", "h1"),
            MakeEntry("Mods/B.pak", "h2"),
            MakeEntry("Mods/C.pak", "h3"),
        };
        var reversed = new List<ResolvedEntry>
        {
            MakeEntry("Mods/C.pak", "h3"),
            MakeEntry("Mods/B.pak", "h2"),
            MakeEntry("Mods/A.pak", "h1"),
        };

        Assert.Equal(
            ResolvedView.ComputeViewHash(ordered),
            ResolvedView.ComputeViewHash(reversed));
    }

    [Fact]
    public void DifferentFileHash_ChangesViewHash()
    {
        var v1 = ResolvedView.ComputeViewHash(new() { MakeEntry("Mods/A.pak", "old") });
        var v2 = ResolvedView.ComputeViewHash(new() { MakeEntry("Mods/A.pak", "new") });

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void DifferentPath_ChangesViewHash()
    {
        var v1 = ResolvedView.ComputeViewHash(new() { MakeEntry("Mods/A.pak", "h") });
        var v2 = ResolvedView.ComputeViewHash(new() { MakeEntry("Mods/B.pak", "h") });

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void PathCase_DoesNotAffectHash()
    {
        // ComputeViewHash 内部 ToLowerInvariant — 路径大小写不应影响哈希
        var lower = ResolvedView.ComputeViewHash(new() { MakeEntry("mods/a.pak", "h") });
        var upper = ResolvedView.ComputeViewHash(new() { MakeEntry("MODS/A.PAK", "h") });

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void IsIdenticalTo_UsesViewHash()
    {
        var entries = new List<ResolvedEntry> { MakeEntry("Mods/A.pak", "h") };
        var hash = ResolvedView.ComputeViewHash(entries);

        var view1 = new ResolvedView { Entries = entries, ViewHash = hash };
        var view2 = new ResolvedView { Entries = entries, ViewHash = hash };
        var view3 = new ResolvedView { Entries = entries, ViewHash = "different" };

        Assert.True(view1.IsIdenticalTo(view2));
        Assert.False(view1.IsIdenticalTo(view3));
    }

    [Fact]
    public void NullFileHash_IsTreatedDistinctlyFromEmptyString()
    {
        var withNull = ResolvedView.ComputeViewHash(new() { MakeEntry("p", null) });
        var withEmpty = ResolvedView.ComputeViewHash(new() { MakeEntry("p", "") });

        // 当前实现里 null → "null" 字面量，"" 是真实空字符串，应不相等
        Assert.NotEqual(withNull, withEmpty);
    }
}
