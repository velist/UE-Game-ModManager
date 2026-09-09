using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class LaunchFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeploymentPlanningException_StopsPipelineAndRecordsFailure()
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, "test.exe");
        await File.WriteAllBytesAsync(executable, []);
        var profiles = new ProfileService(NullLogger<ProfileService>.Instance, Path.Combine(_root, "Data"));
        var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(_root, "config.json"));
        // 没有当前方案时，视图为空，真实规划器会抛“没有活跃的 Profile”。
        var view = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, null!, profiles, null!, null!, game);
        var planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance, null!, null!, profiles, game, view, null!);
        var sessions = Path.Combine(_root, "Sessions");
        var launcher = new LaunchOrchestrator(NullLogger<LaunchOrchestrator>.Instance,
            game, profiles, view, planner, null!, null!, sessions);
        var started = new List<LaunchStepType>();
        launcher.StepChanged += step =>
        {
            if (step.Status == LaunchStepStatus.Running) started.Add(step.Type);
        };

        // 保留冲突检查。即使旧实现错误地继续流水线，也会在空分析器处失败，
        // 不会进入 Process.Start；测试始终不启动真实进程。
        var result = await launcher.LaunchAsync(new LaunchContext
        {
            GameName = "Test", GameRootPath = _root, ExecutablePath = executable, WorkingDirectory = _root
        });

        Assert.False(result.Success);
        var deployment = Assert.Single(result.Steps, step => step.Type == LaunchStepType.Deploy);
        Assert.Equal(LaunchStepStatus.Failed, deployment.Status);
        Assert.Contains("没有活跃的 Profile", result.FailureReason);
        Assert.DoesNotContain(LaunchStepType.ConflictCheck, started);
        Assert.DoesNotContain(LaunchStepType.LaunchProcess, started);
        Assert.All(result.Steps.Where(step => step.Type is LaunchStepType.ConflictCheck or LaunchStepType.LaunchProcess),
            step => Assert.Equal(LaunchStepStatus.Pending, step.Status));
        Assert.Same(result, launcher.LastSession);
        var saved = await File.ReadAllTextAsync(Path.Combine(sessions, $"{result.Id:N}.json"));
        Assert.Contains("没有活跃的 Profile", saved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
