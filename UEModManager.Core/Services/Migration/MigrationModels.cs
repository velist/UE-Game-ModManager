using System.Collections.Generic;

namespace UEModManager.Services.Migration
{
    /// <summary>
    /// 数据迁移进度（v1.8 → v2.0 迁移过程中的步骤/百分比/详情）。
    ///
    /// 主项目 DataMigrationService 在每步完成时构造并通过 ProgressChanged 事件回传给 UI。
    /// 自身无状态、无 IO；移到 Core 让 UI 测试和 Core 测试共用同一类型。
    /// </summary>
    public class MigrationProgress
    {
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public double Percentage => TotalSteps > 0 ? (double)CurrentStep / TotalSteps * 100 : 0;
    }

    /// <summary>
    /// 数据迁移结果。
    /// </summary>
    public class MigrationResult
    {
        public bool Success { get; init; }
        public int MigratedPackages { get; init; }
        public int SkippedPackages { get; init; }
        public List<string> Warnings { get; init; } = [];
        public string? ErrorMessage { get; init; }
    }
}
