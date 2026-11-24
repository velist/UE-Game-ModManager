using UEModManager.Models;
using UEModManager.Services.Launch;

namespace UEModManager.Core.Tests.Services.Launch;

public class GameLaunchPlannerTests
{
    private static GameLaunchFacts Facts(
        uint appId = 100, bool steam = true, bool preferDirect = false, bool env = false, string? args = null)
        => new(appId, steam, preferDirect, env, args);

    [Fact]
    public void Decide_GameInSteamLibrary_LaunchesThroughSteam()
    {
        var plan = GameLaunchPlanner.Decide(Facts());

        Assert.Equal(GameLaunchMethod.Steam, plan.Method);
        Assert.Equal(100u, plan.SteamAppId);
        Assert.Equal("steam://rungameid/100", plan.SteamUri);
        Assert.True(plan.CanUseSteam);
    }

    [Fact]
    public void Decide_NoAppId_FallsBackToDirect()
    {
        var plan = GameLaunchPlanner.Decide(Facts(appId: 0));

        Assert.Equal(GameLaunchMethod.DirectExecutable, plan.Method);
        Assert.Null(plan.SteamUri);
        Assert.False(plan.CanUseSteam);
    }

    [Fact]
    public void Decide_SteamClientMissing_FallsBackToDirect()
    {
        var plan = GameLaunchPlanner.Decide(Facts(steam: false));

        Assert.Equal(GameLaunchMethod.DirectExecutable, plan.Method);
        Assert.False(plan.CanUseSteam);
    }

    [Fact]
    public void Decide_PlayerPrefersDirect_RunsExeButKeepsSteamAsAnOption()
    {
        var plan = GameLaunchPlanner.Decide(Facts(preferDirect: true));

        Assert.Equal(GameLaunchMethod.DirectExecutable, plan.Method);
        Assert.Null(plan.SteamUri);
        Assert.True(plan.CanUseSteam);
        Assert.Equal(100u, plan.SteamAppId);
    }

    [Fact]
    public void Decide_EnvironmentVariablesRequested_RunsExeDirectly()
    {
        // 环境变量经 Steam 传不到游戏进程
        Assert.Equal(GameLaunchMethod.DirectExecutable, GameLaunchPlanner.Decide(Facts(env: true)).Method);
    }

    [Fact]
    public void Decide_WithArguments_UsesRunUriWithEscapedArguments()
    {
        var plan = GameLaunchPlanner.Decide(Facts(args: " -windowed -name=a b/c "));

        Assert.Equal("steam://run/100//-windowed%20-name%3Da%20b%2Fc/", plan.SteamUri);
    }

    [Fact]
    public void Decide_NullFacts_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GameLaunchPlanner.Decide(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildSteamUri_NoArguments_UsesRunGameId(string? args)
    {
        Assert.Equal("steam://rungameid/4294967295", GameLaunchPlanner.BuildSteamUri(uint.MaxValue, args));
    }

    [Fact]
    public void BuildSteamUri_ZeroAppId_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameLaunchPlanner.BuildSteamUri(0));
    }
}

public class GameProcessMatcherTests
{
    private const string GameDir = @"E:\Lib\steamapps\common\Game";

    [Theory]
    [InlineData(@"E:\Lib\steamapps\common\Game\Game.exe", true)]
    [InlineData(@"e:\lib\STEAMAPPS\common\game\Sub\Binaries\Win64\Game-Win64-Shipping.exe", true)]
    [InlineData(@"E:/Lib/steamapps/common/Game/x.exe", true)]
    [InlineData(@"E:\Lib\steamapps\common\Game Demo\Game.exe", false)]
    [InlineData(@"E:\Lib\steamapps\common\Other\Game.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", false)]
    public void IsGameProcess_WithPath_DecidesByInstallDirectory(string path, bool expected)
    {
        var process = new RunningProcessInfo(1, "whatever", path);

        Assert.Equal(expected, GameProcessMatcher.IsGameProcess(process, GameDir, "Game.exe"));
    }

    [Fact]
    public void IsGameProcess_PathKnown_NameAloneDoesNotMatch()
    {
        // 别处一个同名程序不是这款游戏
        var process = new RunningProcessInfo(1, "Game", @"D:\Tools\Game.exe");

        Assert.False(GameProcessMatcher.IsGameProcess(process, GameDir, "Game.exe"));
    }

    [Theory]
    [InlineData("Game", true)]
    [InlineData("game.EXE", true)]
    [InlineData("Game-Win64-Shipping", true)]
    [InlineData("GameLauncherHelper", false)]
    [InlineData("MyGame", false)]
    [InlineData("", false)]
    public void IsGameProcess_PathUnreadable_FallsBackToExecutableName(string name, bool expected)
    {
        var process = new RunningProcessInfo(1, name, null);

        Assert.Equal(expected, GameProcessMatcher.IsGameProcess(process, GameDir, "Game.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"E:\")]
    public void IsGameProcess_UnusableGameDirectory_NeverMatchesByPath(string? directory)
    {
        var process = new RunningProcessInfo(1, "x", @"E:\anything\x.exe");

        Assert.False(GameProcessMatcher.IsGameProcess(process, directory, "Game.exe"));
    }

    [Fact]
    public void IsGameProcess_NoPathAndNoExecutableName_DoesNotMatch()
    {
        Assert.False(GameProcessMatcher.IsGameProcess(new RunningProcessInfo(1, "Game", null), GameDir, null));
    }

    [Fact]
    public void IsGameProcess_NullProcess_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GameProcessMatcher.IsGameProcess(null!, GameDir, "Game.exe"));
    }
}

public class LaunchWatchPolicyTests
{
    private static readonly LaunchWatchTimings Timings = new()
    {
        PollInterval = TimeSpan.FromSeconds(1),
        DirectSettle = TimeSpan.FromSeconds(5),
        HandOffGrace = TimeSpan.FromSeconds(6),
        SteamTimeout = TimeSpan.FromSeconds(40),
    };

    private static LaunchOutcome? Steam(double elapsed, bool seen)
        => LaunchWatchPolicy.Decide(
            new LaunchWatchSnapshot(GameLaunchMethod.Steam, TimeSpan.FromSeconds(elapsed), false, null, seen, true),
            Timings);

    private static LaunchOutcome? Direct(
        double elapsed, bool alive, double? sinceExit = null, bool seen = false, bool steamMayTakeOver = false)
        => LaunchWatchPolicy.Decide(
            new LaunchWatchSnapshot(
                GameLaunchMethod.DirectExecutable, TimeSpan.FromSeconds(elapsed), alive,
                sinceExit.HasValue ? TimeSpan.FromSeconds(sinceExit.Value) : null, seen, steamMayTakeOver),
            Timings);

    [Fact]
    public void Steam_GameProcessSeen_IsRunning()
    {
        Assert.Equal(LaunchOutcome.Running, Steam(0, seen: true));
        Assert.Equal(LaunchOutcome.Running, Steam(39, seen: true));
    }

    [Fact]
    public void Steam_NothingSeenYet_KeepsWatchingUntilTimeout()
    {
        Assert.Null(Steam(0, seen: false));
        Assert.Null(Steam(39.5, seen: false));
        Assert.Equal(LaunchOutcome.NotConfirmed, Steam(40, seen: false));
    }

    [Fact]
    public void Direct_ProcessAlive_IsRunningOnlyAfterSettleTime()
    {
        Assert.Null(Direct(0, alive: true));
        Assert.Null(Direct(4.5, alive: true));
        Assert.Equal(LaunchOutcome.Running, Direct(5, alive: true));
    }

    [Fact]
    public void Direct_LauncherExitedAndGameProcessRunning_IsRunning()
    {
        Assert.Equal(LaunchOutcome.Running, Direct(1, alive: false, sinceExit: 0, seen: true));
    }

    [Fact]
    public void Direct_ExitedWithNoGameProcess_FailsAfterHandOffGrace()
    {
        Assert.Null(Direct(1, alive: false, sinceExit: 0));
        Assert.Null(Direct(6, alive: false, sinceExit: 5.5));
        Assert.Equal(LaunchOutcome.ExitedImmediately, Direct(7, alive: false, sinceExit: 6));
    }

    [Fact]
    public void Direct_ExitedInSteamLibrary_WaitsForSteamTakeOverBeforeFailing()
    {
        Assert.Null(Direct(7, alive: false, sinceExit: 6, steamMayTakeOver: true));
        Assert.Equal(LaunchOutcome.Running, Direct(20, alive: false, sinceExit: 19, seen: true, steamMayTakeOver: true));
        Assert.Equal(LaunchOutcome.ExitedImmediately, Direct(41, alive: false, sinceExit: 40, steamMayTakeOver: true));
    }

    [Fact]
    public void Direct_NoProcessObjectAtAll_CountsFromLaunch()
    {
        Assert.Null(Direct(5, alive: false));
        Assert.Equal(LaunchOutcome.ExitedImmediately, Direct(6, alive: false));
    }

    [Fact]
    public void Decide_NullArguments_Throw()
    {
        var snapshot = new LaunchWatchSnapshot(GameLaunchMethod.Steam, TimeSpan.Zero, false, null, false, true);

        Assert.Throws<ArgumentNullException>(() => LaunchWatchPolicy.Decide(null!, Timings));
        Assert.Throws<ArgumentNullException>(() => LaunchWatchPolicy.Decide(snapshot, null!));
    }
}

public class LaunchOutcomeTextTests
{
    [Fact]
    public void Running_HasNoAdvice()
    {
        var message = LaunchOutcomeText.Describe(LaunchOutcome.Running, GameLaunchMethod.Steam, true);

        Assert.Equal("启动成功", message.Title);
        Assert.Equal(LaunchAdvice.None, message.Advice);
    }

    [Fact]
    public void NotConfirmed_PointsAtSteamWindowAndOffersDirect()
    {
        var message = LaunchOutcomeText.Describe(LaunchOutcome.NotConfirmed, GameLaunchMethod.Steam, true);

        Assert.Contains("Steam 窗口", message.Detail);
        Assert.Equal(LaunchAdvice.SwitchToDirect, message.Advice);
    }

    [Fact]
    public void ExitedImmediately_DirectLaunchOfSteamGame_OffersSteam()
    {
        var message = LaunchOutcomeText.Describe(
            LaunchOutcome.ExitedImmediately, GameLaunchMethod.DirectExecutable, canUseSteam: true);

        Assert.Equal(LaunchAdvice.SwitchToSteam, message.Advice);
        Assert.Contains("Steam", message.Detail);
    }

    [Fact]
    public void ExitedImmediately_NonSteamGame_DoesNotOfferSteam()
    {
        var message = LaunchOutcomeText.Describe(
            LaunchOutcome.ExitedImmediately, GameLaunchMethod.DirectExecutable, canUseSteam: false);

        Assert.Equal(LaunchAdvice.None, message.Advice);
        Assert.NotEmpty(message.Detail);
    }

    [Theory]
    [InlineData(LaunchOutcome.Failed)]
    [InlineData(LaunchOutcome.NotStarted)]
    public void OtherOutcomes_AreReportedAsFailure(LaunchOutcome outcome)
    {
        var message = LaunchOutcomeText.Describe(outcome, GameLaunchMethod.Steam, true);

        Assert.Equal("启动失败", message.Title);
        Assert.NotEmpty(message.Detail);
    }
}
