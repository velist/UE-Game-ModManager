using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UEModManager.Services.Repository;

namespace UEModManager.Services
{
    /// <summary>
    /// 一次回收的执行结果。
    /// </summary>
    /// <param name="DeletedCount">实际删掉的目录数。</param>
    /// <param name="FreedBytes">实际释放的字节数（按计划里的统计值累加）。</param>
    /// <param name="Failures">删不掉的目录及原因（文件被占用/权限不足），下次仍可重试。</param>
    public sealed record RepositoryReclaimResult(
        int DeletedCount,
        long FreedBytes,
        IReadOnlyList<(string DirectoryName, string Error)> Failures);

    /// <summary>
    /// 仓库孤儿对象的事后回收。
    ///
    /// <para><b>为什么需要它</b></para>
    /// 包实体（<see cref="ObjectStore"/> 的目录）与包索引（<see cref="PackageRepository"/> 的 JSON）
    /// 是两份状态，导入是"先逐个文件落盘、最后才写 manifest + 索引"。
    /// <see cref="PackageImportService"/> 已经在 catch 里做了即时补偿删除，但那条路兜不住两种情况：
    /// <list type="number">
    ///   <item>补偿本身删不掉（文件被杀毒软件/游戏占用），只留下一行 error 日志；</item>
    ///   <item>进程被强杀（任务管理器结束进程、断电、OOM），catch 根本不会执行。</item>
    /// </list>
    /// 于是磁盘上留下有 <c>files/</c>、无 manifest、索引里也没有记录的目录：占着几十 GB，
    /// 用户在界面上既看不见也删不掉。本服务负责把这类残留找出来并（在用户确认后）删掉。
    ///
    /// <para><b>分工</b></para>
    /// "哪些目录是孤儿"的判定全部在 <see cref="RepositoryReclaimPlanner"/>（Core，纯函数，有单测），
    /// 本类只做 IO：扫描磁盘填探测结果、读索引文件、执行删除。
    ///
    /// <para><b>删除动作的克制</b></para>
    /// 本类**不会**自己决定何时删。<see cref="BuildPlan"/> 是纯只读的，
    /// <see cref="Reclaim"/> 只删调用方传进来的、且计划里判定为
    /// <see cref="RepositoryEntryDisposition.Reclaimable"/> 的条目。
    /// 包级回收一律走用户确认；只有 <see cref="ReclaimStaleImportTemp"/> 是自动的——
    /// 它的判据（目录名是本程序生成的 <c>uemod_import_*</c> + 已静置一小时）不存在歧义。
    /// </summary>
    public class RepositoryReclaimService
    {
        private readonly ILogger<RepositoryReclaimService> _logger;
        private readonly ObjectStore _objectStore;
        private readonly string _dataDirectory;

        public RepositoryReclaimService(ILogger<RepositoryReclaimService> logger, ObjectStore objectStore)
            : this(logger, objectStore, Infrastructure.AppPaths.DataDirectory)
        {
        }

        /// <summary>
        /// 指定索引目录的构造函数（测试用）。DI 走上面的双参数构造函数——
        /// 容器无法解析 string，不会误选此重载。理由同 <see cref="PackageRepository"/>：
        /// 测试若不注入位置就会读开发者真实的 <c>%LOCALAPPDATA%\UEModManager\Data</c>，
        /// 而本服务读到的索引决定了它认为哪些目录"无主"——读错索引就是误删的直接来源。
        /// </summary>
        public RepositoryReclaimService(
            ILogger<RepositoryReclaimService> logger, ObjectStore objectStore, string dataDirectory)
        {
            _logger = logger;
            _objectStore = objectStore;
            _dataDirectory = dataDirectory;
        }

        /// <summary>
        /// 扫描仓库根，产出回收计划。**纯只读**，不删任何东西。
        /// </summary>
        public RepositoryReclaimPlan BuildPlan()
            => BuildPlan(DateTime.UtcNow, RepositoryReclaimPlanner.DefaultQuietPeriod);

        /// <summary>
        /// 扫描仓库根，产出回收计划（可注入时钟与静置期，供测试）。
        /// </summary>
        public RepositoryReclaimPlan BuildPlan(DateTime nowUtc, TimeSpan quietPeriod)
        {
            var (registeredKeys, scanComplete) = LoadRegisteredKeys();
            var probes = new List<RepositoryEntryProbe>();

            foreach (var key in _objectStore.EnumeratePackageKeys())
            {
                var dir = Path.Combine(_objectStore.RepositoryRoot, key);
                var hasManifest = File.Exists(Path.Combine(dir, "manifest.json"));

                // 只对真正的候选目录做深探测：递归求子树最后写入时间与大小在大仓库上很贵，
                // 而 Planner 对"已登记 / 有 manifest / 内部目录"根本不看这些字段。
                probes.Add(RepositoryReclaimPlanner.NeedsDeepProbe(key, hasManifest, registeredKeys)
                    ? DeepProbe(dir, key, hasManifest)
                    : new RepositoryEntryProbe(key, hasManifest, default, 0, Array.Empty<string>(), Array.Empty<string>()));
            }

            var plan = RepositoryReclaimPlanner.Plan(probes, registeredKeys, scanComplete, nowUtc, quietPeriod);

            if (plan.Reclaimable.Count > 0 || plan.Unregistered.Count > 0)
            {
                _logger.LogWarning(
                    "[Reclaim] 仓库扫描: {Reclaimable} 个可回收残留（{Bytes} 字节）, {Unregistered} 个失联包",
                    plan.Reclaimable.Count, plan.ReclaimableBytes, plan.Unregistered.Count);
                foreach (var entry in plan.Reclaimable.Concat(plan.Unregistered))
                    _logger.LogWarning("[Reclaim] {Dir}: {Disposition} — {Reason}",
                        entry.DirectoryName, entry.Disposition, entry.Reason);
            }

            return plan;
        }

        /// <summary>
        /// 执行回收：删除计划中判定为 <see cref="RepositoryEntryDisposition.Reclaimable"/> 的目录。
        ///
        /// <para>
        /// 传进来的计划里非 Reclaimable 的条目会被忽略而不是"顺手也删了"——
        /// 判定与执行分离的意义就在于执行方不再自行解释判据。
        /// 删不掉的（被占用/权限不足）只记进 <see cref="RepositoryReclaimResult.Failures"/>，
        /// 不抛异常：一个删不掉不该让其余的也回收不了，而且下一次扫描仍会把它找出来。
        /// </para>
        /// </summary>
        public RepositoryReclaimResult Reclaim(RepositoryReclaimPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            // 回收是递归删除仓库根下的目录，不经过 ObjectStore 的写方法，所以自己问一次搬移闸门。
            // 而且这里比别处更要紧：计划是搬移<b>之前</b>扫出来的，搬移之后那些目录名指的已经是
            // 另一个位置里的东西，照着老计划删等于按一份过期名单去删一个新仓库。
            _objectStore.ThrowIfRelocating("清理仓库残留");

            var failures = new List<(string, string)>();
            var deleted = 0;
            long freed = 0;

            foreach (var entry in plan.Reclaimable)
            {
                var dir = Path.Combine(_objectStore.RepositoryRoot, entry.DirectoryName);
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);

                    deleted++;
                    freed += entry.SizeBytes;
                    _logger.LogInformation("[Reclaim] 已回收残留目录 {Dir}（{Bytes} 字节）：{Reason}",
                        entry.DirectoryName, entry.SizeBytes, entry.Reason);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Reclaim] 回收残留目录失败: {Dir}", entry.DirectoryName);
                    failures.Add((entry.DirectoryName, ex.Message));
                }
            }

            return new RepositoryReclaimResult(deleted, freed, failures);
        }

        /// <summary>
        /// 回收 <c>.import-tmp</c> 下进程被强杀留下的解压临时目录。
        ///
        /// <para>
        /// 由 <see cref="PackageImportService"/> 在每次压缩包导入前调用：那时用户已经明确表达了
        /// "我要导入"，顺带把上次没能清掉的解压产物（可能几十 GB）回收掉，比另起一个
        /// 用户看不懂的按钮更合理。判据无歧义故不需要确认——目录名是本程序生成的
        /// <c>uemod_import_{Guid}</c>，且整整一小时没有写入。
        /// </para>
        /// </summary>
        public RepositoryReclaimResult ReclaimStaleImportTemp(string tempRoot)
            => ReclaimStaleImportTemp(tempRoot, DateTime.UtcNow, RepositoryReclaimPlanner.DefaultQuietPeriod);

        /// <summary>回收陈旧解压临时目录（可注入时钟与静置期，供测试）。</summary>
        public RepositoryReclaimResult ReclaimStaleImportTemp(
            string tempRoot, DateTime nowUtc, TimeSpan quietPeriod)
        {
            if (!Directory.Exists(tempRoot))
                return new RepositoryReclaimResult(0, 0, Array.Empty<(string, string)>());

            List<RepositoryEntryProbe> probes;
            try
            {
                probes = Directory.EnumerateDirectories(tempRoot)
                    .Select(d => DeepProbe(d, Path.GetFileName(d), hasManifest: false))
                    .ToList();
            }
            catch (Exception ex)
            {
                // 扫不动就不回收：临时目录的残留只是占空间，为它中断导入不值得。
                _logger.LogWarning(ex, "[Reclaim] 扫描解压临时目录失败: {Root}", tempRoot);
                return new RepositoryReclaimResult(0, 0, Array.Empty<(string, string)>());
            }

            var entries = RepositoryReclaimPlanner.PlanImportTemp(probes, nowUtc, quietPeriod);
            var failures = new List<(string, string)>();
            var deleted = 0;
            long freed = 0;

            foreach (var entry in entries.Where(e => e.Disposition == RepositoryEntryDisposition.Reclaimable))
            {
                var dir = Path.Combine(tempRoot, entry.DirectoryName);
                try
                {
                    Directory.Delete(dir, true);
                    deleted++;
                    freed += entry.SizeBytes;
                    _logger.LogInformation("[Reclaim] 已回收解压临时目录 {Dir}（{Bytes} 字节）：{Reason}",
                        entry.DirectoryName, entry.SizeBytes, entry.Reason);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Reclaim] 回收解压临时目录失败: {Dir}", entry.DirectoryName);
                    failures.Add((entry.DirectoryName, ex.Message));
                }
            }

            return new RepositoryReclaimResult(deleted, freed, failures);
        }

        // ─── 磁盘探测 ───

        /// <summary>
        /// 深探测：递归求子树内最晚写入时间与总大小，并列出一级子项供 Planner 判断目录形态。
        ///
        /// <para>
        /// 最晚写入时间必须取整棵子树的最大值，不能只看目录自身的 LastWriteTime：
        /// 目录的时间戳只在直接子项增删时更新，往 <c>files/a.pak</c> 里持续写几个 GB
        /// 不会让包目录本身"变新"，于是正在导入的包会被误判成已静置。
        /// </para>
        /// <para>
        /// 枚举失败（权限不足/路径过长）时把时间戳当作"就是现在"返回：这样静置期判据必然不通过，
        /// 目录会被保留。探测不了就不回收，是这里唯一可接受的失败方向。
        /// </para>
        /// </summary>
        private RepositoryEntryProbe DeepProbe(string directory, string name, bool hasManifest)
        {
            try
            {
                var info = new DirectoryInfo(directory);
                var lastWrite = info.LastWriteTimeUtc;
                long size = 0;

                foreach (var item in info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    if (item.LastWriteTimeUtc > lastWrite) lastWrite = item.LastWriteTimeUtc;
                    if (item is FileInfo file) size += file.Length;
                }

                var topDirs = info.EnumerateDirectories().Select(d => d.Name).ToList();
                var topFiles = info.EnumerateFiles().Select(f => f.Name).ToList();

                return new RepositoryEntryProbe(name, hasManifest, lastWrite, size, topDirs, topFiles);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Reclaim] 探测仓库目录失败，按'不可回收'处理: {Dir}", directory);
                return new RepositoryEntryProbe(
                    name, hasManifest, DateTime.UtcNow, 0, Array.Empty<string>(), Array.Empty<string>());
            }
        }

        // ─── 索引扫描 ───

        /// <summary>索引条目里唯一需要的字段。用最小 DTO 而不是 Package，避免被模型演进带着走。</summary>
        private sealed class IndexEntry
        {
            public string? PackageKey { get; set; }
        }

        private static readonly JsonSerializerOptions IndexJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// 读出**所有游戏**索引里已登记的 packageKey。
        ///
        /// <para>
        /// 必须跨游戏：仓库根是全局共享的（<c>AppPaths.RepositoryRoot</c> 只有一个），
        /// 而索引按游戏分成 <c>Data/{game}_packages.json</c>。只读当前游戏的索引，
        /// 会把其它 10 个游戏的包全部判成"不在索引里"。
        /// </para>
        /// <para>
        /// 返回的 scanComplete 为 false 表示至少有一个索引文件读不出来。此时"不在索引里"
        /// 这个前提本身就不成立，Planner 会一律不回收。
        /// </para>
        /// </summary>
        private (HashSet<string> Keys, bool ScanComplete) LoadRegisteredKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(_dataDirectory))
            {
                // 索引目录都不存在，却在仓库里看到了包目录 —— 这是"索引整体丢失"的形态，
                // 绝不能理解成"所有包都无主"。
                _logger.LogWarning("[Reclaim] 索引目录不存在，本次不做任何回收: {Dir}", _dataDirectory);
                return (keys, false);
            }

            var complete = true;
            foreach (var path in Directory.EnumerateFiles(_dataDirectory, "*_packages.json"))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var entries = JsonSerializer.Deserialize<List<IndexEntry>>(json, IndexJsonOptions);
                    if (entries == null)
                    {
                        complete = false;
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.PackageKey))
                            keys.Add(entry.PackageKey);
                    }
                }
                catch (Exception ex)
                {
                    // 一个索引读不出来就放弃整次回收，而不是"跳过这个文件继续"：
                    // 跳过等于把该游戏的所有包都当成无主的。
                    _logger.LogError(ex, "[Reclaim] 读取包索引失败，本次不做任何回收: {Path}", path);
                    complete = false;
                }
            }

            return (keys, complete);
        }
    }
}
