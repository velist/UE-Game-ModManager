using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using UEModManager.Services.Import;
using UEModManager.Services.Security;

namespace UEModManager.Services;

internal static class ArchiveExtractor
{
    // 根包和全部嵌套包共用同一个预算；包括随后清理的中间压缩包与失败写入。
    internal const int MaxNestingDepth = 3;
    internal const long MaxTotalExtractedBytes = 4L * 1024 * 1024 * 1024;

    internal readonly record struct NestedExtractionResult(
        int ArchivesExtracted,
        int MaxDepthReached,
        long TotalExtractedBytes,
        bool StoppedByDepthLimit,
        bool StoppedBySizeLimit);

    public static bool ExtractCompressedFile(string filePath, string extractPath, ILogger logger)
        => ExtractCompressedFile(filePath, extractPath, logger, new ExtractionBudget(MaxTotalExtractedBytes));

    internal static bool ExtractCompressedFile(
        string filePath, string extractPath, ILogger logger, ExtractionBudget budget)
    {
        var createdFiles = new List<string>();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var options = new ReaderOptions
            {
                LeaveStreamOpen = false,
                // Default 只作用于未标 Unicode 的旧包；不能 Forced=GBK，否则会损坏 UTF-8 名称。
                ArchiveEncoding = new ArchiveEncoding { Default = Encoding.GetEncoding(936) }
            };
            // 7z 需要随机访问的 archive reader；ZIP / RAR（含非 solid）走顺序 reader。
            using var archive = ArchiveFactory.OpenArchive(filePath, options);
            // ZIP 的 Unix 链接位只在中央目录，顺序 reader 的本地条目头不携带它。
            if (archive.Entries.Any(IsLink))
                throw new InvalidDataException("压缩包不允许包含链接");
            using var reader = archive.Type == ArchiveType.SevenZip
                ? archive.ExtractAllEntries()
                : ReaderFactory.OpenReader(filePath, options);
            try
            {
                while (reader.MoveToNextEntry())
                {
                    if (IsLink(reader.Entry))
                        throw new InvalidDataException("压缩包不允许包含链接");
                    if (reader.Entry.IsDirectory) continue;
                    ExtractEntry(reader.Entry.Key ?? "", extractPath,
                        reader.WriteEntryTo, budget, createdFiles);
                }
            }
            catch
            {
                reader.Cancel();
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            // 只删本次成功 CreateNew 的文件；已有目标文件从不被覆盖或误删。
            foreach (var path in createdFiles)
            {
                try { File.Delete(path); }
                catch (Exception cleanupError) { logger.LogWarning(cleanupError, "清理解压残留失败: {Path}", path); }
            }
            logger.LogError(ex, "Extract archive failed: {Path}", filePath);
            return false;
        }
    }

    private static bool IsLink(IEntry entry)
    {
        var attributes = entry.Attrib ?? 0;
        return !string.IsNullOrEmpty(entry.LinkTarget)
            || ((attributes >> 16) & 0xF000) == 0xA000
            || (attributes & 0xF000) == 0xA000
            || (attributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static void ExtractEntry(string entryName, string extractPath, Action<Stream> extract,
        ExtractionBudget budget, List<string> createdFiles)
    {
        if (string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName))
            throw new InvalidDataException($"非法压缩包路径: {entryName}");
        var relativePath = PathSanitizer.SanitizeRelative(entryName);
        foreach (var part in relativePath.Split(Path.DirectorySeparatorChar))
            PathSanitizer.SanitizeSegment(part);
        var outputPath = PathSanitizer.SafeCombine(extractPath, relativePath);
        RejectReparsePoints(extractPath, outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var destination = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        createdFiles.Add(outputPath);
        using var limited = budget.Limit(destination);
        extract(limited);
    }

    private static void RejectReparsePoints(string root, string target)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var path = target; path != null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"解压目标包含链接: {path}");
            if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), rootPath, StringComparison.OrdinalIgnoreCase))
                break;
        }
    }

    public static void ProcessNestedArchives(string directory, ILogger logger)
        => ProcessNestedArchives(directory, logger, MaxNestingDepth, MaxTotalExtractedBytes);

    internal static NestedExtractionResult ProcessNestedArchives(
        string directory, ILogger logger, int maxDepth, long maxTotalBytes)
        => ProcessNestedArchives(directory, logger, maxDepth, new ExtractionBudget(maxTotalBytes));

    /// <summary>
    /// 在写入流中检查同一预算。越界包的全部输出清理，之前完整展开的包保留；
    /// 导入入口据 StoppedBySizeLimit 拒绝将不完整包注册进仓库。
    /// </summary>
    internal static NestedExtractionResult ProcessNestedArchives(
        string directory, ILogger logger, int maxDepth, ExtractionBudget budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Archive, int Depth)>(
            EnumerateFiles(directory).Where(CompressedArchive.IsCompressed).Select(path => (path, 1)));

        var extracted = 0;
        var maxDepthReached = 0;
        var stoppedByDepth = false;
        var stoppedBySize = false;

        while (pending.Count > 0)
        {
            var (archive, depth) = pending.Dequeue();
            if (!processed.Add(archive)) continue;
            if (depth > maxDepth)
            {
                stoppedByDepth = true;
                logger.LogWarning("嵌套压缩包超过最大层数 {MaxDepth}，停止展开: {Path}", maxDepth, archive);
                continue;
            }
            if (budget.RemainingBytes == 0 || budget.IsExceeded)
            {
                stoppedBySize = true;
                break;
            }

            var extractDir = archive + "_extracted";
            // 已存在的目录可能是压缩包自带内容，不得在失败补偿时清理它。
            if (Directory.Exists(extractDir) || File.Exists(extractDir))
            {
                logger.LogWarning("嵌套解压目录已经存在，跳过: {Path}", extractDir);
                continue;
            }
            try
            {
                Directory.CreateDirectory(extractDir);
                if (!ExtractCompressedFile(archive, extractDir, logger, budget))
                {
                    // 此路径由本次创建且来源位于 directory 内，删除前仍验证实际边界。
                    var relative = Path.GetRelativePath(directory, extractDir);
                    var verified = PathSanitizer.SafeCombine(directory, relative);
                    RejectReparsePoints(directory, verified);
                    Directory.Delete(verified, recursive: true);
                    if (budget.IsExceeded)
                    {
                        stoppedBySize = true;
                        break;
                    }
                    continue;
                }

                extracted++;
                maxDepthReached = Math.Max(maxDepthReached, depth);
                foreach (var nestedArchive in EnumerateFiles(extractDir).Where(CompressedArchive.IsCompressed))
                    pending.Enqueue((nestedArchive, depth + 1));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Process nested archive failed: {Path}", archive);
                if (budget.IsExceeded)
                {
                    stoppedBySize = true;
                    break;
                }
            }
        }

        if (stoppedBySize)
            logger.LogWarning("解压累计写入达到 {LimitBytes} 字节上限，停止展开", budget.MaximumBytes);
        return new NestedExtractionResult(extracted, maxDepthReached, budget.WrittenBytes,
            stoppedByDepth, stoppedBySize);
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
        => Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    public static void CleanupArchives(string directory)
    {
        foreach (var archive in EnumerateFiles(directory).Where(CompressedArchive.IsCompressed))
        {
            try { File.Delete(archive); } catch { }
        }
    }
}
