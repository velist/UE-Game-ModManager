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
