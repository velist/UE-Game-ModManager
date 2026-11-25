using UEModManager.Localization;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Conflict;
using UEModManager.Services.Launch;

namespace UEModManager.ViewModels
{
    public partial class LaunchViewModel : ObservableObject
    {
        private readonly LaunchOrchestrator _launcher;
        private readonly ProfileService _profileService;
        private readonly GameConfigService _gameConfig;
        private readonly ConflictModuleService _conflictModule;
        private readonly ILogger _logger;

        public ObservableCollection<LaunchStepItem> Steps { get; } = new();

        [ObservableProperty]
        private string _gameName = "";

        [ObservableProperty]
        private string _profileName = "";

        [ObservableProperty]
        private string _statusTitle = UiText.Get("准备启动");

        [ObservableProperty]
        private string _statusSubtitle = "";

        [ObservableProperty]
        private bool _isLaunching;

        [ObservableProperty]
        private bool _canLaunch = true;

        [ObservableProperty]
        private string _lastLaunchInfo = "";

        [ObservableProperty]
        private string _modCountInfo = "";

        [ObservableProperty]
        private string _lastEventInfo = UiText.Get("无异常");

        [ObservableProperty]
        private int _enabledPackageCount;

        /// <summary>
        /// 冲突组数 —— 面板上有几个框，就是用户要做几个决策。
        ///
        /// 旧模型报的是"582 个文件冲突"（目标路径级），用户数的是自己的 MOD 个数，
        /// 两边永远对不上。组数才是可核对的数字。
        /// </summary>
        [ObservableProperty]
        private int _conflictGroupCount;

        /// <summary>
        /// 冲突预检的失败原因；非空表示"冲突数未知"。
        ///
        /// 不能只靠 <see cref="ConflictGroupCount"/>：预检失败时它停在 0，
        /// 而 0 在界面上被渲染成绿色的"无 MOD 冲突"——"没检查成功"被显示成"检查通过"，
        /// 比直接报错危险得多。界面另外监听这个属性来重画检查清单。
        /// </summary>
        [ObservableProperty]
        private string _conflictPreCheckError = string.Empty;

        /// <summary>这次会怎么启动（"通过 Steam 启动" / "直接运行游戏程序"），给玩家看的一句话。</summary>
        [ObservableProperty]
        private string _launchMethodInfo = "";

        /// <summary>切换启动方式的链接文字；这款游戏不能经 Steam 启动时为空串（界面隐藏入口）。</summary>
        [ObservableProperty]
        private string _switchLaunchMethodText = "";

        /// <summary>最近一次启动的结论给出的可代办下一步。</summary>
        public LaunchAdvice LastAdvice { get; private set; } = LaunchAdvice.None;

        public LaunchViewModel(
            LaunchOrchestrator launcher,
            ProfileService profileService,
            GameConfigService gameConfig,
            ConflictModuleService conflictModule,
            ILogger logger)
        {
            _launcher = launcher;
            _profileService = profileService;
            _gameConfig = gameConfig;
            _conflictModule = conflictModule;
            _logger = logger;
        }

        public void Initialize()
        {
            GameName = _gameConfig.CurrentGameName;
            var profile = _profileService.CurrentProfile;
            ProfileName = profile?.Name ?? UiText.Get("默认");
            StatusSubtitle = UiText.Interpolate($"切换 {GameDisplayNames.For(GameName)} — {ProfileName}");

            // 统计已启用包
            EnabledPackageCount = profile?.Packages?.Count(p => p.IsEnabled) ?? 0;
            ModCountInfo = UiText.Interpolate($"{EnabledPackageCount} 个包已启用");

            // 冲突统计
            ConflictGroupCount = 0;
            ConflictPreCheckError = string.Empty;

            // 上次启动信息
            var last = _launcher.LastSession;
            if (last != null)
            {
                var ago = DateTime.Now - last.LaunchedAt;
                LastLaunchInfo = ago.TotalMinutes < 60
                    ? UiText.Interpolate($"上次启动 {(int)ago.TotalMinutes} 分钟前")
                    : ago.TotalHours < 24
                        ? UiText.Interpolate($"上次启动 {(int)ago.TotalHours} 小时前")
                        : UiText.Interpolate($"上次启动 {last.LaunchedAt:MM/dd HH:mm}");
                // 上次启动前装回过丢失的 MOD 文件的话，这里接着显示，免得窗口一关这句话就没了
                LastEventInfo = last.Success ? RestoredNotice(last) ?? UiText.Get("无异常") : ShortFailure(last);
            }
            else
            {
                LastLaunchInfo = UiText.Get("首次启动");
            }

            BuildPreCheckSteps();
            RefreshLaunchMethod();
        }

        /// <summary>
        /// 重新判断这款游戏会怎么启动，并更新界面上的说明与切换入口。
        /// 只读 Steam 清单，不启动任何东西；读不出来就按"直接运行"显示。
        /// </summary>
        public void RefreshLaunchMethod()
        {
            try
            {
                var plan = _launcher.ResolveLaunchPlan();
                LaunchMethodInfo = plan.Method == GameLaunchMethod.Steam
                    ? UiText.Get("启动方式：通过 Steam 启动")
                    : UiText.Get("启动方式：直接运行游戏程序");
                SwitchLaunchMethodText = !plan.CanUseSteam
                    ? ""
                    : plan.Method == GameLaunchMethod.Steam
                        ? UiText.Get("改为直接运行游戏程序")
                        : UiText.Get("改为通过 Steam 启动");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "判断启动方式失败");
                LaunchMethodInfo = UiText.Get("启动方式：直接运行游戏程序");
                SwitchLaunchMethodText = "";
            }
        }

        /// <summary>在「通过 Steam 启动」与「直接运行游戏程序」之间切换，并记住这款游戏的选择。</summary>
        public async Task ToggleLaunchMethodAsync()
        {
            if (IsLaunching) return;
            await _gameConfig.SetDirectLaunchPreferredAsync(
                GameName, !_gameConfig.IsDirectLaunchPreferred(GameName));
            RefreshLaunchMethod();
        }

        /// <summary>按建议切换启动方式（失败提示里的"一键改用另一种方式"）。</summary>
        public async Task ApplyAdviceAsync(LaunchAdvice advice)
        {
            if (advice == LaunchAdvice.None) return;
            await _gameConfig.SetDirectLaunchPreferredAsync(GameName, advice == LaunchAdvice.SwitchToDirect);
            RefreshLaunchMethod();
        }

        private static LaunchOutcomeMessage OutcomeMessage(LaunchSession session)
            => LaunchOutcomeText.Describe(session.Outcome, session.LaunchMethod, session.CanUseSteam);

        /// <summary>底部状态栏放得下的一句失败说明。</summary>
        private static string ShortFailure(LaunchSession session)
            => session.Outcome == LaunchOutcome.NotStarted
                ? session.FailureReason ?? UiText.Get("启动失败")
                : UiText.Get(OutcomeMessage(session).Title);

        private void BuildPreCheckSteps()
        {
            Steps.Clear();

            var context = _launcher.BuildContext();

            // 1. 游戏路径验证
            bool pathValid = !string.IsNullOrEmpty(context.GameRootPath)
                             && System.IO.Directory.Exists(context.GameRootPath);
            Steps.Add(new LaunchStepItem
            {
                DisplayName = UiText.Get("游戏路径已验证"),
                Message = pathValid ? context.GameRootPath : UiText.Get("路径未设置或不存在"),
                Status = pathValid ? StepItemStatus.Passed : StepItemStatus.Failed
            });

            // 2. 已启用文件数
            Steps.Add(new LaunchStepItem
            {
                DisplayName = UiText.Interpolate($"{EnabledPackageCount} 个文件已启用"),
                Message = EnabledPackageCount > 0 ? UiText.Get("就绪") : UiText.Get("无已启用的包"),
                Status = EnabledPackageCount > 0 ? StepItemStatus.Passed : StepItemStatus.Warning
            });

            // 3. 冲突检查（快速预检）
            Steps.Add(ConflictStep());

            // 4. 配置有效性
            bool exeValid = !string.IsNullOrEmpty(context.ExecutablePath)
                            && System.IO.File.Exists(context.ExecutablePath);
            Steps.Add(new LaunchStepItem
            {
                DisplayName = UiText.Get("验证配置有效性"),
                Message = exeValid ? System.IO.Path.GetFileName(context.ExecutablePath) : UiText.Get("可执行文件未找到"),
                Status = exeValid ? StepItemStatus.Passed : StepItemStatus.Failed
            });

            // 判断能否启动
            CanLaunch = Steps.All(s => s.Status != StepItemStatus.Failed);
        }

        public async Task RunConflictPreCheckAsync()
        {
            try
            {
                var report = await _conflictModule.AnalyzeAsync();
                ConflictGroupCount = report.Groups.Count;

                // 更新冲突步骤
                if (Steps.Count >= 3)
                    Steps[2] = ConflictStep();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "冲突预检失败");

                // 只记日志是不够的：BuildPreCheckSteps 把第 3 步预置成了绿色的
                // "无 MOD 冲突"，预检抛异常后它会一直停在那儿，
                // 用户看到的是"冲突检查通过"。冲突数未知必须显式降级成警告——
                // 但不用 Failed，冲突分析只是启动前的提示，不该把游戏拦下来。
                if (Steps.Count >= 3)
                {
                    Steps[2] = new LaunchStepItem
                    {
                        DisplayName = UiText.Get("冲突预检未完成"),
                        Message = UiText.Interpolate($"无法确认是否存在文件冲突：{ex.Message}"),
                        Status = StepItemStatus.Warning
                    };
                }

                // 放在最后：属性变更会触发界面重画清单，此时 Steps[2] 必须已经更新
                ConflictPreCheckError = ex.Message;
            }
        }

        /// <summary>
        /// 冲突检查步骤的渲染。
        ///
        /// 两个位置（预检占位与预检结果）共用一份文案，避免"占位时写 A、
        /// 结果时写 B"导致同一状态在不同时刻显示不同的话。
        /// </summary>
        private LaunchStepItem ConflictStep()
        {
            var groups = ConflictGroupCount;

            // 摘要里点名前 3 组的成员，让用户不用打开面板就知道跟哪几个 MOD 有关。
            var named = _conflictModule.LastReport?.Groups
                .Take(3)
                .Select(g => string.Join("、", g.Members.Select(m => m.DisplayName)))
                .ToList() ?? [];

            var message = groups == 0 ? string.Empty : UiText.Interpolate($"涉及：{string.Join("；", named)}");

            return new LaunchStepItem
            {
                DisplayName = groups > 0
                    ? UiText.Interpolate($"{groups} 组 MOD 冲突待处理")
                    : UiText.Get("无 MOD 冲突"),
                Message = message,
                Status = groups > 0 ? StepItemStatus.Warning : StepItemStatus.Passed
            };
        }

        public async Task<LaunchSession?> LaunchGameAsync()
        {
            if (IsLaunching || !CanLaunch) return null;

            IsLaunching = true;
            CanLaunch = false;
            LastAdvice = LaunchAdvice.None;
            StatusTitle = UiText.Get("正在启动...");
            // 上一次失败的说明不要留到这一次
            StatusSubtitle = "";

            try
            {
                // 订阅步骤事件
                _launcher.StepChanged += OnStepChanged;

                var session = await _launcher.LaunchAsync();

                if (session.Success)
                {
                    StatusTitle = UiText.Get("启动成功");
                    var launched = session.LaunchMethod == GameLaunchMethod.Steam
                        ? UiText.Get("游戏已通过 Steam 启动")
                        : $"PID: {session.ProcessId}";
                    // 启动前把丢失的 MOD 文件装回去了：这件事以前悄悄发生，现在说一声
                    // （窗口随后就关，主窗口重新激活时还会用轻提示再说一遍）
                    var restored = RestoredNotice(session);
                    StatusSubtitle = restored == null ? launched : restored + Environment.NewLine + launched;
                    LastEventInfo = restored ?? UiText.Get("无异常");
                }
                else if (session.Outcome != LaunchOutcome.NotStarted)
                {
                    // 走到了"启动游戏"这一步但游戏没起来：给玩家看发生了什么、下一步怎么办
                    var message = OutcomeMessage(session);
                    StatusTitle = UiText.Get(message.Title);
                    StatusSubtitle = session.Outcome == LaunchOutcome.Failed && !string.IsNullOrEmpty(session.FailureReason)
                        ? session.FailureReason
                        : UiText.Get(message.Detail);
                    LastEventInfo = UiText.Get(message.Title);
                    LastAdvice = message.Advice;
                }
                else
                {
                    StatusTitle = UiText.Get("启动失败");
                    StatusSubtitle = session.FailureReason ?? UiText.Get("未知错误");
                    LastEventInfo = session.FailureReason ?? UiText.Get("启动失败");
                }

                return session;
            }
            catch (Exception ex)
            {
                StatusTitle = UiText.Get("启动异常");
                StatusSubtitle = ex.Message;
                _logger.LogError(ex, "启动游戏异常");
                return null;
            }
            finally
            {
                _launcher.StepChanged -= OnStepChanged;
                IsLaunching = false;
                CanLaunch = true;
            }
        }

        /// <summary>这次启动前有 MOD 的文件丢了并已装回时的一句话；没有这种情况返回 null。</summary>
        private static string? RestoredNotice(LaunchSession session)
            => session.RestoredPackages.Count == 0
                ? null
                : DiskRefreshText.RestoredBeforeLaunch(session.RestoredPackages.Count);

        private void OnStepChanged(LaunchStep step)
        {
            // 在 UI 线程更新
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                // 查找对应步骤项并更新
                var displayMap = step.Type switch
                {
                    LaunchStepType.ValidateGamePath => 0,
                    LaunchStepType.ValidateExecutable => 3,
                    LaunchStepType.BuildResolvedView => 1,
                    LaunchStepType.Deploy => 1,
                    LaunchStepType.ConflictCheck => 2,
                    LaunchStepType.LaunchProcess => -1,
                    _ => -1
                };

                if (displayMap >= 0 && displayMap < Steps.Count)
                {
                    Steps[displayMap] = new LaunchStepItem
                    {
                        DisplayName = step.DisplayName,
                        Message = step.Message ?? "",
                        Status = step.Status switch
                        {
                            LaunchStepStatus.Passed => StepItemStatus.Passed,
                            LaunchStepStatus.Running => StepItemStatus.Running,
                            LaunchStepStatus.Failed => StepItemStatus.Failed,
                            LaunchStepStatus.Warning => StepItemStatus.Warning,
                            LaunchStepStatus.Skipped => StepItemStatus.Passed,
                            _ => StepItemStatus.Pending
                        }
                    };
                }

                if (step.Status == LaunchStepStatus.Running)
                    StatusTitle = UiText.Interpolate($"正在{step.DisplayName}...");

                // 等游戏出现可能要几十秒：把"现在在等什么"写在标题下面，免得玩家以为卡住了又去点
                if (step.Type == LaunchStepType.LaunchProcess && step.Status == LaunchStepStatus.Running
                    && !string.IsNullOrEmpty(step.Message))
                    StatusSubtitle = UiText.Get(step.Message);
            });
        }
    }

    public enum StepItemStatus
    {
        Pending,
        Running,
        Passed,
        Failed,
        Warning
    }

    public class LaunchStepItem
    {
        public string DisplayName { get; init; } = "";
        public string Message { get; init; } = "";
        public StepItemStatus Status { get; init; } = StepItemStatus.Pending;
    }
}
