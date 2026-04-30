using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Backends;
using UEModManager.Services.Deployment;

namespace UEModManager.Services
{
    /// <summary>
    /// 部署执行服务。
    /// 接收 DeploymentPlan，创建事务，执行备份 → 部署 → 提交/回滚。
    /// </summary>
    public class DeploymentService
    {
        private readonly ILogger<DeploymentService> _logger;
        private readonly Dictionary<DeploymentBackendType, IDeploymentBackend> _backends;
        private readonly OverwriteStore _overwriteStore;
        private readonly string _backupRootPath;

        /// <summary>最近一次事务（用于 UI 展示和回滚）。</summary>
        public DeploymentTransaction? LastTransaction { get; private set; }

        /// <summary>部署进度变化事件。</summary>
        public event Action<DeploymentTransaction>? ProgressChanged;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public DeploymentService(
            ILogger<DeploymentService> logger,
            CopyBackend copyBackend,
            HardLinkBackend hardLinkBackend,
            SymlinkBackend symlinkBackend,
            OverwriteStore overwriteStore)
        {
            _logger = logger;
            _overwriteStore = overwriteStore;

            _backends = new Dictionary<DeploymentBackendType, IDeploymentBackend>
            {
                [DeploymentBackendType.Copy] = copyBackend,
                [DeploymentBackendType.HardLink] = hardLinkBackend,
                [DeploymentBackendType.Symlink] = symlinkBackend
            };

            _backupRootPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Data", "Backups");
        }

        /// <summary>
        /// 获取指定类型的后端。
        /// </summary>
        public IDeploymentBackend GetBackend(DeploymentBackendType type)
        {
            return _backends.TryGetValue(type, out var backend)
                ? backend
                : _backends[DeploymentBackendType.Copy]; // 默认降级为 Copy
        }

        /// <summary>
        /// 检测后端是否可用。
        /// </summary>
        public async Task<bool> IsBackendAvailableAsync(DeploymentBackendType type)
        {
            if (_backends.TryGetValue(type, out var backend))
                return await backend.CanUseAsync();
            return false;
        }

        /// <summary>
        /// 执行部署计划。
        /// 流程：创建事务 → 备份受影响文件 → 逐个执行操作 → 提交/回滚。
        /// </summary>
        public async Task<DeploymentTransaction> ExecuteAsync(DeploymentPlan plan)
        {
            if (!plan.HasChanges)
            {
                _logger.LogInformation("部署计划无变更，跳过");
                return DeploymentResultBuilder.BuildEmptyCommittedTransaction(plan);
            }

            // 选择后端（不可用时降级为 Copy）
            var backendType = plan.BackendType;
            if (!await IsBackendAvailableAsync(backendType))
            {
                _logger.LogWarning("后端 {Type} 不可用，降级为 Copy", backendType);
                backendType = DeploymentBackendType.Copy;
            }
            var backend = GetBackend(backendType);

            // 创建事务
            var backupDir = Path.Combine(_backupRootPath, Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(backupDir);

            var transaction = new DeploymentTransaction
            {
                PlanId = plan.Id,
                ProfileId = plan.ProfileId,
                HostGameName = plan.HostGameName,
                BackendType = backendType,
                BackupDirectory = backupDir,
                TotalOperations = plan.TotalCount,
                Status = DeploymentStatus.InProgress
            };

            LastTransaction = transaction;
            ProgressChanged?.Invoke(transaction);

            // S6: 立即持久化一次（Pending 状态），保证崩溃在备份/执行阶段也能被 CrashRecoveryScanner 识别
            await SaveTransactionLogAsync(transaction);

            _logger.LogInformation(
                "开始执行部署: {Id} ({Count} 个操作, 后端={Backend})",
                transaction.Id, plan.TotalCount, backendType);

            try
            {
                // 阶段 1: 备份所有需要备份的目标文件
                await BackupTargetFilesAsync(plan, backupDir);

                // 阶段 2: 逐个执行操作
                foreach (var operation in plan.Operations)
                {
                    await ExecuteOperationAsync(operation, backend, backupDir);
                    operation.IsExecuted = true;
                    transaction.ExecutedOperations.Add(operation);
                    transaction.CompletedOperations++;
                    ProgressChanged?.Invoke(transaction);
                }

                // 提交
                transaction.Status = DeploymentStatus.Committed;
                transaction.CompletedAt = DateTime.Now;
                _logger.LogInformation("部署成功提交: {Id}", transaction.Id);

                // v2.0 Phase 5: 为备份文件注册生成物
                foreach (var op in transaction.ExecutedOperations.Where(o => o.BackupPath != null))
                {
                    try
                    {
                        await _overwriteStore.RegisterAsync(
                            op.BackupPath!,
                            GeneratedArtifactType.DeploymentSnapshot,
                            $"备份: {op.RelativeTargetPath}",
                            sourcePackageKey: op.PackageKey,
                            sourceTransactionId: transaction.Id,
                            sourceDescription: $"部署事务 {transaction.Id.ToString("N")[..8]} 的备份");
                    }
                    catch { /* 注册失败不影响部署 */ }
                }
            }
            catch (Exception ex)
            {
                transaction.Status = DeploymentStatus.Failed;
                transaction.ErrorMessage = ex.Message;
                transaction.CompletedAt = DateTime.Now;
                _logger.LogError(ex, "部署失败: {Id}", transaction.Id);

                // 自动回滚
                try
                {
                    await RollbackAsync(transaction);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "自动回滚也失败了: {Id}", transaction.Id);
                }
            }
            finally
            {
                // 保存事务日志
                await SaveTransactionLogAsync(transaction);
                ProgressChanged?.Invoke(transaction);
            }

            return transaction;
        }

        /// <summary>
        /// 回滚事务：从备份恢复受影响的文件。
        /// 中途单步失败不再吞异常 — 累计到 RollbackFailures，最终 Status 标记为 PartiallyRolledBack
        /// 让 CrashRecoveryScanner 强制人工核查，避免"伪回滚成功"。
        /// </summary>
        public async Task RollbackAsync(DeploymentTransaction transaction)
        {
            if (!transaction.CanRollback)
            {
                _logger.LogWarning("事务 {Id} 状态为 {Status}，不可回滚",
                    transaction.Id, transaction.Status);
                return;
            }

            _logger.LogInformation("开始回滚事务: {Id}", transaction.Id);
            transaction.RollbackFailures.Clear();

            // 按执行顺序的逆序回滚
            foreach (var op in transaction.ExecutedOperations.AsEnumerable().Reverse())
            {
                try
                {
                    var action = RollbackActionPlanner.PlanRollback(op, File.Exists);
                    switch (action.Type)
                    {
                        case RollbackActionType.DeleteAdded:
                            if (File.Exists(action.TargetPath))
                            {
                                File.Delete(action.TargetPath);
                                CleanEmptyDirectories(Path.GetDirectoryName(action.TargetPath));
                            }
                            break;

                        case RollbackActionType.RestoreFromBackup:
                            if (!string.IsNullOrEmpty(action.BackupPath) && File.Exists(action.BackupPath))
                            {
                                var targetDir = Path.GetDirectoryName(action.TargetPath);
                                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                                    Directory.CreateDirectory(targetDir);
                                File.Copy(action.BackupPath, action.TargetPath, overwrite: true);
                            }
                            else
                            {
                                // Planner 已通过 File.Exists 校验，这里仍未命中说明并发删除/竞态
                                transaction.RollbackFailures.Add(new RollbackFailure(
                                    action.TargetPath,
                                    $"备份文件在回滚前消失: {action.BackupPath}"));
                                _logger.LogError("备份文件消失: {Backup} → 目标 {Target}",
                                    action.BackupPath, action.TargetPath);
                            }
                            break;

                        case RollbackActionType.BackupMissing:
                            transaction.RollbackFailures.Add(new RollbackFailure(
                                action.TargetPath,
                                $"备份文件不存在或已被删除: {action.BackupPath}"));
                            _logger.LogError("回滚失败 — 备份缺失: {Target} (备份记录: {Backup})",
                                action.TargetPath, action.BackupPath);
                            break;

                        case RollbackActionType.NoBackupRecorded:
                            // Remove/Replace 操作的备份从未生成（备份阶段被跳过 / 原文件不存在）
                            // 不算失败，但仍记录供审计
                            _logger.LogWarning("回滚跳过 — 操作未备份: {Target} (Type={Type})",
                                op.TargetPath, op.Type);
                            break;

                        case RollbackActionType.None:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    transaction.RollbackFailures.Add(new RollbackFailure(
                        op.TargetPath,
                        $"{ex.GetType().Name}: {ex.Message}"));
                    _logger.LogError(ex, "回滚操作失败: {Target}", op.TargetPath);
                }
            }

            transaction.Status = transaction.RollbackFailures.Count > 0
                ? DeploymentStatus.PartiallyRolledBack
                : DeploymentStatus.RolledBack;
            transaction.CompletedAt = DateTime.Now;
            await SaveTransactionLogAsync(transaction);

            if (transaction.Status == DeploymentStatus.PartiallyRolledBack)
            {
                _logger.LogError(
                    "事务部分回滚 — {Count} 个操作未恢复，需人工核查: {Id}",
                    transaction.RollbackFailures.Count, transaction.Id);
            }
            else
            {
                _logger.LogInformation("事务已回滚: {Id}", transaction.Id);
            }
        }

        /// <summary>
        /// 清理旧备份目录（保留最近 N 个）。
        /// </summary>
        public void CleanupOldBackups(int keepCount = 5)
        {
            if (!Directory.Exists(_backupRootPath)) return;

            var dirs = Directory.GetDirectories(_backupRootPath)
                .OrderByDescending(d => Directory.GetCreationTime(d))
                .Skip(keepCount)
                .ToList();

            foreach (var dir in dirs)
            {
                try
                {
                    Directory.Delete(dir, true);
                    _logger.LogDebug("清理旧备份: {Dir}", dir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理备份目录失败: {Dir}", dir);
                }
            }
        }

        /// <summary>
        /// 扫描 Backups 目录，加载所有事务历史记录。
        /// </summary>
        public async Task<List<DeploymentTransaction>> GetTransactionHistoryAsync()
        {
            var result = new List<DeploymentTransaction>();

            if (!Directory.Exists(_backupRootPath))
                return result;

            foreach (var dir in Directory.GetDirectories(_backupRootPath))
            {
                var logPath = Path.Combine(dir, "transaction.json");
                if (!File.Exists(logPath)) continue;

                try
                {
                    var json = await File.ReadAllTextAsync(logPath);
                    var tx = JsonSerializer.Deserialize<DeploymentTransaction>(json, JsonOptions);
                    if (tx != null)
                        result.Add(tx);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取事务日志失败: {Path}", logPath);
                }
            }

            return result.OrderByDescending(t => t.CreatedAt).ToList();
        }

        // ─── 内部方法 ───

        private async Task BackupTargetFilesAsync(DeploymentPlan plan, string backupDir)
        {
            foreach (var op in plan.Operations.Where(
                o => o.Type is DeploymentOperationType.Remove or DeploymentOperationType.Replace))
            {
                if (File.Exists(op.TargetPath))
                {
                    var backupPath = Path.Combine(backupDir,
                        $"{op.Id:N}{Path.GetExtension(op.TargetPath)}");
                    await Task.Run(() => File.Copy(op.TargetPath, backupPath));
                    op.BackupPath = backupPath;
                    _logger.LogDebug("备份文件: {Target} → {Backup}", op.TargetPath, backupPath);
                }
            }
        }

        private async Task ExecuteOperationAsync(
            DeploymentOperation operation, IDeploymentBackend backend, string backupDir)
        {
            switch (operation.Type)
            {
                case DeploymentOperationType.Add:
                case DeploymentOperationType.Replace:
                    if (string.IsNullOrEmpty(operation.SourcePath))
                        throw new InvalidOperationException(
                            $"Add/Replace 操作缺少 SourcePath: {operation.TargetPath}");
                    if (!File.Exists(operation.SourcePath))
                        throw new FileNotFoundException(
                            $"源文件不存在: {operation.SourcePath}");

                    await backend.DeployFileAsync(operation.SourcePath, operation.TargetPath);
                    break;

                case DeploymentOperationType.Remove:
                    await backend.RemoveFileAsync(operation.TargetPath);
                    break;
            }
        }

        private async Task SaveTransactionLogAsync(DeploymentTransaction transaction)
        {
            if (string.IsNullOrEmpty(transaction.BackupDirectory))
                return;

            try
            {
                if (!Directory.Exists(transaction.BackupDirectory))
                    Directory.CreateDirectory(transaction.BackupDirectory);

                var logPath = Path.Combine(transaction.BackupDirectory, "transaction.json");
                var json = JsonSerializer.Serialize(transaction, JsonOptions);
                await File.WriteAllTextAsync(logPath, json);
            }
            catch (Exception ex)
            {
                // W5: 不静默 — 把内存状态降级为 LogPersistenceFailed 让 CrashRecoveryScanner 拾取。
                // 仅在状态原本是"成功类"时降级；如果已经是 Failed/PartiallyRolledBack 不覆盖。
                if (transaction.Status is DeploymentStatus.Committed or DeploymentStatus.RolledBack)
                {
                    transaction.Status = DeploymentStatus.LogPersistenceFailed;
                    transaction.ErrorMessage = $"事务日志写入失败: {ex.Message}";
                }
                _logger.LogError(ex, "保存事务日志失败 — 事务 {Id} 状态降级为 LogPersistenceFailed",
                    transaction.Id);
            }
        }

        private static void CleanEmptyDirectories(string? directory)
        {
            while (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                if (Directory.GetFileSystemEntries(directory).Length > 0)
                    break;
                try
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
