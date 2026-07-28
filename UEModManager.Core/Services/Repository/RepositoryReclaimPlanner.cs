using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.Repository
{
    /// <summary>
    /// 仓库根下一个直接子目录的观测结果。由主项目扫描磁盘后填充，Core 只判定不碰 IO。
    ///
    /// <para>
    /// 字段读取顺序是一份**契约**：<see cref="RepositoryReclaimPlanner.Plan"/> 的前三条判据
    /// 只看 <see cref="DirectoryName"/> 与 <see cref="HasManifest"/>，只有走到后面的判据才会读
    /// 时间戳、大小与目录形态。因此调用方可以对"显然有主"的目录只做浅探测
    /// （递归求子树最后写入时间和大小在大仓库上很贵），把深探测留给真正的候选。
    /// </para>
    /// </summary>
    /// <param name="DirectoryName">目录名。仓库里等同于 packageKey。</param>
    /// <param name="HasManifest">目录下是否有 manifest.json。</param>
    /// <param name="LastWriteTimeUtc">子树内最晚的写入时间（UTC）。浅探测时可传 default。</param>
    /// <param name="SizeBytes">子树占用字节数。浅探测时可传 0，仅用于给用户报数字。</param>
    /// <param name="TopLevelDirectoryNames">目录下的一级子目录名。浅探测时可传空。</param>
    /// <param name="TopLevelFileNames">目录下的一级文件名。浅探测时可传空。</param>
    public sealed record RepositoryEntryProbe(
        string DirectoryName,
        bool HasManifest,
        DateTime LastWriteTimeUtc,
        long SizeBytes,
        IReadOnlyList<string> TopLevelDirectoryNames,
        IReadOnlyList<string> TopLevelFileNames);

    /// <summary>仓库目录的处置结论。</summary>
    public enum RepositoryEntryDisposition
    {
        /// <summary>有主，或证据不足以证明它无主 —— 保持原样。</summary>
        Keep,

        /// <summary>可回收：证据齐全的导入残留。</summary>
        Reclaimable,

        /// <summary>
        /// 有 manifest.json 却不在任何游戏的包索引里 —— 失联包。
        /// 数据是完整的（manifest 足以重建 Package），**绝不删除**，只提示用户。
        /// </summary>
        Unregistered,
    }

    /// <summary>单个目录的处置结论 + 判据说明（判据要能直接呈现给用户，故写成中文完整句）。</summary>
    public sealed record RepositoryReclaimEntry(
        string DirectoryName,
        RepositoryEntryDisposition Disposition,
        string Reason,
        long SizeBytes);

    /// <summary>一次仓库回收扫描的完整结论。</summary>
    public sealed record RepositoryReclaimPlan(IReadOnlyList<RepositoryReclaimEntry> Entries)
    {
        /// <summary>证据齐全、可以删除的残留目录。</summary>
        public IReadOnlyList<RepositoryReclaimEntry> Reclaimable { get; } =
            Entries.Where(e => e.Disposition == RepositoryEntryDisposition.Reclaimable).ToList();

        /// <summary>有 manifest 但没有索引登记的失联包（只提示，不删）。</summary>
        public IReadOnlyList<RepositoryReclaimEntry> Unregistered { get; } =
            Entries.Where(e => e.Disposition == RepositoryEntryDisposition.Unregistered).ToList();

        /// <summary>回收后可释放的字节数。</summary>
        public long ReclaimableBytes { get; } =
            Entries.Where(e => e.Disposition == RepositoryEntryDisposition.Reclaimable).Sum(e => e.SizeBytes);
    }

    /// <summary>
    /// 仓库孤儿对象的回收判定（纯函数，不做 IO）。
    ///
    /// <para><b>要解决的不一致</b></para>
    /// 包实体（<c>ObjectStore</c> 里按 packageKey 存的目录）与包索引
    /// （<c>Data/{game}_packages.json</c>）是两份状态，导入是"先逐个文件落盘、最后才写
    /// manifest + 索引"。中途失败（磁盘满/文件被占用/进程被杀）就会留下有 <c>files/</c>、
    /// 无 manifest、索引里也没有记录的目录：占着几十 GB，用户在界面上既看不见也删不掉。
    /// 进程内的失败有补偿删除兜着，但补偿本身可能删不掉（文件被占用），进程被强杀时更是根本
    /// 不会执行——所以必须有一条事后回收的路。
    ///
    /// <para><b>为什么判据这么啰嗦</b></para>
    /// 本仓库刚经历过一次"搬迁器差点清空用户升级后的全部数据"。回收是删除动作，宁可漏回收
    /// 也绝不能误删，所以下面每一条判据都对应一种"看起来像垃圾其实不是"的真实情形，
    /// 五条全部通过才动手。
    /// </summary>
    public static class RepositoryReclaimPlanner
    {
        /// <summary>
        /// 静置期：子树最后一次写入距今不足此时长的目录一律不回收。
        ///
        /// <para>
        /// 挡的是"正在被写"：仓库根是全局共享的，用户完全可能开两个实例，一个正在导入几十 GB
        /// 的整合包，另一个在管理中心点了回收。导入过程中目标目录的写入时间是秒级更新的，
        /// 取一小时意味着只有"整整一小时没落下一个字节"的目录才算残留——哪怕在慢速网络盘上，
        /// 一次正常的文件复制也不会有这么长的空档。
        /// </para>
        /// </summary>
        public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromHours(1);

        /// <summary>
        /// 仓库内部目录（以 '.' 开头），如导入用的 <c>.import-tmp</c>。
        /// 这类目录不是包，不参与包级回收判定——把正在解压的临时目录判成"残留包"再删掉，
        /// 就是亲手掐死用户正在跑的导入。
        /// </summary>
        public static bool IsInternalDirectory(string directoryName)
            => directoryName.StartsWith('.');

        /// <summary>
        /// 压缩包导入的临时目录名前缀，见 <c>PackageImportService.ImportCompressedAsync</c>。
        /// </summary>
        public const string ImportTempDirectoryPrefix = "uemod_import_";

        /// <summary>
        /// <c>ObjectStore</c> 写进包目录的一级子目录名。除此之外的子目录说明这个目录不是我们建的。
        /// </summary>
        private const string FilesDirectoryName = "files";

        /// <summary>
        /// <c>ObjectStore.StorePreviewImage</c> 写进包目录的一级文件名前缀。
        /// </summary>
        private const string PreviewFilePrefix = "preview";

        /// <summary>
        /// 判定仓库根下每个目录该怎么处置。
        /// </summary>
        /// <param name="entries">仓库根下的直接子目录观测结果。</param>
        /// <param name="registeredKeys">
        /// **所有游戏**索引里已登记的 packageKey。仓库根跨游戏共享而索引按游戏分文件，
        /// 只看当前游戏的索引会把别的游戏的包全判成孤儿。
        /// </param>
        /// <param name="registryScanComplete">
        /// 索引是否被完整读出。任何一个 <c>*_packages.json</c> 读失败（损坏/被占用）都要传 false：
        /// 此时"不在索引里"根本不成立，一律不回收，只报告。
        /// </param>
        /// <param name="nowUtc">当前时刻（UTC）。</param>
        /// <param name="quietPeriod">静置期，见 <see cref="DefaultQuietPeriod"/>。</param>
        public static RepositoryReclaimPlan Plan(
            IEnumerable<RepositoryEntryProbe> entries,
            IReadOnlyCollection<string> registeredKeys,
            bool registryScanComplete,
            DateTime nowUtc,
            TimeSpan quietPeriod)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (registeredKeys == null) throw new ArgumentNullException(nameof(registeredKeys));

            var registered = new HashSet<string>(registeredKeys, StringComparer.OrdinalIgnoreCase);
            var results = new List<RepositoryReclaimEntry>();

            foreach (var probe in entries)
            {
                results.Add(Classify(probe, registered, registryScanComplete, nowUtc, quietPeriod));
            }

            return new RepositoryReclaimPlan(results);
        }

        /// <summary>
        /// 前三条判据只看目录名与 manifest，见 <see cref="RepositoryEntryProbe"/> 的字段读取契约。
        /// 调用方据此决定要不要为某个目录付出深探测的代价。
        /// </summary>
        /// <param name="registeredKeys">
        /// 必须是忽略大小写的集合（与 <c>PackageRepository.GetByKey</c> 的
        /// OrdinalIgnoreCase 比较保持一致），否则 <c>MyMod</c> 与 <c>mymod</c>
        /// 会被当成两个不同的键，已登记的包会被判成需要深探测的候选。
        /// </param>
        public static bool NeedsDeepProbe(
            string directoryName, bool hasManifest, IReadOnlySet<string> registeredKeys)
        {
            if (registeredKeys == null) throw new ArgumentNullException(nameof(registeredKeys));
            if (IsInternalDirectory(directoryName)) return false;
            if (hasManifest) return false;
            return !registeredKeys.Contains(directoryName);
        }

        private static RepositoryReclaimEntry Classify(
            RepositoryEntryProbe probe,
            HashSet<string> registered,
            bool registryScanComplete,
            DateTime nowUtc,
            TimeSpan quietPeriod)
        {
            // ① 内部目录不是包。
            if (IsInternalDirectory(probe.DirectoryName))
            {
                return new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Keep,
                    "仓库内部目录，不参与包级回收", probe.SizeBytes);
            }

            // ② 任何一个游戏的索引登记过它，就是有主的包。
            if (registered.Contains(probe.DirectoryName))
            {
                return new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Keep,
                    "已登记在包索引中", probe.SizeBytes);
            }

            // ③ 有 manifest = 注册流程至少走到了写 manifest 那一步（RegisterPackageAsync 先写
            //    manifest 再写索引），数据是完整的、可重建的。不在索引里更可能是索引写失败或
            //    索引文件损坏后被备份清空，删掉就是把用户唯一的副本删了。只提示。
            if (probe.HasManifest)
            {
                return new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Unregistered,
                    "有 manifest.json 但任何游戏的索引里都没有记录（失联包，数据完整，不会自动删除）",
                    probe.SizeBytes);
            }

            // ④ 索引没读全时"不在索引里"不成立。
            if (!registryScanComplete)
            {
                return new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Keep,
                    "包索引未能完整读出，无法证明此目录无主", probe.SizeBytes);
            }

            // ⑤ 目录形态必须与 ObjectStore 写出来的一致：一个 files/ 子目录，外加可选的 preview.*。
            //    这一条挡的是"用户把仓库根指到了自己的文件夹"——RepositoryRoot 是用户可自定义的
            //    （UiPreferences.SaveRepositoryRoot），指到 D:\Games\Mods 这种既有目录完全可能。
            //    那里面的子目录既没有 manifest 也不在索引里，前四条判据全都拦不住，
            //    只有"长得不像 ObjectStore 的产物"能把它们救下来。
            if (!HasObjectStoreShape(probe, out var shapeIssue))
            {
                return new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Keep,
                    $"目录结构与仓库产物不符（{shapeIssue}），可能不是本程序创建的", probe.SizeBytes);
            }

            // ⑥ 静置期，见 DefaultQuietPeriod。
            var idleFor = nowUtc - probe.LastWriteTimeUtc;
            if (idleFor < quietPeriod)
            {
                return new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Keep,
                    $"最近 {FormatDuration(quietPeriod)} 内仍有写入，可能是正在进行的导入", probe.SizeBytes);
            }

            return new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Reclaimable,
                "导入残留：无 manifest.json、任何游戏索引里都没有记录、目录结构是仓库产物、"
                + $"且已静置 {FormatDuration(idleFor)}",
                probe.SizeBytes);
        }

        /// <summary>
        /// 目录形态是否与 <c>ObjectStore</c> 的产物一致：
        /// 有且只有 <c>files</c> 一个子目录，一级文件只允许 <c>preview*</c>。
        /// </summary>
        private static bool HasObjectStoreShape(RepositoryEntryProbe probe, out string issue)
        {
            var dirs = probe.TopLevelDirectoryNames ?? Array.Empty<string>();
            var files = probe.TopLevelFileNames ?? Array.Empty<string>();

            if (!dirs.Any(d => string.Equals(d, FilesDirectoryName, StringComparison.OrdinalIgnoreCase)))
            {
                issue = "缺少 files 子目录";
                return false;
            }

            var extraDir = dirs.FirstOrDefault(
                d => !string.Equals(d, FilesDirectoryName, StringComparison.OrdinalIgnoreCase));
            if (extraDir != null)
            {
                issue = $"存在多余子目录 {extraDir}";
                return false;
            }

            var extraFile = files.FirstOrDefault(
                f => !f.StartsWith(PreviewFilePrefix, StringComparison.OrdinalIgnoreCase));
            if (extraFile != null)
            {
                issue = $"存在多余文件 {extraFile}";
                return false;
            }

            issue = string.Empty;
            return true;
        }

        /// <summary>
        /// 判定 <c>.import-tmp</c> 下哪些解压临时目录可以回收。
        ///
        /// <para>
        /// 压缩包导入的 finally 会删自己的临时目录，但进程被强杀时不会执行，
        /// 几十 GB 的解压产物就永久留在仓库根下。这里的判据比包级回收更硬：
        /// 目录名必须是我们自己生成的 <c>uemod_import_*</c>（Guid 后缀），且已过静置期。
        /// </para>
        /// </summary>
        public static IReadOnlyList<RepositoryReclaimEntry> PlanImportTemp(
            IEnumerable<RepositoryEntryProbe> tempEntries,
            DateTime nowUtc,
            TimeSpan quietPeriod)
        {
            if (tempEntries == null) throw new ArgumentNullException(nameof(tempEntries));

            var results = new List<RepositoryReclaimEntry>();
            foreach (var probe in tempEntries)
            {
                if (!probe.DirectoryName.StartsWith(ImportTempDirectoryPrefix, StringComparison.Ordinal))
                {
                    results.Add(new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Keep,
                        "不是本程序生成的解压临时目录", probe.SizeBytes));
                    continue;
                }

                var idleFor = nowUtc - probe.LastWriteTimeUtc;
                if (idleFor < quietPeriod)
                {
                    results.Add(new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Keep,
                        $"最近 {FormatDuration(quietPeriod)} 内仍有写入，可能是正在进行的解压", probe.SizeBytes));
                    continue;
                }

                results.Add(new RepositoryReclaimEntry(probe.DirectoryName, RepositoryEntryDisposition.Reclaimable,
                    $"进程未正常退出留下的解压临时目录，已静置 {FormatDuration(idleFor)}", probe.SizeBytes));
            }

            return results;
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时";
            if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} 分钟";
            return "不足 1 分钟";
        }
    }
}
