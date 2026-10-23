using System;
using System.IO;
using System.Threading.Tasks;

namespace UEModManager.Services.Persistence;

public static class AtomicFileWriter
{
    /// <summary>临时文件 + File.Replace 原子写。目标不存在时用 File.Move。</summary>
    public static async Task WriteAllTextAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = TempPathFor(path);
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(content);
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            ReplaceOrMove(tmp, path);
        }
        finally
        {
            DeleteTempBestEffort(tmp);
        }
    }

    /// <summary>同步重载，供同步保存路径使用。</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = TempPathFor(path);
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            ReplaceOrMove(tmp, path);
        }
        finally
        {
            DeleteTempBestEffort(tmp);
        }
    }

    /// <summary>
    /// 每次写入用独立的临时文件名：固定的 <c>path + ".tmp"</c> 在两个写入者并发时会互相覆盖、
    /// 互删对方的临时文件，表现为"文件正由另一进程使用"或 File.Replace 找不到源文件。
    /// </summary>
    private static string TempPathFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    private static void ReplaceOrMove(string tmp, string path)
    {
        if (File.Exists(path))
        {
            File.Replace(tmp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static void DeleteTempBestEffort(string tmp)
    {
        if (!File.Exists(tmp))
        {
            return;
        }

        try
        {
            File.Delete(tmp);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
