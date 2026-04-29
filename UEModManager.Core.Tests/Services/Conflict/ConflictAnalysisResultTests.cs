using UEModManager.Models;
using UEModManager.Services.Conflict;

namespace UEModManager.Core.Tests.Services.Conflict;

public class ConflictAnalysisResultTests
{
    private static ConflictRecord MakeRecord(
        string winner,
        ConflictType type = ConflictType.LoadOrder,
        ConflictSeverity severity = ConflictSeverity.Warning,
        bool isUserOverride = false,
        params string[] losers)
        => new()
        {
            TargetPath = $"path/{winner}.pak",
            Type = type,
            Severity = severity,
            WinnerPackageKey = winner,
            WinnerDisplayName = winner.ToUpperInvariant(),
            IsUserOverride = isUserOverride,
            Reason = "test",
            Resolution = ResolutionMethod.Priority,
            Losers = losers.Select(l => new ConflictLoser
            {
                PackageKey = l,
                DisplayName = l.ToUpperInvariant(),
                Priority = 5,
            }).ToList(),
            HostGameName = "demo",
        };

    [Fact]
    public void Empty_NoConflicts()
    {
        var r = new ConflictAnalysisResult();

        Assert.Equal(0, r.TotalConflicts);
        Assert.False(r.HasConflicts);
        Assert.Empty(r.AffectedPackages);
        Assert.Empty(r.ConflictsByType);
        Assert.Empty(r.ConflictsBySeverity);
        Assert.Equal(0, r.UserOverrideCount);
    }

    [Fact]
    public void TotalConflicts_AndHasConflicts()
    {
        var r = new ConflictAnalysisResult
        {
            Conflicts = { MakeRecord("a", losers: "b") },
        };

        Assert.Equal(1, r.TotalConflicts);
        Assert.True(r.HasConflicts);
    }

    [Fact]
    public void ConflictsByType_GroupsCorrectly()
    {
        var r = new ConflictAnalysisResult
        {
            Conflicts =
            {
                MakeRecord("a", type: ConflictType.LoadOrder, losers: "b"),
                MakeRecord("c", type: ConflictType.LoadOrder, losers: "d"),
                MakeRecord("e", type: ConflictType.ConfigKey, losers: "f"),
            },
        };

        var byType = r.ConflictsByType;
        Assert.Equal(2, byType[ConflictType.LoadOrder]);
        Assert.Equal(1, byType[ConflictType.ConfigKey]);
    }

    [Fact]
    public void ConflictsBySeverity_GroupsCorrectly()
    {
        var r = new ConflictAnalysisResult
        {
            Conflicts =
            {
                MakeRecord("a", severity: ConflictSeverity.Warning, losers: "b"),
                MakeRecord("c", severity: ConflictSeverity.Error, losers: "d"),
                MakeRecord("e", severity: ConflictSeverity.Warning, losers: "f"),
                MakeRecord("g", severity: ConflictSeverity.Info, losers: "h"),
            },
        };

        var bySev = r.ConflictsBySeverity;
        Assert.Equal(2, bySev[ConflictSeverity.Warning]);
        Assert.Equal(1, bySev[ConflictSeverity.Error]);
        Assert.Equal(1, bySev[ConflictSeverity.Info]);
    }

    [Fact]
    public void UserOverrideCount_OnlyCountsOverridden()
    {
        var r = new ConflictAnalysisResult
        {
            Conflicts =
            {
                MakeRecord("a", isUserOverride: true, losers: "b"),
                MakeRecord("c", isUserOverride: false, losers: "d"),
                MakeRecord("e", isUserOverride: true, losers: "f"),
            },
        };

        Assert.Equal(2, r.UserOverrideCount);
    }

    [Fact]
    public void AffectedPackages_DedupsWinnerAndLosers()
    {
        var r = new ConflictAnalysisResult
        {
            Conflicts =
            {
                MakeRecord("alice", losers: "bob"),
                MakeRecord("alice", losers: "charlie", type: ConflictType.ConfigKey),
                // alice 既是两次 winner，又被列两次 → 应去重
            },
        };

        var affected = r.AffectedPackages;
        Assert.Equal(3, affected.Count);
        Assert.Contains("alice", affected);
        Assert.Contains("bob", affected);
        Assert.Contains("charlie", affected);
    }
}
