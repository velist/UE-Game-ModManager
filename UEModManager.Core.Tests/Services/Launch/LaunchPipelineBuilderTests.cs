using UEModManager.Models;
using UEModManager.Services.Launch;

namespace UEModManager.Core.Tests.Services.Launch;

public class LaunchPipelineBuilderTests
{
    [Fact]
    public void Default_Pipeline_Has6Steps()
    {
        var ctx = new LaunchContext { GameName = "demo" };

        var steps = LaunchPipelineBuilder.BuildPipeline(ctx);

        Assert.Equal(6, steps.Count);
    }

    [Fact]
    public void Default_Pipeline_StepOrder()
    {
        var ctx = new LaunchContext { GameName = "demo" };

        var types = LaunchPipelineBuilder.BuildPipeline(ctx).Select(s => s.Type).ToList();

        Assert.Equal(new[]
        {
            LaunchStepType.ValidateGamePath,
            LaunchStepType.ValidateExecutable,
            LaunchStepType.BuildResolvedView,
            LaunchStepType.Deploy,
            LaunchStepType.ConflictCheck,
            LaunchStepType.LaunchProcess,
        }, types);
    }

    [Fact]
    public void SkipDeployCheck_OmitsBuildViewAndDeploy()
    {
        var ctx = new LaunchContext { GameName = "demo", SkipDeployCheck = true };

        var types = LaunchPipelineBuilder.BuildPipeline(ctx).Select(s => s.Type).ToList();

        Assert.DoesNotContain(LaunchStepType.BuildResolvedView, types);
        Assert.DoesNotContain(LaunchStepType.Deploy, types);
        Assert.Contains(LaunchStepType.ConflictCheck, types); // 仍保留
        Assert.Equal(4, types.Count);
    }

    [Fact]
    public void SkipConflictCheck_OmitsConflictCheck()
    {
        var ctx = new LaunchContext { GameName = "demo", SkipConflictCheck = true };

        var types = LaunchPipelineBuilder.BuildPipeline(ctx).Select(s => s.Type).ToList();

        Assert.DoesNotContain(LaunchStepType.ConflictCheck, types);
        Assert.Contains(LaunchStepType.BuildResolvedView, types); // 仍保留
        Assert.Equal(5, types.Count);
    }

    [Fact]
    public void BothSkipped_OnlyValidationAndLaunch()
    {
        var ctx = new LaunchContext
        {
            GameName = "demo",
            SkipDeployCheck = true,
            SkipConflictCheck = true,
        };

        var types = LaunchPipelineBuilder.BuildPipeline(ctx).Select(s => s.Type).ToList();

        Assert.Equal(new[]
        {
            LaunchStepType.ValidateGamePath,
            LaunchStepType.ValidateExecutable,
            LaunchStepType.LaunchProcess,
        }, types);
    }

    [Fact]
    public void AllSteps_HaveNonEmptyDisplayName()
    {
        var ctx = new LaunchContext { GameName = "demo" };

        var steps = LaunchPipelineBuilder.BuildPipeline(ctx);

        Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s.DisplayName)));
    }

    [Fact]
    public void AllSteps_StartPending()
    {
        var ctx = new LaunchContext { GameName = "demo" };

        var steps = LaunchPipelineBuilder.BuildPipeline(ctx);

        Assert.All(steps, s => Assert.Equal(LaunchStepStatus.Pending, s.Status));
    }

    [Fact]
    public void NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LaunchPipelineBuilder.BuildPipeline(null!));
    }
}
