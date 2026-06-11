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

    public static void ProcessNestedArchives(string directory, ILogger logger)
    {
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(CompressedArchive.IsCompressed));

        while (pending.Count > 0)
        {
            var archive = pending.Dequeue();
            if (!processed.Add(archive))
                continue;

            var extractDir = archive + "_extracted";
            try
            {
                Directory.CreateDirectory(extractDir);
                if (!ExtractCompressedFile(archive, extractDir, logger))
                    continue;

                foreach (var nestedArchive in Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories)
                    .Where(CompressedArchive.IsCompressed))
                {
                    pending.Enqueue(nestedArchive);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Process nested archive failed: {Path}", archive);
            }
        }
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