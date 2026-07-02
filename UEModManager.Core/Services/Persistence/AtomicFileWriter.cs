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

        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content);
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

        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            ReplaceOrMove(tmp, path);
        }
        finally
        {
            DeleteTempBestEffort(tmp);
        }
    }

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
