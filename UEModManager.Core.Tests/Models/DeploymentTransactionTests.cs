using UEModManager.Models;

namespace UEModManager.Core.Tests.Models;

public class DeploymentTransactionTests
{
    [Theory]
    [InlineData(DeploymentStatus.Committed, true)]
    [InlineData(DeploymentStatus.Failed, true)]
    [InlineData(DeploymentStatus.PartiallyRolledBack, true)] // 新：允许重试回滚
    [InlineData(DeploymentStatus.RolledBack, false)]
    [InlineData(DeploymentStatus.InProgress, false)]
    [InlineData(DeploymentStatus.Pending, false)]
    [InlineData(DeploymentStatus.Dismissed, false)]
    [InlineData(DeploymentStatus.LogPersistenceFailed, false)]
    public void CanRollback_MatchesExpected(DeploymentStatus status, bool expected)
    {
        var tx = new DeploymentTransaction { Status = status };
        Assert.Equal(expected, tx.CanRollback);
    }

    [Fact]
    public void RollbackFailures_DefaultsToEmptyList()
    {
        var tx = new DeploymentTransaction();

        Assert.NotNull(tx.RollbackFailures);
        Assert.Empty(tx.RollbackFailures);
    }

    [Fact]
    public void DismissedAt_DefaultsToNull()
    {
        var tx = new DeploymentTransaction();
        Assert.Null(tx.DismissedAt);
        Assert.Null(tx.DismissedReason);
    }

    [Fact]
    public void SchemaVersion_DefaultsTo2()
    {
        var tx = new DeploymentTransaction();
        Assert.Equal(2, tx.SchemaVersion);
    }

    [Fact]
    public void RollbackFailure_RecordEqualityWorks()
    {
        var a = new RollbackFailure("/path/x", "权限拒绝");
        var b = new RollbackFailure("/path/x", "权限拒绝");
        var c = new RollbackFailure("/path/y", "权限拒绝");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Progress_ZeroTotal_ReturnsZero()
    {
        var tx = new DeploymentTransaction { TotalOperations = 0, CompletedOperations = 0 };
        Assert.Equal(0.0, tx.Progress);
    }

    [Fact]
    public void Progress_HalfDone_Returns50()
    {
        var tx = new DeploymentTransaction { TotalOperations = 10, CompletedOperations = 5 };
        Assert.Equal(50.0, tx.Progress);
    }
}
