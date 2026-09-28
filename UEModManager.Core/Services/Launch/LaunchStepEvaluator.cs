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
        /// 评估冲突检查步骤。
        ///
        /// 参数是**冲突组数**（面板上有几个框），不是资产数或路径数：
        /// 旧模型按目标路径报出"582 个冲突"，用户数的是自己的 MOD 个数，
        /// 两边永远对不上；而组数就是用户将要做的决策个数。
        ///
        /// 冲突只是提示，不拦启动 —— 所以一律 Passed，有冲突时降级成 Warning。
        /// </summary>
        public static LaunchStepEvaluation EvaluateConflictCheck(int conflictGroups)
        {
            if (conflictGroups <= 0)
                return new LaunchStepEvaluation(LaunchStepStatus.Passed, "无 MOD 冲突", Passed: true);

            return new LaunchStepEvaluation(
                LaunchStepStatus.Warning,
                $"{conflictGroups} 组 MOD 冲突待处理",
                Passed: true);
        }

        /// <summary>
        /// 评估视图构建步骤（不阻止启动，只提示）。
        ///
        /// 只报文件数。冲突调查由 <see cref="EvaluateConflictCheck"/> 单独负责 ——
        /// 两个步骤各报一次冲突数，会让同一件事在清单上出现两遍。
        /// </summary>
        public static LaunchStepEvaluation EvaluateBuildView(int totalEntries)
            => new(LaunchStepStatus.Passed, $"{totalEntries} 个文件", Passed: true);

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
