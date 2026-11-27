using UEModManager.Services.Persistence;

namespace UEModManager.Core.Tests.Services.Persistence;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "uemodmanager-atomic-writer-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task WriteAllTextAsync_CreatesNewFile()
    {
        var path = Path.Combine(_tempDir, "new", "config.json");

        await AtomicFileWriter.WriteAllTextAsync(path, """{"value":1}""");

        Assert.Equal("""{"value":1}""", await File.ReadAllTextAsync(path));
        AssertNothingElseLeft(path);
    }

    [Fact]
    public async Task WriteAllTextAsync_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "profiles.json");
        await File.WriteAllTextAsync(path, "old");

        await AtomicFileWriter.WriteAllTextAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        AssertNothingElseLeft(path);
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenReplaceFails_KeepsOriginalAndDeletesTemp()
    {
        var path = Path.Combine(_tempDir, "locked.json");
        await File.WriteAllTextAsync(path, "old");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => AtomicFileWriter.WriteAllTextAsync(path, "new"));
        }

        Assert.Equal("old", await File.ReadAllTextAsync(path));
        AssertNothingElseLeft(path);

        // 失败的那一次也要把队让出来：后面的写入不能被它堵住
        await AtomicFileWriter.WriteAllTextAsync(path, "after");
        Assert.Equal("after", await File.ReadAllTextAsync(path));
        Assert.False(AtomicFileWriter.HasQueueFor(path));
    }

    [Fact]
    public void WriteAllText_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "sync.json");
        File.WriteAllText(path, "old");

        AtomicFileWriter.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        AssertNothingElseLeft(path);
    }

    // ─── 同一个文件的并发写 ───

    [Fact]
    public async Task WriteAllTextAsync_并发写同一文件_全部成功_目标完整_不留任何残留()
    {
        // 以前这条用例允许"撞上的写入者失败"，而且会偶发失败：16 个写入者同时对同一个目标调 File.Replace，
        // Windows 的 ReplaceFile 互相打断——后到的抛 IOException（1175/1176/1177）、偶尔抛 UnauthorizedAccessException，
        // 系统给旧文件改的备份名 cache.json~RF….TMP 留在目录里，极少数情况下 cache.json 这个名字本身暂时消失。
        // 大多数调用方并没有自己串行化，所以现在由 AtomicFileWriter 按目标文件排队：一个都不许失败。
        var path = Path.Combine(_tempDir, "cache.json");
        var contents = Enumerable.Range(0, 16).Select(i => new string((char)('a' + i), 4096)).ToList();

        // 多跑几轮：这类问题一轮未必撞得出来
        for (var round = 0; round < 20; round++)
        {
            var failures = await WriteConcurrentlyAsync(contents.Select(c =>
                (Func<Task>)(() => AtomicFileWriter.WriteAllTextAsync(path, c))));

            Assert.Empty(failures);
            Assert.Contains(await File.ReadAllTextAsync(path), contents);
            AssertNothingElseLeft(path);
        }

        Assert.False(AtomicFileWriter.HasQueueFor(path));
    }

    [Fact]
    public async Task 同步与异步重载_以及同一文件的不同写法_排的是同一个队()
    {
        // 界面偏好、游戏配置走同步重载，其余走异步重载；同一个文件还可能被写成不同大小写、带 . 或 .. 的路径。
        // 这些都是同一个目标，必须排进同一个队。
        var path = Path.Combine(_tempDir, "Mixed.json");
        var spellings = new[]
        {
            path,
            path.ToUpperInvariant(),
            Path.Combine(_tempDir, ".", "Mixed.json"),
            Path.Combine(_tempDir, "sub", "..", "mixed.JSON"),
        };
        var contents = Enumerable.Range(0, 16).Select(i => new string((char)('A' + i), 4096)).ToList();

        for (var round = 0; round < 10; round++)
        {
            var failures = await WriteConcurrentlyAsync(contents.Select((c, i) =>
            {
                var spelling = spellings[i % spellings.Length];
                return i % 2 == 0
                    ? (Func<Task>)(() => AtomicFileWriter.WriteAllTextAsync(spelling, c))
                    : () => { AtomicFileWriter.WriteAllText(spelling, c); return Task.CompletedTask; };
            }));

            Assert.Empty(failures);
            Assert.Contains(await File.ReadAllTextAsync(path), contents);
            // 目录里只有这一个文件（文件名的大小写取决于哪种写法先把它建出来，不比）
            Assert.Equal(["Mixed.json"], Directory.GetFiles(_tempDir).Select(Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);
        }

        Assert.False(AtomicFileWriter.HasQueueFor(path));
    }

    [Fact]
    public void 界面线程上同步写_不会和进行中的异步写互相等死()
    {
        // 界面线程发起一次异步写（没写完），紧接着又同步写同一个文件——设置页就是这么用的。
        // 同步写在界面线程上等锁；持锁的异步写要是还得回界面线程才能继续，两边就永远等下去。
        // 这里的"界面线程"是一个只收不办的同步上下文：凡是被派回来的后续步骤都不会执行。
        var path = Path.Combine(_tempDir, "ui.json");
        var big = new string('x', 8 * 1024 * 1024);
        Exception? failure = null;

        var uiThread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
                var pending = AtomicFileWriter.WriteAllTextAsync(path, big);
                AtomicFileWriter.WriteAllText(path, "second");
                if (!pending.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("异步写一直没有完成");
            }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };
        uiThread.Start();

        Assert.True(uiThread.Join(TimeSpan.FromSeconds(60)), "同步写和异步写互相等死了");
        Assert.Null(failure);
        Assert.Equal("second", File.ReadAllText(path));
        AssertNothingElseLeft(path);
    }

    // ─── 替换被打断之后 ───

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_替换被打断且目标文件名消失_仍把新内容放到目标位置(bool accessDenied)
    {
        // ReplaceFile 的 1177（ERROR_UNABLE_TO_MOVE_REPLACEMENT_2）：旧文件已经被改成备份名，
        // 新文件却没能挪过去——目标文件名不存在了，新内容还在临时文件里。
        // 排队之后本进程内不会再撞出这种状态，但另一个进程（开了两个管理器）、杀毒软件仍然可能，
        // 所以靠真实并发摆不稳的这个残局在这里直接摆出来。
        var path = Path.Combine(_tempDir, "state.json");
        var tmp = path + ".0123456789abcdef0123456789abcdef.tmp";
        File.WriteAllText(path, "old");
        File.WriteAllText(tmp, "new");

        AtomicFileWriter.Commit(tmp, path, (_, target, _) =>
        {
            File.Move(target, target + "~RF1a2b3c.TMP");
            throw accessDenied
                ? new UnauthorizedAccessException("Access to the path is denied.")
                : new IOException("Unable to move the replacement file.", unchecked((int)0x80070499));
        });

        Assert.Equal("new", File.ReadAllText(path));
        AssertNothingElseLeft(path);
    }

    [Fact]
    public void Commit_替换失败而目标还在_原样抛出_旧内容不动()
    {
        var path = Path.Combine(_tempDir, "busy.json");
        var tmp = path + ".0123456789abcdef0123456789abcdef.tmp";
        File.WriteAllText(path, "old");
        File.WriteAllText(tmp, "new");

        var thrown = Assert.Throws<IOException>(() => AtomicFileWriter.Commit(tmp, path,
            (_, _, _) => throw new IOException("The process cannot access the file.", unchecked((int)0x80070020))));

        Assert.Equal(unchecked((int)0x80070020), thrown.HResult);
        Assert.Equal("old", File.ReadAllText(path));
        // 临时文件由调用方的 finally 清理，这里只确认没有把它误挪到目标上
        Assert.Equal("new", File.ReadAllText(tmp));
    }

    [Fact]
    public async Task 写成功后_清掉系统以前留下的备份残留_不碰别的文件()
    {
        // 老版本撞出来的 ~RF 残留可能已经躺在玩家的数据目录里了：下一次写成功时顺手清掉。
        var path = Path.Combine(_tempDir, "index.json");
        await File.WriteAllTextAsync(path, "old");
        var leftovers = new[] { path + "~RF3f2a1b.TMP", path + "~RFabc.TMP" };
        foreach (var leftover in leftovers) await File.WriteAllTextAsync(leftover, "older");
        // 名字相近、但不是这个目标的备份残留：一个都不许动
        var bystanders = new[]
        {
            Path.Combine(_tempDir, "other.json~RF3f2a1b.TMP"),
            Path.Combine(_tempDir, "index.json.bak"),
            Path.Combine(_tempDir, "index.json.corrupt-20261126.json"),
            Path.Combine(_tempDir, "my-index.json~RF1.TMP"),
        };
        foreach (var bystander in bystanders) await File.WriteAllTextAsync(bystander, "keep");

        await AtomicFileWriter.WriteAllTextAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.All(leftovers, leftover => Assert.False(File.Exists(leftover), leftover));
        Assert.All(bystanders, bystander => Assert.Equal("keep", File.ReadAllText(bystander)));
    }

    [Fact]
    public async Task 写失败时_不清备份残留()
    {
        // 这次没写成功的话，残留里的旧内容可能是唯一还能找回的东西
        var path = Path.Combine(_tempDir, "kept.json");
        await File.WriteAllTextAsync(path, "old");
        var leftover = path + "~RF3f2a1b.TMP";
        await File.WriteAllTextAsync(leftover, "older");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => AtomicFileWriter.WriteAllTextAsync(path, "new"));
        }

        Assert.Equal("older", await File.ReadAllTextAsync(leftover));
    }

    // ─── 工具 ───

    /// <summary>同时放出全部写入者，返回失败的那些（任何异常都算）。</summary>
    private static async Task<List<Exception>> WriteConcurrentlyAsync(IEnumerable<Func<Task>> writers)
    {
        var failures = new List<Exception>();
        using var start = new ManualResetEventSlim();
        var tasks = writers.Select(write => Task.Run(async () =>
        {
            start.Wait();
            try { await write(); }
            catch (Exception ex) { lock (failures) failures.Add(ex); }
        })).ToList();
        start.Set();
        await Task.WhenAll(tasks);
        return failures;
    }

    /// <summary>目标所在目录里除了目标自己什么都不剩：没有临时文件，也没有系统留下的 ~RF 备份。</summary>
    private static void AssertNothingElseLeft(string path)
        => Assert.Equal([Path.GetFileName(path)],
            Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));

    /// <summary>被阻塞住的界面线程：派回来的后续步骤只收下、永远不执行。</summary>
    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) { }
        public override SynchronizationContext CreateCopy() => this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for locked-file failures.
        }
    }
}
