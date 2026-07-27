using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// OverwriteStore 的包 key 比较口径与索引损坏容错测试。
///
/// 构造说明：OverwriteStore 的构造函数只是保存 PackageRepository / PackageImportService 引用，
/// 本文件覆盖的两条路径（按包筛选、加载索引）都不会触碰它们，因此传 null 即可，
/// 避免为了两个纯逻辑分支去搭建整条导入依赖链。
/// </summary>
public sealed class OverwriteStoreTests : IDisposable
{
    private readonly string _indexDirectory =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

    private readonly List<string> _gameNames = [];

    private OverwriteStore CreateStore()
        => new(NullLogger<OverwriteStore>.Instance, null!, null!);

    // ─── 包 key 大小写口径（与 PackageRepository.GetByKey 对齐）───

    [Theory]
    [InlineData("MyPackage")]   // 完全一致
    [InlineData("mypackage")]   // 全小写
    [InlineData("MYPACKAGE")]   // 全大写
    [InlineData("MyPaCkAgE")]   // 混合
    public async Task GetBySourcePackage_MatchesIgnoringCase(string queryKey)
    {
        var game = await SeedIndexAsync(MakeArtifact("MyPackage"));
        var store = CreateStore();
        await store.SetCurrentGameAsync(game);

        var found = store.GetBySourcePackage(queryKey);

        Assert.Single(found);
    }

    [Fact]
    public async Task GetBySourcePackage_DoesNotMatchDifferentKey()
    {
        var game = await SeedIndexAsync(MakeArtifact("MyPackage"));
        var store = CreateStore();
        await store.SetCurrentGameAsync(game);

        Assert.Empty(store.GetBySourcePackage("OtherPackage"));
    }

    [Fact]
    public async Task GetBySourcePackage_NullSourceKey_NeverMatches()
    {
        // SourcePackageKey 可空；忽略大小写的比较不能把 null 误判成匹配
        var game = await SeedIndexAsync(MakeArtifact(null));
        var store = CreateStore();
        await store.SetCurrentGameAsync(game);

        Assert.Empty(store.GetBySourcePackage("anything"));
    }

    [Fact]
    public async Task CleanupByPackageAsync_RemovesEntriesWithDifferentCasing()
    {
        // 这是本条修复的实际危害：大小写不同导致漏清理，留下孤儿生成物
        var game = await SeedIndexAsync(MakeArtifact("MyPackage"), MakeArtifact("Keep"));
        var store = CreateStore();
        await store.SetCurrentGameAsync(game);

        await store.CleanupByPackageAsync("MYPACKAGE");

        Assert.Empty(store.GetBySourcePackage("MyPackage"));
        Assert.Single(store.GetBySourcePackage("Keep"));
    }

    // ─── 索引损坏容错 ───

    [Fact]
    public async Task SetCurrentGameAsync_CorruptIndex_DoesNotThrow()
    {
        // 损坏索引以前会直接抛出，冒泡到 SetCurrentGameAsync 的调用链
        //（游戏切换 / 启动初始化），导致整个初始化中断。
        var game = await SeedRawIndexAsync("{ this is not valid json");
        var store = CreateStore();

        await store.SetCurrentGameAsync(game);

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task SetCurrentGameAsync_CorruptIndex_BacksUpTheBrokenFile()
    {
        var game = await SeedRawIndexAsync("{ this is not valid json");
        var store = CreateStore();

        await store.SetCurrentGameAsync(game);

        Assert.NotEmpty(FindBackups(game));
    }

    [Fact]
    public async Task SetCurrentGameAsync_ValidIndex_LoadsWithoutBackup()
    {
        var game = await SeedIndexAsync(MakeArtifact("pkg"));
        var store = CreateStore();

        await store.SetCurrentGameAsync(game);

        Assert.Single(store.GetAll());
        Assert.Empty(FindBackups(game));
    }

    [Fact]
    public async Task SetCurrentGameAsync_MissingIndex_StartsEmpty()
    {
        var game = NewGameName();
        var store = CreateStore();

        await store.SetCurrentGameAsync(game);

        Assert.Empty(store.GetAll());
        Assert.Empty(FindBackups(game));
    }

    // ─── 夹具 ───

    private static GeneratedArtifact MakeArtifact(string? sourcePackageKey) => new()
    {
        RelativePath = $"mod/{Guid.NewGuid():N}.pak",
        DisplayName = "artifact",
        Type = GeneratedArtifactType.ToolOutput,
        Status = GeneratedArtifactStatus.Active,
        SourcePackageKey = sourcePackageKey
    };

    private string NewGameName()
    {
        var game = "UEMMTest_" + Guid.NewGuid().ToString("N");
        _gameNames.Add(game);
        return game;
    }

    private async Task<string> SeedIndexAsync(params GeneratedArtifact[] artifacts)
        => await SeedRawIndexAsync(JsonConvert.SerializeObject(artifacts));

    private async Task<string> SeedRawIndexAsync(string json)
    {
        var game = NewGameName();
        Directory.CreateDirectory(_indexDirectory);
        await File.WriteAllTextAsync(IndexPath(game), json);
        return game;
    }

    private string IndexPath(string game)
        => Path.Combine(_indexDirectory, $"{game}_overwrites.json");

    private string[] FindBackups(string game)
        => Directory.Exists(_indexDirectory)
            ? Directory.GetFiles(_indexDirectory, $"{game}_overwrites.json.corrupt-*.bak")
            : [];

    public void Dispose()
    {
        // 索引文件写在测试输出目录的 Data/ 下；生成物根目录在 %APPDATA%，
        // SetCurrentGameAsync 会在那里建一个空的游戏目录，一并清掉。
        var overwriteRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UEModManager", "Overwrites");

        foreach (var game in _gameNames)
        {
            foreach (var file in FindBackups(game))
            {
                try { File.Delete(file); } catch { }
            }
            try { File.Delete(IndexPath(game)); } catch { }
            try { Directory.Delete(Path.Combine(overwriteRoot, game), recursive: true); } catch { }
        }
    }
}
