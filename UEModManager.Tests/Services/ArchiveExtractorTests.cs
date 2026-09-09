using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;
using UEModManager.Services.Import;

namespace UEModManager.Tests.Services;

/// <summary>
/// ArchiveExtractor 嵌套解压的层数与体积上限测试。
///
/// 这两个上限是纯逻辑，但只有真正造出多层嵌套的 zip 才能验证"第 N 层没被展开"，
/// 所以这里在临时目录里现搭嵌套包，而不是 mock 文件系统。
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "UEModManager.Tests",
        "ArchiveExtractor",
        Guid.NewGuid().ToString("N"));

    public ArchiveExtractorTests() => Directory.CreateDirectory(_root);

    // ─── 层数上限 ───

    [Fact]
    public void ProcessNestedArchives_WithinDepthLimit_ExtractsInnermostPayload()
    {
        // level1.zip ⊃ level2.zip ⊃ level3.zip ⊃ payload.txt
        var workDir = BuildNestedChain(depth: 3);

        var result = ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 3, maxTotalBytes: long.MaxValue);

        Assert.False(result.StoppedByDepthLimit);
        Assert.Equal(3, result.MaxDepthReached);
        Assert.Single(FindPayloads(workDir));
    }

    [Fact]
    public void ProcessNestedArchives_ExceedingDepthLimit_StopsAndLeavesInnermostPacked()
    {
        var workDir = BuildNestedChain(depth: 3);

        // 只允许 2 层：level3.zip 出队时 depth=3 > 2，应被拦下，payload 永远不落地
        var result = ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 2, maxTotalBytes: long.MaxValue);

        Assert.True(result.StoppedByDepthLimit);
        Assert.Equal(2, result.MaxDepthReached);
        Assert.Empty(FindPayloads(workDir));
    }

    [Fact]
    public void ProcessNestedArchives_DepthLimitDoesNotDiscardShallowerResults()
    {
        // 触顶只应停止继续深挖，已经解出来的浅层内容必须保留——
        // 导入要以"少解一层"降级，而不是整体失败。
        var workDir = BuildNestedChain(depth: 3);

        ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 1, maxTotalBytes: long.MaxValue);

        // 第 1 层解开后，level2.zip 应当已经躺在解压目录里
        var level2 = Directory.GetFiles(workDir, "level2.zip", SearchOption.AllDirectories);
        Assert.NotEmpty(level2);
    }

    // ─── 体积上限 ───

    [Fact]
    public void ProcessNestedArchives_OversizedEntry_NeverWritesPastBudgetAndCleansPartialFile()
    {
        var workDir = BuildNestedChain(depth: 1, payloadSize: 65536);

        var result = ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 3, maxTotalBytes: 8192);

        Assert.True(result.StoppedBySizeLimit);
        Assert.InRange(result.TotalExtractedBytes, 0, 8192);
        Assert.Empty(FindPayloads(workDir));
    }

    [Fact]
    public void ProcessNestedArchives_ExceedingSizeLimit_StopsExpanding()
    {
        var workDir = BuildNestedChain(depth: 3, payloadSize: 4096);

        // 上限设成 1 字节：第一层尚未完整落盘就停止，不留下截断的嵌套包。
        var result = ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 3, maxTotalBytes: 1);

        Assert.True(result.StoppedBySizeLimit);
        Assert.Equal(0, result.MaxDepthReached);
        Assert.InRange(result.TotalExtractedBytes, 0, 1);
        Assert.Empty(FindPayloads(workDir));
    }

    [Fact]
    public void ProcessNestedArchives_CountsExtractedBytes()
    {
        var workDir = BuildNestedChain(depth: 1, payloadSize: 2048);

        var result = ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 3, maxTotalBytes: long.MaxValue);

        Assert.Equal(1, result.ArchivesExtracted);
        Assert.Equal(2048, result.TotalExtractedBytes);
        Assert.False(result.StoppedBySizeLimit);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("rar")]
    [InlineData("7z")]
    public void SupportedFormats_WriteHardLimitAndRemovePartialFiles(string format)
    {
        var archive = CreatePayloadArchive(format);
        var output = Path.Combine(_root, "limited");
        var budget = new ExtractionBudget(8192);
        var logger = new CaptureLogger();

        Assert.False(ArchiveExtractor.ExtractCompressedFile(archive, output, logger, budget));
        Assert.True(budget.IsExceeded, logger.LastError?.ToString());
        Assert.Equal(8192, budget.WrittenBytes);
        Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));

        // 同一个真实格式夹具在充足预算下必须完整通过，排除“文件根本打不开”的假绿。
        var full = Path.Combine(_root, "complete");
        Assert.True(ArchiveExtractor.ExtractCompressedFile(archive, full, NullLogger.Instance));
        Assert.Equal(65536, new FileInfo(Path.Combine(full, PayloadName)).Length);
    }

    [Fact]
    public void LimitStream_BlocksBeforeUnderlyingStreamExceedsBudgetAcrossEntries()
    {
        var budget = new ExtractionBudget(8);
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using var firstLimited = budget.Limit(first);
        using var secondLimited = budget.Limit(second);
        firstLimited.Write(new byte[5]);

        Assert.Throws<InvalidDataException>(() => secondLimited.Write(new byte[65536]));

        Assert.Equal(5, first.Length);
        Assert.Equal(3, second.Length);
        Assert.Equal(8, budget.WrittenBytes);
        Assert.Throws<InvalidDataException>(() => firstLimited.WriteByte(1));
        Assert.Equal(8, first.Length + second.Length);
    }

    [Fact]
    public void ExtractCompressedFile_MultipleEntriesShareBudgetAndAllFailedArchiveOutputIsRemoved()
    {
        var archive = CreateZip("multiple.zip", ("first.pak", new byte[5000]), ("second.pak", new byte[5000]));
        var output = Path.Combine(_root, "multiple-output");
        var budget = new ExtractionBudget(8192);

        Assert.False(ArchiveExtractor.ExtractCompressedFile(archive, output, NullLogger.Instance, budget), $"Written={budget.WrittenBytes}");

        Assert.Equal(8192, budget.WrittenBytes);
        Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ProcessNestedArchives_SiblingArchivesShareBudgetAndKeepOnlyCompletedArchives()
    {
        var work = Path.Combine(_root, "siblings");
        Directory.CreateDirectory(work);
        File.Move(CreateZip("a.zip", ("first.pak", new byte[4000])), Path.Combine(work, "a.zip"));
        File.Move(CreateZip("b.zip", ("second.pak", new byte[6000])), Path.Combine(work, "b.zip"));
        File.Move(CreateZip("c.zip", ("third.pak", new byte[1])), Path.Combine(work, "c.zip"));

        var result = ArchiveExtractor.ProcessNestedArchives(work, NullLogger.Instance, 3, 8192);

        Assert.True(result.StoppedBySizeLimit);
        Assert.Equal(8192, result.TotalExtractedBytes);
        Assert.Equal(1, result.ArchivesExtracted);
        Assert.Equal(4000, new FileInfo(Assert.Single(Directory.GetFiles(work, "*.pak", SearchOption.AllDirectories))).Length);
    }

    [Fact]
    public void RootAndNestedArchives_UseOneCumulativeBudget()
    {
        var child = CreateZip("child.zip", ("payload.pak", new byte[4096]));
        var childBytes = File.ReadAllBytes(child);
        var root = CreateZip("root.zip", ("root.pak", new byte[2048]), ("child.zip", childBytes));
        var output = Path.Combine(_root, "root-output");
        var budget = new ExtractionBudget(2048 + childBytes.Length + 1024);

        Assert.True(ArchiveExtractor.ExtractCompressedFile(root, output, NullLogger.Instance, budget));
        var result = ArchiveExtractor.ProcessNestedArchives(output, NullLogger.Instance, 3, budget);

        Assert.True(result.StoppedBySizeLimit);
        Assert.Equal(budget.MaximumBytes, result.TotalExtractedBytes);
        Assert.Equal(0, result.ArchivesExtracted);
        Assert.False(File.Exists(Path.Combine(output, "child.zip_extracted", "payload.pak")));
        Assert.Equal(2048, new FileInfo(Path.Combine(output, "root.pak")).Length);
    }

    [Fact]
    public void ExtractCompressedFile_ExactBudgetSucceeds()
    {
        var archive = CreateZip("exact.zip", ("payload.pak", new byte[8192]));
        var budget = new ExtractionBudget(8192);
        var output = Path.Combine(_root, "exact-output");

        Assert.True(ArchiveExtractor.ExtractCompressedFile(archive, output, NullLogger.Instance, budget));
        Assert.False(budget.IsExceeded);
        Assert.Equal(8192, new FileInfo(Path.Combine(output, "payload.pak")).Length);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(1000000u)]
    public void ExtractCompressedFile_FalseZipEntrySizeCannotBypassWriteLimit(uint declaredSize)
    {
        var archive = CreateZip("lying-size.zip", ("payload.pak", new byte[65536]));
        var bytes = File.ReadAllBytes(archive);
        for (var i = 0; i <= bytes.Length - 28; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4));
            if (signature == 0x04034b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22, 4), declaredSize);
            if (signature == 0x02014b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24, 4), declaredSize);
        }
        File.WriteAllBytes(archive, bytes);
        var budget = new ExtractionBudget(8192);
        var output = Path.Combine(_root, "lying-output");

        var success = ArchiveExtractor.ExtractCompressedFile(archive, output, NullLogger.Instance, budget);
        Assert.InRange(budget.WrittenBytes, 0, 8192);
        var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
        Assert.InRange(files.Sum(path => new FileInfo(path).Length), 0, 8192);
        if (declaredSize > 8192)
        {
            Assert.False(success);
            Assert.True(budget.IsExceeded);
            Assert.Empty(files);
        }
    }

    [Theory]
    [InlineData("../outside.pak")]
    [InlineData("folder/../../outside.pak")]
    [InlineData("/outside.pak")]
    [InlineData("C:/outside.pak")]
    [InlineData("folder/file.pak:stream")]
    [InlineData("CON.pak")]
    public void ExtractCompressedFile_UnsafeEntryCannotEscapeAndCleansEarlierOutput(string unsafeName)
    {
        var archive = CreateZip("unsafe.zip", ("safe.pak", new byte[1]), (unsafeName, new byte[1]));
        var output = Path.Combine(_root, "safe-root");

        Assert.False(ArchiveExtractor.ExtractCompressedFile(archive, output, NullLogger.Instance));

        Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_root, "outside.pak")));
    }

    [Fact]
    public void ExtractCompressedFile_PreexistingDestinationIsNeverOverwrittenOrDeleted()
    {
        var archive = CreateZip("existing.zip", ("payload.pak", new byte[8192]));
        var output = Path.Combine(_root, "existing-output");
        Directory.CreateDirectory(output);
        var existing = Path.Combine(output, "payload.pak");
        File.WriteAllText(existing, "user file");

        Assert.False(ArchiveExtractor.ExtractCompressedFile(archive, output, NullLogger.Instance));
        Assert.Equal("user file", File.ReadAllText(existing));
    }

    [Fact]
    public void ExtractCompressedFile_ZipSymbolicLinkIsRejected()
    {
        var archive = Path.Combine(_root, "symlink.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var payload = entry.Open();
            payload.Write(Encoding.UTF8.GetBytes("../outside"));
        }
        var output = Path.Combine(_root, "symlink-output");

        Assert.False(ArchiveExtractor.ExtractCompressedFile(archive, output, NullLogger.Instance));
        Assert.False(File.Exists(Path.Combine(output, "link")));
    }

    [Theory]
    [InlineData(936)]
    [InlineData(65001)]
    public void ExtractCompressedFile_PreservesChineseNames(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var archive = Path.Combine(_root, "names.zip");
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, false,
            codePage == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(codePage)))
        {
            using var entry = zip.CreateEntry("中文目录/测试.pak").Open();
            entry.WriteByte(42);
        }
        var output = Path.Combine(_root, "names");
        Assert.True(ArchiveExtractor.ExtractCompressedFile(archive, output, NullLogger.Instance));
        Assert.True(File.Exists(Path.Combine(output, "中文目录", "测试.pak")),
            string.Join(";", Directory.GetFiles(output, "*", SearchOption.AllDirectories))
                + " flags=" + Convert.ToHexString(File.ReadAllBytes(archive).AsSpan(6, 2)));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(output, "中文目录", "测试.pak")));
    }

    private string CreateZip(string name, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var output = zip.CreateEntry(entry.Name).Open();
            output.Write(entry.Bytes);
        }
        return path;
    }

    private string CreatePayloadArchive(string format)
    {
        if (format == "zip") return CreateZip("payload.zip", (PayloadName, new byte[65536]));
        var path = Path.Combine(_root, "payload." + format);
        if (format == "7z")
        {
            // libarchive 生成的真实 LZMA 7z，唯一文件 payload.txt 为 65536 个零字节。
            File.WriteAllBytes(path, Convert.FromBase64String(
                "N3q8ryccAANOezqcUwAAAAAAAABwAAAAAAAAAOvmp5UAAG/9//+jt/9HPkgVcjlhUbiSKOajhgf57uQegtMvxTo8AUuxfsmKik0vow3Zf6bjjCMRU+BZGMV1iuJ3+LaUfwxqwN50SWRco83no///DBwAAAEEBgABCVMABwsBAAEjAwEBBV0AAIAADMEAAAAICgHrjpfXAAAFAREZAHAAYQB5AGwAbwBhAGQALgB0AHgAdAAAABQKAQDlHmBPOT3dARIKAQDlHmBPOT3dARMKAQDlHmBPOT3dARUGAQAggLaBAAA="));
        }
        else
        {
            // RAR4 store 格式，带有效头/文件 CRC；无需依赖开发机安装商业压缩工具。
            using var output = File.Create(path);
            output.Write(new byte[] { 82,97,114,33,26,7,0,207,144,115,0,0,13,0,0,0,0,0,0,0,234,229,116,0,128,43,0,0,0,1,0,0,0,1,0,2,235,142,151,215,0,0,0,0,20,48,11,0,32,0,0,0,112,97,121,108,111,97,100,46,116,120,116 });
            output.Write(new byte[65536]);
            output.Write(new byte[] { 4,176,123,0,0,7,0 });
        }
        return path;
    }

    private sealed class CaptureLogger : ILogger
    {
        public Exception? LastError { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => LastError = exception;
    }

    // ─── 默认值护栏 ───

    [Fact]
    public void DefaultLimits_AreTheReviewedValues()
    {
        // 这两个常量是安全上限，调整前应当先想清楚理由（依据写在 ArchiveExtractor 的注释里）。
        // 本测试的作用是让"顺手放宽"变成一次显式的改测试动作。
        Assert.Equal(3, ArchiveExtractor.MaxNestingDepth);
        Assert.Equal(4L * 1024 * 1024 * 1024, ArchiveExtractor.MaxTotalExtractedBytes);
    }

    [Fact]
    public void ProcessNestedArchives_NoArchives_IsNoOp()
    {
        var workDir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, "readme.txt"), "not an archive");

        var result = ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 3, maxTotalBytes: long.MaxValue);

        Assert.Equal(0, result.ArchivesExtracted);
        Assert.Equal(0, result.MaxDepthReached);
        Assert.False(result.StoppedByDepthLimit);
        Assert.False(result.StoppedBySizeLimit);
    }

    // ─── 构造嵌套包 ───

    private const string PayloadName = "payload.txt";

    /// <summary>
    /// 在一个新的工作目录里放置 level1.zip，其嵌套结构为
    /// level1.zip ⊃ level2.zip ⊃ … ⊃ level{depth}.zip ⊃ payload.txt。
    /// </summary>
    private string BuildNestedChain(int depth, int payloadSize = 64)
    {
        var buildDir = Path.Combine(_root, "build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildDir);

        // 从最内层往外套
        var innerFile = Path.Combine(buildDir, PayloadName);
        File.WriteAllBytes(innerFile, new byte[payloadSize]);

        var current = innerFile;
        for (var level = depth; level >= 1; level--)
        {
            var zipPath = Path.Combine(buildDir, $"level{level}.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(current, Path.GetFileName(current));
            }
            // 中间产物不能留在目录里，否则 payload/内层 zip 会被当成第 1 层直接扫到
            File.Delete(current);
            current = zipPath;
        }

        var workDir = Path.Combine(_root, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        File.Move(current, Path.Combine(workDir, Path.GetFileName(current)));
        Directory.Delete(buildDir, recursive: true);
        return workDir;
    }

    private static string[] FindPayloads(string dir)
        => Directory.GetFiles(dir, PayloadName, SearchOption.AllDirectories);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
