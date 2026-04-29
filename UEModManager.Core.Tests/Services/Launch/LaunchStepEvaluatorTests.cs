using UEModManager.Models;
using UEModManager.Services.Launch;

namespace UEModManager.Core.Tests.Services.Launch;

public class LaunchStepEvaluatorTests
{
    // ─── EvaluateConflictCheck ───

    [Fact]
    public void Conflict_Zero_PassedAndCleanMessage()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(0, 0);

        Assert.True(eval.Passed);
        Assert.Equal(LaunchStepStatus.Passed, eval.Status);
        Assert.Equal("无冲突", eval.Message);
    }

    [Fact]
    public void Conflict_AnyError_FailsWithCount()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(totalConflicts: 5, errorConflicts: 2);

        Assert.False(eval.Passed);
        Assert.Equal(LaunchStepStatus.Failed, eval.Status);
        Assert.Contains("5", eval.Message);
        Assert.Contains("2", eval.Message);
    }

    [Fact]
    public void Conflict_OnlyWarnings_PassedAsWarning()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(totalConflicts: 3, errorConflicts: 0);

        Assert.True(eval.Passed);
        Assert.Equal(LaunchStepStatus.Warning, eval.Status);
        Assert.Contains("3", eval.Message);
        Assert.Contains("警告", eval.Message);
    }

    [Fact]
    public void Conflict_OneErrorAmongMany_FailsAnyway()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(totalConflicts: 100, errorConflicts: 1);

        Assert.False(eval.Passed);
        Assert.Equal(LaunchStepStatus.Failed, eval.Status);
    }

    // ─── EvaluateBuildView ───

    [Fact]
    public void BuildView_NoConflict_Passed()
    {
        var eval = LaunchStepEvaluator.EvaluateBuildView(totalEntries: 42, conflictCount: 0);

        Assert.True(eval.Passed);
        Assert.Equal(LaunchStepStatus.Passed, eval.Status);
        Assert.Contains("42", eval.Message);
    }

    [Fact]
    public void BuildView_WithConflict_WarningButPassed()
    {
        var eval = LaunchStepEvaluator.EvaluateBuildView(totalEntries: 10, conflictCount: 3);

        Assert.True(eval.Passed); // 视图构建有冲突仍允许继续
        Assert.Equal(LaunchStepStatus.Warning, eval.Status);
        Assert.Contains("10", eval.Message);
        Assert.Contains("3", eval.Message);
    }

    [Fact]
    public void BuildView_EmptyView_StillPasses()
    {
        var eval = LaunchStepEvaluator.EvaluateBuildView(0, 0);

        Assert.True(eval.Passed);
        Assert.Equal(LaunchStepStatus.Passed, eval.Status);
    }

    // ─── EvaluateDeploymentSkip ───

    [Fact]
    public void DeploymentSkip_ZeroOps_Skipped()
    {
        var eval = LaunchStepEvaluator.EvaluateDeploymentSkip(0);

        Assert.Equal(LaunchStepStatus.Skipped, eval.Status);
        Assert.True(eval.Passed);
        Assert.Contains("无需部署", eval.Message);
    }

    [Fact]
    public void DeploymentSkip_NegativeOps_TreatedAsZero()
    {
        var eval = LaunchStepEvaluator.EvaluateDeploymentSkip(-1);

        Assert.Equal(LaunchStepStatus.Skipped, eval.Status);
    }

    [Fact]
    public void DeploymentSkip_HasOps_RunningWithCount()
    {
        var eval = LaunchStepEvaluator.EvaluateDeploymentSkip(7);

        Assert.Equal(LaunchStepStatus.Running, eval.Status);
        Assert.True(eval.Passed);
        Assert.Contains("7", eval.Message);
    }

    // ─── EvaluateDeploymentResult ───

    [Fact]
    public void DeploymentResult_Committed_Passed()
    {
        var eval = LaunchStepEvaluator.EvaluateDeploymentResult(
            DeploymentStatus.Committed, completedOperations: 3, errorMessage: null);

        Assert.Equal(LaunchStepStatus.Passed, eval.Status);
        Assert.True(eval.Passed);
        Assert.Contains("3", eval.Message);
    }

    [Fact]
    public void DeploymentResult_FailedWithMessage_FailsAndShowsMessage()
    {
        var eval = LaunchStepEvaluator.EvaluateDeploymentResult(
            DeploymentStatus.Failed, completedOperations: 1, errorMessage: "磁盘已满");

        Assert.Equal(LaunchStepStatus.Failed, eval.Status);
        Assert.False(eval.Passed);
        Assert.Contains("磁盘已满", eval.Message);
    }

    [Fact]
    public void DeploymentResult_FailedNoMessage_StillFails()
    {
        var eval = LaunchStepEvaluator.EvaluateDeploymentResult(
            DeploymentStatus.Failed, completedOperations: 0, errorMessage: null);

        Assert.Equal(LaunchStepStatus.Failed, eval.Status);
        Assert.False(eval.Passed);
        Assert.Contains("Failed", eval.Message);
    }

    [Fact]
    public void DeploymentResult_RolledBack_FailsLikeOtherNonCommitted()
    {
        var eval = LaunchStepEvaluator.EvaluateDeploymentResult(
            DeploymentStatus.RolledBack, completedOperations: 2, errorMessage: "用户取消");

        Assert.Equal(LaunchStepStatus.Failed, eval.Status);
        Assert.False(eval.Passed);
    }
}
