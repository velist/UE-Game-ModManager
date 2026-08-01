using UEModManager.Models;

namespace UEModManager.Core.Tests.Models;

public class DeploymentTransactionTests
{
    [Theory]
    [InlineData(DeploymentStatus.Committed, true)]
    [InlineData(DeploymentStatus.Failed, true)]
    [InlineData(DeploymentStatus.PartiallyRolledBack, true)] // 新：允许重试回滚
    [InlineData(DeploymentStatus.InProgress, true)]          // 新：崩溃留下的状态，必须可回滚
    [InlineData(DeploymentStatus.RolledBack, false)]
    [InlineData(DeploymentStatus.Pending, false)]
    [InlineData(DeploymentStatus.Dismissed, false)]
    [InlineData(DeploymentStatus.LogPersistenceFailed, false)]
    public void CanRollback_MatchesExpected(DeploymentStatus status, bool expected)
    {
        var tx = new DeploymentTransaction { Status = status };
        Assert.Equal(expected, tx.CanRollback);
    }

    [Fact]
    public void RollbackSource_PrefersExecutedOperations()
    {
        var executed = new DeploymentOperation { TargetPath = "/executed" };
        var planned = new DeploymentOperation { TargetPath = "/planned" };
        var tx = new DeploymentTransaction
        {
            ExecutedOperations = [executed],
            PlannedOperations = [planned],
        };

        Assert.Same(executed, Assert.Single(tx.RollbackSource));
    }

    [Fact]
    public void RollbackSource_FallsBackToPlannedWhenExecutionRecordLost()
    {
        // 崩溃场景：进程在执行循环中被杀，磁盘上的 ExecutedOperations 是空数组，
        // 唯一的"备份 → 目标"映射来自备份阶段落盘的计划快照。
        var planned = new DeploymentOperation { TargetPath = "/planned" };
        var tx = new DeploymentTransaction
        {
            Status = DeploymentStatus.InProgress,
            PlannedOperations = [planned],
        };

        Assert.Same(planned, Assert.Single(tx.RollbackSource));
    }

    [Fact]
    public void RollbackSource_EmptyWhenNeitherRecorded()
    {
        Assert.Empty(new DeploymentTransaction().RollbackSource);
    }

    [Fact]
    public void PlannedOperations_DefaultsToEmptyList()
    {
        var tx = new DeploymentTransaction();

        Assert.NotNull(tx.PlannedOperations);
        Assert.Empty(tx.PlannedOperations);
    }

    [Fact]
    public void RollbackOutcome_SkippedIsNotAttemptedAndNotSucceeded()
    {
        var outcome = RollbackOutcome.Skipped("状态不允许");

        Assert.False(outcome.Attempted);
        Assert.False(outcome.Succeeded);
        Assert.Equal("状态不允许", outcome.SkipReason);
        Assert.Empty(outcome.Failures);
    }

    [Fact]
    public void RollbackOutcome_CompleteIsAttemptedAndSucceeded()
    {
        var outcome = RollbackOutcome.Complete();

        Assert.True(outcome.Attempted);
        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.SkipReason);
    }

    [Fact]
    public void RollbackOutcome_PartialIsAttemptedButNotSucceeded()
    {
        var failures = new[] { new RollbackFailure("/path/x", "备份缺失") };

        var outcome = RollbackOutcome.Partial(failures);

        Assert.True(outcome.Attempted);
        Assert.False(outcome.Succeeded);
        Assert.Single(outcome.Failures);
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
