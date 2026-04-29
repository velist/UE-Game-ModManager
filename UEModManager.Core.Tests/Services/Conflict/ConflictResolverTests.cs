using UEModManager.Models;
using UEModManager.Services.Conflict;

namespace UEModManager.Core.Tests.Services.Conflict;

public class ConflictResolverTests
{
    private static ArtifactOwner Owner(string key, int prio, string? hash = null, string? name = null)
        => new(
            PackageKey: key,
            DisplayName: name ?? key.ToUpperInvariant(),
            Priority: prio,
            Kind: PackageKind.Mod,
            ArtifactHash: hash,
            FileSize: 1024);

    [Fact]
    public void Resolve_SingleOwner_ReturnsNull_NoConflict()
    {
        var result = ConflictResolver.Resolve("path/A.pak", new[] { Owner("a", 0) });
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_EmptyOwners_ReturnsNull()
    {
        var result = ConflictResolver.Resolve("path/A.pak", Array.Empty<ArtifactOwner>());
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ByPriority_LowerNumberWins()
    {
        var owners = new[]
        {
            Owner("low",  10, "h1"),
            Owner("high",  0, "h2"),  // 优先级数字小 → 胜者
        };

        var r = ConflictResolver.Resolve("p", owners);

        Assert.NotNull(r);
        Assert.Equal("high", r!.WinnerPackageKey);
        Assert.Equal(ResolutionMethod.Priority, r.Method);
        Assert.Single(r.Losers);
        Assert.Equal("low", r.Losers[0].PackageKey);
    }

    [Fact]
    public void Resolve_UserOverride_BeatsPriority()
    {
        var owners = new[]
        {
            Owner("a", 0, "h1"),  // 默认胜者
            Owner("b", 10, "h2"),
        };
        var overrides = new Dictionary<string, string> { ["p"] = "b" };

        var r = ConflictResolver.Resolve("p", owners, overrides);

        Assert.NotNull(r);
        Assert.Equal("b", r!.WinnerPackageKey);
        Assert.Equal(ResolutionMethod.UserOverride, r.Method);
        Assert.Contains("用户指定", r.Reason);
    }

    [Fact]
    public void Resolve_UserOverride_NonExistentPackage_FallsBackToPriority()
    {
        var owners = new[]
        {
            Owner("a", 0, "h1"),
            Owner("b", 10, "h2"),
        };
        var overrides = new Dictionary<string, string> { ["p"] = "ghost-pkg" };

        var r = ConflictResolver.Resolve("p", owners, overrides);

        Assert.NotNull(r);
        Assert.Equal("a", r!.WinnerPackageKey);
        Assert.Equal(ResolutionMethod.Priority, r.Method);
    }

    [Fact]
    public void Resolve_LosersOrderedByPriority_AllExceptWinner()
    {
        var owners = new[]
        {
            Owner("p1", 5,  "h1"),
            Owner("p2", 1,  "h2"),  // 胜
            Owner("p3", 10, "h3"),
            Owner("p4", 7,  "h4"),
        };

        var r = ConflictResolver.Resolve("p", owners)!;

        Assert.Equal("p2", r.WinnerPackageKey);
        Assert.Equal(3, r.Losers.Count);
        // 败者按优先级数字升序（与 sorted 顺序一致）
        Assert.Equal(new[] { "p1", "p4", "p3" }, r.Losers.Select(l => l.PackageKey));
    }

    [Fact]
    public void Severity_AllSameHash_IsInfo()
    {
        var owners = new[]
        {
            Owner("a", 0, "same-hash"),
            Owner("b", 1, "same-hash"),
        };

        Assert.Equal(ConflictSeverity.Info, ConflictResolver.DetermineSeverity(owners));
    }

    [Fact]
    public void Severity_DifferentHash_IsWarning()
    {
        var owners = new[]
        {
            Owner("a", 0, "h1"),
            Owner("b", 1, "h2"),
        };

        Assert.Equal(ConflictSeverity.Warning, ConflictResolver.DetermineSeverity(owners));
    }

    [Fact]
    public void Severity_NullHashes_AreIgnored_TreatedAsInfo()
    {
        // 全部 null hash → distinct count = 0 ≤ 1 → Info
        var owners = new[]
        {
            Owner("a", 0, null),
            Owner("b", 1, null),
        };

        Assert.Equal(ConflictSeverity.Info, ConflictResolver.DetermineSeverity(owners));
    }

    [Fact]
    public void Resolve_PropagatesSeverityToResolution()
    {
        var owners = new[]
        {
            Owner("a", 0, "h1"),
            Owner("b", 1, "h2"),  // 不同哈希 → Warning
        };

        var r = ConflictResolver.Resolve("p", owners)!;

        Assert.Equal(ConflictSeverity.Warning, r.Severity);
    }

    [Fact]
    public void Resolve_NullOwners_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConflictResolver.Resolve("p", null!));
    }
}
