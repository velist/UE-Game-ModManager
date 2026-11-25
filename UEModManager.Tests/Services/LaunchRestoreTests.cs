using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Health;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.ViewModels;

namespace UEModManager.Tests.Services;

/// <summary>
/// 启动游戏前以磁盘为准：管理器记着装好了、游戏目录里却没有的 MOD 文件，启动前要补回去，并且告诉玩家。
///
/// <para>
/// "补回去"这件事计划器本来就会做（它对每个目标都看磁盘），但此前没有任何测试钉住它，
/// 玩家也看不到它发生过。这里把两件事都钉死：文件真的回来了；会话和界面上说了这句话。
/// 全程用假的系统接口，<b>不会启动任何程序</b>。
/// </para>
/// </summary>
public sealed class LaunchRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "launch-restore-" + Guid.NewGuid().ToString("N"));
    private DiskRefreshHost? _host;

    private async Task<DiskRefreshHost> OpenAsync() => _host = await DiskRefreshHost.OpenAsync(_root);

    public void Dispose()
    {
        try
        {
            _host?.Deployment.PendingBackupCleanup.GetAwaiter().GetResult();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    [Fact]
    public Task Launch_DeployedModFolderWasDeleted_PutsItBackBeforeStartingAndSaysSo() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"), (@"sub\a2.bin", "A2A2"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.AddPackageAsync("off", ("x.bin", "XXXX"));
        await host.EnableAsync("a", "b");
        // 玩家手动删了、或者游戏更新 / 重装清掉了整个 MOD 目录
        Directory.Delete(host.ModPath, recursive: true);

        var session = await host.Launcher.LaunchAsync();

        Assert.True(session.Success, session.FailureReason);
        var deploy = Assert.Single(session.Steps, step => step.Type == LaunchStepType.Deploy);
        Assert.Equal(LaunchStepStatus.Passed, deploy.Status);
        Assert.Equal("已部署 3 个操作", deploy.Message);
        // 文件在"启动游戏"那一步之前就已经回到游戏目录里
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.GameFile("a", "a.bin")));
        Assert.Equal("A2A2", await File.ReadAllTextAsync(host.GameFile("a", @"sub\a2.bin")));
        Assert.Equal("BBBB", await File.ReadAllTextAsync(host.GameFile("b", "b.bin")));
        Assert.False(File.Exists(host.GameFile("off", "x.bin")));
        // 会话里记着是哪几个 MOD，界面据此告诉玩家
        Assert.Equal(["MOD a", "MOD b"], session.RestoredPackages);
        Assert.Same(session, host.Launcher.LastSession);
    });

    [Fact]
    public Task Launch_OnlyOneFileOfAModWasDeleted_RestoresJustThatOne() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"), (@"sub\a2.bin", "A2A2"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.EnableAsync("a", "b");
        File.Delete(host.GameFile("a", @"sub\a2.bin"));

        var session = await host.Launcher.LaunchAsync();

        Assert.True(session.Success, session.FailureReason);
        Assert.Equal("已部署 1 个操作", Assert.Single(session.Steps, step => step.Type == LaunchStepType.Deploy).Message);
        Assert.Equal("A2A2", await File.ReadAllTextAsync(host.GameFile("a", @"sub\a2.bin")));
        Assert.Equal(["MOD a"], session.RestoredPackages);
    });

    [Fact]
    public Task Launch_NothingMissing_SkipsDeploymentAndReportsNothing() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.EnableAsync("a");

        var session = await host.Launcher.LaunchAsync();

        Assert.True(session.Success, session.FailureReason);
        Assert.Equal(LaunchStepStatus.Skipped, Assert.Single(session.Steps, step => step.Type == LaunchStepType.Deploy).Status);
        Assert.Empty(session.RestoredPackages);
    });

    [Fact]
    public Task Launch_ModEnabledButNeverInstalledBefore_IsAnOrdinaryInstallNotARestore() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 关着"自动部署"时启用的 MOD：第一次装进游戏目录，不是"丢了又装回去"
        var host = await OpenAsync();
        await host.AddPackageAsync("fresh", ("f.bin", "FFFF"));
        await host.Profiles.SetPackageEnabledFlagAsync("fresh", true);

        var session = await host.Launcher.LaunchAsync();

        Assert.True(session.Success, session.FailureReason);
        Assert.Equal("FFFF", await File.ReadAllTextAsync(host.GameFile("fresh", "f.bin")));
        Assert.Empty(session.RestoredPackages);
    });

    [Fact]
    public Task Launch_SourceOfAnEnabledModIsGone_StopsBeforeStartingAndPointsAtRefresh() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.EnableAsync("gone");
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);

        var session = await host.Launcher.LaunchAsync();

        Assert.False(session.Success);
        Assert.Equal(LaunchOutcome.NotStarted, session.Outcome);
        Assert.Contains("「MOD gone」在 MOD 库里的文件不见了", session.FailureReason);
        Assert.Contains("「刷新」", session.FailureReason);
        // 游戏里还装着的那一份没有因为启动失败被动过
        Assert.Equal("GGGG", await File.ReadAllTextAsync(host.GameFile("gone", "g.bin")));
        Assert.Empty(session.RestoredPackages);
    });

    [Fact]
    public Task LaunchCenter_TellsThePlayerThatMissingFilesWerePutBack() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.EnableAsync("a", "b");
        Directory.Delete(host.ModPath, recursive: true);
        var launch = new LaunchViewModel(host.Launcher, host.Profiles, host.Game, host.Conflicts, NullLogger.Instance);
        launch.Initialize();

        var session = await launch.LaunchGameAsync();

        Assert.NotNull(session);
        Assert.True(session!.Success, session.FailureReason);
        Assert.Equal("启动成功", launch.StatusTitle);
        Assert.StartsWith("有 2 个 MOD 的文件在游戏目录里丢了，已重新装好", launch.StatusSubtitle);
        Assert.Equal("有 2 个 MOD 的文件在游戏目录里丢了，已重新装好", launch.LastEventInfo);

        // 启动中心关掉再打开，"上次事件"里还看得到这句话
        var reopened = new LaunchViewModel(host.Launcher, host.Profiles, host.Game, host.Conflicts, NullLogger.Instance);
        reopened.Initialize();
        Assert.Equal("有 2 个 MOD 的文件在游戏目录里丢了，已重新装好", reopened.LastEventInfo);

        // 下一次启动什么都没丢：不再提这件事
        var again = await launch.LaunchGameAsync();
        Assert.True(again!.Success, again.FailureReason);
        Assert.DoesNotContain("丢了", launch.StatusSubtitle);
        Assert.Equal("无异常", launch.LastEventInfo);
    });

    // ═══════════════════════════════════════
    //  体检
    // ═══════════════════════════════════════

    [Fact]
    public Task HealthReport_EverythingInPlace_SaysHowMuchWasChecked() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"), ("a2.bin", "A2"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.EnableAsync("a");

        var check = ModFilesCheck(await host.Health.CheckAsync());

        Assert.Equal(HealthStatus.Ok, check.Status);
        Assert.Equal("2 个 MOD 的 3 个源文件、2 个已安装文件都在", check.Message);
    });

    [Fact]
    public Task HealthReport_EnabledModMissingFromGameFolder_IsAWarningThatNamesIt() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 排查「MOD 没加载」时最需要的一句：方案里启用着，游戏目录里却没有
        var host = await OpenAsync();
        await host.AddPackageAsync("victim", ("v.bin", "VVVV"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        await host.EnableAsync("victim", "fine");
        Directory.Delete(Path.Combine(host.ModPath, "victim"), recursive: true);

        var report = await host.Health.CheckAsync();
        var check = ModFilesCheck(report);

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains("1 个已启用 MOD 的文件在游戏目录里丢失（victim），它们现在在游戏里不会生效", check.Message);
        Assert.Contains("点 MOD 列表上方的「刷新」（F5）处理", check.Message);
        Assert.Equal(host.GameFile("victim", "v.bin"), check.Detail, ignoreCase: true);
        Assert.Contains("[WARN] ModFiles:", report.ToText());
    });

    [Fact]
    public Task HealthReport_ModMissingFromLibrary_IsAWarningThatNamesIt() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);

        var check = ModFilesCheck(await host.Health.CheckAsync());

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains("1 个 MOD 在 MOD 库里的文件丢失或不完整（gone）", check.Message);
    });

    [Fact]
    public Task HealthReport_LibraryLocationGone_SaysSoInsteadOfListingEveryMod() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        Directory.Move(host.RepositoryRoot, host.RepositoryRoot + "-unplugged");

        var check = ModFilesCheck(await host.Health.CheckAsync());

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains("MOD 库的位置访问不到，没法核对 MOD 文件", check.Message);
        Assert.DoesNotContain("丢失或不完整", check.Message);
        Assert.Equal(host.RepositoryRoot, check.Detail);
    });

    [Fact]
    public Task HealthReport_EnabledEntryWithoutARecord_IsAWarning() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 这种引用会让所有部署失败，而列表里看不到它：体检里要写出来，诊断包才看得出原因
        var host = await OpenAsync();
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        var ghost = new Package { PackageKey = "ghost", DisplayName = "Ghost", HostGameName = DiskRefreshHost.GameName };
        await host.Profiles.AddPackagesToCurrentProfileAsync([ghost]);
        await host.Profiles.SetPackageEnabledFlagAsync("ghost", true);

        var check = ModFilesCheck(await host.Health.CheckAsync());

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains("方案里启用着 1 个找不到记录的 MOD（ghost），启用、禁用和启动都会因此失败", check.Message);
    });

    [Fact]
    public Task HealthReport_DeploymentRecordUnreadable_SaysTheGameFolderCouldNotBeChecked() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.EnableAsync("a");
        var stateFile = Assert.Single(Directory.GetFiles(
            Path.Combine(host.Root, "DeploymentState"), "state.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(stateFile, "{ 读不出来的清单");

        var check = ModFilesCheck(await host.Health.CheckAsync());

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains("已安装文件的记录读不出来，没法核对游戏目录里的 MOD 文件", check.Message);
    });

    private static HealthCheck ModFilesCheck(HealthReport report)
        => Assert.Single(report.Checks, check => check.Name == HealthCheckService.ModFilesCheckName);
}
