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
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task WriteAllTextAsync_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "profiles.json");
        await File.WriteAllTextAsync(path, "old");

        await AtomicFileWriter.WriteAllTextAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
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
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteAllText_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempDir, "sync.json");
        File.WriteAllText(path, "old");

        AtomicFileWriter.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
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
