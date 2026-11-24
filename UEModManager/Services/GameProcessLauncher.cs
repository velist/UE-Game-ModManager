using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Launch;
using UEModManager.Services.Steam;

namespace UEModManager.Services
{
    /// <summary>一次启动动作的结果。</summary>
    /// <param name="Outcome">游戏实际怎么样了。</param>
    /// <param name="Plan">用的启动方式。</param>
    /// <param name="ProcessId">直接启动时管理器拉起的进程 ID；经 Steam 启动没有。</param>
    /// <param name="Error">启动动作本身抛出的错误信息；没有为 null。</param>
    public sealed record GameLaunchResult(LaunchOutcome Outcome, GameLaunchPlan Plan, int? ProcessId, string? Error);

    /// <summary>
    /// 把游戏真正拉起来、并确认它起来了。
    ///
    /// 决策都在 Core（<see cref="SteamInstallLocator"/> 认 Steam 库、<see cref="GameLaunchPlanner"/> 选启动方式、
    /// <see cref="LaunchWatchPolicy"/> 判定结果），这里只做 IO：读清单、打开链接、起进程、轮询进程。
    /// </summary>
    public sealed class GameProcessLauncher
    {
        private readonly ILogger _logger;
        private readonly IGameLaunchPlatform _platform;
        private readonly LaunchWatchTimings _timings;

        public GameProcessLauncher(ILogger logger, IGameLaunchPlatform platform, LaunchWatchTimings? timings = null)
        {
            _logger = logger;
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _timings = timings ?? LaunchWatchTimings.Default;
        }

        /// <summary>
        /// 这款游戏这一次该怎么启动。不启动任何东西，界面也用它来显示"启动方式"。
        /// </summary>
        public GameLaunchPlan ResolvePlan(LaunchContext context, bool preferDirect)
        {
            ArgumentNullException.ThrowIfNull(context);

            uint appId = 0;
            var location = LocateSteamInstall(context);
            if (location != null)
            {
                var owner = SteamInstallLocator.FindOwner(location, _platform.ReadAppManifests(location.SteamAppsDirectory));
                appId = owner?.AppId ?? 0;
            }

            return GameLaunchPlanner.Decide(new GameLaunchFacts(
                appId,
                // 不在 Steam 库里就不用去查注册表
                appId != 0 && _platform.IsSteamClientAvailable(),
                preferDirect,
                context.EnvironmentVariables.Count > 0,
                context.LaunchArguments));
        }

        /// <summary>
        /// 启动并观察，直到能下结论。<paramref name="progress"/> 收到的是给玩家看的等待说明（固定句子，可直接查译文）。
        /// 不抛异常：启动动作失败以 <see cref="LaunchOutcome.Failed"/> 返回。
        /// </summary>
        public async Task<GameLaunchResult> LaunchAsync(LaunchContext context, bool preferDirect, Action<string>? progress = null)
        {
            var plan = ResolvePlan(context, preferDirect);
            var gameDirectory = LocateSteamInstall(context)?.InstallPath ?? context.GameRootPath;
            var executableName = Path.GetFileName(context.ExecutablePath);

            IStartedProcess? starter = null;
            try
            {
                if (plan.Method == GameLaunchMethod.Steam)
                {
                    _platform.OpenUri(plan.SteamUri!);
                    _logger.LogInformation("Game launch handed to Steam: appid {AppId}", plan.SteamAppId);
                    progress?.Invoke(LaunchOutcomeText.WaitingForSteam);
                }
                else
                {
                    starter = _platform.StartExecutable(
                        context.ExecutablePath, context.WorkingDirectory, context.LaunchArguments, context.EnvironmentVariables);
                    _logger.LogInformation("Game process started directly: PID {Pid}, {Path}",
                        starter?.Id, context.ExecutablePath);
                    progress?.Invoke(LaunchOutcomeText.WaitingForGame);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Game launch action failed ({Method})", plan.Method);
                return new GameLaunchResult(LaunchOutcome.Failed, plan, null, ex.Message);
            }

            var outcome = await WatchAsync(plan, starter, gameDirectory, executableName, progress);
            _logger.LogInformation("Game launch outcome: {Outcome} ({Method})", outcome, plan.Method);
            return new GameLaunchResult(outcome, plan, starter?.Id, null);
        }

        private async Task<LaunchOutcome> WatchAsync(
            GameLaunchPlan plan, IStartedProcess? starter, string gameDirectory, string executableName, Action<string>? progress)
        {
            var elapsed = TimeSpan.Zero;
            TimeSpan? starterExitedAt = plan.Method == GameLaunchMethod.DirectExecutable && starter == null
                ? TimeSpan.Zero
                : null;
            var takeOverAnnounced = false;

            while (true)
            {
                var starterAlive = starter != null && !starter.HasExited;
                if (plan.Method == GameLaunchMethod.DirectExecutable && !starterAlive)
                    starterExitedAt ??= elapsed;

                // 启动的那个进程还活着时不必枚举全部进程；枚举放到后台线程，等待期间界面不卡
                var starterId = starter?.Id;
                var seen = !starterAlive
                           && await Task.Run(() => AnyGameProcess(gameDirectory, executableName, starterId));

                var decision = LaunchWatchPolicy.Decide(
                    new LaunchWatchSnapshot(
                        plan.Method, elapsed, starterAlive,
                        starterExitedAt.HasValue ? elapsed - starterExitedAt.Value : null,
                        seen, plan.CanUseSteam),
                    _timings);
                if (decision.HasValue) return decision.Value;

                if (!takeOverAnnounced && plan.Method == GameLaunchMethod.DirectExecutable
                    && starterExitedAt.HasValue && plan.CanUseSteam)
                {
                    takeOverAnnounced = true;
                    progress?.Invoke(LaunchOutcomeText.WaitingForSteamTakeOver);
                }

                await _platform.DelayAsync(_timings.PollInterval);
                elapsed += _timings.PollInterval;
            }
        }

        private bool AnyGameProcess(string gameDirectory, string executableName, int? excludeProcessId)
        {
            try
            {
                return _platform.SnapshotProcesses().Any(process =>
                    process.Id != excludeProcessId
                    && GameProcessMatcher.IsGameProcess(process, gameDirectory, executableName));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Process snapshot failed");
                return false;
            }
        }

        private static SteamInstallLocation? LocateSteamInstall(LaunchContext context)
            => SteamInstallLocator.Locate(context.ExecutablePath) ?? SteamInstallLocator.Locate(context.GameRootPath);
    }
}
