using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Services.Paths;

namespace UEModManager.Services
{
    /// <summary>迁移执行结果。</summary>
    public sealed record DataMigrationOutcome(
        bool Completed,
        int Executed,
        int Skipped,
        int Deferred,
        int Failed,
        string Summary);

    /// <summary>
    /// 数据目录搬迁器。职责只有编排：探测磁盘 → 交 Core 规划 → 交执行器落地 → 打版本标记。
    ///
    /// <para>
    /// 判定逻辑在 Core 的 <see cref="DataRelocationPlanner"/>（纯函数，74 个单测），
    /// 文件操作在 <see cref="DataRelocationExecutor"/>（可在临时目录上测），
    /// 本类只剩下与 <see cref="UiPreferences"/> 全局状态打交道的部分。
    /// </para>
    ///
    /// <para>
    /// 本类的任何失败都不得阻断启动：数据留在旧位置尚可用，启动不了则彻底不可用。
    /// </para>
    /// </summary>
    public sealed class DataLocationMigrator
    {
        /// <summary>
        /// 搬移类动作（Copy / PurgeTargetThenCopy / ResumeCleanup）的执行开关。
        ///
        /// <para>
        /// <b>当前为 false，且在下述条件全部满足前不得改为 true。</b>
        /// <c>{安装目录}\Data</c> 目前有 12 个读取方，其中 <c>ProfileService</c>、
        /// <c>PackageRepository</c>、<c>OverwriteStore</c>、<c>DeploymentService</c>
        /// 尚未切到 <see cref="AppPaths"/>。在它们切换之前把数据搬走，这四个服务会读到空目录
        /// —— 用户的方案、包索引、生成物索引与部署备份会当场"消失"。
        /// </para>
        ///
        /// <para>
        /// 因此本阶段只做两件事：把计划完整算出来<b>写进日志</b>（可在真实用户机器上验证
        /// 探测与判定是否符合预期），以及<b>执行原地登记</b>——后者只写配置值，
        /// 要么与当前行为完全等价，要么写的是还没有读取方的新键，零风险。
        /// </para>
        ///
        /// <para>
        /// 翻开此开关时必须同时完成：全部读取方切到 <see cref="AppPaths"/>，
        /// 且跑通迁移方案里的 D0–D5 测试矩阵。
        /// </para>
        /// </summary>
        private const bool RelocationExecutionEnabled = false;

        private const string RepositoryItemName = "包仓库";
        private const string OverwritesItemName = "生成物存储";

        private readonly ILogger<DataLocationMigrator> _logger;
        private readonly DataRelocationExecutor _executor;

        public DataLocationMigrator(ILogger<DataLocationMigrator> logger)
        {
            _logger = logger;
            _executor = new DataRelocationExecutor(logger);
        }

        /// <summary>执行一次迁移。绝不抛异常——失败时应用沿用旧位置继续运行。</summary>
        public Task<DataMigrationOutcome> RunAsync()
        {
            try
            {
                return Task.FromResult(Run());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DataMigration] 迁移过程异常，沿用旧数据位置继续运行");
                return Task.FromResult(
                    new DataMigrationOutcome(false, 0, 0, 0, 1, "迁移异常，已沿用旧位置"));
            }
        }

        private DataMigrationOutcome Run()
        {
            var migratedVersion = UiPreferences.LoadDataLayoutVersion();
            var plan = DataRelocationPlanner.Plan(migratedVersion, BuildProbes());

            int executed = 0, skipped = 0, deferred = 0, failed = 0;

            foreach (var step in plan.Steps)
            {
                if (step.Action == RelocationAction.None)
                {
                    skipped++;
                    _logger.LogInformation("[DataMigration] 跳过 {Name}：{Reason}", step.Name, step.Reason);
                    continue;
                }

                if (step.Action != RelocationAction.RegisterInPlace && !RelocationExecutionEnabled)
                {
                    deferred++;
                    _logger.LogInformation(
                        "[DataMigration] 计划（本阶段不执行）{Name}: {Action} — {Reason}｜{From} → {To}",
                        step.Name, step.Action, step.Reason, step.LegacyPath, step.TargetPath);
                    continue;
                }

                if (TryExecute(step)) executed++;
                else failed++;
            }

            // 只有既无失败、也无推迟项时，本布局版本才算彻底完成。
            // 推迟项在开关打开后仍要处理，提前打版本标记会让它们被永久跳过。
            var completed = failed == 0 && deferred == 0;
            if (completed && plan.ShouldStampVersion)
            {
                UiPreferences.SaveDataLayoutVersion(plan.ToVersion);
                _logger.LogInformation("[DataMigration] 数据布局版本标记为 v{Version}", plan.ToVersion);
            }

            var summary = $"执行 {executed}，跳过 {skipped}，推迟 {deferred}，失败 {failed}";
            _logger.LogInformation("[DataMigration] {Summary}", summary);
            return new DataMigrationOutcome(completed, executed, skipped, deferred, failed, summary);
        }

        // ─── 探测 ───

        private IReadOnlyList<RelocationProbe> BuildProbes() => new[]
        {
            // 搬移类：体积恒定在 MB 级
            FileProbe("主配置", AppPaths.Legacy.ConfigFile, AppPaths.ConfigFile),
            DirectoryProbe("数据索引", AppPaths.Legacy.DataDirectory, AppPaths.DataDirectory),
            DirectoryProbe("MOD 备份", AppPaths.Legacy.ModBackupsDirectory, AppPaths.ModBackupsDirectory),

            // 原地登记类：可能几十 GB，且已不在安装目录，卸载不会丢
            RegisterInPlaceProbe(RepositoryItemName, AppPaths.Legacy.RepositoryRoot,
                UiPreferences.LoadRepositoryRoot()),
            RegisterInPlaceProbe(OverwritesItemName, AppPaths.Legacy.OverwritesRoot,
                UiPreferences.LoadOverwritesRoot()),
        };

        // TODO(数据目录迁移 步骤 5)：部署事务备份当前位于 {安装目录}\Data\Backups，
        // 即"数据索引"这一项的子目录，会被一并搬到 {LOCALAPPDATA}\Data\Backups，
        // 而目标布局是 {LOCALAPPDATA}\Backups\Deployments（AppPaths.DeploymentBackupsDirectory）。
        // 打开 RelocationExecutionEnabled 之前必须把它拆成独立一项，否则会变成两跳搬移。
        // 之所以没有现在就拆：DeploymentService 是其唯一读取方，当前不在可改范围内，
        // 拆了也没有读取方能配合，反而让探测项与真实行为脱节。

        private static RelocationProbe FileProbe(string name, string legacy, string target)
            => new(name, RelocationKind.Relocate, legacy, target,
                LegacyExists: DataRelocationExecutor.FileExists(legacy),
                TargetExists: DataRelocationExecutor.FileExists(target),
                TombstoneExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetTombstonePath(legacy, isFile: true)));

        private static RelocationProbe DirectoryProbe(string name, string legacy, string target)
            => new(name, RelocationKind.Relocate, legacy, target,
                LegacyExists: DataRelocationExecutor.DirectoryHasContent(legacy),
                TargetExists: DataRelocationExecutor.DirectoryHasContent(target),
                TombstoneExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetTombstonePath(legacy, isFile: false)));

        private static RelocationProbe RegisterInPlaceProbe(string name, string legacy, string? userOverride)
            => new(name, RelocationKind.RegisterInPlace, legacy, legacy,
                LegacyExists: DataRelocationExecutor.DirectoryHasContent(legacy),
                TargetExists: false,
                TombstoneExists: false,
                IsUserOverridden: !string.IsNullOrWhiteSpace(userOverride));

        // ─── 执行 ───

        private bool TryExecute(RelocationStep step)
        {
            try
            {
                if (step.Action == RelocationAction.RegisterInPlace)
                {
                    RegisterInPlace(step);
                    return true;
                }

                _executor.Execute(step, IsFileItem(step));
                return true;
            }
            catch (Exception ex)
            {
                // 单项失败不影响其它项；该项数据仍完整留在旧位置
                _logger.LogError(ex, "[DataMigration] {Name} 迁移失败，数据保留在旧位置 {Path}",
                    step.Name, step.LegacyPath);
                return false;
            }
        }

        private void RegisterInPlace(RelocationStep step)
        {
            switch (step.Name)
            {
                case RepositoryItemName:
                    UiPreferences.SaveRepositoryRoot(step.LegacyPath);
                    break;
                case OverwritesItemName:
                    UiPreferences.SaveOverwritesRoot(step.LegacyPath);
                    break;
                default:
                    _logger.LogWarning("[DataMigration] 未知的原地登记项：{Name}", step.Name);
                    return;
            }

            _logger.LogInformation("[DataMigration] {Name} 原地登记：{Path}", step.Name, step.LegacyPath);
        }

        private static bool IsFileItem(RelocationStep step)
            => Path.HasExtension(step.LegacyPath) && !Directory.Exists(step.LegacyPath);
    }
}
