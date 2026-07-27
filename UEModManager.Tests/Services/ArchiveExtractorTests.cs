using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;

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
    public void ProcessNestedArchives_ExceedingSizeLimit_StopsExpanding()
    {
        var workDir = BuildNestedChain(depth: 3, payloadSize: 4096);

        // 上限设成 1 字节：第 1 层解完就已越界，后续压缩包全部拒绝展开
        var result = ArchiveExtractor.ProcessNestedArchives(
            workDir, NullLogger.Instance, maxDepth: 3, maxTotalBytes: 1);

        Assert.True(result.StoppedBySizeLimit);
        Assert.Equal(1, result.MaxDepthReached);
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
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
