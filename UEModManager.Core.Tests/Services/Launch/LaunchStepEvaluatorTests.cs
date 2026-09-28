using UEModManager.Models;
using UEModManager.Services.Launch;

namespace UEModManager.Core.Tests.Services.Launch;

public class LaunchStepEvaluatorTests
{
    // ─── EvaluateConflictCheck ───

    [Fact]
    public void Conflict_NoGroups_PassedWithCleanMessage()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(0);

        Assert.True(eval.Passed);
        Assert.Equal(LaunchStepStatus.Passed, eval.Status);
        Assert.Equal("无 MOD 冲突", eval.Message);
    }

    /// <summary>
    /// 冲突只是提示，不拦启动。
    ///
    /// 旧实现把"内容不同的目标路径冲突"归成 Failed —— 用户装两个改同一张贴图的 MOD
    /// 就被挡在启动按钮外面。新版一律 Passed，有冲突时降级成 Warning。
    /// </summary>
    [Fact]
    public void Conflict_AnyGroup_WarningButStillPassed()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(3);

        Assert.True(eval.Passed);
        Assert.Equal(LaunchStepStatus.Warning, eval.Status);
        Assert.Contains("3", eval.Message);
        Assert.Contains("组 MOD 冲突待处理", eval.Message);
    }

    /// <summary>
    /// 数字必须是"组数"而不是"目标路径冲突数"。
    ///
    /// 现场回归：旧模型报"582 个文件冲突"，用户数自己的 MOD 个数（十几个），
    /// 两边永远对不上，只能判定软件在乱报。组数就是面板上的框数，也是用户
    /// 真正要做的决策数，可以一条条核对。
    /// </summary>
    [Fact]
    public void Conflict_ReportsGroupCountNotPathCount()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(7);

        Assert.Contains("7 组", eval.Message);
        Assert.DoesNotContain("582", eval.Message);
    }

    [Fact]
    public void Conflict_NegativeCount_TreatedAsClean()
    {
        var eval = LaunchStepEvaluator.EvaluateConflictCheck(-1);

        Assert.Equal(LaunchStepStatus.Passed, eval.Status);
        Assert.Equal("无 MOD 冲突", eval.Message);
    }

    // ─── EvaluateBuildView ───

    [Fact]
    public void BuildView_ReportsFileCountAndPasses()
    {
        var eval = LaunchStepEvaluator.EvaluateBuildView(totalEntries: 42);

        Assert.True(eval.Passed);
        Assert.Equal(LaunchStepStatus.Passed, eval.Status);
        Assert.Contains("42", eval.Message);
    }

    /// <summary>
    /// 视图构建不再掺冲突数。
    ///
    /// 旧实现这一步报"607 个文件, 582 个冲突"，而冲突检查步骤另报一次 —— 同一件事
    /// 在清单上出现两遍且说法不同。现在冲突调查只由冲突步骤负责。
    /// </summary>
    [Fact]
    public void BuildView_DoesNotMentionConflicts()
    {
        var eval = LaunchStepEvaluator.EvaluateBuildView(607);

        Assert.DoesNotContain("冲突", eval.Message);
        Assert.Contains("607", eval.Message);
    }

    [Fact]
    public void BuildView_EmptyView_StillPasses()
    {
        var eval = LaunchStepEvaluator.EvaluateBuildView(0);

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
