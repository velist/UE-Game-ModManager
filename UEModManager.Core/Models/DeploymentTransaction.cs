using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// 部署事务。
    /// 跟踪一次部署计划的执行状态，支持回滚。
    /// 事务日志持久化到 Data/Backups/{transactionId}/transaction.json。
    /// </summary>
    public class DeploymentTransaction
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>关联的部署计划 ID。</summary>
        public Guid PlanId { get; init; }

        /// <summary>关联的 Profile ID。</summary>
        public Guid ProfileId { get; init; }

        /// <summary>关联的游戏名称。</summary>
        public string HostGameName { get; init; } = default!;

        /// <summary>事务状态。</summary>
        public DeploymentStatus Status { get; set; } = DeploymentStatus.Pending;

        /// <summary>备份根目录。</summary>
        public string BackupDirectory { get; init; } = default!;

        /// <summary>部署后端类型。</summary>
        public DeploymentBackendType BackendType { get; init; }

        /// <summary>已执行的操作列表（用于回滚）。</summary>
        public List<DeploymentOperation> ExecutedOperations { get; set; } = [];

        /// <summary>创建时间。</summary>
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>完成时间。</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>错误信息（失败时）。</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>操作总数。</summary>
        public int TotalOperations { get; init; }

        /// <summary>已完成操作数。</summary>
        public int CompletedOperations { get; set; }

        /// <summary>
        /// 回滚执行期间失败的操作详情（仅 PartiallyRolledBack 时有值）。
        /// 每条记录"哪个目标路径回滚失败、失败原因"，供恢复 UI 展示。
        /// </summary>
        public List<RollbackFailure> RollbackFailures { get; set; } = [];

        /// <summary>
        /// 用户曾忽略此事务的时间（Dismissed 状态下设置）。
        /// 用于审计和"重置已忽略事务"管理操作。
        /// </summary>
        public DateTime? DismissedAt { get; set; }

        /// <summary>用户忽略此事务时给出的原因（可空）。</summary>
        public string? DismissedReason { get; set; }

        /// <summary>
        /// 事务日志格式版本。当前 = 2（v2.0-rc 引入 PartiallyRolledBack/Dismissed/LogPersistenceFailed）。
        /// 反序列化时若缺失则视为 1（向前兼容）。
        /// </summary>
        public int SchemaVersion { get; set; } = 2;

        // ─── 计算属性 ───

        /// <summary>进度百分比。</summary>
        [JsonIgnore]
        public double Progress => TotalOperations > 0
            ? (double)CompletedOperations / TotalOperations * 100
            : 0;

        /// <summary>是否可回滚。</summary>
        [JsonIgnore]
        public bool CanRollback => Status is DeploymentStatus.Committed
                                          or DeploymentStatus.Failed
                                          or DeploymentStatus.PartiallyRolledBack;
    }

    /// <summary>
    /// 单个回滚操作的失败记录。
    /// 用于 PartiallyRolledBack 状态下向用户展示"哪些文件没回滚干净"。
    /// </summary>
    public sealed record RollbackFailure(
        string TargetPath,
        string Reason);
}
