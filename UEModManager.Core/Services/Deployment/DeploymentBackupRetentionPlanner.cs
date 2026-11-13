using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Deployment
{
    /// <summary>
    /// 备份根下的一个事务目录。
    /// </summary>
    /// <param name="Directory">目录完整路径。</param>
    /// <param name="Status">
    /// 事务状态；<c>null</c> 表示目录里根本没有 <c>transaction.json</c>
    /// （进程在建目录与首次落盘之间被杀留下的空壳）。
    /// <b>日志存在但读不出来的目录不要传进来</b>——那种目录可能正是崩溃恢复要用的，一律保留。
    /// </param>
    /// <param name="LastActivityUtc">事务完成时间；没有则用目录创建时间。</param>
    /// <param name="SizeBytes">目录内文件总大小。</param>
    /// <param name="HasDurableState">
    /// 这次事务是否带「部署文件清单」（2.1.0 起的事务都带）。带清单的事务，文件归属与游戏原始文件
    /// 都另有长期存储，备份只是回滚保险，可以按时间和总量清。
    /// <b>不带清单的是 2.1.0 之前留下的事务</b>：从旧版本直接升上来的用户，第一次部署时要靠这些
    /// 事务记录认领旧文件，被替换掉的游戏原始文件也只存在这些备份里——
    /// 对它们只沿用原先「最近 N 次」的规则，不按时间和总量提前清。
    /// </param>
    public sealed record DeploymentBackupEntry(
        string Directory,
        DeploymentStatus? Status,
        DateTime LastActivityUtc,
        long SizeBytes,
        bool HasDurableState = true);

    /// <summary>
    /// 部署事务备份的保留策略。三条上限同时生效，命中任何一条就清。
    /// </summary>
    /// <param name="MaxCount">最多保留几次已结束的事务。</param>
    /// <param name="MaxAge">已结束的事务最多保留多久。</param>
    /// <param name="MaxTotalBytes">
    /// 已结束事务的备份合计上限。最新的一次不受这条限制——
    /// 否则一次大改动刚做完，「撤销上一次部署」就没了。
    /// </param>
    /// <param name="OrphanGrace">没有事务日志的空壳目录放多久再清。</param>
    public sealed record DeploymentBackupRetentionPolicy(
        int MaxCount,
        TimeSpan MaxAge,
        long MaxTotalBytes,
        TimeSpan OrphanGrace)
    {
        /// <summary>
        /// 默认策略：10 次 / 3 天 / 2 GB。
        ///
        /// <para>
        /// 这些备份是「部署中途失败时回滚」用的保险。事务一旦提交，它们只剩一个用途：
        /// 在管理中心手动回滚那一次部署。游戏原始文件另有 <c>DeploymentState\Originals</c>
        /// 长期保存，MOD 文件本身在仓库里，所以清掉旧备份不会让任何东西找不回来。
        /// 此前只按「最近 10 次」清，不看大小也不看时间：停用十次大型 MOD 就能在系统盘上
        /// 压住几十 GB，而且不再部署就永远不清。
        /// </para>
        /// </summary>
        public static DeploymentBackupRetentionPolicy Default { get; } = new(
            MaxCount: 10,
            MaxAge: TimeSpan.FromDays(3),
            MaxTotalBytes: 2L * 1024 * 1024 * 1024,
            OrphanGrace: TimeSpan.FromDays(1));
    }

    /// <summary>
    /// 判定哪些部署事务备份目录可以删除。纯计算，不碰磁盘。
    /// </summary>
    public static class DeploymentBackupRetentionPlanner
    {
        /// <summary>
        /// 只有这几种状态的事务才允许清理。其余状态（进行中、失败、部分回滚、日志落盘失败…）
        /// 都还可能需要回滚或人工核查，无论多旧多大都保留。
        /// </summary>
        public static bool IsCleanupSafeStatus(DeploymentStatus status)
            => status is DeploymentStatus.Committed
                or DeploymentStatus.RolledBack
                or DeploymentStatus.Dismissed;

        /// <summary>返回应删除的目录，顺序为从旧到新无保证，调用方逐个删即可。</summary>
        public static IReadOnlyList<string> PlanDeletions(
            IEnumerable<DeploymentBackupEntry> entries,
            DateTime nowUtc,
            DeploymentBackupRetentionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(policy);

            var all = entries.ToList();
            var deletions = new List<string>();

            // 没有事务日志的空壳：过了宽限期就清。宽限期是为了不碰正在创建中的目录。
            deletions.AddRange(all
                .Where(e => e.Status is null && nowUtc - e.LastActivityUtc > policy.OrphanGrace)
                .Select(e => e.Directory));

            var finished = all
                .Where(e => e.Status is { } status && IsCleanupSafeStatus(status))
                .OrderByDescending(e => e.LastActivityUtc)
                .ToList();

            long keptBytes = 0;
            var keptCount = 0;
            for (var i = 0; i < finished.Count; i++)
            {
                var entry = finished[i];
                var size = Math.Max(0, entry.SizeBytes);

                var overCount = keptCount >= Math.Max(0, policy.MaxCount);
                // 时间与总量两条只管带清单的事务，原因见 DeploymentBackupEntry.HasDurableState。
                var tooOld = entry.HasDurableState && nowUtc - entry.LastActivityUtc > policy.MaxAge;
                // 最新一次（i == 0）不受总量上限约束，见策略注释。
                var overBudget = entry.HasDurableState && i > 0 && keptBytes + size > policy.MaxTotalBytes;

                if (overCount || tooOld || overBudget)
                {
                    deletions.Add(entry.Directory);
                    continue;
                }

                keptCount++;
                keptBytes += size;
            }

            return deletions;
        }
    }
}
