using UEModManager.Services.Repository;

namespace UEModManager.Core.Tests.Services.Repository;

/// <summary>
/// MOD 库正向完整性判定的测试。
///
/// <para>
/// MOD 库是这个产品的备份本体，完整性判断出错的表现是"用户以为备份还在、其实已经坏了"，
/// 而且没有第二个信号会提醒他。所以这里对**文案**的断言和对**分类**的断言一样重要：
/// 三个生产调用方（两个窗口 + DataMigrationService）都直接把 Description 显示给用户。
/// </para>
/// <para>
/// 抽取自 <c>PackageRepository.CheckIntegrityAsync</c>。抽取前只有走真实文件系统的两个
/// 集成用例，七类判据里只覆盖了哈希不符与未登记文件两类。
/// </para>
/// </summary>
public class PackageIntegrityAnalyzerTests
{
    private const string Key = "pkg";

    // ─── ResolveRepositoryRelativePath ───

    [Theory]
    [InlineData("pkg/files/mod.pak", "mod.pak")]
    [InlineData("pkg/files/nested/mod.pak", "nested/mod.pak")]
    [InlineData(@"pkg\files\nested\mod.pak", "nested/mod.pak")]
    [InlineData("PKG/FILES/mod.pak", "mod.pak")]
    [InlineData("pkg/files/./mod.pak", "mod.pak")]
    public void ResolveRepositoryRelativePath_ValidSourcePath_ReturnsPathRelativeToFilesDir(
        string sourcePath, string expected)
    {
        Assert.Equal(expected, PackageIntegrityAnalyzer.ResolveRepositoryRelativePath(Key, sourcePath));
    }

    /// <summary>
    /// 源路径正好指向 files/ 目录自身：**合法返回空串**，随后按"文件缺失"报告。
    /// 抽取前就是这个行为（SafeCombine 拼出目录路径、File.Exists 为 false），
    /// 改成抛异常会让同一份坏数据的报错从"文件缺失"变成"非法源路径"。
    /// </summary>
    [Fact]
    public void ResolveRepositoryRelativePath_PathIsFilesDirectoryItself_ReturnsEmptyNotThrow()
    {
        Assert.Equal(string.Empty, PackageIntegrityAnalyzer.ResolveRepositoryRelativePath(Key, "pkg/files/"));
    }

    /// <summary>
    /// RelativeTargetPath 是部署到游戏目录的位置，不能用来定位仓库实体 ——
    /// 两者混用过一次，表现是把整个仓库判成缺失。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mods/mod.pak")]
    [InlineData("pkg/mod.pak")]
    [InlineData("otherpkg/files/mod.pak")]
    [InlineData("pkg/files/../../etc/passwd")]
    [InlineData("pkg/files/C:/windows/system32")]
    public void ResolveRepositoryRelativePath_IllegalSourcePath_Throws(string? sourcePath)
    {
        Assert.Throws<ArgumentException>(
            () => PackageIntegrityAnalyzer.ResolveRepositoryRelativePath(Key, sourcePath));
    }

    [Fact]
    public void ResolveRepositoryRelativePath_NullPackageKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PackageIntegrityAnalyzer.ResolveRepositoryRelativePath(null!, "pkg/files/mod.pak"));
    }

    // ─── Analyze：整体 ───

    [Fact]
    public void Analyze_IntactPackage_ReportsNothing()
    {
        var report = Analyze(new[] { Probe() }, "mod.pak");

        Assert.Empty(report.Issues);
        Assert.True(report.IsIntact);
        Assert.False(report.HasDataLoss);
    }

    /// <summary>
    /// manifest 是仓库的自描述来源，它不在的时候逐文件比对没有意义 ——
    /// 会把一个问题放大成几十条噪音，用户反而看不到真正的原因。
    /// </summary>
    [Fact]
    public void Analyze_NoManifest_ShortCircuitsToSingleIssue()
    {
        var report = PackageIntegrityAnalyzer.Analyze(
            Key, hasManifest: false,
            new[] { Probe(exists: false), Probe(source: "bad", resolved: null) },
            new[] { "orphan.bin" });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.ManifestMissing, issue.Kind);
        Assert.Equal("manifest.json 缺失", issue.Description);
    }

    [Fact]
    public void Analyze_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => PackageIntegrityAnalyzer.Analyze(null!, true, Array.Empty<PackageFileProbe>(), Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(
            () => PackageIntegrityAnalyzer.Analyze(Key, true, null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(
            () => PackageIntegrityAnalyzer.Analyze(Key, true, Array.Empty<PackageFileProbe>(), null!));
    }

    // ─── Analyze：逐条判据 ───

    /// <summary>非法源路径的文案用 manifest 原样值，不是归一化后的值 —— 用户要按它去 manifest 里找那一行。</summary>
    [Fact]
    public void Analyze_IllegalSourcePath_ReportsRawPath()
    {
        var report = Analyze(new[] { Probe(source: @"mods\mod.pak", resolved: null) });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.IllegalSourcePath, issue.Kind);
        Assert.Equal(@"非法仓库源路径: mods\mod.pak", issue.Description);
        Assert.False(report.HasDataLoss);
    }

    [Fact]
    public void Analyze_DuplicateRegistration_ReportsOnlySecondEntry()
    {
        var report = Analyze(
            new[] { Probe(source: "pkg/files/a.pak"), Probe(source: "pkg/files/again.pak") },
            "mod.pak");

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.DuplicateRegistration, issue.Kind);
        Assert.Equal("重复登记文件: pkg/files/again.pak", issue.Description);
    }

    /// <summary>
    /// 去重键必须忽略大小写：Windows 上 MyMod.pak 与 mymod.pak 是同一个文件，
    /// 区分大小写会让"同一实体登记两次"漏报。
    /// </summary>
    [Fact]
    public void Analyze_DuplicateRegistrationDifferingCase_IsDetected()
    {
        var report = Analyze(
            new[] { Probe(resolved: "Mod.pak"), Probe(resolved: "mod.pak") },
            "Mod.pak");

        Assert.Equal(PackageIntegrityIssueKind.DuplicateRegistration, Assert.Single(report.Issues).Kind);
    }

    /// <summary>去重键必须与平台分隔符无关，否则同一份数据在不同平台的判定不一致。</summary>
    [Fact]
    public void Analyze_DuplicateRegistrationDifferingSeparators_IsDetected()
    {
        var report = Analyze(
            new[] { Probe(resolved: @"nested\mod.pak"), Probe(resolved: "nested/mod.pak") },
            "nested/mod.pak");

        Assert.Equal(PackageIntegrityIssueKind.DuplicateRegistration, Assert.Single(report.Issues).Kind);
    }

    [Fact]
    public void Analyze_MissingFile_ReportsDataLoss()
    {
        var report = Analyze(new[] { Probe(source: "pkg/files/mod.pak", exists: false) });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.FileMissing, issue.Kind);
        Assert.Equal("文件缺失: pkg/files/mod.pak", issue.Description);
        Assert.True(report.HasDataLoss);
    }

    /// <summary>
    /// 缺失文件的路径仍然进入"已登记"集合。真正被这条挡住的是紧随其后的未登记统计：
    /// 若缺失文件不登记，同一个包里另一个同名实体就会被再报一次"未登记文件"。
    /// </summary>
    [Fact]
    public void Analyze_MissingFile_StillOccupiesItsRegisteredPath()
    {
        var report = Analyze(new[] { Probe(resolved: "mod.pak", exists: false) }, "mod.pak");

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.FileMissing, issue.Kind);
    }

    [Fact]
    public void Analyze_SizeMismatch_ReportsExpectedAndActual()
    {
        var report = Analyze(new[] {
            Probe(source: "pkg/files/mod.pak", expectedSize: 100, actualSize: 42, expectedHash: null) });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.SizeMismatch, issue.Kind);
        Assert.Equal("文件大小不符: pkg/files/mod.pak（期望 100, 实际 42）", issue.Description);
        Assert.True(report.HasDataLoss);
    }

    /// <summary>
    /// 大小不符**不中断**哈希校验。一个被换掉的文件通常两者都对不上，两条都报出来
    /// 用户才知道是"内容被换了"而不只是"被截断了"。
    /// </summary>
    [Fact]
    public void Analyze_SizeAndHashBothMismatch_ReportsBothIssues()
    {
        var report = Analyze(new[] {
            Probe(expectedSize: 100, actualSize: 42, expectedHash: "aaa", actualHash: "bbb") });

        Assert.Equal(2, report.Issues.Count);
        Assert.Equal(PackageIntegrityIssueKind.SizeMismatch, report.Issues[0].Kind);
        Assert.Equal(PackageIntegrityIssueKind.HashMismatch, report.Issues[1].Kind);
    }

    /// <summary>连长度都读不到的文件，哈希更不可能算得出来，再试一次只是把同一个 IO 错误报两遍。</summary>
    [Fact]
    public void Analyze_SizeReadError_SkipsHashCheckAndReportsProbeFailed()
    {
        var report = Analyze(new[] {
            Probe(source: "pkg/files/mod.pak", sizeReadError: "文件正由另一进程使用",
                  expectedHash: "aaa", actualHash: "bbb") });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.ProbeFailed, issue.Kind);
        Assert.Equal("无法读取文件: pkg/files/mod.pak（文件正由另一进程使用）", issue.Description);
        Assert.False(report.HasDataLoss);
    }

    [Fact]
    public void Analyze_NegativeExpectedSize_SkipsSizeCheck()
    {
        var report = Analyze(new[] { Probe(expectedSize: -1, actualSize: 42, expectedHash: null) });

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Analyze_HashMismatch_ReportsDataLoss()
    {
        var report = Analyze(new[] {
            Probe(source: "pkg/files/mod.pak", expectedHash: "aaa", actualHash: "bbb") });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.HashMismatch, issue.Kind);
        Assert.Equal("文件哈希不符: pkg/files/mod.pak", issue.Description);
        Assert.True(report.HasDataLoss);
    }

    /// <summary>ComputeFileHashAsync 产出小写十六进制，但历史索引里可能是大写，不该报成损坏。</summary>
    [Fact]
    public void Analyze_HashDifferingOnlyInCase_IsConsideredEqual()
    {
        var report = Analyze(new[] { Probe(expectedHash: "ABCDEF01", actualHash: "abcdef01") });

        Assert.Empty(report.Issues);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Analyze_NoExpectedHash_SkipsHashCheck(string? expectedHash)
    {
        var report = Analyze(new[] { Probe(expectedHash: expectedHash, actualHash: null) });

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Analyze_HashReadError_ReportsProbeFailed()
    {
        var report = Analyze(new[] {
            Probe(source: "pkg/files/mod.pak", hashReadError: "拒绝访问") });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.ProbeFailed, issue.Kind);
        Assert.Equal("无法校验文件: pkg/files/mod.pak（拒绝访问）", issue.Description);
        Assert.False(report.HasDataLoss);
    }

    // ─── Analyze：未登记文件 ───

    [Fact]
    public void Analyze_UnregisteredFiles_CountsOnlyUnregisteredOnes()
    {
        var report = Analyze(new[] { Probe(resolved: "mod.pak") }, "mod.pak", "extra1.bin", "sub/extra2.bin");

        var issue = Assert.Single(report.Issues);
        Assert.Equal(PackageIntegrityIssueKind.UnregisteredFile, issue.Kind);
        Assert.Equal("发现 2 个未登记文件", issue.Description);
        Assert.False(report.HasDataLoss);
    }

    [Fact]
    public void Analyze_UnregisteredFiles_IgnoresSeparatorAndCaseDifferences()
    {
        var report = Analyze(new[] { Probe(resolved: "nested/mod.pak") }, @"Nested\Mod.pak");

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Analyze_UnregisteredFiles_DeduplicatesActualPaths()
    {
        var report = Analyze(new[] { Probe(resolved: "mod.pak") }, "mod.pak", "extra.bin", "extra.bin");

        Assert.Equal("发现 1 个未登记文件", Assert.Single(report.Issues).Description);
    }

    /// <summary>非法源路径没能进入已登记集合，磁盘上那个文件因此确实是"未登记"的。</summary>
    [Fact]
    public void Analyze_IllegalSourcePath_LeavesItsFileUnregistered()
    {
        var report = Analyze(new[] { Probe(source: "bad", resolved: null) }, "mod.pak");

        Assert.Equal(2, report.Issues.Count);
        Assert.Equal(PackageIntegrityIssueKind.IllegalSourcePath, report.Issues[0].Kind);
        Assert.Equal(PackageIntegrityIssueKind.UnregisteredFile, report.Issues[1].Kind);
    }

    // ─── DescribeIssues ───

    [Fact]
    public void DescribeIssues_FewIssues_JoinsWithoutTruncation()
    {
        var text = PackageIntegrityAnalyzer.DescribeIssues(Issues(3));

        Assert.Equal("问题 1；问题 2；问题 3", text);
        Assert.DoesNotContain("另有", text);
    }

    [Fact]
    public void DescribeIssues_ExactlyMaxDetail_DoesNotAppendRemainder()
    {
        Assert.DoesNotContain("另有", PackageIntegrityAnalyzer.DescribeIssues(Issues(5)));
    }

    /// <summary>仓库整体损坏时一个包能报出几百条，不截断会撑爆消息框；折叠后仍要告诉用户真实规模。</summary>
    [Fact]
    public void DescribeIssues_ManyIssues_TruncatesAndReportsRemainder()
    {
        var text = PackageIntegrityAnalyzer.DescribeIssues(Issues(8));

        Assert.Equal("问题 1；问题 2；问题 3；问题 4；问题 5；另有 3 项", text);
    }

    [Fact]
    public void DescribeIssues_NoIssues_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PackageIntegrityAnalyzer.DescribeIssues(Array.Empty<PackageIntegrityIssue>()));
    }

    [Fact]
    public void DescribeIssues_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackageIntegrityAnalyzer.DescribeIssues(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PackageIntegrityAnalyzer.DescribeIssues(Issues(1), maxDetail: 0));
    }

    // ─── 构造辅助 ───

    /// <summary>一个"一切正常"的探测结果；每个用例只改它关心的那一两个字段。</summary>
    private static PackageFileProbe Probe(
        string source = "pkg/files/mod.pak",
        string? resolved = "mod.pak",
        long expectedSize = 100,
        string? expectedHash = "abc123",
        bool exists = true,
        long actualSize = 100,
        string? actualHash = "abc123",
        string? sizeReadError = null,
        string? hashReadError = null)
        => new(
            RawSourcePath: source,
            NormalizedSourcePath: source.Replace('\\', '/'),
            ExpectedSize: expectedSize,
            ExpectedHash: expectedHash,
            ResolvedRelativePath: resolved,
            Exists: exists,
            ActualSize: actualSize,
            SizeReadError: sizeReadError,
            ActualHash: actualHash,
            HashReadError: hashReadError);

    private static PackageIntegrityReport Analyze(
        PackageFileProbe[] probes, params string[] actualRelativeFilePaths)
        => PackageIntegrityAnalyzer.Analyze(Key, hasManifest: true, probes, actualRelativeFilePaths);

    private static PackageIntegrityIssue[] Issues(int count)
        => Enumerable.Range(1, count)
            .Select(i => new PackageIntegrityIssue(
                PackageIntegrityIssueKind.FileMissing, null, $"问题 {i}"))
            .ToArray();
}
