using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Recovery;

namespace UEModManager.Services
{
    /// <summary>
    /// 崩溃恢复服务（IO 适配器）。
    ///
    /// 启动时调用 <see cref="ScanForCrashesAsync"/> 检查未完成事务；
    /// UI 决定如何处理后调用 <see cref="ApplyRecoveryAsync"/> 执行回滚或标记失败。
    /// 纯分类逻辑下沉到 Core 的 <see cref="CrashRecoveryScanner"/>。
    /// </summary>
    public class CrashRecoveryService
    {
        private readonly ILogger<CrashRecoveryService> _logger;
        private readonly DeploymentService _deploymentService;

        // Id → 完整事务记录（用于 ApplyRecoveryAsync 时查回）
        private Dictionary<Guid, DeploymentTransaction> _knownTransactions = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public CrashRecoveryService(
            ILogger<CrashRecoveryService> logger,
            DeploymentService deploymentService)
        {
            _logger = logger;
            _deploymentService = deploymentService;
        }

        /// <summary>
        /// 扫描事务历史，返回需要用户介入的崩溃恢复候选项。
        /// </summary>
        public async Task<List<RecoveryCandidate>> ScanForCrashesAsync()
        {
            var transactions = await _deploymentService.GetTransactionHistoryAsync();
            _knownTransactions = transactions.ToDictionary(t => t.Id);

            var candidates = CrashRecoveryScanner.Scan(transactions);

            if (candidates.Count > 0)
            {
                _logger.LogWarning(
                    "[Recovery] 发现 {Count} 个未完成事务（崩溃恢复候选）",
                    candidates.Count);
            }
            else
            {
                _logger.LogInformation("[Recovery] 事务历史检查完成，无需恢复");
            }

            return candidates;
        }

        /// <summary>
        /// 对一个候选项执行用户选择的动作。
        /// </summary>
        public async Task<bool> ApplyRecoveryAsync(Guid transactionId, RecoveryAction action)
        {
            if (!_knownTransactions.TryGetValue(transactionId, out var tx))
            {
                _logger.LogWarning("[Recovery] 找不到事务 {Id}", transactionId);
                return false;
            }

            try
            {
                switch (action)
                {
                    case RecoveryAction.RollbackRecommended:
                        await _deploymentService.RollbackAsync(tx);
                        _logger.LogInformation("[Recovery] 事务 {Id} 已回滚", transactionId);
                        return true;

                    case RecoveryAction.MarkFailedRecommended:
                        tx.Status = DeploymentStatus.Failed;
                        await PersistTransactionAsync(tx);
                        _logger.LogInformation("[Recovery] 事务 {Id} 已标记为 Failed", transactionId);
                        return true;

                    case RecoveryAction.ManualReviewRequired:
                        // 仅记录用户已查看；不改状态 — 事务保持 PartiallyRolledBack 直到用户显式 Dismiss 或 Reset
                        _logger.LogWarning(
                            "[Recovery] 事务 {Id} 标记为人工核查（保留 PartiallyRolledBack 状态）",
                            transactionId);
                        return true;

                    case RecoveryAction.VerifyAndResubmit:
                        // 用户已确认 LogPersistenceFailed 的事务 — 同样不改状态，保留供审计
                        _logger.LogInformation(
                            "[Recovery] 事务 {Id} 用户确认日志写入失败，保留状态供审计",
                            transactionId);
                        return true;

                    case RecoveryAction.NoAction:
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Recovery] 事务 {Id} 恢复失败", transactionId);
                return false;
            }
        }

        /// <summary>
        /// 用户主动忽略此事务（W6：避免取消恢复对话框后死循环提示）。
        /// 把事务标记为 Dismissed，CrashRecoveryScanner 之后会跳过它。
        /// 可通过 <see cref="ResetDismissedAsync"/> 撤销。
        /// </summary>
        public async Task<bool> DismissTransactionAsync(Guid transactionId, string? reason = null)
        {
            if (!_knownTransactions.TryGetValue(transactionId, out var tx))
            {
                _logger.LogWarning("[Recovery] Dismiss 失败 — 找不到事务 {Id}", transactionId);
                return false;
            }

            tx.Status = DeploymentStatus.Dismissed;
            tx.DismissedAt = DateTime.Now;
            tx.DismissedReason = reason;

            try
            {
                await PersistTransactionAsync(tx);
                _logger.LogInformation(
                    "[Recovery] 事务 {Id} 已被用户忽略 (原因: {Reason})",
                    transactionId, reason ?? "未提供");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Recovery] Dismiss 持久化失败 — 事务 {Id} 下次启动仍会再次提示",
                    transactionId);
                return false;
            }
        }

        /// <summary>
        /// 把已 Dismissed 的事务重置回原始状态（用于"我后悔了想恢复"管理操作）。
        /// 仅当事务原本是 Dismissed 才能重置；返回 true 表示重置成功。
        /// </summary>
        public async Task<bool> ResetDismissedAsync(Guid transactionId)
        {
            if (!_knownTransactions.TryGetValue(transactionId, out var tx))
                return false;
            if (tx.Status != DeploymentStatus.Dismissed)
                return false;

            // 启发式恢复：根据已完成操作数推断回到 InProgress（最保守）或 Failed
            tx.Status = tx.CompletedOperations < tx.TotalOperations
                ? DeploymentStatus.InProgress
                : DeploymentStatus.Failed;
            tx.DismissedAt = null;
            tx.DismissedReason = null;

            try
            {
                await PersistTransactionAsync(tx);
                _logger.LogInformation(
                    "[Recovery] 事务 {Id} 已重置 Dismissed 标记 → {Status}",
                    transactionId, tx.Status);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Recovery] 重置 Dismissed 失败 — 事务 {Id}", transactionId);
                return false;
            }
        }

        /// <summary>
        /// 把 transaction.json 写回备份目录。
        /// </summary>
        private static async Task PersistTransactionAsync(DeploymentTransaction tx)
        {
            if (string.IsNullOrEmpty(tx.BackupDirectory)) return;
            if (!Directory.Exists(tx.BackupDirectory)) return;

            var path = Path.Combine(tx.BackupDirectory, "transaction.json");
            var json = JsonSerializer.Serialize(tx, JsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
    }
}
