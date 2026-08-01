using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using UEModManager.Services.Backends;

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

        /// <summary>
        /// 计划内的全部操作（含备份路径映射），在备份阶段结束、执行开始之前一次性落盘。
        ///
        /// 存在的理由：执行循环只改内存中的 <see cref="ExecutedOperations"/>，
        /// 进程若在循环中被杀/断电，磁盘上的 ExecutedOperations 是空数组，
        /// "备份文件 → 目标路径"的映射随内存一起丢失，备份目录里的文件就成了无主数据。
        /// 有了这份快照，崩溃恢复即使拿不到执行进度也能完整回滚。
        ///
        /// 对未真正执行的操作做回滚是幂等的：Add 的目标文件不存在会被跳过，
        /// Remove/Replace 从备份恢复得到的就是原文件本身。
        /// </summary>
        public List<DeploymentOperation> PlannedOperations { get; set; } = [];

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
        /// 本次部署里"没能按用户选的方式做"的降级（已按原因聚合，每种原因一条）。
        ///
        /// 存进事务日志而不只是发个事件：用户在管理中心翻历史事务、或者排障时看
        /// transaction.json，都应当能看出"这一次其实是复制的"——
        /// 只发事件的话，这个事实在弹窗关掉之后就再也查不到了。
        /// 旧日志里没有这个字段，反序列化后为空列表，语义正好是"没发生过降级"。
        /// </summary>
        public List<DeploymentDegradationSummary> Degradations { get; set; } = [];

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

        /// <summary>
        /// 是否可回滚。
        /// 包含 InProgress：崩溃留下的事务正是停在这个状态，若不允许回滚，
        /// CrashRecoveryScanner 判定的 RollbackRecommended 会永远无法执行，
        /// 事务卡在 InProgress 每次启动重复弹窗。
        /// </summary>
        [JsonIgnore]
        public bool CanRollback => Status is DeploymentStatus.Committed
                                          or DeploymentStatus.Failed
                                          or DeploymentStatus.InProgress
                                          or DeploymentStatus.PartiallyRolledBack;

        /// <summary>
        /// 回滚时应遍历的操作集合：优先用执行记录，
        /// 崩溃导致执行记录为空时回落到备份阶段落盘的完整计划。
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<DeploymentOperation> RollbackSource
            => ExecutedOperations.Count > 0 ? ExecutedOperations : PlannedOperations;
    }

    /// <summary>
    /// 一次回滚的真实结果。
    /// RollbackAsync 曾是 void，调用方无从区分"回滚成功"与"因状态不可回滚而直接返回"，
    /// 于是崩溃恢复会在什么都没做的情况下向用户报告"已回滚"。
    /// </summary>
    public sealed record RollbackOutcome(
        bool Attempted,
        bool Succeeded,
        IReadOnlyList<RollbackFailure> Failures,
        string? SkipReason = null)
    {
        /// <summary>未执行回滚（状态不允许）。</summary>
        public static RollbackOutcome Skipped(string reason)
            => new(Attempted: false, Succeeded: false, Failures: [], SkipReason: reason);

        /// <summary>回滚已执行且全部成功。</summary>
        public static RollbackOutcome Complete()
            => new(Attempted: true, Succeeded: true, Failures: []);

        /// <summary>回滚已执行但部分操作失败。</summary>
        public static RollbackOutcome Partial(IReadOnlyList<RollbackFailure> failures)
            => new(Attempted: true, Succeeded: false, Failures: failures);
    }

    /// <summary>
    /// 单个回滚操作的失败记录。
    /// 用于 PartiallyRolledBack 状态下向用户展示"哪些文件没回滚干净"。
    /// </summary>
    public sealed record RollbackFailure(
        string TargetPath,
        string Reason);
}
