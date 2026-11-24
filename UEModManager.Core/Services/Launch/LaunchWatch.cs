using System;
using UEModManager.Models;

namespace UEModManager.Services.Launch
{
    /// <summary>一个正在运行的进程（由主程序枚举后传入）。</summary>
    /// <param name="Id">进程 ID。</param>
    /// <param name="Name">进程名，不带 <c>.exe</c>。</param>
    /// <param name="ExecutablePath">可执行文件完整路径；读不到（权限不够、受保护进程）时为 null。</param>
    public sealed record RunningProcessInfo(int Id, string Name, string? ExecutablePath);

    /// <summary>
    /// 「这个进程是不是我们要启动的那款游戏」（纯函数）。
    ///
    /// 启动器进程往往一闪而过（UE 游戏根目录的 <c>Game.exe</c> 只负责拉起
    /// <c>Game-Win64-Shipping.exe</c>），经 Steam 启动时更是拿不到任何 PID，
    /// 所以判断"游戏起来没有"只能看：有没有从游戏安装目录里跑起来的进程。
    /// </summary>
    public static class GameProcessMatcher
    {
        /// <param name="process">被检查的进程。</param>
        /// <param name="gameDirectory">游戏安装根目录。</param>
        /// <param name="executableName">游戏可执行文件名（带不带 <c>.exe</c> 都行），读不到进程路径时按名字兜底。</param>
        public static bool IsGameProcess(RunningProcessInfo process, string? gameDirectory, string? executableName)
        {
            ArgumentNullException.ThrowIfNull(process);

            if (!string.IsNullOrEmpty(process.ExecutablePath))
                return IsUnder(process.ExecutablePath, gameDirectory);

            // 读不到路径：只认"同名"或"同名加后缀"（PBZ → PBZ-Win64-Shipping），不做包含匹配
            var baseName = StripExe(executableName);
            if (baseName.Length == 0 || string.IsNullOrEmpty(process.Name)) return false;
            var name = StripExe(process.Name);
            return string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnder(string path, string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            var root = directory.Trim().Replace('/', '\\').TrimEnd('\\') + "\\";
            // 只有盘符的 "D:\" 会把整个盘都算成游戏目录
            if (root.Length <= 3) return false;
            return path.Replace('/', '\\').StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string StripExe(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var trimmed = name.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(0, trimmed.Length - 4)
                : trimmed;
        }
    }

    /// <summary>启动后观察游戏进程用的几个时长。</summary>
    public sealed record LaunchWatchTimings
    {
        /// <summary>两次查看进程之间的间隔。</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

        /// <summary>直接启动：进程活过这么久就算起来了（秒退的游戏通常 1~2 秒内就没了）。</summary>
        public TimeSpan DirectSettle { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>直接启动：启动器退出后，再等这么久看它有没有拉起真正的游戏进程。</summary>
        public TimeSpan HandOffGrace { get; init; } = TimeSpan.FromSeconds(6);

        /// <summary>
        /// 等 Steam 把游戏拉起来的上限。Steam 自己可能还要启动、同步云存档，所以给得宽；
        /// 到点仍没看到游戏只说明"没确认"，不说明失败。
        /// </summary>
        public TimeSpan SteamTimeout { get; init; } = TimeSpan.FromSeconds(40);

        public static LaunchWatchTimings Default { get; } = new();
    }

    /// <summary>某一时刻看到的情况。</summary>
    /// <param name="Method">这次用的启动方式。</param>
    /// <param name="Elapsed">发出启动动作后过了多久。</param>
    /// <param name="StarterAlive">直接启动的那个进程还活着（经 Steam 启动恒为 false）。</param>
    /// <param name="SinceStarterExit">直接启动的那个进程已退出多久；还活着或没有这个进程时为 null。</param>
    /// <param name="GameProcessSeen">此刻有（除启动的那个之外的）游戏进程在运行。</param>
    /// <param name="SteamMayTakeOver">游戏在 Steam 库里且本机有 Steam：直接启动的进程退出后，Steam 可能正在接手重新拉起。</param>
    public sealed record LaunchWatchSnapshot(
        GameLaunchMethod Method,
        TimeSpan Elapsed,
        bool StarterAlive,
        TimeSpan? SinceStarterExit,
        bool GameProcessSeen,
        bool SteamMayTakeOver);

    /// <summary>
    /// 启动后"游戏到底起来没有"的判定（纯函数）。
    ///
    /// 旧逻辑只要 <c>Process.Start</c> 返回了进程就记成功，那个进程一秒后退出也不知道，
    /// 玩家看到的是"启动成功"而游戏没出现。现在主程序每隔一小会儿把看到的情况交进来，
    /// 这里给出结论；还下不了结论时返回 null，表示继续看。
    /// </summary>
    public static class LaunchWatchPolicy
    {
        public static LaunchOutcome? Decide(LaunchWatchSnapshot snapshot, LaunchWatchTimings timings)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(timings);

            if (snapshot.Method == GameLaunchMethod.Steam)
            {
                if (snapshot.GameProcessSeen) return LaunchOutcome.Running;
                return snapshot.Elapsed >= timings.SteamTimeout ? LaunchOutcome.NotConfirmed : null;
            }

            if (snapshot.StarterAlive)
                return snapshot.Elapsed >= timings.DirectSettle ? LaunchOutcome.Running : null;

            // 启动的那个进程已经没了：有别的游戏进程在跑，说明它只是启动器，交接成功
            if (snapshot.GameProcessSeen) return LaunchOutcome.Running;

            var sinceExit = snapshot.SinceStarterExit ?? snapshot.Elapsed;
            var wait = snapshot.SteamMayTakeOver ? timings.SteamTimeout : timings.HandOffGrace;
            return sinceExit >= wait ? LaunchOutcome.ExitedImmediately : null;
        }
    }

    /// <summary>给玩家看的启动结论：一句标题、一段说明、一个建议动作。</summary>
    /// <param name="Title">短标题。</param>
    /// <param name="Detail">发生了什么、接下来怎么办；成功时为空串。</param>
    /// <param name="Advice">界面可以一键代办的下一步。</param>
    public sealed record LaunchOutcomeMessage(string Title, string Detail, LaunchAdvice Advice);

    /// <summary>界面可以替玩家做的下一步。</summary>
    public enum LaunchAdvice
    {
        None,
        /// <summary>改为通过 Steam 启动再试。</summary>
        SwitchToSteam,
        /// <summary>改为直接运行游戏程序再试。</summary>
        SwitchToDirect
    }

    /// <summary>
    /// 启动结论的文案（纯函数）。文案都是固定句子、不拼变量，主程序可以直接拿去查译文。
    /// </summary>
    public static class LaunchOutcomeText
    {
        public const string WaitingForSteam = "已通知 Steam 启动游戏，正在等游戏出现…";
        public const string WaitingForGame = "正在确认游戏是否已经运行…";
        public const string WaitingForSteamTakeOver = "游戏程序自己退出了，正在看 Steam 是否接手启动…";

        /// <param name="outcome">启动结论。</param>
        /// <param name="method">这次用的启动方式。</param>
        /// <param name="canUseSteam">这款游戏能不能经 Steam 启动。</param>
        public static LaunchOutcomeMessage Describe(LaunchOutcome outcome, GameLaunchMethod method, bool canUseSteam)
        {
            switch (outcome)
            {
                case LaunchOutcome.Running:
                    return new LaunchOutcomeMessage("启动成功", string.Empty, LaunchAdvice.None);

                case LaunchOutcome.NotConfirmed:
                    return new LaunchOutcomeMessage(
                        "已通知 Steam，但还没看到游戏",
                        "请看一下 Steam 窗口：可能在更新游戏、同步云存档，或者弹了需要你点确认的提示。Steam 一直没反应的话，可以改为直接运行游戏程序再试。",
                        LaunchAdvice.SwitchToDirect);

                case LaunchOutcome.ExitedImmediately:
                    return method == GameLaunchMethod.DirectExecutable && canUseSteam
                        ? new LaunchOutcomeMessage(
                            "游戏没有启动起来",
                            "游戏程序刚打开就自己退出了。这款游戏装在 Steam 里，通常需要由 Steam 来启动。可以改为通过 Steam 启动再试。",
                            LaunchAdvice.SwitchToSteam)
                        : new LaunchOutcomeMessage(
                            "游戏没有启动起来",
                            "游戏程序刚打开就自己退出了。请先从游戏平台（Steam、Epic 等）手动启动一次，确认游戏本身能正常运行；如果刚装了新的 MOD，试着先禁用它。",
                            LaunchAdvice.None);

                default:
                    return new LaunchOutcomeMessage(
                        "启动失败",
                        "没能把游戏启动起来。请确认游戏路径和游戏程序是否正确。",
                        LaunchAdvice.None);
            }
        }
    }
}
