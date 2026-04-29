using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Services.Launch
{
    /// <summary>
    /// 启动流水线构造（纯函数）。
    ///
    /// 给定 <see cref="LaunchContext"/>，输出该次启动应该执行的 <see cref="LaunchStep"/> 列表（按顺序）。
    /// 默认流水线 6 步：
    /// 1. ValidateGamePath
    /// 2. ValidateExecutable
    /// 3. BuildResolvedView      （SkipDeployCheck=true 时跳过此步及下一步）
    /// 4. Deploy                 （受 SkipDeployCheck 控制）
    /// 5. ConflictCheck          （受 SkipConflictCheck 控制）
    /// 6. LaunchProcess
    ///
    /// 主项目 LaunchOrchestrator 编排执行，本类只负责"该跑哪些步骤"。
    /// </summary>
    public static class LaunchPipelineBuilder
    {
        /// <summary>构建默认流水线步骤列表。</summary>
        public static List<LaunchStep> BuildPipeline(LaunchContext context)
        {
            if (context == null) throw new System.ArgumentNullException(nameof(context));

            var steps = new List<LaunchStep>(6)
            {
                new() { Type = LaunchStepType.ValidateGamePath, DisplayName = "验证游戏路径" },
                new() { Type = LaunchStepType.ValidateExecutable, DisplayName = "验证可执行文件" },
            };

            if (!context.SkipDeployCheck)
            {
                steps.Add(new LaunchStep { Type = LaunchStepType.BuildResolvedView, DisplayName = "构建最终视图" });
                steps.Add(new LaunchStep { Type = LaunchStepType.Deploy, DisplayName = "部署变更" });
            }

            if (!context.SkipConflictCheck)
            {
                steps.Add(new LaunchStep { Type = LaunchStepType.ConflictCheck, DisplayName = "冲突检查" });
            }

            steps.Add(new LaunchStep { Type = LaunchStepType.LaunchProcess, DisplayName = "启动游戏" });

            return steps;
        }
    }
}
