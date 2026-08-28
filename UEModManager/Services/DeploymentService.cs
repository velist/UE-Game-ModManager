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
using UEModManager.Services.Persistence;

namespace UEModManager.Services
{
    /// <summary>
    /// 部署执行服务。
    /// 接收 DeploymentPlan，创建事务，执行备份 → 部署 → 提交/回滚。
    /// </summary>
    public class DeploymentService
    {
        /// <summary>执行循环中每隔多少个操作把进度落盘一次。</summary>
        private const int ExecutionLogFlushInterval = 25;

        private readonly ILogger<DeploymentService> _logger;
        private readonly IReadOnlyDictionary<DeploymentBackendType, IDeploymentBackend> _backends;
        private readonly OverwriteStore _overwriteStore;
        private readonly string _backupRootPath;

        /// <summary>最近一次事务（用于 UI 展示和回滚）。</summary>
        public DeploymentTransaction? LastTransaction { get; private set; }

        /// <summary>部署进度变化事件。</summary>
        public event Action<DeploymentTransaction>? ProgressChanged;

        /// <summary>
        /// 本次部署发生过降级、且事务成功提交时抛出一次（已按原因聚合，不是每个文件一次）。
        ///
        /// <para>
        /// <b>只在提交成功时抛。</b>部署失败有自己的错误呈现，在一个"已自动回滚"的错误框后面
        /// 再补一句"顺便说这次没用上硬链接"，只会让用户以为两件事有因果关系。
        /// 降级本身不是错误——MOD 装好了，只是多占了一份空间。
        /// </para>
        ///
        /// <para>
        /// 呈现放在 UI 层（<c>MainWindow</c> 订阅一次，覆盖全部部署入口：单个开关、批量开关、
        /// 导入后自动部署、启动前部署）。本服务只负责把事实送出来，不决定弹不弹——
        /// "同一种情形只说一次"的判定在 Core 的 <see cref="DeploymentDegradationNotice"/>，
        /// 记账靠用户偏好，两者都不属于部署执行。
        /// </para>
        /// </summary>
        public event Action<DeploymentTransaction>? DegradationDetected;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <param name="backends">
        /// 由 DI 汇集的全部部署后端。此前这里是写死的 <c>CopyBackend</c> + <c>HardLinkBackend</c>
        /// 两个具体类型，导致新增后端必须改本类的构造函数签名——接口是真的，扩展点是假的。
        /// 改为 <see cref="IEnumerable{T}"/> 后，新增后端只需在 <c>App.xaml.cs</c> 注册一处。
        /// <para>
        /// 注意这**不是**插件式加载：后端仍需编译进主项目，全项目没有任何
        /// <c>Assembly.Load</c>/<c>AssemblyLoadContext</c>。区别只是从"改两处"变成"改一处"。
        /// </para>
        /// </param>
        public DeploymentService(
            ILogger<DeploymentService> logger,
            IEnumerable<IDeploymentBackend> backends,
            OverwriteStore overwriteStore)
        {
            _logger = logger;
            _overwriteStore = overwriteStore;

            // Symlink 后端已下线：普通用户需开发者模式/管理员权限，实际不可用。
            // 旧数据中的 Symlink 计划经 GetBackend 自动降级为 Copy。
            _backends = DeploymentBackendRegistry.Build(
                backends, message => _logger.LogWarning("{Message}", message));

            _logger.LogInformation("已装配 {Count} 个部署后端: {Types}",
                _backends.Count, string.Join(", ", _backends.Values.Select(b => b.DisplayName)));

            _backupRootPath = Infrastructure.AppPaths.DeploymentBackupsDirectory;
        }

        /// <summary>
        /// 获取指定类型的后端。找不到时兜底为 Copy —— 这是 Symlink 这类已下线后端
        /// 留在旧计划里时的必经之路，属于预期行为，但<b>必须留痕</b>：
        /// 兜底一旦发生，用户选的部署方式就被整体换掉了，
        /// 而此前这里连一条日志都没有，排障时看不出后端是怎么变成 Copy 的。
        /// </summary>
        public IDeploymentBackend GetBackend(DeploymentBackendType type)
        {
            if (_backends.TryGetValue(type, out var backend)) return backend;

            _logger.LogWarning("未注册的部署后端 {Type}，兜底为 Copy", type);
            return _backends[DeploymentBackendType.Copy];
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
            //
            // 降级从这一步就开始记：后端不可用此前同样只写一条 LogWarning，
            // 用户在设置里选的部署方式被整体换掉，界面上没有任何痕迹。
            var degradations = new DeploymentDegradationCollector();
            var backendType = plan.BackendType;
            if (!await IsBackendAvailableAsync(backendType))
            {
                _logger.LogWarning("后端 {Type} 不可用，降级为 Copy", backendType);
                degradations.Add(new DeploymentDegradation(
                    DeploymentDegradationKind.BackendUnavailable,
                    string.Empty, string.Empty,
                    $"后端 {backendType} 未注册或 CanUseAsync 返回 false"));
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

            // 订阅后端的降级上报。用 is 探测而不是要求所有后端都实现：
            // IDeploymentDegradationReporter 是可选能力，复制类后端没有降级这回事，
            // samples/UEModManager.SampleBackend 那样的外部实现也不必跟着改。
            // 订阅紧挨着 try 放，中间不留任何可能抛出的语句 —— 否则这个订阅会永久挂在
            // 单例后端上，之后每一次部署的降级都会重复累进这份已经没人要的收集器里。
            var reporter = backend as IDeploymentDegradationReporter;
            if (reporter != null) reporter.Degraded += degradations.Add;

            try
            {
                // 阶段 1: 备份所有需要备份的目标文件
                await BackupTargetFilesAsync(plan, backupDir);

                // 阶段 1.5: 备份已完成，此刻每个操作的 BackupPath 都已确定。
                // 必须在开始动游戏文件之前把这份"备份 → 目标"映射落盘：
                // 否则进程在下面的循环中被杀，磁盘上就只剩一个空的 ExecutedOperations，
                // 备份目录里的文件将无法对应回目标路径，崩溃恢复没有任何可回滚的信息。
                transaction.PlannedOperations = plan.Operations.ToList();
                await SaveTransactionLogAsync(transaction);

                // 阶段 2: 逐个执行操作
                //
                // 进度事件经闸门节流：订阅方 DeployPreviewDialog.OnDeployProgress 里是
                // Dispatcher.Invoke（同步阻塞marshal到 UI 线程），每个操作发一次的话，
                // 上万文件的整合包会产生上万次跨线程同步调用，UI 反而被进度更新拖垮。
                var progressGate = new ProgressEmitGate(plan.Operations.Count, DateTime.Now);

                for (var i = 0; i < plan.Operations.Count; i++)
                {
                    var operation = plan.Operations[i];
                    transaction.ExecutedOperations.Add(operation);
                    await ExecuteOperationAsync(operation, backend, backupDir);
                    operation.IsExecuted = true;
                    transaction.CompletedOperations++;

                    if (progressGate.ShouldEmit(transaction.CompletedOperations, DateTime.Now))
                        ProgressChanged?.Invoke(transaction);

                    // 节流落盘执行进度：崩溃恢复靠 PlannedOperations 已能完整回滚，
                    // 这里只是让恢复界面能显示"崩溃时进行到哪一步"，故无需每步都写。
                    if ((i + 1) % ExecutionLogFlushInterval == 0)
                        await SaveTransactionLogAsync(transaction);
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
                    catch (Exception ex)
                    {
                        // 注册失败不影响部署（备份文件本身已经在磁盘上），但不能一声不吭：
                        // 没登记上的备份不会进生成物索引，将来的清理与回收都看不见它，
                        // 排查"备份目录越攒越大"时这条日志是唯一的线索。
                        _logger.LogWarning(ex, "备份文件登记为生成物失败: {Backup}", op.BackupPath);
                    }
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
                if (reporter != null) reporter.Degraded -= degradations.Add;

                // 聚合后落进事务：一次部署上万个文件的降级是整批同因的，
                // 存成"每种原因一条 + 文件数"，事务日志和告知都只看这一份。
                transaction.Degradations = degradations.Summarize().ToList();
                if (transaction.Degradations.Count > 0)
                {
                    _logger.LogWarning("部署 {Id} 发生降级: {Summary}",
                        transaction.Id,
                        string.Join("；", transaction.Degradations.Select(
                            d => $"{d.Kind} × {d.FileCount} 个文件（{d.Detail}）")));
                }

                // 保存事务日志
                await SaveTransactionLogAsync(transaction);
                // 终态事件不经闸门节流：这一发承载的是 Committed/Failed 的最终状态，
                // 与循环里的进度更新不是一回事，任何情况下都必须送达。
                ProgressChanged?.Invoke(transaction);

                if (transaction.Status == DeploymentStatus.Committed)
                {
                    ScheduleBackupCleanup();
                    RaiseDegradationDetected(transaction);
                }
            }

            return transaction;
        }

        /// <summary>
        /// 抛出降级告知事件。
        /// <b>包一层 try 是必需的</b>：这一发在 <c>ExecuteAsync</c> 的 finally 里，
        /// 订阅方（UI）抛出的异常会盖掉部署本身的异常与状态，
        /// 让"部署失败"变成一个指向弹窗代码的莫名其妙的错误。
        /// 告知送不出去只是少一次提示，事务日志里那条降级记录仍然在。
        /// </summary>
        private void RaiseDegradationDetected(DeploymentTransaction transaction)
        {
            if (transaction.Degradations.Count == 0) return;

            try
            {
                DegradationDetected?.Invoke(transaction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "派发部署降级告知失败: {Id}", transaction.Id);
            }
        }

        /// <summary>
        /// 回滚事务：从备份恢复受影响的文件。        /// 中途单步失败不再吞异常 — 累计到 RollbackFailures，最终 Status 标记为 PartiallyRolledBack
        /// 让 CrashRecoveryScanner 强制人工核查，避免"伪回滚成功"。
        ///
        /// 返回 <see cref="RollbackOutcome"/> 而非 void：调用方（尤其是崩溃恢复）
        /// 必须能区分"回滚成功"与"因状态不可回滚而什么都没做"。
        /// </summary>
        public async Task<RollbackOutcome> RollbackAsync(DeploymentTransaction transaction)
        {
            if (!transaction.CanRollback)
            {
                var reason = $"事务状态为 {transaction.Status}，不可回滚";
                _logger.LogWarning("事务 {Id} {Reason}", transaction.Id, reason);
                return RollbackOutcome.Skipped(reason);
            }

            // 崩溃场景下 ExecutedOperations 可能为空，此时回落到备份阶段落盘的完整计划。
            var operations = transaction.RollbackSource;
            if (operations.Count == 0)
            {
                const string reason = "事务既无执行记录也无计划快照，无可回滚的信息";
                _logger.LogError("事务 {Id} {Reason}", transaction.Id, reason);
                return RollbackOutcome.Skipped(reason);
            }

            _logger.LogInformation("开始回滚事务: {Id}（{Count} 个操作，来源={Source}）",
                transaction.Id,
                operations.Count,
                transaction.ExecutedOperations.Count > 0 ? "执行记录" : "计划快照");
            transaction.RollbackFailures.Clear();

            // 按执行顺序的逆序回滚
            foreach (var op in operations.AsEnumerable().Reverse())
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
                                CleanEmptyDirectoriesWithinDeploymentRoot(op);
                            }
                            break;

                        case RollbackActionType.RestoreFromBackup:
                            if (!string.IsNullOrEmpty(action.BackupPath) && File.Exists(action.BackupPath))
                            {
                                var targetDir = Path.GetDirectoryName(action.TargetPath);
                                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                                    Directory.CreateDirectory(targetDir);
                                // 必须先删除目标再复制，不能直接 overwrite。
                                //
                                // File.Copy(overwrite:true) 走 Win32 CopyFile 的 CREATE_ALWAYS，它是**原地截断并写入
                                // 既有文件**、文件标识不变。而硬链接部署下目标与仓库里的 MOD 源文件是同一份数据，
                                // 于是这次恢复会穿透过去，把仓库源文件的内容改成游戏原版文件——一次失败部署的回滚
                                // 会把用户仓库里的 MOD 销毁，而仓库正是用户以为的干净备份。
                                //
                                // File.Delete 删的是目录项、不碰共享数据，源文件毫发无损。
                                // HardLinkBackend.DeployFileAsync 建链接前也是这么做的，两处保持一致。
                                if (File.Exists(action.TargetPath))
                                    File.Delete(action.TargetPath);
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
                return RollbackOutcome.Partial(transaction.RollbackFailures.ToList());
            }

            _logger.LogInformation("事务已回滚: {Id}", transaction.Id);
            return RollbackOutcome.Complete();
        }

        /// <summary>
        /// 清理旧备份目录（保留最近 N 个终态事务）。
        /// </summary>
        public void CleanupOldBackups(int keepCount = 10)
        {
            if (!Directory.Exists(_backupRootPath)) return;

            var dirs = Directory.GetDirectories(_backupRootPath)
                .Select(LoadBackupTransaction)
                .Where(x => x.Transaction != null && IsCleanupSafeStatus(x.Transaction.Status))
                .OrderByDescending(x => x.Transaction!.CompletedAt ?? Directory.GetCreationTime(x.Directory))
                .Skip(keepCount)
                .ToList();

            foreach (var (dir, _) in dirs)
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

        private void ScheduleBackupCleanup()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    CleanupOldBackups();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "部署提交后清理旧备份失败");
                }
            });
        }

        private (string Directory, DeploymentTransaction? Transaction) LoadBackupTransaction(string directory)
        {
            var logPath = Path.Combine(directory, "transaction.json");
            if (!File.Exists(logPath))
            {
                return (directory, null);
            }

            try
            {
                var json = File.ReadAllText(logPath);
                return (directory, JsonSerializer.Deserialize<DeploymentTransaction>(json, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "跳过无法解析的备份目录: {Dir}", directory);
                return (directory, null);
            }
        }

        private static bool IsCleanupSafeStatus(DeploymentStatus status)
            => status is DeploymentStatus.Committed
                or DeploymentStatus.RolledBack
                or DeploymentStatus.Dismissed;

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
                    CleanEmptyDirectoriesWithinDeploymentRoot(operation);
                    break;
            }
        }

        /// <summary>
        /// 删除目标文件后清理它留下的空目录，范围严格限制在该操作的部署根之内。
        /// 反推不出部署根时不清理：宁可留下一个空目录，也不做无边界的向上删除
        /// （旧实现会把 ~mods 目录本身一并删掉，上层若也空还会继续上删）。
        /// </summary>
        private void CleanEmptyDirectoriesWithinDeploymentRoot(DeploymentOperation operation)
        {
            var root = EmptyDirectoryCleaner.ResolveDeploymentRoot(
                operation.TargetPath, operation.RelativeTargetPath);

            if (root == null)
            {
                _logger.LogDebug("无法反推部署根，跳过空目录清理: {Target}", operation.TargetPath);
                return;
            }

            var removed = EmptyDirectoryCleaner.CleanUpwards(
                Path.GetDirectoryName(operation.TargetPath), root);

            if (removed > 0)
                _logger.LogDebug("清理了 {Count} 个空目录（限于部署根 {Root}）", removed, root);
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
                // transaction.json 是崩溃恢复的唯一依据，必须原子写：
                // 非原子写在断电/被杀时会留下截断的 json，恢复扫描既读不出状态也拿不到备份映射。
                await AtomicFileWriter.WriteAllTextAsync(logPath, json);
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
    }
}
