using System;

namespace UEModManager.Services.Migration
{
    /// <summary>
    /// 构造 <see cref="MigrationProgress"/> 的纯函数辅助器。
    ///
    /// 主项目 DataMigrationService.ReportProgress 原本硬编码 "ReportProgress(N, 5, name, detail)"，
    /// 改为通过 Tracker + StepCatalog 取定义，魔法数和文案全部下沉到 Core 可独立单测。
    /// </summary>
    public static class MigrationProgressTracker
    {
        /// <summary>
        /// 用步骤定义 + 详情构造一份进度。文案使用 Catalog 默认。
        /// </summary>
        public static MigrationProgress Build(MigrationStep step, string detail)
        {
            var descriptor = MigrationStepCatalog.Get(step);
            return BuildCore(descriptor.Number, descriptor.Name, detail);
        }

        /// <summary>
        /// 同上，但允许覆盖步骤名称（用于本地化 / 自定义文案）。
        /// </summary>
        public static MigrationProgress Build(MigrationStep step, string detail, string overrideName)
        {
            if (overrideName == null) throw new ArgumentNullException(nameof(overrideName));
            var descriptor = MigrationStepCatalog.Get(step);
            return BuildCore(descriptor.Number, overrideName, detail);
        }

        /// <summary>
        /// 计算百分比；total &lt;= 0 返回 0，避免除零。
        /// </summary>
        public static double ComputePercentage(int currentStep, int totalSteps)
            => totalSteps > 0 ? (double)currentStep / totalSteps * 100 : 0;

        private static MigrationProgress BuildCore(int stepNumber, string name, string detail)
            => new()
            {
                CurrentStep = stepNumber,
                TotalSteps = MigrationStepCatalog.TotalSteps,
                StepName = name,
                Detail = detail ?? string.Empty,
            };
    }
}
