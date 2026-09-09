using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Services.Security;

namespace UEModManager.Services.Repository
{
    /// <summary>
    /// 一个已登记文件的观测结果。期望值来自 manifest，实测值由主项目探测磁盘后填充，
    /// Core 只判定不碰 IO。
    ///
    /// <para>
    /// 字段分两组是一份**契约**：<see cref="ResolvedRelativePath"/> 为 null 时（路径非法）
    /// 后面所有实测字段都不会被读，调用方无需为它付出任何 IO 代价。同理
    /// <see cref="Exists"/> 为 false 时不会读大小与哈希 —— 逐文件 SHA-256 在大仓库上
    /// 是这次检查的绝大部分开销，不该为一个根本不存在的文件付。
    /// </para>
    /// </summary>
    /// <param name="RawSourcePath">manifest 里的原样值。"非法源路径"与"重复登记"的文案用它。</param>
    /// <param name="NormalizedSourcePath">反斜杠归一为正斜杠后的值。其余文案用它。</param>
    /// <param name="ExpectedSize">manifest 声明的字节数。负数表示不校验大小。</param>
    /// <param name="ExpectedHash">manifest 声明的哈希。null/空白表示不校验哈希。</param>
    /// <param name="ResolvedRelativePath">
    /// 相对包 <c>files/</c> 目录、正斜杠归一的路径；null 表示解析失败（非法源路径）。
    /// 空串是合法值：源路径正好指向 <c>files/</c> 目录本身，按"文件缺失"处理。
    /// </param>
    /// <param name="Exists">实测文件是否存在。</param>
    /// <param name="ActualSize">实测字节数。</param>
    /// <param name="SizeReadError">读取大小时的异常消息；null 表示读取成功。</param>
    /// <param name="ActualHash">实测哈希；null 表示未计算。</param>
    /// <param name="HashReadError">计算哈希时的异常消息；null 表示计算成功。</param>
    public sealed record PackageFileProbe(
        string RawSourcePath,
        string NormalizedSourcePath,
        long ExpectedSize,
        string? ExpectedHash,
        string? ResolvedRelativePath,
        bool Exists,
        long ActualSize,
        string? SizeReadError,
        string? ActualHash,
        string? HashReadError);

    /// <summary>完整性问题的类别。</summary>
    public enum PackageIntegrityIssueKind
    {
        /// <summary>包目录或 manifest.json 不在了。</summary>
        ManifestMissing,

        /// <summary>manifest 里的源路径不指向本包的 files/ 目录，或含 .. 上跳。</summary>
        IllegalSourcePath,

        /// <summary>同一个实体文件被 manifest 登记了两次。</summary>
        DuplicateRegistration,

        /// <summary>登记了但磁盘上没有。</summary>
        FileMissing,

        /// <summary>大小与 manifest 声明不符。</summary>
        SizeMismatch,

        /// <summary>哈希与 manifest 声明不符。</summary>
        HashMismatch,

        /// <summary>文件在，但读大小或算哈希时出错（被占用/权限/IO 错误）。</summary>
        ProbeFailed,

        /// <summary>在 files/ 里但不在 manifest 里。</summary>
        UnregisteredFile,
    }

    /// <summary>单条问题。<see cref="Description"/> 直接呈现给用户，故写成中文完整句。</summary>
    public sealed record PackageIntegrityIssue(
        PackageIntegrityIssueKind Kind,
        string? RelativeSourcePath,
        string Description);

    /// <summary>一个包的完整性结论。</summary>
    public sealed record PackageIntegrityReport(
        string PackageKey,
        IReadOnlyList<PackageIntegrityIssue> Issues)
    {
        /// <summary>没有任何问题。</summary>
        public bool IsIntact => Issues.Count == 0;

        /// <summary>
        /// 是否存在**数据丢失**（而非垃圾）。
        ///
        /// <para>
        /// 这三类的共同点是"索引记了、实体不对"，处置方式与 <see cref="RepositoryReclaimPlanner"/>
        /// 判出的残留目录相反：残留是垃圾可以删，这里是用户的备份少了东西，删掉只会让用户
        /// 连"曾经有这个包"都看不到。**永远不要据此自动清理。**
        /// </para>
        /// </summary>
        public bool HasDataLoss { get; } = Issues.Any(i =>
            i.Kind is PackageIntegrityIssueKind.FileMissing
                   or PackageIntegrityIssueKind.SizeMismatch
                   or PackageIntegrityIssueKind.HashMismatch);
    }

    /// <summary>
    /// MOD 库完整性的正向判定（纯函数，不做 IO）：从索引出发，逐个包核对 manifest 与实体文件。
    ///
    /// <para><b>为什么这块必须可单测</b></para>
    /// MOD 库是这个产品的备份本体 —— 它的价值就在于是一份与游戏目录隔离的独立副本。
    /// 完整性判断一旦出错，用户看到的是"备份还在"而实际已经损坏，且不会有任何其它信号提醒他。
    /// 这里的七类判据过去混在 <c>PackageRepository.CheckIntegrityAsync</c> 的 IO 里，
    /// 只有走真实文件系统才能测到，实际只有两类被覆盖过。
    ///
    /// <para><b>与 RepositoryReclaimPlanner 的分工</b></para>
    /// 本类只做**正向**检查（索引 → 磁盘）。"磁盘上有目录、索引里没记录"的**反向**检查归
    /// <see cref="RepositoryReclaimPlanner"/>：那条判定需要跨游戏读取全部 <c>*_packages.json</c>、
    /// 排除 <c>.import-tmp</c> 这类内部目录、还要看目录形态才能区分"导入残留"与"用户把仓库根
    /// 指到了自己已有的文件夹"。两边的判据都不许复制到对方那里 —— 漂移的后果是误删用户数据。
    /// </summary>
    public static class PackageIntegrityAnalyzer
    {
        /// <summary><c>ObjectStore</c> 在包目录下存放实体文件的子目录名。</summary>
        private const string FilesDirectoryName = "files";

        /// <summary>文案里最多列出的问题条数，超出部分折叠成"另有 N 项"。</summary>
        public const int DefaultMaxDetailCount = 5;

        /// <summary>
        /// 把 manifest 的 <c>RelativeSourcePath</c> 解析成相对包 <c>files/</c> 目录的路径。
        ///
        /// <para>
        /// 源路径必须明确落在 <c>{packageKey}/files/</c> 前缀内。<c>RelativeTargetPath</c>
        /// 是部署到游戏目录的目标位置，与仓库实体位置无关，**不能用来定位仓库文件** ——
        /// 两者混用过一次，表现是完整性检查对着游戏目录找仓库文件、把好包全判成缺失。
        /// </para>
        /// </summary>
        /// <returns>
        /// 正斜杠归一的相对路径。**空串是合法返回值**：源路径正好是 <c>{packageKey}/files/</c>
        /// 时指向 files 目录自身，调用方拼出来的就是目录路径，随后按"文件缺失"报告 ——
        /// 与抽取前的行为一致，不要改成抛异常。
        /// </returns>
        /// <exception cref="ArgumentException">源路径为空、不在 files/ 前缀内、或含 .. 上跳。</exception>
        public static string ResolveRepositoryRelativePath(string packageKey, string? relativeSourcePath)
        {
            if (packageKey == null) throw new ArgumentNullException(nameof(packageKey));

            var normalized = relativeSourcePath?.Replace('\\', '/');
            var prefix = $"{packageKey}/{FilesDirectoryName}/";

            if (string.IsNullOrWhiteSpace(normalized)
                || !normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"仓库源路径不属于当前包的 files 目录: {relativeSourcePath}", nameof(relativeSourcePath));
            }

            // 复用 PathSanitizer 的 UNC / 绝对路径 / .. 上跳拒绝，不在这里重写一份判据。
            var sanitized = PathSanitizer.SanitizeRelative(normalized[prefix.Length..]);
            return NormalizeSeparators(sanitized);
        }

        /// <summary>
        /// 判定一个包的完整性。
        /// </summary>
        /// <param name="packageKey">包键。</param>
        /// <param name="hasManifest">
        /// 包目录与 manifest.json 是否都在（<c>ObjectStore.PackageExists</c>）。
        /// false 时**短路**：manifest 是仓库的自描述来源，它不在的时候逐文件比对没有意义，
        /// 只会把一个问题放大成几十条噪音。
        /// </param>
        /// <param name="probes">每个已登记文件的观测结果，顺序即 manifest 中的登记顺序。</param>
        /// <param name="actualRelativeFilePaths">
        /// <c>files/</c> 目录下实际存在的全部文件，相对 <c>files/</c>、正斜杠归一。
        /// 目录不存在时传空集合。
        /// </param>
        public static PackageIntegrityReport Analyze(
            string packageKey,
            bool hasManifest,
            IReadOnlyList<PackageFileProbe> probes,
            IReadOnlyCollection<string> actualRelativeFilePaths)
        {
            if (packageKey == null) throw new ArgumentNullException(nameof(packageKey));
            if (probes == null) throw new ArgumentNullException(nameof(probes));
            if (actualRelativeFilePaths == null) throw new ArgumentNullException(nameof(actualRelativeFilePaths));

            if (!hasManifest)
            {
                return new PackageIntegrityReport(packageKey, new[]
                {
                    new PackageIntegrityIssue(
                        PackageIntegrityIssueKind.ManifestMissing, null, "manifest.json 缺失"),
                });
            }

            var issues = new List<PackageIntegrityIssue>();
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var probe in probes)
            {
                Classify(probe, registered, issues);
            }

            // 已登记但缺失的文件其路径也在 registered 里，所以不会被算成"未登记"——
            // 它已经以"文件缺失"报过一次了，重复报只会让用户以为有两个问题。
            var extraCount = actualRelativeFilePaths
                .Select(NormalizeSeparators)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(p => !registered.Contains(p));

            if (extraCount > 0)
            {
                issues.Add(new PackageIntegrityIssue(
                    PackageIntegrityIssueKind.UnregisteredFile, null, $"发现 {extraCount} 个未登记文件"));
            }

            return new PackageIntegrityReport(packageKey, issues);
        }

        /// <summary>
        /// 判定单个已登记文件，把命中的问题追加进 <paramref name="issues"/>。
        ///
        /// <para>
        /// 判据顺序不可调整：解析失败 → 重复登记 → 缺失 → 大小 → 哈希。前三条任一命中就不再往下查，
        /// 因为后面的判据都以"这个路径唯一且文件确实在"为前提，抢跑只会报出误导性的第二条问题。
        /// </para>
        /// <para>
        /// **大小不符不会中断哈希校验** —— 一个被换掉的文件通常大小和哈希都对不上，两条都报出来
        /// 用户才知道是"内容被换了"而不只是"被截断了"。只有大小**读取失败**才中断：
        /// 连长度都读不到的文件，哈希更不可能算得出来，再试一次只是把同一个 IO 错误报两遍。
        /// </para>
        /// </summary>
        private static void Classify(
            PackageFileProbe probe, HashSet<string> registered, List<PackageIntegrityIssue> issues)
        {
            if (probe.ResolvedRelativePath == null)
            {
                issues.Add(new PackageIntegrityIssue(
                    PackageIntegrityIssueKind.IllegalSourcePath, probe.RawSourcePath,
                    $"非法仓库源路径: {probe.RawSourcePath}"));
                return;
            }

            if (!registered.Add(NormalizeSeparators(probe.ResolvedRelativePath)))
            {
                issues.Add(new PackageIntegrityIssue(
                    PackageIntegrityIssueKind.DuplicateRegistration, probe.RawSourcePath,
                    $"重复登记文件: {probe.RawSourcePath}"));
                return;
            }

            if (!probe.Exists)
            {
                issues.Add(new PackageIntegrityIssue(
                    PackageIntegrityIssueKind.FileMissing, probe.NormalizedSourcePath,
                    $"文件缺失: {probe.NormalizedSourcePath}"));
                return;
            }

            if (probe.ExpectedSize >= 0)
            {
                if (probe.SizeReadError != null)
                {
                    issues.Add(new PackageIntegrityIssue(
                        PackageIntegrityIssueKind.ProbeFailed, probe.NormalizedSourcePath,
                        $"无法读取文件: {probe.NormalizedSourcePath}（{probe.SizeReadError}）"));
                    return;
                }

                if (probe.ActualSize != probe.ExpectedSize)
                {
                    issues.Add(new PackageIntegrityIssue(
                        PackageIntegrityIssueKind.SizeMismatch, probe.NormalizedSourcePath,
                        $"文件大小不符: {probe.NormalizedSourcePath}"
                        + $"（期望 {probe.ExpectedSize}, 实际 {probe.ActualSize}）"));
                }
            }

            if (string.IsNullOrWhiteSpace(probe.ExpectedHash)) return;

            if (probe.HashReadError != null)
            {
                issues.Add(new PackageIntegrityIssue(
                    PackageIntegrityIssueKind.ProbeFailed, probe.NormalizedSourcePath,
                    $"无法校验文件: {probe.NormalizedSourcePath}（{probe.HashReadError}）"));
                return;
            }

            if (!string.Equals(probe.ActualHash, probe.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new PackageIntegrityIssue(
                    PackageIntegrityIssueKind.HashMismatch, probe.NormalizedSourcePath,
                    $"文件哈希不符: {probe.NormalizedSourcePath}"));
            }
        }

        /// <summary>
        /// 把一个包的问题渲染成单行文案。
        ///
        /// <para>
        /// 截断是必要的：一个包的 manifest 可能登记几百个文件，仓库整体损坏时每个都会报错，
        /// 未截断的文案会撑爆消息框。折叠后的"另有 N 项"仍然告诉用户真实规模。
        /// </para>
        /// </summary>
        public static string DescribeIssues(
            IReadOnlyList<PackageIntegrityIssue> issues, int maxDetail = DefaultMaxDetailCount)
        {
            if (issues == null) throw new ArgumentNullException(nameof(issues));
            if (maxDetail <= 0) throw new ArgumentOutOfRangeException(nameof(maxDetail));

            var detail = string.Join("；", issues.Take(maxDetail).Select(i => i.Description));
            if (issues.Count > maxDetail) detail += $"；另有 {issues.Count - maxDetail} 项";
            return detail;
        }

        /// <summary>
        /// 正斜杠归一。<see cref="PathSanitizer.SanitizeRelative"/> 用
        /// <c>Path.DirectorySeparatorChar</c> 拼接，其结果在 Windows 上是反斜杠、在别处是正斜杠；
        /// 比较键必须与平台无关，否则同一份数据在不同平台上的去重结果会不一样。
        /// </summary>
        private static string NormalizeSeparators(string path) => path.Replace('\\', '/');
    }
}
