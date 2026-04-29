using UEModManager.Models;

namespace UEModManager.Services.Launch
{
    /// <summary>
    /// 启动流水线步骤评估结果（纯数据）。
    /// </summary>
    public sealed record LaunchStepEvaluation(LaunchStepStatus Status, string Message, bool Passed);

    /// <summary>
    /// 启动流水线步骤的纯函数评估器。
    ///
    /// 主项目 LaunchOrchestrator 在执行 IO（构建视图 / 部署 / 冲突分析）后，
    /// 把结果数据传入这里换出"该步骤的最终状态 + UI 消息"，避免决策树和 IO 纠缠。
    /// </summary>
    public static class LaunchStepEvaluator
    {
        /// <summary>
        /// 评估冲突检查步骤：
        /// - 0 冲突 → Passed + "无冲突"
        /// - 含 Error 级冲突 → Failed
        /// - 仅 Warning/Info → Warning + 通过
        /// </summary>
        public static LaunchStepEvaluation EvaluateConflictCheck(int totalConflicts, int errorConflicts)
        {
            if (totalConflicts == 0)
                return new LaunchStepEvaluation(LaunchStepStatus.Passed, "无冲突", Passed: true);

            if (errorConflicts > 0)
                return new LaunchStepEvaluation(
                    LaunchStepStatus.Failed,
                    $"{totalConflicts} 个冲突（{errorConflicts} 个严重）",
                    Passed: false);

            return new LaunchStepEvaluation(
                LaunchStepStatus.Warning,
                $"{totalConflicts} 个冲突（均为警告级别）",
                Passed: true);
        }

        /// <summary>
        /// 评估视图构建步骤：
        /// - 有冲突 → Warning（不阻止）
        /// - 无冲突 → Passed
        /// </summary>
        public static LaunchStepEvaluation EvaluateBuildView(int totalEntries, int conflictCount)
        {
            var status = conflictCount > 0 ? LaunchStepStatus.Warning : LaunchStepStatus.Passed;
            return new LaunchStepEvaluation(
                status,
                $"{totalEntries} 个文件, {conflictCount} 个冲突",
                Passed: true);
        }

        /// <summary>
        /// 评估部署步骤的"是否需要执行"决策（执行前）。
        /// - operationCount==0 → Skipped + "无需部署"
        /// - 否则 → Running + "部署 N 个操作..."（让 UI 在执行期间显示进度提示）
        /// </summary>
        public static LaunchStepEvaluation EvaluateDeploymentSkip(int operationCount)
        {
            if (operationCount <= 0)
                return new LaunchStepEvaluation(
                    LaunchStepStatus.Skipped, "无需部署，已是最新", Passed: true);

            return new LaunchStepEvaluation(
                LaunchStepStatus.Running, $"部署 {operationCount} 个操作...", Passed: true);
        }

        /// <summary>
        /// 评估部署步骤的"执行结果"（执行后）。
        /// - <see cref="DeploymentStatus.Committed"/> → Passed + "已部署 N 个操作"
        /// - 其他 → Failed + 具体错误消息
        /// </summary>
        public static LaunchStepEvaluation EvaluateDeploymentResult(
            DeploymentStatus status, int completedOperations, string? errorMessage)
        {
            if (status == DeploymentStatus.Committed)
                return new LaunchStepEvaluation(
                    LaunchStepStatus.Passed,
                    $"已部署 {completedOperations} 个操作",
                    Passed: true);

            var msg = string.IsNullOrEmpty(errorMessage)
                ? $"部署失败: {status}"
                : $"部署失败: {errorMessage}";
            return new LaunchStepEvaluation(LaunchStepStatus.Failed, msg, Passed: false);
        }
    }
}
