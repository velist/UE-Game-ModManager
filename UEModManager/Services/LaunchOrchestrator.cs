using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services.Conflict;
using UEModManager.Services.Launch;
using UEModManager.Services.Persistence;

namespace UEModManager.Services
{
    /// <summary>
    /// 启动编排器。
    /// 统一启动入口：预检 → 构建视图 → 部署 → 启动进程 → 记录会话。
    /// 从"静态管理器"升级为"运行环境管理器"。
    /// </summary>
    public class LaunchOrchestrator
    {
        private readonly ILogger<LaunchOrchestrator> _logger;
        private readonly GameConfigService _gameConfig;
        private readonly ProfileService _profileService;
        private readonly ResolvedViewBuilder _viewBuilder;
        private readonly DeploymentPlanner _deployPlanner;
        private readonly DeploymentService _deployService;
        private readonly ConflictModuleService _conflictModule;
        private readonly string _sessionLogDir;
        private readonly GameProcessLauncher _processLauncher;
        private int _launching;

        /// <summary>启动步骤进度变化事件。</summary>
        public event Action<LaunchStep>? StepChanged;

        /// <summary>最近一次启动会话。</summary>
        public LaunchSession? LastSession { get; private set; }

        /// <summary>历史会话列表（内存缓存，最多 20 条）。</summary>
        public List<LaunchSession> SessionHistory { get; } = [];

        public LaunchOrchestrator(
            ILogger<LaunchOrchestrator> logger,
            GameConfigService gameConfig,
            ProfileService profileService,
            ResolvedViewBuilder viewBuilder,
            DeploymentPlanner deployPlanner,
            DeploymentService deployService,
            ConflictModuleService conflictModule)
            : this(logger, gameConfig, profileService, viewBuilder, deployPlanner,
                deployService, conflictModule, AppPaths.LaunchSessionsDirectory)
        {
        }

        /// <summary>允许测试将启动记录写入独立目录；DI 使用不含 string 的构造函数。</summary>
        public LaunchOrchestrator(
            ILogger<LaunchOrchestrator> logger,
            GameConfigService gameConfig,
            ProfileService profileService,
            ResolvedViewBuilder viewBuilder,
            DeploymentPlanner deployPlanner,
            DeploymentService deployService,
            ConflictModuleService conflictModule,
            string sessionLogDirectory)
            : this(logger, gameConfig, profileService, viewBuilder, deployPlanner,
                deployService, conflictModule, sessionLogDirectory,
                new WindowsGameLaunchPlatform(), LaunchWatchTimings.Default)
        {
        }

        /// <summary>测试用：替换掉"起进程 / 看进程 / 等待"这些系统接口，不会真的启动任何程序。</summary>
        public LaunchOrchestrator(
            ILogger<LaunchOrchestrator> logger,
            GameConfigService gameConfig,
            ProfileService profileService,
            ResolvedViewBuilder viewBuilder,
            DeploymentPlanner deployPlanner,
            DeploymentService deployService,
            ConflictModuleService conflictModule,
            string sessionLogDirectory,
            IGameLaunchPlatform launchPlatform,
            LaunchWatchTimings watchTimings)
        {
            _logger = logger;
            _gameConfig = gameConfig;
            _profileService = profileService;
            _viewBuilder = viewBuilder;
            _deployPlanner = deployPlanner;
            _deployService = deployService;
            _conflictModule = conflictModule;

            _sessionLogDir = sessionLogDirectory;
            _processLauncher = new GameProcessLauncher(logger, launchPlatform, watchTimings);
            AppPaths.TryEnsureDirectory(_sessionLogDir);
        }

        /// <summary>
        /// 构建启动上下文（从当前配置和 Profile）。
        /// </summary>
        public LaunchContext BuildContext()
        {
            var profile = _profileService.CurrentProfile;
            var config = _gameConfig.Config;

            return new LaunchContext
            {
                GameName = _gameConfig.CurrentGameName,
                ProfileId = profile?.Id ?? Guid.Empty,
                ProfileName = profile?.Name ?? "默认",
                GameRootPath = config.GamePath ?? "",
                ExecutablePath = FindExecutable(config),
                WorkingDirectory = config.GamePath ?? ""
            };
        }

        /// <summary>
        /// 当前游戏这一次会怎么启动（经 Steam 还是直接运行 exe）。只读清单、不启动任何东西，界面用它显示启动方式。
        /// </summary>
        public GameLaunchPlan ResolveLaunchPlan(LaunchContext? context = null)
        {
            context ??= BuildContext();
            return _processLauncher.ResolvePlan(context, _gameConfig.IsDirectLaunchPreferred(context.GameName));
        }

        /// <summary>
        /// 执行完整启动流水线。
        ///
        /// 同一时刻只跑一条：上一次还在部署或还在等游戏出现时再点，直接返回一个"正在启动"的失败会话，
        /// 不重复部署、也不重复拉起游戏（该会话不进历史、不落盘）。
        /// </summary>
        public async Task<LaunchSession> LaunchAsync(LaunchContext? context = null)
        {
            context ??= BuildContext();

            if (System.Threading.Interlocked.CompareExchange(ref _launching, 1, 0) != 0)
            {
                _logger.LogInformation("Launch request ignored: a launch is already in progress");
                return new LaunchSession
                {
                    GameName = context.GameName,
                    ProfileName = context.ProfileName,
                    ExecutablePath = context.ExecutablePath,
                    Success = false,
                    FailureReason = LaunchInProgressMessage
                };
            }

            try
            {
                return await RunPipelineAsync(context);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _launching, 0);
            }
        }

        /// <summary>重复点击启动时返回的说明。</summary>
        public const string LaunchInProgressMessage = "游戏正在启动中，请稍等片刻。";

        private async Task<LaunchSession> RunPipelineAsync(LaunchContext context)
        {
            var session = new LaunchSession
            {
                GameName = context.GameName,
                ProfileName = context.ProfileName,
                ExecutablePath = context.ExecutablePath
            };

            var steps = BuildPipeline(context);
            var preparation = new LaunchPreparation();
            session.Steps.AddRange(steps);

            _logger.LogInformation("Launch pipeline started for {Game} / {Profile}, {Steps} steps",
                context.GameName, context.ProfileName, steps.Count);

            foreach (var step in steps)
            {
                step.Status = LaunchStepStatus.Running;
                step.StartedAt = DateTime.Now;
                StepChanged?.Invoke(step);

                try
                {
                    var passed = await ExecuteStepAsync(step, context, session, preparation);

                    step.CompletedAt = DateTime.Now;

                    if (!passed && step.Status != LaunchStepStatus.Warning)
                    {
                        step.Status = LaunchStepStatus.Failed;
                        StepChanged?.Invoke(step);

                        session.Success = false;
                        // 启动这一步自己写了给玩家看的原因和下一步，不要盖掉
                        session.FailureReason ??= $"步骤「{step.DisplayName}」失败: {step.Message}";
                        _logger.LogWarning("Launch aborted at step {Step}: {Message}", step.DisplayName, step.Message);
                        break;
                    }

                    if (step.Status == LaunchStepStatus.Running)
                        step.Status = LaunchStepStatus.Passed;

                    StepChanged?.Invoke(step);
                }
                catch (Exception ex)
                {
                    step.Status = LaunchStepStatus.Failed;
                    step.Message = ex.Message;
                    step.CompletedAt = DateTime.Now;
                    StepChanged?.Invoke(step);

                    session.Success = false;
                    session.FailureReason = $"步骤「{step.DisplayName}」异常: {ex.Message}";
                    _logger.LogError(ex, "Launch step {Step} threw exception", step.DisplayName);
                    break;
                }
            }

            // 只有确实看到游戏在运行才算成功：进程起来又秒退、交给 Steam 后迟迟不见游戏，都不是成功。
            session.Success = session.Outcome == LaunchOutcome.Running
                              && !session.Steps.Any(s => s.Status == LaunchStepStatus.Failed);
            if (!session.Success && string.IsNullOrEmpty(session.FailureReason))
                session.FailureReason = "游戏没有启动起来";

            // 记录会话
            LastSession = session;
            SessionHistory.Insert(0, session);
            if (SessionHistory.Count > 20) SessionHistory.RemoveAt(20);

            await SaveSessionAsync(session);

            _logger.LogInformation("Launch session {Id}: {Result}",
                session.Id,
                session.Success ? "SUCCESS"
                    : session.Outcome == LaunchOutcome.NotConfirmed ? $"UNCONFIRMED - {session.FailureReason}"
                    : $"FAILED - {session.FailureReason}");

            return session;
        }

        // ─── 流水线构建 ───

        private List<LaunchStep> BuildPipeline(LaunchContext context)
            => LaunchPipelineBuilder.BuildPipeline(context);

        // ─── 步骤执行 ───

        private sealed class LaunchPreparation
        {
            public ResolvedView? View { get; set; }
        }

        private async Task<bool> ExecuteStepAsync(LaunchStep step, LaunchContext context, LaunchSession session, LaunchPreparation preparation)
        {
            return step.Type switch
            {
                LaunchStepType.ValidateGamePath => ValidateGamePath(step, context),
                LaunchStepType.ValidateExecutable => ValidateExecutable(step, context),
                LaunchStepType.BuildResolvedView => await BuildView(step, context, session, preparation),
                LaunchStepType.Deploy => await DeployIfNeeded(step, preparation),
                LaunchStepType.ConflictCheck => await CheckConflicts(step, context),
                LaunchStepType.LaunchProcess => await LaunchProcessAsync(step, context, session),
                _ => true
            };
        }

        private bool ValidateGamePath(LaunchStep step, LaunchContext context)
        {
            if (string.IsNullOrEmpty(context.GameRootPath) || !Directory.Exists(context.GameRootPath))
            {
                step.Message = $"游戏目录不存在: {context.GameRootPath}";
                return false;
            }
            step.Message = context.GameRootPath;
            return true;
        }

        private bool ValidateExecutable(LaunchStep step, LaunchContext context)
        {
            if (string.IsNullOrEmpty(context.ExecutablePath) || !File.Exists(context.ExecutablePath))
            {
                step.Message = $"可执行文件不存在: {context.ExecutablePath}";
                return false;
            }
            step.Message = Path.GetFileName(context.ExecutablePath);
            return true;
        }

        private async Task<bool> BuildView(LaunchStep step, LaunchContext context, LaunchSession session, LaunchPreparation preparation)
        {
            var view = await _viewBuilder.BuildAsync();
            preparation.View = view;
            context.ResolvedViewHash = view.ViewHash;
            session.ViewHash = view.ViewHash;

                var eval = LaunchStepEvaluator.EvaluateBuildView(view.TotalEntries);
            step.Message = eval.Message;
            if (eval.Status == LaunchStepStatus.Warning)
                step.Status = LaunchStepStatus.Warning;

            return eval.Passed;
        }

        private async Task<bool> DeployIfNeeded(LaunchStep step, LaunchPreparation preparation)
        {
            // 异常交由 LaunchAsync 记录并终止流水线，与失败事务保持同一语义。
            var plan = await _deployPlanner.CreatePlanForViewAsync(
                preparation.View ?? throw new InvalidOperationException("尚未构建部署视图"));
            var skipEval = LaunchStepEvaluator.EvaluateDeploymentSkip(plan.Operations.Count);

            if (skipEval.Status == LaunchStepStatus.Skipped)
            {
                if (plan.RequiresStateCommit)
                {
                    var stateTransaction = await _deployService.ExecuteAsync(plan);
                    if (stateTransaction.Status != DeploymentStatus.Committed)
                    {
                        step.Message = stateTransaction.ErrorMessage ?? "部署归属清单保存失败";
                        return false;
                    }
                }
                step.Message = skipEval.Message;
                step.Status = LaunchStepStatus.Skipped;
                return true;
            }

            step.Message = skipEval.Message;
            StepChanged?.Invoke(step);

            var tx = await _deployService.ExecuteAsync(plan);
            var resultEval = LaunchStepEvaluator.EvaluateDeploymentResult(
                tx.Status, tx.CompletedOperations, tx.ErrorMessage);

            step.Message = resultEval.Message;
            return resultEval.Passed;
        }

        private async Task<bool> CheckConflicts(LaunchStep step, LaunchContext context)
        {
            // 报的是**冲突组数**（面板上有几个框），不是资产数或路径数 ——
            // 旧模型按目标路径报出"582 个冲突"，用户数的是自己的 MOD 个数，
            // 两边永远对不上。
            var report = await _conflictModule.AnalyzeAsync();

            var eval = LaunchStepEvaluator.EvaluateConflictCheck(report.Groups.Count);
            step.Message = eval.Message;
            if (eval.Status == LaunchStepStatus.Warning)
                step.Status = LaunchStepStatus.Warning;

            return eval.Passed;
        }

        /// <summary>
        /// 拉起游戏并确认它真的在运行。
        ///
        /// 此前这里直接 Process.Start 游戏 exe，只要拿到进程对象就记成功。装在 Steam 里的游戏被直接运行时，
        /// Steam 客户端会接管（弹回 Steam、甚至重启 Steam），那个进程随即退出，游戏并没有出现 ——
        /// 而管理器仍然显示"启动成功"。现在装在 Steam 库里的游戏默认交给 Steam 启动，并且看到游戏进程才算成功。
        /// </summary>
        private async Task<bool> LaunchProcessAsync(LaunchStep step, LaunchContext context, LaunchSession session)
        {
            var preferDirect = _gameConfig.IsDirectLaunchPreferred(context.GameName);
            var result = await _processLauncher.LaunchAsync(context, preferDirect, message =>
            {
                step.Message = message;
                StepChanged?.Invoke(step);
            });

            session.LaunchMethod = result.Plan.Method;
            session.SteamAppId = result.Plan.Method == GameLaunchMethod.Steam ? result.Plan.SteamAppId : 0;
            session.CanUseSteam = result.Plan.CanUseSteam;
            session.Outcome = result.Outcome;
            session.ProcessId = result.ProcessId;

            var text = LaunchOutcomeText.Describe(result.Outcome, result.Plan.Method, result.Plan.CanUseSteam);
            if (result.Outcome == LaunchOutcome.Running)
            {
                step.Message = result.Plan.Method == GameLaunchMethod.Steam
                    ? $"Steam AppID {result.Plan.SteamAppId}"
                    : $"PID {result.ProcessId}";
                return true;
            }

            step.Message = string.IsNullOrEmpty(result.Error) ? text.Title : result.Error;
            session.FailureReason = string.IsNullOrEmpty(result.Error)
                ? $"{text.Title}：{text.Detail}"
                : $"{text.Title}：{result.Error}";

            // "没确认"不是失败：游戏很可能稍后就出现，步骤标成警告而不是红叉。
            if (result.Outcome == LaunchOutcome.NotConfirmed)
            {
                step.Status = LaunchStepStatus.Warning;
                return true;
            }
            return false;
        }

        // ─── 辅助 ───

        /// <summary>
        /// 解析要启动的可执行文件。
        ///
        /// 检测逻辑复用 <see cref="GameConfigService.AutoDetectExecutablePath"/>：
        /// 此处原本是 GetFiles(TopDirectoryOnly).FirstOrDefault()，没有任何排除，
        /// 结果由目录枚举顺序决定，可能直接启动 unins000.exe / launcher.exe，
        /// 且与主界面「启动游戏」用的是两套逻辑、行为不一致。
        /// </summary>
        private string FindExecutable(AppConfig config)
        {
            if (!string.IsNullOrEmpty(config.ExecutableName) && !string.IsNullOrEmpty(config.GamePath))
            {
                var fullPath = Path.Combine(config.GamePath, config.ExecutableName);
                if (File.Exists(fullPath)) return fullPath;
            }

            if (!string.IsNullOrEmpty(config.GamePath))
            {
                var detected = _gameConfig.AutoDetectExecutablePath(config.GamePath, config.GameName ?? "");
                if (!string.IsNullOrEmpty(detected)) return detected;
            }

            return config.ExecutableName ?? "";
        }

        private async Task SaveSessionAsync(LaunchSession session)
        {
            try
            {
                var path = Path.Combine(_sessionLogDir, $"{session.Id:N}.json");
                var json = JsonConvert.SerializeObject(session, Formatting.Indented);
                await AtomicFileWriter.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save launch session {Id}", session.Id);
            }
        }
    }
}
