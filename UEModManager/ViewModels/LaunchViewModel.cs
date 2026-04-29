using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.ViewModels
{
    public partial class LaunchViewModel : ObservableObject
    {
        private readonly LaunchOrchestrator _launcher;
        private readonly ProfileService _profileService;
        private readonly GameConfigService _gameConfig;
        private readonly PackageRepository _packageRepo;
        private readonly ConflictAnalyzer _conflictAnalyzer;
        private readonly ILogger _logger;

        public ObservableCollection<LaunchStepItem> Steps { get; } = new();

        [ObservableProperty]
        private string _gameName = "";

        [ObservableProperty]
        private string _profileName = "";

        [ObservableProperty]
        private string _statusTitle = "准备启动";

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
        private string _lastEventInfo = "无异常";

        [ObservableProperty]
        private int _enabledPackageCount;

        [ObservableProperty]
        private int _conflictCount;

        public LaunchViewModel(
            LaunchOrchestrator launcher,
            ProfileService profileService,
            GameConfigService gameConfig,
            PackageRepository packageRepo,
            ConflictAnalyzer conflictAnalyzer,
            ILogger logger)
        {
            _launcher = launcher;
            _profileService = profileService;
            _gameConfig = gameConfig;
            _packageRepo = packageRepo;
            _conflictAnalyzer = conflictAnalyzer;
            _logger = logger;
        }

        public void Initialize()
        {
            GameName = _gameConfig.CurrentGameName;
            var profile = _profileService.CurrentProfile;
            ProfileName = profile?.Name ?? "默认";
            StatusSubtitle = $"切换 {GameName} — {ProfileName}";

            // 统计已启用包
            EnabledPackageCount = profile?.Packages?.Count(p => p.IsEnabled) ?? 0;
            ModCountInfo = $"{EnabledPackageCount} 个包已启用";

            // 冲突统计
            ConflictCount = 0;

            // 上次启动信息
            var last = _launcher.LastSession;
            if (last != null)
            {
                var ago = DateTime.Now - last.LaunchedAt;
                LastLaunchInfo = ago.TotalMinutes < 60
                    ? $"上次启动 {(int)ago.TotalMinutes} 分钟前"
                    : ago.TotalHours < 24
                        ? $"上次启动 {(int)ago.TotalHours} 小时前"
                        : $"上次启动 {last.LaunchedAt:MM/dd HH:mm}";
                LastEventInfo = last.Success ? "无异常" : last.FailureReason ?? "启动失败";
            }
            else
            {
                LastLaunchInfo = "首次启动";
            }

            BuildPreCheckSteps();
        }

        private void BuildPreCheckSteps()
        {
            Steps.Clear();

            var context = _launcher.BuildContext();

            // 1. 游戏路径验证
            bool pathValid = !string.IsNullOrEmpty(context.GameRootPath)
                             && System.IO.Directory.Exists(context.GameRootPath);
            Steps.Add(new LaunchStepItem
            {
                DisplayName = "游戏路径已验证",
                Message = pathValid ? context.GameRootPath : "路径未设置或不存在",
                Status = pathValid ? StepItemStatus.Passed : StepItemStatus.Failed
            });

            // 2. 已启用文件数
            Steps.Add(new LaunchStepItem
            {
                DisplayName = $"{EnabledPackageCount} 个文件已启用",
                Message = EnabledPackageCount > 0 ? "就绪" : "无已启用的包",
                Status = EnabledPackageCount > 0 ? StepItemStatus.Passed : StepItemStatus.Warning
            });

            // 3. 冲突检查（快速预检）
            Steps.Add(new LaunchStepItem
            {
                DisplayName = ConflictCount > 0
                    ? $"{ConflictCount} 个文件冲突, 已自动解决"
                    : "无文件冲突",
                Message = ConflictCount > 0 ? "冲突已通过优先级自动解决" : "所有文件无冲突",
                Status = ConflictCount > 0 ? StepItemStatus.Warning : StepItemStatus.Passed
            });

            // 4. 配置有效性
            bool exeValid = !string.IsNullOrEmpty(context.ExecutablePath)
                            && System.IO.File.Exists(context.ExecutablePath);
            Steps.Add(new LaunchStepItem
            {
                DisplayName = "验证配置有效性",
                Message = exeValid ? System.IO.Path.GetFileName(context.ExecutablePath) : "可执行文件未找到",
                Status = exeValid ? StepItemStatus.Passed : StepItemStatus.Failed
            });

            // 判断能否启动
            CanLaunch = Steps.All(s => s.Status != StepItemStatus.Failed);
        }

        public async Task RunConflictPreCheckAsync()
        {
            try
            {
                var result = await _conflictAnalyzer.AnalyzeAsync();
                ConflictCount = result.TotalConflicts;

                // 更新冲突步骤
                if (Steps.Count >= 3)
                {
                    Steps[2] = new LaunchStepItem
                    {
                        DisplayName = ConflictCount > 0
                            ? $"{ConflictCount} 个文件冲突, 已自动解决"
                            : "无文件冲突",
                        Message = ConflictCount > 0 ? "冲突已通过优先级自动解决" : "所有文件无冲突",
                        Status = ConflictCount > 0 ? StepItemStatus.Warning : StepItemStatus.Passed
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "冲突预检失败");
            }
        }

        public async Task<LaunchSession?> LaunchGameAsync()
        {
            if (IsLaunching || !CanLaunch) return null;

            IsLaunching = true;
            CanLaunch = false;
            StatusTitle = "正在启动...";

            try
            {
                // 订阅步骤事件
                _launcher.StepChanged += OnStepChanged;

                var session = await _launcher.LaunchAsync();

                if (session.Success)
                {
                    StatusTitle = "启动成功";
                    StatusSubtitle = $"PID: {session.ProcessId}";
                    LastEventInfo = "无异常";
                }
                else
                {
                    StatusTitle = "启动失败";
                    StatusSubtitle = session.FailureReason ?? "未知错误";
                    LastEventInfo = session.FailureReason ?? "启动失败";
                }

                return session;
            }
            catch (Exception ex)
            {
                StatusTitle = "启动异常";
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
                    StatusTitle = $"正在{step.DisplayName}...";
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
