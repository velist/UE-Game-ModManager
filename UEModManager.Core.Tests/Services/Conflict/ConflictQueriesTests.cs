using UEModManager.Models;
using UEModManager.Services.Conflict;

namespace UEModManager.Core.Tests.Services.Conflict;

public class ConflictQueriesTests
{
    private static ConflictRecord Make(string winner, params string[] losers)
        => new()
        {
            TargetPath = $"path/{winner}.pak",
            Type = ConflictType.LoadOrder,
            WinnerPackageKey = winner,
            WinnerDisplayName = winner,
            Reason = "test",
            Resolution = ResolutionMethod.Priority,
            Severity = ConflictSeverity.Warning,
            HostGameName = "demo",
            Losers = losers.Select(l => new ConflictLoser
            {
                PackageKey = l, DisplayName = l, Priority = 5,
            }).ToList(),
        };

    // ─── GetConflictsForPackage ───

    [Fact]
    public void GetConflictsForPackage_Empty_ReturnsEmpty()
    {
        var result = ConflictQueries.GetConflictsForPackage(Array.Empty<ConflictRecord>(), "x");
        Assert.Empty(result);
    }

    [Fact]
    public void GetConflictsForPackage_MatchesWinner()
    {
        var conflicts = new[] { Make("alice", "bob") };

        var result = ConflictQueries.GetConflictsForPackage(conflicts, "alice");

        Assert.Single(result);
    }

    [Fact]
    public void GetConflictsForPackage_MatchesLoser()
    {
        var conflicts = new[] { Make("alice", "bob") };

        var result = ConflictQueries.GetConflictsForPackage(conflicts, "bob");

        Assert.Single(result);
    }

    [Fact]
    public void GetConflictsForPackage_NoMatch_Empty()
    {
        var conflicts = new[] { Make("alice", "bob") };

        var result = ConflictQueries.GetConflictsForPackage(conflicts, "ghost");

        Assert.Empty(result);
    }

    [Fact]
    public void GetConflictsForPackage_MultipleHits_ReturnsAll()
    {
        var conflicts = new[]
        {
            Make("alice", "bob"),
            Make("alice", "charlie"),
            Make("dave", "alice"),
        };

        var result = ConflictQueries.GetConflictsForPackage(conflicts, "alice");

        Assert.Equal(3, result.Count); // 两次胜者 + 一次败者
    }

    [Fact]
    public void GetConflictsForPackage_NullConflicts_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ConflictQueries.GetConflictsForPackage(null!, "x"));

    [Fact]
    public void GetConflictsForPackage_NullKey_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ConflictQueries.GetConflictsForPackage(Array.Empty<ConflictRecord>(), null!));

    // ─── GetLossCount ───

    [Fact]
    public void GetLossCount_NoLosses_ReturnsZero()
    {
        var conflicts = new[] { Make("alice", "bob") };

        Assert.Equal(0, ConflictQueries.GetLossCount(conflicts, "alice")); // alice 是胜者
    }

    [Fact]
    public void GetLossCount_CountsAcrossMultipleConflicts()
    {
        var conflicts = new[]
        {
            Make("alice", "bob"),
            Make("charlie", "bob", "dave"),
            Make("eve", "frank"),
        };

        Assert.Equal(2, ConflictQueries.GetLossCount(conflicts, "bob"));
    }

    [Fact]
    public void GetLossCount_NonExistentKey_ReturnsZero()
    {
        var conflicts = new[] { Make("alice", "bob") };

        Assert.Equal(0, ConflictQueries.GetLossCount(conflicts, "ghost"));
    }

    [Fact]
    public void GetLossCount_NullConflicts_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ConflictQueries.GetLossCount(null!, "x"));

    // ─── GetWinCount ───

    [Fact]
    public void GetWinCount_CountsWins()
    {
        var conflicts = new[]
        {
            Make("alice", "bob"),
            Make("alice", "charlie"),
            Make("dave", "alice"),
        };

        Assert.Equal(2, ConflictQueries.GetWinCount(conflicts, "alice"));
    }

    [Fact]
    public void GetWinCount_LoserPackageReturnsZero()
    {
        var conflicts = new[] { Make("alice", "bob") };

        Assert.Equal(0, ConflictQueries.GetWinCount(conflicts, "bob"));
    }

    [Fact]
    public void GetWinCount_NullConflicts_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ConflictQueries.GetWinCount(null!, "x"));
}
