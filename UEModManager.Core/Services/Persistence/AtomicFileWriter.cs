using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UEModManager.Services.Persistence;

/// <summary>
/// 原子写：先写临时文件，再一步换到目标位置。读的人要么看到旧内容，要么看到新内容，看不到写了一半的。
///
/// <para><b>同一个文件的写入在这里排队</b></para>
/// 以前靠各个调用方自己串行化，而实际有一多半没做（资产索引缓存、启动会话、部署事务日志、游戏配置……）。
/// 两个写入者同时对同一个目标调 <see cref="File.Replace(string, string, string?)"/> 时，Windows 的 ReplaceFile
/// 会互相打断：后到的那个失败，系统给目标文件改的备份名（<c>目标名~RF十六进制.TMP</c>）留在目录里，
/// 极少数情况下目标文件名本身会暂时消失。排队之后，本进程内不会再有两个写入者同时碰同一个目标。
///
/// <para><b>排队挡不住的，事后收拾</b></para>
/// 另一个进程（开了两个管理器）、杀毒软件、索引服务仍然可能在那一瞬间打断替换。所以替换失败时再看一眼：
/// 目标不见了而新内容还在手里，就把新内容放到目标位置；每次写成功后顺手清掉系统以前留下的备份残留。
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>临时文件 + File.Replace 原子写。目标不存在时用 File.Move。</summary>
    public static async Task WriteAllTextAsync(string path, string content)
    {
        EnsureDirectory(path);

        var key = GateKey(path);
        var gate = EnterGate(key);
        try
        {
            // 等锁和写文件都不回到调用方的线程：同步重载可能正在界面线程上等同一把锁，
            // 持锁的这一方要是还得回界面线程才能写完，两边就互相等死了。
            await gate.Semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var tmp = TempPathFor(path);
                try
                {
                    var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await using (stream.ConfigureAwait(false))
                    {
                        var writer = new StreamWriter(stream);
                        await using (writer.ConfigureAwait(false))
                        {
                            await writer.WriteAsync(content).ConfigureAwait(false);
                            await writer.FlushAsync().ConfigureAwait(false);
                            stream.Flush(flushToDisk: true);
                        }
                    }
                    Commit(tmp, path, File.Replace);
                }
                finally
                {
                    DeleteBestEffort(tmp);
                }
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            LeaveGate(key, gate);
        }
    }

    /// <summary>同步重载，供同步保存路径使用。与异步重载排的是同一个队。</summary>
    public static void WriteAllText(string path, string content)
    {
        EnsureDirectory(path);

        var key = GateKey(path);
        var gate = EnterGate(key);
        try
        {
            gate.Semaphore.Wait();
            try
            {
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
                    Commit(tmp, path, File.Replace);
                }
                finally
                {
                    DeleteBestEffort(tmp);
                }
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            LeaveGate(key, gate);
        }
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// 每次写入用独立的临时文件名：固定的 <c>path + ".tmp"</c> 在两个写入者并发时会互相覆盖、
    /// 互删对方的临时文件，表现为"文件正由另一进程使用"或 File.Replace 找不到源文件。
    /// 排队之后本进程内不会再撞；另一个进程仍然可能，所以名字照旧各用各的。
    /// </summary>
    private static string TempPathFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// 把写好的临时文件换到目标位置。
    ///
    /// <paramref name="replace"/> 就是 <see cref="File.Replace(string, string, string?)"/>；
    /// 做成参数只为了让测试能摆出"替换被打断、目标文件名消失"这种靠真实并发才撞得出来的残局。
    /// </summary>
    internal static void Commit(string tmp, string path, Action<string, string, string?> replace)
    {
        try
        {
            if (File.Exists(path))
            {
                replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException)
                                   && !File.Exists(path) && File.Exists(tmp))
        {
            // 替换被打断在半路：系统已经把旧文件改成了备份名（或者别的进程刚把它换走），新内容还在临时文件里。
            // 这时不把新内容放过去，目标文件就没了——下次启动读不到它，等于整份数据丢了。
            // 这一步再失败就原样抛出去（临时文件由调用方的 finally 清掉）。
            File.Move(tmp, path);
        }

        DeleteReplaceLeftovers(path);
    }

    /// <summary>
    /// 清掉 ReplaceFile 被打断时留在目标旁边的备份（<c>目标名~RF十六进制.TMP</c>）。
    ///
    /// 只在这次写入成功之后调用：里面是更早的旧内容，新内容已经落盘，它们没有用了。
    /// 删不掉（别的进程正占着）就留着，下次写成功时再清。
    /// </summary>
    private static void DeleteReplaceLeftovers(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            foreach (var leftover in Directory.EnumerateFiles(dir, Path.GetFileName(path) + "~RF*.TMP"))
            {
                DeleteBestEffort(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 只是打扫：列不出目录不影响已经完成的写入。
        }
    }

    private static void DeleteBestEffort(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    // ─── 按目标文件排队 ───

    /// <summary>一个目标文件的队。<see cref="Users"/> 受 <see cref="Gates"/> 的锁保护。</summary>
    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    /// <summary>正在被写（或有人在等着写）的目标文件。没人用的队立刻移除，不随写过的文件数增长。</summary>
    private static readonly Dictionary<string, Gate> Gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同一个文件的不同写法（相对路径、大小写）要排进同一个队。</summary>
    private static string GateKey(string path) => Path.GetFullPath(path);

    private static Gate EnterGate(string key)
    {
        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out var gate))
            {
                Gates[key] = gate = new Gate();
            }

            gate.Users++;
            return gate;
        }
    }

    private static void LeaveGate(string key, Gate gate)
    {
        lock (Gates)
        {
            if (--gate.Users == 0)
            {
                Gates.Remove(key);
            }
        }
    }

    /// <summary>
    /// 这个目标文件现在有没有人在写或等着写（测试用：确认用完的队确实被移除了）。
    /// 按文件问、不数总数：别的测试同时在写别的文件时，总数不是 0 也正常。
    /// </summary>
    internal static bool HasQueueFor(string path)
    {
        lock (Gates)
        {
            return Gates.ContainsKey(GateKey(path));
        }
    }
}
