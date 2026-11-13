using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>一轮清理的结果。</summary>
    /// <param name="FreedBytes">本轮释放的字节数（部署备份 + 旧部署快照 + 解压临时目录）。</param>
    public sealed record TempCacheSweepResult(long FreedBytes);

    /// <summary>
    /// 临时数据的定时清理：启动后跑一次，之后每隔 <see cref="Interval"/> 再跑。
    ///
    /// <para>
    /// 存在的理由：此前清理都挂在「下一次同类操作」上——部署备份只在下一次部署提交后清，
    /// 解压临时目录只在下一次导入前清。用户装完 MOD 就一直玩游戏的话，这些东西就一直占着盘。
    /// 本类管三样：
    /// <list type="bullet">
    /// <item>部署事务备份：按 <c>DeploymentBackupRetentionPolicy</c> 的次数 / 时间 / 总量上限裁剪；</item>
    /// <item>老版本留下的「部署快照」副本：新版本不再产生，存量整目录删掉；</item>
    /// <item>导入时进程被强杀留下的解压临时目录。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 每一步各自兜底：清理是尽力而为，任何一步失败都只留日志，不影响其余步骤，更不能影响主流程。
    /// 本类只删上面三样自己认得的东西，不扫别的目录。
    /// </para>
    /// </summary>
    public sealed class TempCacheJanitor : IDisposable
    {
        /// <summary>启动后等多久跑第一次。避开启动时最忙的那一段（数据迁移、崩溃恢复、首屏加载）。</summary>
        public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

        /// <summary>两次清理的间隔。</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

        private readonly ILogger<TempCacheJanitor> _logger;
        private readonly Func<DeploymentService> _deployment;
        private readonly Func<OverwriteStore> _overwriteStore;
        private readonly Func<RepositoryReclaimService> _reclaim;
        private readonly Func<ObjectStore> _objectStore;

        private Timer? _timer;
        private int _running;

        /// <remarks>
        /// 依赖以委托形式传入、到真正清理时才取：这几个服务在构造时就会把数据目录定下来，
        /// 而本类在主窗口构造阶段就被启动。若在这里直接要实例，会把它们的构造提前到
        /// 首次引导改仓库位置之前，定下一个随后就作废的路径。
        /// </remarks>
        public TempCacheJanitor(
            ILogger<TempCacheJanitor> logger,
            Func<DeploymentService> deployment,
            Func<OverwriteStore> overwriteStore,
            Func<RepositoryReclaimService> reclaim,
            Func<ObjectStore> objectStore)
        {
            _logger = logger;
            _deployment = deployment;
            _overwriteStore = overwriteStore;
            _reclaim = reclaim;
            _objectStore = objectStore;
        }

        /// <summary>开始定时清理。重复调用无效果。</summary>
        public void Start()
        {
            var timer = new Timer(_ => _ = RunOnceAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (Interlocked.CompareExchange(ref _timer, timer, null) != null)
            {
                timer.Dispose();
                return;
            }
            timer.Change(FirstRunDelay, Interval);
        }

        /// <summary>
        /// 立即跑一轮。上一轮还没跑完时直接返回（不排队）——下一个周期自然会再跑。
        /// </summary>
        public async Task<TempCacheSweepResult> RunOnceAsync()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return new TempCacheSweepResult(0);

            long freed = 0;
            try
            {
                // ① 老版本留下的部署快照副本。先删它：占用最大，且与下面两步互不相干。
                try
                {
                    var overwriteStore = _overwriteStore();
                    freed += await Task.Run(overwriteStore.PurgeDeploymentSnapshotFolders).ConfigureAwait(false);
                    await overwriteStore.PurgeDeploymentSnapshotsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Janitor] 清理旧的部署快照失败");
                }

                // ② 部署事务备份。走部署服务自己的清理链，不会与「部署提交后的清理」撞车。
                try
                {
                    await _deployment().ScheduleBackupCleanup().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Janitor] 清理部署备份失败");
                }

                // ③ 导入中途被强杀留下的解压临时目录（有静置期保护，不会碰正在进行的导入）。
                try
                {
                    var tempRoot = Path.Combine(_objectStore().RepositoryRoot, ".import-tmp");
                    var reclaim = _reclaim();
                    var reclaimed = await Task.Run(() => reclaim.ReclaimStaleImportTemp(tempRoot)).ConfigureAwait(false);
                    freed += reclaimed.FreedBytes;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Janitor] 清理解压临时目录失败");
                }

                if (freed > 0)
                    _logger.LogInformation("[Janitor] 本轮清理释放 {MB:F1} MB", freed / 1048576d);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }

            return new TempCacheSweepResult(freed);
        }

        public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Dispose();
    }
}
