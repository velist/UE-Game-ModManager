using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Data;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 头像目录迁移的集成测试：真实的临时目录 + 真实的临时 SQLite。
///
/// <para>
/// 这里刻意不 mock 文件系统与 EF。要防的是"复制成功了但列没回写""列改了但文件没到位"
/// 这类跨介质的顺序问题，而那正是 mock 掉之后就测不到的部分。
/// </para>
/// </summary>
public sealed class AvatarLocationMigratorTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacyDir;
    private readonly string _currentDir;
    private readonly string _dbPath;

    public AvatarLocationMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm-avatar-" + Guid.NewGuid().ToString("N"));
        _legacyDir = Path.Combine(_root, "Install", "UserData", "Avatars");
        _currentDir = Path.Combine(_root, "Roaming", "Avatars");
        _dbPath = Path.Combine(_root, "test.db");
        Directory.CreateDirectory(_legacyDir);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败无所谓 */ }
    }

    private LocalDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var db = new LocalDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static AvatarLocationMigrator NewMigrator() =>
        new(NullLogger<AvatarLocationMigrator>.Instance);

    private string WriteLegacyFile(string name, string content = "fake-image")
    {
        var path = Path.Combine(_legacyDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<int> AddUserAsync(string? avatar)
    {
        using var db = NewDb();
        var user = new LocalUser { Email = $"u{Guid.NewGuid():N}@test.local", PasswordHash = "x", Avatar = avatar };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<string?> ReadAvatarAsync(int userId)
    {
        using var db = NewDb();
        var user = await db.Users.FindAsync(userId);
        return user?.Avatar;
    }

    private Task<AvatarMigrationOutcome> RunAsync()
    {
        using var db = NewDb();
        return NewMigrator().RunAsync(db, _legacyDir, _currentDir);
    }

    [Fact]
    public async Task 旧目录里的头像_复制到新目录并回写列_且不删源文件()
    {
        var source = WriteLegacyFile("1_20260101120000.png");
        var userId = await AddUserAsync(source);

        var outcome = await RunAsync();

        Assert.Equal(1, outcome.Migrated);
        var stored = await ReadAvatarAsync(userId);
        Assert.NotNull(stored);
        Assert.StartsWith(_currentDir, stored!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(stored), "目标文件应已就位");
        Assert.True(File.Exists(source), "源文件不该被删除");
    }

    [Fact]
    public async Task 复制失败时_列保持原值不动()
    {
        var source = WriteLegacyFile("2_20260101120000.png");
        var userId = await AddUserAsync(source);

        // 在目标目录的位置放一个同名**文件**，CreateDirectory 会因此失败
        Directory.CreateDirectory(Path.GetDirectoryName(_currentDir)!);
        File.WriteAllText(_currentDir, "占位，使得同名目录无法创建");

        var outcome = await RunAsync();

        Assert.Equal(1, outcome.Failed);
        Assert.Equal(0, outcome.Migrated);
        Assert.Equal(source, await ReadAvatarAsync(userId));
    }

    [Fact]
    public async Task 旧目录里的文件已丢失_列置空()
    {
        var missing = Path.Combine(_legacyDir, "3_gone.png");
        var userId = await AddUserAsync(missing);

        var outcome = await RunAsync();

        Assert.Equal(1, outcome.Cleared);
        Assert.Null(await ReadAvatarAsync(userId));
    }

    /// <summary>
    /// 外部路径即使文件不存在也**不能**清空：可能只是 U 盘或网络盘暂时不可访问。
    /// </summary>
    [Fact]
    public async Task 外部路径的文件不存在_列不动()
    {
        var foreign = Path.Combine(_root, "Elsewhere", "photo.png");
        var userId = await AddUserAsync(foreign);

        var outcome = await RunAsync();

        Assert.Equal(0, outcome.Cleared);
        Assert.Equal(0, outcome.Migrated);
        Assert.Equal(foreign, await ReadAvatarAsync(userId));
    }

    [Fact]
    public async Task 目标同名冲突_改名而非覆盖()
    {
        var source = WriteLegacyFile("4_20260101120000.png", "新内容");
        var userId = await AddUserAsync(source);

        Directory.CreateDirectory(_currentDir);
        var occupied = Path.Combine(_currentDir, "4_20260101120000.png");
        File.WriteAllText(occupied, "原有内容");

        await RunAsync();

        var stored = await ReadAvatarAsync(userId);
        Assert.NotEqual(occupied, stored);
        Assert.Equal("原有内容", File.ReadAllText(occupied));
        Assert.Equal("新内容", File.ReadAllText(stored!));
    }

    [Fact]
    public async Task 连跑两次_第二次零写入()
    {
        WriteLegacyFile("5_20260101120000.png");
        await AddUserAsync(Path.Combine(_legacyDir, "5_20260101120000.png"));

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(1, first.Migrated);
        Assert.False(second.ChangedAnything, "第二次应完全空转");
        Assert.Equal(0, second.Migrated);
        Assert.Equal(0, second.Cleared);
    }

    [Fact]
    public async Task 未设置头像_不产生任何动作()
    {
        var userId = await AddUserAsync(null);

        var outcome = await RunAsync();

        Assert.False(outcome.ChangedAnything);
        Assert.Null(await ReadAvatarAsync(userId));
    }

    [Fact]
    public async Task 不留下临时文件()
    {
        WriteLegacyFile("6_20260101120000.png");
        await AddUserAsync(Path.Combine(_legacyDir, "6_20260101120000.png"));

        await RunAsync();

        Assert.Empty(Directory.GetFiles(_currentDir, "*.tmp"));
    }
}
