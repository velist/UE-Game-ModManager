using UEModManager.Models;

namespace UEModManager.Services.Deployment
{
    /// <summary>回滚动作类型。</summary>
    public enum RollbackActionType
    {
        /// <summary>无需动作（操作未执行或已无影响）。</summary>
        None,

        /// <summary>删除新增的目标文件。</summary>
        DeleteAdded,

        /// <summary>从备份恢复目标文件。</summary>
        RestoreFromBackup,

        /// <summary>
        /// Remove/Replace 操作但 BackupPath 为空 — 备份从未生成（Add 操作或备份阶段被跳过）。
        /// 区别于 BackupMissing：这是"从未有过"，不是"丢了"。
        /// </summary>
        NoBackupRecorded,

        /// <summary>
        /// 备份路径有记录但文件已不存在（被人手动删 / 磁盘错误）。
        /// 此状态必须冒泡为 PartiallyRolledBack，不允许静默成功。
        /// </summary>
        BackupMissing,
    }

    /// <summary>回滚单个操作的动作描述。</summary>
    public sealed record RollbackAction(
        RollbackActionType Type,
        string TargetPath,
        string? BackupPath);

    /// <summary>
    /// 部署操作的回滚动作规划（纯函数）。
    ///
    /// 给定一个已执行的 DeploymentOperation，决定回滚时该做什么：
    /// - Add  → 删除新增目标
    /// - Remove / Replace + 备份路径有效 → 从备份恢复目标
    /// - Remove / Replace + 备份路径为空 → NoBackupRecorded（从未备份）
    /// - Remove / Replace + 备份路径已记录但文件丢失 → BackupMissing（必须冒泡）
    ///
    /// 主项目 DeploymentService.RollbackAsync 拿到 RollbackAction 后执行 IO（File.Delete / File.Copy）。
    /// </summary>
    public static class RollbackActionPlanner
    {
        /// <summary>
        /// 不做 IO 的纯静态版本，仅基于 BackupPath 字符串判断。
        /// 调用方若需检测"备份文件确实存在"应使用 <see cref="PlanRollback(DeploymentOperation, System.Func{string, bool})"/>。
        /// </summary>
        public static RollbackAction PlanRollback(DeploymentOperation operation)
            => PlanRollback(operation, backupExists: null);

        /// <summary>
        /// 带"备份是否存在"判定的版本。允许 Planner 检测 BackupMissing 而仍保持纯函数（依赖注入）。
        /// </summary>
        public static RollbackAction PlanRollback(
            DeploymentOperation operation,
            System.Func<string, bool>? backupExists)
        {
            if (operation == null) throw new System.ArgumentNullException(nameof(operation));

            return operation.Type switch
            {
                DeploymentOperationType.Add =>
                    new RollbackAction(RollbackActionType.DeleteAdded, operation.TargetPath, BackupPath: null),

                DeploymentOperationType.Remove or DeploymentOperationType.Replace
                    when !string.IsNullOrEmpty(operation.BackupPath) =>
                    backupExists is null || backupExists(operation.BackupPath!)
                        ? new RollbackAction(RollbackActionType.RestoreFromBackup,
                            operation.TargetPath, operation.BackupPath)
                        : new RollbackAction(RollbackActionType.BackupMissing,
                            operation.TargetPath, operation.BackupPath),

                // Remove/Replace 但没有备份路径 → 从未备份（区别于"备份丢了"）
                DeploymentOperationType.Remove or DeploymentOperationType.Replace =>
                    new RollbackAction(RollbackActionType.NoBackupRecorded, operation.TargetPath, BackupPath: null),

                _ => new RollbackAction(RollbackActionType.None, operation.TargetPath, BackupPath: null),
            };
        }
    }
}
