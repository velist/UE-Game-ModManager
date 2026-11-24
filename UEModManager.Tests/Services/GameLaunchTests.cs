using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Localization;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Launch;
using UEModManager.Services.Steam;

namespace UEModManager.Tests.Services;

/// <summary>
/// 启动游戏的真实结果：经 Steam 启动、进程秒退、重复点击。
/// 全部用假的系统接口，不会启动任何程序，也不会真的等待。
/// </summary>
public sealed class GameLaunchTests : IDisposable
{
    private const string SteamApps = @"E:\Lib\steamapps";
    private const string InstallPath = SteamApps + @"\common\Sample";
    private const string SteamExe = InstallPath + @"\steam\Sample.exe";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", Guid.NewGuid().ToString("N"));

    private static readonly LaunchWatchTimings Timings = new()
    {
        PollInterval = TimeSpan.FromSeconds(1),
        DirectSettle = TimeSpan.FromSeconds(3),
        HandOffGrace = TimeSpan.FromSeconds(4),
        SteamTimeout = TimeSpan.FromSeconds(10),
    };

    private sealed class FakeProcess : IStartedProcess
    {
        public int Id { get; init; } = 4242;
        public bool HasExited { get; set; }
    }

    private sealed class FakePlatform : IGameLaunchPlatform
    {
        public bool SteamAvailable = true;
        public List<SteamAppManifest> Manifests = [new SteamAppManifest(777, "Sample", "Sample")];
        public List<string> OpenedUris = [];
        public List<string> StartedExecutables = [];
        public List<string> ManifestDirectoriesRead = [];
        public FakeProcess? ProcessToReturn = new();
        public Exception? OpenUriError;
        public int Delays;
        /// <summary>第几次等待之后有哪些进程在跑。</summary>
        public Func<int, IReadOnlyList<RunningProcessInfo>> Processes = _ => [];
        /// <summary>每次等待时调用（测试用它让进程在某一刻退出）。</summary>
        public Action<int>? OnDelay;
        public TaskCompletionSource? Gate;

        public bool IsSteamClientAvailable() => SteamAvailable;

        public IReadOnlyList<SteamAppManifest> ReadAppManifests(string steamAppsDirectory)
        {
            ManifestDirectoriesRead.Add(steamAppsDirectory);
            return Manifests;
        }

        public void OpenUri(string uri)
        {
            if (OpenUriError != null) throw OpenUriError;
            OpenedUris.Add(uri);
        }

        public IStartedProcess? StartExecutable(
            string path, string workingDirectory, string? arguments, IReadOnlyDictionary<string, string> environment)
        {
            StartedExecutables.Add(path);
            return ProcessToReturn;
        }

        public IReadOnlyList<RunningProcessInfo> SnapshotProcesses() => Processes(Delays);

        public async Task DelayAsync(TimeSpan delay)
        {
            if (Gate != null) await Gate.Task;
            Delays++;
            OnDelay?.Invoke(Delays);
        }
    }

    private static LaunchContext SteamContext() => new()
    {
        GameName = "Sample",
        GameRootPath = InstallPath + @"\steam",
        ExecutablePath = SteamExe,
        WorkingDirectory = InstallPath + @"\steam",
    };

    private static RunningProcessInfo GameProcess(int id = 9000)
        => new(id, "Sample-Win64-Shipping", InstallPath + @"\Sample\Binaries\Win64\Sample-Win64-Shipping.exe");

    private static GameProcessLauncher Launcher(FakePlatform platform)
        => new(NullLogger.Instance, platform, Timings);

    // ─── 启动方式 ───

    [Fact]
    public void ResolvePlan_GameInSteamLibrary_ReadsAppIdFromManifestNextToIt()
    {
        var platform = new FakePlatform();

        var plan = Launcher(platform).ResolvePlan(SteamContext(), preferDirect: false);

        Assert.Equal(GameLaunchMethod.Steam, plan.Method);
        Assert.Equal("steam://rungameid/777", plan.SteamUri);
        Assert.Equal(new[] { SteamApps }, platform.ManifestDirectoriesRead);
    }

    [Fact]
    public void ResolvePlan_NoMatchingManifest_FallsBackToDirect()
    {
        var platform = new FakePlatform { Manifests = [new SteamAppManifest(1, "Other", "Other")] };

        var plan = Launcher(platform).ResolvePlan(SteamContext(), preferDirect: false);

        Assert.Equal(GameLaunchMethod.DirectExecutable, plan.Method);
        Assert.False(plan.CanUseSteam);
    }

    [Fact]
    public void ResolvePlan_GameOutsideSteam_DoesNotTouchSteamAtAll()
    {
        var platform = new FakePlatform();
        var context = new LaunchContext
        {
            GameName = "Sample", GameRootPath = @"D:\Games\Sample", ExecutablePath = @"D:\Games\Sample\Sample.exe",
        };

        var plan = Launcher(platform).ResolvePlan(context, preferDirect: false);

        Assert.Equal(GameLaunchMethod.DirectExecutable, plan.Method);
        Assert.Empty(platform.ManifestDirectoriesRead);
    }

    // ─── 经 Steam 启动 ───

    [Fact]
    public async Task Steam_GameAppears_IsRunningAndTheExeIsNeverStartedDirectly()
    {
        var platform = new FakePlatform { Processes = delays => delays >= 3 ? [GameProcess()] : [] };
        var progress = new List<string>();

        var result = await Launcher(platform).LaunchAsync(SteamContext(), false, progress.Add);

        Assert.Equal(LaunchOutcome.Running, result.Outcome);
        Assert.Equal(new[] { "steam://rungameid/777" }, platform.OpenedUris);
        Assert.Empty(platform.StartedExecutables);
        Assert.Null(result.ProcessId);
        Assert.Equal(3, platform.Delays);
        Assert.Equal(new[] { LaunchOutcomeText.WaitingForSteam }, progress);
    }

    [Fact]
    public async Task Steam_GameNeverAppears_IsNotConfirmedAfterTimeout()
    {
        var platform = new FakePlatform();

        var result = await Launcher(platform).LaunchAsync(SteamContext(), false);

        Assert.Equal(LaunchOutcome.NotConfirmed, result.Outcome);
        Assert.Equal(10, platform.Delays);
    }

    [Fact]
    public async Task Steam_UnrelatedProcesses_DoNotCountAsTheGame()
    {
        var platform = new FakePlatform
        {
            Processes = _ =>
            [
                new RunningProcessInfo(1, "steam", @"C:\Program Files (x86)\Steam\steam.exe"),
                new RunningProcessInfo(2, "Sample", @"D:\Elsewhere\Sample.exe"),
            ],
        };

        var result = await Launcher(platform).LaunchAsync(SteamContext(), false);

        Assert.Equal(LaunchOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task Steam_LinkCannotBeOpened_IsFailedWithTheError()
    {
        var platform = new FakePlatform { OpenUriError = new InvalidOperationException("no handler") };

        var result = await Launcher(platform).LaunchAsync(SteamContext(), false);

        Assert.Equal(LaunchOutcome.Failed, result.Outcome);
        Assert.Equal("no handler", result.Error);
        Assert.Equal(0, platform.Delays);
    }

    // ─── 直接运行 ───

    [Fact]
    public async Task Direct_ProcessStaysAlive_IsRunning()
    {
        var platform = new FakePlatform();

        var result = await Launcher(platform).LaunchAsync(SteamContext(), preferDirect: true);

        Assert.Equal(LaunchOutcome.Running, result.Outcome);
        Assert.Equal(new[] { SteamExe }, platform.StartedExecutables);
        Assert.Empty(platform.OpenedUris);
        Assert.Equal(4242, result.ProcessId);
        Assert.Equal(3, platform.Delays);
    }

    [Fact]
    public async Task Direct_SteamGameExitsAtOnce_IsExitedImmediatelyNotSuccess()
    {
        // 这就是玩家遇到的情况：exe 起来、被 Steam 接管、马上退出，游戏没有出现
        var process = new FakeProcess();
        var platform = new FakePlatform { ProcessToReturn = process };
        platform.OnDelay = delays => { if (delays == 1) process.HasExited = true; };
        var progress = new List<string>();

        var result = await Launcher(platform).LaunchAsync(SteamContext(), preferDirect: true, progress.Add);

        Assert.Equal(LaunchOutcome.ExitedImmediately, result.Outcome);
        Assert.True(result.Plan.CanUseSteam);
        Assert.Contains(LaunchOutcomeText.WaitingForSteamTakeOver, progress);
    }

    [Fact]
    public async Task Direct_LauncherHandsOffToRealGameProcess_IsRunning()
    {
        var process = new FakeProcess();
        var platform = new FakePlatform { ProcessToReturn = process };
        platform.OnDelay = delays => { if (delays == 1) process.HasExited = true; };
        platform.Processes = delays => delays >= 2 ? [GameProcess()] : [];

        var result = await Launcher(platform).LaunchAsync(SteamContext(), preferDirect: true);

        Assert.Equal(LaunchOutcome.Running, result.Outcome);
        Assert.Equal(2, platform.Delays);
    }

    [Fact]
    public async Task Direct_TheStartedProcessItselfIsNotMistakenForAHandOff()
    {
        // 进程刚退出时系统快照里可能还留着它：不能把它自己算成"接手的游戏进程"
        var process = new FakeProcess { Id = 4242, HasExited = true };
        var platform = new FakePlatform
        {
            ProcessToReturn = process,
            Manifests = [],
            Processes = _ => [new RunningProcessInfo(4242, "Sample", SteamExe)],
        };

        var result = await Launcher(platform).LaunchAsync(SteamContext(), preferDirect: true);

        Assert.Equal(LaunchOutcome.ExitedImmediately, result.Outcome);
        Assert.Equal(4, platform.Delays);
    }

    // ─── 整条流水线 ───

    private LaunchOrchestrator Orchestrator(FakePlatform platform, out GameConfigService game, out string sessions)
    {
        Directory.CreateDirectory(_root);
        game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(_root, "config.json"));
        var profiles = new ProfileService(NullLogger<ProfileService>.Instance, Path.Combine(_root, "Data"));
        sessions = Path.Combine(_root, "Sessions");
        return new LaunchOrchestrator(NullLogger<LaunchOrchestrator>.Instance,
            game, profiles, null!, null!, null!, null!, sessions, platform, Timings);
    }

    /// <summary>只跑"验证路径 → 验证程序 → 启动游戏"三步；磁盘上的假游戏目录带 steamapps 结构。</summary>
    private LaunchContext OnDiskSteamContext()
    {
        var install = Path.Combine(_root, "Lib", "steamapps", "common", "Sample");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "Sample.exe");
        File.WriteAllBytes(exe, []);
        return new LaunchContext
        {
            GameName = "Sample", GameRootPath = install, ExecutablePath = exe, WorkingDirectory = install,
            SkipDeployCheck = true, SkipConflictCheck = true,
        };
    }

    [Fact]
    public async Task Pipeline_SteamGame_GoesThroughSteamAndRecordsHowItWasLaunched()
    {
        var platform = new FakePlatform();
        var context = OnDiskSteamContext();
        platform.Processes = _ => [new RunningProcessInfo(5, "Sample", context.ExecutablePath)];
        var launcher = Orchestrator(platform, out _, out var sessions);

        var session = await launcher.LaunchAsync(context);

        Assert.True(session.Success);
        Assert.Equal(LaunchOutcome.Running, session.Outcome);
        Assert.Equal(GameLaunchMethod.Steam, session.LaunchMethod);
        Assert.Equal(777u, session.SteamAppId);
        Assert.Empty(platform.StartedExecutables);
        Assert.True(File.Exists(Path.Combine(sessions, $"{session.Id:N}.json")));
    }

    [Fact]
    public async Task Pipeline_ProcessExitsAtOnce_IsNotRecordedAsSuccess()
    {
        var platform = new FakePlatform { ProcessToReturn = new FakeProcess { HasExited = true } };
        var launcher = Orchestrator(platform, out var game, out _);
        await game.SetDirectLaunchPreferredAsync("Sample", true);

        var session = await launcher.LaunchAsync(OnDiskSteamContext());

        Assert.False(session.Success);
        Assert.Equal(LaunchOutcome.ExitedImmediately, session.Outcome);
        Assert.Equal(GameLaunchMethod.DirectExecutable, session.LaunchMethod);
        Assert.True(session.CanUseSteam);
        Assert.Contains("Steam", session.FailureReason);
        Assert.Equal(LaunchStepStatus.Failed, session.Steps.Single(s => s.Type == LaunchStepType.LaunchProcess).Status);
        Assert.Same(session, launcher.LastSession);
    }

    [Fact]
    public async Task Pipeline_SteamNeverShowsTheGame_IsUnconfirmedNotSuccessAndNotARedCross()
    {
        var launcher = Orchestrator(new FakePlatform(), out _, out _);

        var session = await launcher.LaunchAsync(OnDiskSteamContext());

        Assert.False(session.Success);
        Assert.Equal(LaunchOutcome.NotConfirmed, session.Outcome);
        Assert.Equal(LaunchStepStatus.Warning, session.Steps.Single(s => s.Type == LaunchStepType.LaunchProcess).Status);
        Assert.Contains("Steam 窗口", session.FailureReason);
    }

    [Fact]
    public async Task Pipeline_ClickedAgainWhileStillLaunching_DoesNotLaunchTwice()
    {
        var platform = new FakePlatform { Gate = new TaskCompletionSource() };
        var context = OnDiskSteamContext();
        platform.Processes = delays => delays >= 1 ? [new RunningProcessInfo(5, "Sample", context.ExecutablePath)] : [];
        var launcher = Orchestrator(platform, out _, out _);

        var first = launcher.LaunchAsync(context);
        var second = await launcher.LaunchAsync(context);

        Assert.False(second.Success);
        Assert.Equal(LaunchOrchestrator.LaunchInProgressMessage, second.FailureReason);
        Assert.Single(platform.OpenedUris);
        Assert.Empty(launcher.SessionHistory);

        platform.Gate.SetResult();
        var finished = await first;
        Assert.True(finished.Success);
        Assert.Same(finished, Assert.Single(launcher.SessionHistory));

        // 上一次结束后可以再启动
        platform.Gate = null;
        Assert.True((await launcher.LaunchAsync(context)).Success);
        Assert.Equal(2, platform.OpenedUris.Count);
    }

    // ─── 玩家的选择 ───

    [Fact]
    public async Task DirectLaunchPreference_IsPerGameAndSurvivesRestart()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config.json");
        var game = new GameConfigService(NullLogger<GameConfigService>.Instance, path);
        Assert.False(game.IsDirectLaunchPreferred("Sample"));

        await game.SetDirectLaunchPreferredAsync("Sample", true);
        await game.SetDirectLaunchPreferredAsync("Sample", true);

        Assert.True(game.IsDirectLaunchPreferred(" sample "));
        Assert.False(game.IsDirectLaunchPreferred("Other"));
        Assert.Single(game.Config.DirectLaunchGames);

        var reloaded = new GameConfigService(NullLogger<GameConfigService>.Instance, path);
        await reloaded.LoadConfigAsync();
        Assert.True(reloaded.IsDirectLaunchPreferred("Sample"));

        await reloaded.SetDirectLaunchPreferredAsync("Sample", false);
        Assert.False(reloaded.IsDirectLaunchPreferred("Sample"));
        Assert.Empty(reloaded.Config.DirectLaunchGames);
    }

    [Fact]
    public async Task ResolveLaunchPlan_FollowsThePlayersChoice()
    {
        var launcher = Orchestrator(new FakePlatform(), out var game, out _);
        var context = OnDiskSteamContext();

        Assert.Equal(GameLaunchMethod.Steam, launcher.ResolveLaunchPlan(context).Method);

        await game.SetDirectLaunchPreferredAsync("Sample", true);

        var plan = launcher.ResolveLaunchPlan(context);
        Assert.Equal(GameLaunchMethod.DirectExecutable, plan.Method);
        Assert.True(plan.CanUseSteam);
    }

    // ─── 文案 ───

    [Fact]
    public void EveryLaunchOutcomeMessage_HasAnEnglishTranslation()
    {
        var texts = new List<string>
        {
            LaunchOutcomeText.WaitingForSteam,
            LaunchOutcomeText.WaitingForGame,
            LaunchOutcomeText.WaitingForSteamTakeOver,
            LaunchOrchestrator.LaunchInProgressMessage,
        };
        foreach (var outcome in Enum.GetValues<LaunchOutcome>())
        foreach (var method in Enum.GetValues<GameLaunchMethod>())
        foreach (var canUseSteam in new[] { true, false })
        {
            var message = LaunchOutcomeText.Describe(outcome, method, canUseSteam);
            texts.Add(message.Title);
            if (message.Detail.Length > 0) texts.Add(message.Detail);
        }

        Assert.All(texts.Distinct(), text => Assert.True(UiText.HasEnglish(text), $"缺英文译文：{text}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
