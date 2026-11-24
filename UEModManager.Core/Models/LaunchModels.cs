using System;
using System.Collections.Generic;

namespace UEModManager.Models
{
    /// <summary>启动步骤类型。</summary>
    public enum LaunchStepType
    {
        /// <summary>检查视图是否过期。</summary>
        CheckViewFreshness,
        /// <summary>构建最终视图。</summary>
        BuildResolvedView,
        /// <summary>执行部署（如有变更）。</summary>
        Deploy,
        /// <summary>验证游戏路径。</summary>
        ValidateGamePath,
        /// <summary>验证可执行文件。</summary>
        ValidateExecutable,
        /// <summary>冲突检查。</summary>
        ConflictCheck,
        /// <summary>启动游戏进程。</summary>
        LaunchProcess,
        /// <summary>自定义前置任务。</summary>
        CustomPreTask
    }

    /// <summary>启动步骤状态。</summary>
    public enum LaunchStepStatus
    {
        Pending,
        Running,
        Passed,
        Failed,
        Skipped,
        Warning
    }

    /// <summary>游戏进程是怎么被拉起来的。</summary>
    public enum GameLaunchMethod
    {
        /// <summary>直接运行游戏的可执行文件。</summary>
        DirectExecutable,
        /// <summary>交给 Steam 客户端启动（<c>steam://</c> 链接）。</summary>
        Steam
    }

    /// <summary>发出启动动作之后，游戏实际怎么样了。</summary>
    public enum LaunchOutcome
    {
        /// <summary>还没走到启动这一步（前面的检查或部署失败）。</summary>
        NotStarted,
        /// <summary>看到游戏进程在运行。</summary>
        Running,
        /// <summary>游戏程序刚启动就退出，之后也没有游戏进程出现。</summary>
        ExitedImmediately,
        /// <summary>已交给 Steam，但在等待时间内没看到游戏进程（Steam 可能在更新、同步或等玩家确认）。</summary>
        NotConfirmed,
        /// <summary>启动动作本身失败（文件打不开、Steam 链接没人接）。</summary>
        Failed
    }

    /// <summary>
    /// 启动流水线中的单个步骤。
    /// </summary>
    public class LaunchStep
    {
        /// <summary>步骤类型。</summary>
        public LaunchStepType Type { get; init; }

        /// <summary>显示名称。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>状态。</summary>
        public LaunchStepStatus Status { get; set; } = LaunchStepStatus.Pending;

        /// <summary>状态消息。</summary>
        public string? Message { get; set; }

        /// <summary>开始时间。</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>完成时间。</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>耗时（毫秒）。</summary>
        public long? DurationMs => CompletedAt.HasValue && StartedAt.HasValue
            ? (long)(CompletedAt.Value - StartedAt.Value).TotalMilliseconds
            : null;
    }

    /// <summary>
    /// 启动上下文。包含启动游戏所需的所有信息。
    /// </summary>
    public class LaunchContext
    {
        /// <summary>游戏名称。</summary>
        public string GameName { get; init; } = "";

        /// <summary>Profile ID。</summary>
        public Guid ProfileId { get; init; }

        /// <summary>Profile 名称。</summary>
        public string ProfileName { get; init; } = "";

        /// <summary>游戏根目录。</summary>
        public string GameRootPath { get; init; } = "";

        /// <summary>可执行文件路径。</summary>
        public string ExecutablePath { get; init; } = "";

        /// <summary>工作目录。</summary>
        public string WorkingDirectory { get; init; } = "";

        /// <summary>启动参数。</summary>
        public string LaunchArguments { get; set; } = "";

        /// <summary>环境变量（追加到进程环境）。</summary>
        public Dictionary<string, string> EnvironmentVariables { get; init; } = [];

        /// <summary>关联的最终视图哈希。</summary>
        public string? ResolvedViewHash { get; set; }

        /// <summary>是否跳过部署检查（用户强制启动）。</summary>
        public bool SkipDeployCheck { get; set; }

        /// <summary>是否跳过冲突检查。</summary>
        public bool SkipConflictCheck { get; set; }
    }

    /// <summary>
    /// 启动会话记录。
    /// </summary>
    public class LaunchSession
    {
        /// <summary>会话 ID。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>游戏名称。</summary>
        public string GameName { get; init; } = "";

        /// <summary>Profile 名称。</summary>
        public string ProfileName { get; init; } = "";

        /// <summary>最终视图哈希。</summary>
        public string? ViewHash { get; set; }

        /// <summary>启动时间。</summary>
        public DateTime LaunchedAt { get; init; } = DateTime.Now;

        /// <summary>可执行文件路径。</summary>
        public string ExecutablePath { get; init; } = "";

        /// <summary>流水线步骤列表。</summary>
        public List<LaunchStep> Steps { get; init; } = [];

        /// <summary>这次用的启动方式。</summary>
        public GameLaunchMethod LaunchMethod { get; set; }

        /// <summary>经 Steam 启动时用的 appid（从 appmanifest 读出）；否则为 0。</summary>
        public uint SteamAppId { get; set; }

        /// <summary>游戏实际怎么样了。<see cref="Success"/> 只在它是 <see cref="LaunchOutcome.Running"/> 时为 true。</summary>
        public LaunchOutcome Outcome { get; set; }

        /// <summary>这款游戏能不能经 Steam 启动（界面据此给出切换启动方式的入口）。</summary>
        public bool CanUseSteam { get; set; }

        /// <summary>是否成功启动。</summary>
        public bool Success { get; set; }

        /// <summary>失败原因。</summary>
        public string? FailureReason { get; set; }

        /// <summary>进程 ID（启动成功时）。</summary>
        public int? ProcessId { get; set; }
    }
}
