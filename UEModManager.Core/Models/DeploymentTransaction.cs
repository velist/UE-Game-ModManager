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

        // ─── 计算属性 ───

        /// <summary>进度百分比。</summary>
        [JsonIgnore]
        public double Progress => TotalOperations > 0
            ? (double)CompletedOperations / TotalOperations * 100
            : 0;

        /// <summary>是否可回滚。</summary>
        [JsonIgnore]
        public bool CanRollback => Status is DeploymentStatus.Committed or DeploymentStatus.Failed;
    }
}
