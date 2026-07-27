using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;
using UEModManager.Services.Import;

namespace UEModManager.Services;

internal static class ArchiveExtractor
{
    public static bool ExtractCompressedFile(string filePath, string extractPath, ILogger logger)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".zip")
            {
                ExtractZipFile(filePath, extractPath);
                return true;
            }

            using var stream = File.OpenRead(filePath);
            using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                reader.WriteEntryToDirectory(extractPath, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extract archive failed: {Path}", filePath);
            return false;
        }
    }

    /// <summary>
    /// 嵌套压缩包最大展开层数。层数从 1 起算：顶层压缩包解出来的目录里直接躺着的压缩包算第 1 层。
    ///
    /// 取 3 的依据：真实 MOD 包最多见到两层（整合包 → 子 MOD 压缩包 → 偶见再包一层作者原始包），
    /// 第 3 层已属罕见，再深基本只出现在解压炸弹里。同时每层解压目录名是
    /// <c>{archive}_extracted</c>，路径随层数单调增长，限深也顺带限制了路径长度膨胀。
    /// </summary>
    internal const int MaxNestingDepth = 3;

    /// <summary>
    /// 嵌套展开的累计解压字节上限（4 GiB）。
    ///
    /// 取 4 GiB 的依据：单个体积超过它的 MOD（如 4K 材质整合）会被用户直接作为顶层包导入，
    /// 走不到嵌套这条路；而嵌套场景的合理总量远低于此。上限的作用是给解压炸弹封顶——
    /// 42.zip 这类样本在 3 层内就能展开到 TB 级，4 GiB 会在几秒内截停。
    /// </summary>
    internal const long MaxTotalExtractedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>嵌套展开的统计结果，便于测试与日志。</summary>
    internal readonly record struct NestedExtractionResult(
        int ArchivesExtracted,
        int MaxDepthReached,
        long TotalExtractedBytes,
        bool StoppedByDepthLimit,
        bool StoppedBySizeLimit);

    public static void ProcessNestedArchives(string directory, ILogger logger)
        => ProcessNestedArchives(directory, logger, MaxNestingDepth, MaxTotalExtractedBytes);

    /// <summary>
    /// 递归展开目录下的嵌套压缩包，受最大层数与累计解压体积双重限制。
    /// 触顶时记警告并停止继续展开，但**不会**让已成功解压的内容失效——
    /// 导入应当以"少解一层"降级，而不是整体失败。
    /// </summary>
    internal static NestedExtractionResult ProcessNestedArchives(
        string directory, ILogger logger, int maxDepth, long maxTotalBytes)
    {
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Archive, int Depth)>(
            Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(CompressedArchive.IsCompressed)
                .Select(path => (path, 1)));

        var extracted = 0;
        var maxDepthReached = 0;
        var totalBytes = 0L;
        var stoppedByDepth = false;
        var stoppedBySize = false;

        while (pending.Count > 0)
        {
            var (archive, depth) = pending.Dequeue();
            if (!processed.Add(archive))
                continue;

            if (depth > maxDepth)
            {
                stoppedByDepth = true;
                logger.LogWarning(
                    "嵌套压缩包超过最大层数 {MaxDepth}，停止展开: {Path}", maxDepth, archive);
                continue;
            }

            if (totalBytes >= maxTotalBytes)
            {
                stoppedBySize = true;
                logger.LogWarning(
                    "嵌套解压累计体积已达上限 {LimitBytes} 字节，停止展开剩余压缩包: {Path}",
                    maxTotalBytes, archive);
                continue;
            }

            var extractDir = archive + "_extracted";
            try
            {
                Directory.CreateDirectory(extractDir);
                if (!ExtractCompressedFile(archive, extractDir, logger))
                    continue;

                extracted++;
                maxDepthReached = Math.Max(maxDepthReached, depth);

                // 紧接着解压后统计：此刻 extractDir 下只有本压缩包的产物，
                // 更深层的产物要等它们各自出队后才会写进各自的子目录，不会重复计数。
                var producedFiles = Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in producedFiles)
                {
                    try { totalBytes += new FileInfo(file).Length; } catch { }
                }

                foreach (var nestedArchive in producedFiles.Where(CompressedArchive.IsCompressed))
                {
                    pending.Enqueue((nestedArchive, depth + 1));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Process nested archive failed: {Path}", archive);
            }
        }

        return new NestedExtractionResult(
            extracted, maxDepthReached, totalBytes, stoppedByDepth, stoppedBySize);
    }

    public static void CleanupArchives(string directory)
    {
        foreach (var archive in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(CompressedArchive.IsCompressed))
        {
            try { File.Delete(archive); } catch { }
        }
    }

    private static void ExtractZipFile(string filePath, string extractPath)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ZipFile.ExtractToDirectory(filePath, extractPath, Encoding.GetEncoding(936), overwriteFiles: true);
        }
        catch (InvalidDataException)
        {
            ZipFile.ExtractToDirectory(filePath, extractPath, Encoding.UTF8, overwriteFiles: true);
        }
    }
}