using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Recovery
{
    /// <summary>建议的恢复动作。</summary>
    public enum RecoveryAction
    {
        /// <summary>无需处理（事务已正常完成或回滚）。</summary>
        NoAction,
        /// <summary>建议执行回滚 — 事务在执行中崩溃，部分文件可能已被改动。</summary>
        RollbackRecommended,
        /// <summary>建议标记失败 — 事务已失败但仍占用备份目录，应清理。</summary>
        MarkFailedRecommended,
    }

    /// <summary>崩溃恢复候选项。</summary>
    public sealed record RecoveryCandidate(
        Guid TransactionId,
        DateTime CreatedAt,
        DeploymentStatus Status,
        RecoveryAction Action,
        string Reason,
        int CompletedOperations,
        int TotalOperations,
        string BackupDirectory);

    /// <summary>
    /// 崩溃恢复扫描器。纯函数：给定事务列表，决定哪些需要用户介入。
    ///
    /// 状态语义：
    /// - <see cref="DeploymentStatus.Committed"/> — 已提交，无需处理
    /// - <see cref="DeploymentStatus.RolledBack"/> — 已回滚，无需处理
    /// - <see cref="DeploymentStatus.InProgress"/> — 执行中崩溃 → 强烈建议回滚
    /// - <see cref="DeploymentStatus.Failed"/> — 已失败但未清理 → 建议标记+清理
    /// - <see cref="DeploymentStatus.Pending"/> — 创建后未启动 → 建议标记+清理
    /// </summary>
    public static class CrashRecoveryScanner
    {
        /// <summary>
        /// 扫描事务列表，返回需要用户介入的候选项。
        /// 已正常完成的事务（Committed/RolledBack）不会出现在结果中。
        /// </summary>
        public static List<RecoveryCandidate> Scan(IEnumerable<DeploymentTransaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));

            var candidates = new List<RecoveryCandidate>();

            foreach (var tx in transactions)
            {
                var (action, reason) = ClassifyStatus(tx);
                if (action == RecoveryAction.NoAction) continue;

                candidates.Add(new RecoveryCandidate(
                    TransactionId: tx.Id,
                    CreatedAt: tx.CreatedAt,
                    Status: tx.Status,
                    Action: action,
                    Reason: reason,
                    CompletedOperations: tx.CompletedOperations,
                    TotalOperations: tx.TotalOperations,
                    BackupDirectory: tx.BackupDirectory));
            }

            // 时间倒序：最近的崩溃在最前
            return candidates.OrderByDescending(c => c.CreatedAt).ToList();
        }

        /// <summary>对单个事务分类（纯函数，可单测）。</summary>
        public static (RecoveryAction action, string reason) ClassifyStatus(DeploymentTransaction tx)
        {
            return tx.Status switch
            {
                DeploymentStatus.Committed
                    => (RecoveryAction.NoAction, "事务已正常完成"),

                DeploymentStatus.RolledBack
                    => (RecoveryAction.NoAction, "事务已回滚"),

                DeploymentStatus.InProgress
                    => (RecoveryAction.RollbackRecommended,
                        $"上次执行中崩溃 ({tx.CompletedOperations}/{tx.TotalOperations} 操作完成)，文件状态可能不一致"),

                DeploymentStatus.Failed
                    => (RecoveryAction.MarkFailedRecommended,
                        "事务已失败但占用备份目录，建议清理"),

                DeploymentStatus.Pending
                    => (RecoveryAction.MarkFailedRecommended,
                        "事务创建后未启动，可能为残留记录"),

                _ => (RecoveryAction.NoAction, $"未知状态: {tx.Status}")
            };
        }
    }
}
