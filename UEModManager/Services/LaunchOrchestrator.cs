using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using UEModManager.Models;
using UEModManager.Services.Launch;

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
        private readonly ConflictAnalyzer _conflictAnalyzer;
        private readonly string _sessionLogDir;

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
            ConflictAnalyzer conflictAnalyzer)
        {
            _logger = logger;
            _gameConfig = gameConfig;
            _profileService = profileService;
            _viewBuilder = viewBuilder;
            _deployPlanner = deployPlanner;
            _deployService = deployService;
            _conflictAnalyzer = conflictAnalyzer;

            _sessionLogDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Data", "LaunchSessions");
            Directory.CreateDirectory(_sessionLogDir);
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
        /// 执行完整启动流水线。
        /// </summary>
        public async Task<LaunchSession> LaunchAsync(LaunchContext? context = null)
        {
            context ??= BuildContext();

            var session = new LaunchSession
            {
                GameName = context.GameName,
                ProfileName = context.ProfileName,
                ExecutablePath = context.ExecutablePath
            };

            var steps = BuildPipeline(context);
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
                    var passed = await ExecuteStepAsync(step, context, session);

                    step.CompletedAt = DateTime.Now;

                    if (!passed && step.Status != LaunchStepStatus.Warning)
                    {
                        step.Status = LaunchStepStatus.Failed;
                        StepChanged?.Invoke(step);

                        session.Success = false;
                        session.FailureReason = $"步骤「{step.DisplayName}」失败: {step.Message}";
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

            // 如果所有步骤都通过且有 LaunchProcess 步骤已 Passed，标记成功
            if (!session.Steps.Any(s => s.Status == LaunchStepStatus.Failed))
                session.Success = true;

            // 记录会话
            LastSession = session;
            SessionHistory.Insert(0, session);
            if (SessionHistory.Count > 20) SessionHistory.RemoveAt(20);

            await SaveSessionAsync(session);

            _logger.LogInformation("Launch session {Id}: {Result}",
                session.Id, session.Success ? "SUCCESS" : $"FAILED - {session.FailureReason}");

            return session;
        }

        // ─── 流水线构建 ───

        private List<LaunchStep> BuildPipeline(LaunchContext context)
            => LaunchPipelineBuilder.BuildPipeline(context);

        // ─── 步骤执行 ───

        private async Task<bool> ExecuteStepAsync(LaunchStep step, LaunchContext context, LaunchSession session)
        {
            return step.Type switch
            {
                LaunchStepType.ValidateGamePath => ValidateGamePath(step, context),
                LaunchStepType.ValidateExecutable => ValidateExecutable(step, context),
                LaunchStepType.BuildResolvedView => await BuildView(step, context, session),
                LaunchStepType.Deploy => await DeployIfNeeded(step, context),
                LaunchStepType.ConflictCheck => await CheckConflicts(step, context),
                LaunchStepType.LaunchProcess => LaunchProcess(step, context, session),
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

        private async Task<bool> BuildView(LaunchStep step, LaunchContext context, LaunchSession session)
        {
            var view = await _viewBuilder.BuildAsync();
            context.ResolvedViewHash = view.ViewHash;
            session.ViewHash = view.ViewHash;

            var eval = LaunchStepEvaluator.EvaluateBuildView(view.TotalEntries, view.ConflictCount);
            step.Message = eval.Message;
            if (eval.Status == LaunchStepStatus.Warning)
                step.Status = LaunchStepStatus.Warning;

            return eval.Passed;
        }

        private async Task<bool> DeployIfNeeded(LaunchStep step, LaunchContext context)
        {
            try
            {
                var plan = await _deployPlanner.CreatePlanAsync();
                var skipEval = LaunchStepEvaluator.EvaluateDeploymentSkip(
                    plan?.Operations.Count ?? 0);

                if (skipEval.Status == LaunchStepStatus.Skipped)
                {
                    step.Message = skipEval.Message;
                    step.Status = LaunchStepStatus.Skipped;
                    return true;
                }

                step.Message = skipEval.Message;
                StepChanged?.Invoke(step);

                var tx = await _deployService.ExecuteAsync(plan!);
                var resultEval = LaunchStepEvaluator.EvaluateDeploymentResult(
                    tx.Status, tx.CompletedOperations, tx.ErrorMessage);

                step.Message = resultEval.Message;
                return resultEval.Passed;
            }
            catch (Exception ex)
            {
                step.Message = $"部署异常: {ex.Message}";
                // 部署失败不阻止启动，降级为警告
                step.Status = LaunchStepStatus.Warning;
                return true;
            }
        }

        private async Task<bool> CheckConflicts(LaunchStep step, LaunchContext context)
        {
            var result = await _conflictAnalyzer.AnalyzeAsync();
            var errors = result.Conflicts.Count(c => c.Severity == ConflictSeverity.Error);

            var eval = LaunchStepEvaluator.EvaluateConflictCheck(result.TotalConflicts, errors);
            step.Message = eval.Message;
            if (eval.Status == LaunchStepStatus.Warning)
                step.Status = LaunchStepStatus.Warning;

            return eval.Passed;
        }

        private bool LaunchProcess(LaunchStep step, LaunchContext context, LaunchSession session)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = context.ExecutablePath,
                    WorkingDirectory = context.WorkingDirectory,
                    UseShellExecute = true
                };

                if (!string.IsNullOrEmpty(context.LaunchArguments))
                    psi.Arguments = context.LaunchArguments;

                foreach (var (key, value) in context.EnvironmentVariables)
                    psi.Environment[key] = value;

                var process = Process.Start(psi);
                if (process != null)
                {
                    session.ProcessId = process.Id;
                    step.Message = $"PID {process.Id}";
                    _logger.LogInformation("Game launched: PID {Pid}, {Path}", process.Id, context.ExecutablePath);
                    return true;
                }

                step.Message = "进程启动返回 null";
                return false;
            }
            catch (Exception ex)
            {
                step.Message = ex.Message;
                return false;
            }
        }

        // ─── 辅助 ───

        private string FindExecutable(AppConfig config)
        {
            if (!string.IsNullOrEmpty(config.ExecutableName) && !string.IsNullOrEmpty(config.GamePath))
            {
                var fullPath = Path.Combine(config.GamePath, config.ExecutableName);
                if (File.Exists(fullPath)) return fullPath;
            }

            // 在游戏目录中查找 .exe
            if (!string.IsNullOrEmpty(config.GamePath) && Directory.Exists(config.GamePath))
            {
                var exe = Directory.GetFiles(config.GamePath, "*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (exe != null) return exe;
            }

            return config.ExecutableName ?? "";
        }

        private async Task SaveSessionAsync(LaunchSession session)
        {
            try
            {
                var path = Path.Combine(_sessionLogDir, $"{session.Id:N}.json");
                var json = JsonConvert.SerializeObject(session, Formatting.Indented);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save launch session {Id}", session.Id);
            }
        }
    }
}
