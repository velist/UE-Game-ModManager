using System;
using System.Collections.Generic;

namespace UEModManager.Services.Migration
{
    /// <summary>
    /// 单个迁移步骤的定义：枚举值 + 步骤号（1-based）+ 默认中文名称。
    /// </summary>
    public sealed record MigrationStepDescriptor(MigrationStep Step, int Number, string Name);

    /// <summary>
    /// v1.8 → v2.0 迁移的 5 步定义目录（纯函数，无 IO）。
    ///
    /// 设计目的：
    /// - 让步骤号 / 文案 / 总步数集中在一处，主项目不再硬编码 "1, 5, ScanOldData" 等魔法数。
    /// - UI / 测试可独立查询步骤定义而无需依赖 DataMigrationService。
    /// - 任何步骤新增 / 重命名只需改这里 + 对应测试。
    /// </summary>
    public static class MigrationStepCatalog
    {
        /// <summary>当前迁移流程的总步数（== <see cref="All"/>.Count）。</summary>
        public const int TotalSteps = 5;

        private static readonly MigrationStepDescriptor[] _descriptors =
        [
            new(MigrationStep.ScanOldData,        1, "扫描旧数据"),
            new(MigrationStep.PrepareRepository,  2, "创建默认方案"),
            new(MigrationStep.MigrateToRepository,3, "迁移到仓库"),
            new(MigrationStep.GenerateManifest,   4, "生成 Manifest"),
            new(MigrationStep.VerifyIntegrity,    5, "验证完整性"),
        ];

        /// <summary>按顺序枚举所有步骤定义。</summary>
        public static IReadOnlyList<MigrationStepDescriptor> All => _descriptors;

        /// <summary>按枚举值取定义。</summary>
        /// <exception cref="ArgumentOutOfRangeException">枚举值不在已定义集合内。</exception>
        public static MigrationStepDescriptor Get(MigrationStep step)
        {
            foreach (var d in _descriptors)
                if (d.Step == step) return d;
            throw new ArgumentOutOfRangeException(nameof(step), step, "未定义的迁移步骤");
        }

        /// <summary>按步骤号（1-based）取定义。</summary>
        /// <exception cref="ArgumentOutOfRangeException">步骤号小于 1 或大于 <see cref="TotalSteps"/>。</exception>
        public static MigrationStepDescriptor Get(int stepNumber)
        {
            if (stepNumber < 1 || stepNumber > TotalSteps)
                throw new ArgumentOutOfRangeException(nameof(stepNumber), stepNumber,
                    $"步骤号必须在 1..{TotalSteps} 之间");
            return _descriptors[stepNumber - 1];
        }
    }
}
