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
    /// - Remove / Replace → 从备份恢复目标
    /// - 其他（None 或缺失备份）→ 不动作
    ///
    /// 主项目 DeploymentService.RollbackAsync 拿到 RollbackAction 后执行 IO（File.Delete / File.Copy）。
    /// </summary>
    public static class RollbackActionPlanner
    {
        public static RollbackAction PlanRollback(DeploymentOperation operation)
        {
            if (operation == null) throw new System.ArgumentNullException(nameof(operation));

            return operation.Type switch
            {
                DeploymentOperationType.Add =>
                    new RollbackAction(RollbackActionType.DeleteAdded, operation.TargetPath, BackupPath: null),

                DeploymentOperationType.Remove or DeploymentOperationType.Replace
                    when !string.IsNullOrEmpty(operation.BackupPath) =>
                    new RollbackAction(RollbackActionType.RestoreFromBackup,
                        operation.TargetPath, operation.BackupPath),

                // Remove/Replace 但没有备份路径 → 无法恢复，跳过
                DeploymentOperationType.Remove or DeploymentOperationType.Replace =>
                    new RollbackAction(RollbackActionType.None, operation.TargetPath, BackupPath: null),

                _ => new RollbackAction(RollbackActionType.None, operation.TargetPath, BackupPath: null),
            };
        }
    }
}
