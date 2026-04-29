using UEModManager.Models;
using UEModManager.Services.Recovery;

namespace UEModManager.Core.Tests.Services.Recovery;

public class CrashRecoveryScannerTests
{
    private static DeploymentTransaction Tx(
        DeploymentStatus status,
        DateTime? createdAt = null,
        int total = 5,
        int completed = 0)
        => new()
        {
            Status = status,
            CreatedAt = createdAt ?? new DateTime(2026, 4, 28, 10, 0, 0),
            TotalOperations = total,
            CompletedOperations = completed,
            BackupDirectory = "/tmp/backup/" + Guid.NewGuid(),
        };

    // ─── ClassifyStatus（纯函数） ───

    [Theory]
    [InlineData(DeploymentStatus.Committed, RecoveryAction.NoAction)]
    [InlineData(DeploymentStatus.RolledBack, RecoveryAction.NoAction)]
    [InlineData(DeploymentStatus.InProgress, RecoveryAction.RollbackRecommended)]
    [InlineData(DeploymentStatus.Failed, RecoveryAction.MarkFailedRecommended)]
    [InlineData(DeploymentStatus.Pending, RecoveryAction.MarkFailedRecommended)]
    public void ClassifyStatus_MapsToExpectedAction(DeploymentStatus status, RecoveryAction expected)
    {
        var (action, _) = CrashRecoveryScanner.ClassifyStatus(Tx(status));
        Assert.Equal(expected, action);
    }

    [Fact]
    public void ClassifyStatus_InProgress_ReasonIncludesProgress()
    {
        var tx = Tx(DeploymentStatus.InProgress, total: 10, completed: 7);

        var (_, reason) = CrashRecoveryScanner.ClassifyStatus(tx);

        Assert.Contains("7/10", reason);
        Assert.Contains("崩溃", reason);
    }

    // ─── Scan ───

    [Fact]
    public void Scan_OnlyCommittedAndRolledBack_ReturnsEmpty()
    {
        var transactions = new[]
        {
            Tx(DeploymentStatus.Committed),
            Tx(DeploymentStatus.RolledBack),
            Tx(DeploymentStatus.Committed),
        };

        var candidates = CrashRecoveryScanner.Scan(transactions);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Scan_FiltersOutCompletedTransactions()
    {
        var transactions = new[]
        {
            Tx(DeploymentStatus.Committed),
            Tx(DeploymentStatus.InProgress),  // 应保留
            Tx(DeploymentStatus.RolledBack),
            Tx(DeploymentStatus.Failed),       // 应保留
        };

        var candidates = CrashRecoveryScanner.Scan(transactions);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.Status == DeploymentStatus.InProgress);
        Assert.Contains(candidates, c => c.Status == DeploymentStatus.Failed);
    }

    [Fact]
    public void Scan_ReturnsCandidatesInDescendingTimeOrder()
    {
        var early = Tx(DeploymentStatus.InProgress, createdAt: new DateTime(2026, 4, 1));
        var middle = Tx(DeploymentStatus.Pending, createdAt: new DateTime(2026, 4, 15));
        var late = Tx(DeploymentStatus.Failed, createdAt: new DateTime(2026, 4, 28));

        var candidates = CrashRecoveryScanner.Scan(new[] { early, middle, late });

        Assert.Equal(3, candidates.Count);
        Assert.Equal(late.Id, candidates[0].TransactionId);    // 最新在前
        Assert.Equal(middle.Id, candidates[1].TransactionId);
        Assert.Equal(early.Id, candidates[2].TransactionId);
    }

    [Fact]
    public void Scan_PreservesTransactionContext()
    {
        var tx = Tx(DeploymentStatus.InProgress, total: 8, completed: 3);

        var candidates = CrashRecoveryScanner.Scan(new[] { tx });

        var c = Assert.Single(candidates);
        Assert.Equal(tx.Id, c.TransactionId);
        Assert.Equal(tx.BackupDirectory, c.BackupDirectory);
        Assert.Equal(8, c.TotalOperations);
        Assert.Equal(3, c.CompletedOperations);
    }

    [Fact]
    public void Scan_EmptyInput_ReturnsEmpty()
    {
        var candidates = CrashRecoveryScanner.Scan(Array.Empty<DeploymentTransaction>());
        Assert.Empty(candidates);
    }

    [Fact]
    public void Scan_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CrashRecoveryScanner.Scan(null!));
    }

    [Fact]
    public void Scan_RollbackCandidate_HasMatchingReason()
    {
        var tx = Tx(DeploymentStatus.InProgress);

        var c = Assert.Single(CrashRecoveryScanner.Scan(new[] { tx }));

        Assert.Equal(RecoveryAction.RollbackRecommended, c.Action);
        Assert.Equal(DeploymentStatus.InProgress, c.Status);
        Assert.NotEmpty(c.Reason);
    }
}
