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
        AssertNoTempLeft(path);
    }

    [Fact]
    public async Task WriteAllTextAsync_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "profiles.json");
        await File.WriteAllTextAsync(path, "old");

        await AtomicFileWriter.WriteAllTextAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        AssertNoTempLeft(path);
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
        AssertNoTempLeft(path);
    }

    [Fact]
    public void WriteAllText_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "sync.json");
        File.WriteAllText(path, "old");

        AtomicFileWriter.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        AssertNoTempLeft(path);
    }

    [Fact]
    public async Task WriteAllTextAsync_并发写同一文件_不互相踩临时文件()
    {
        var path = Path.Combine(_tempDir, "cache.json");
        var contents = Enumerable.Range(0, 16).Select(i => new string((char)('a' + i), 4096)).ToList();

        // 同一目标的并发写本该由调用方串行化；这里只兜底两件事：
        // 撞上的写入者可以失败，但目标文件始终是某一次完整的内容，且不留临时文件
        var tasks = contents.Select(c => Task.Run(async () =>
        {
            try { await AtomicFileWriter.WriteAllTextAsync(path, c); }
            catch (IOException) { }
        }));
        await Task.WhenAll(tasks);

        Assert.Contains(await File.ReadAllTextAsync(path), contents);
        AssertNoTempLeft(path);
    }

    private static void AssertNoTempLeft(string path)
        => Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

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
