using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// ProfileService 批处理作用域测试 —— 验证"批量期间只落盘一次"。
///
/// 断言的是 <c>PersistCount</c>（实际写盘次数）而不是靠计时或轮询文件，
/// 因此完全确定性，没有 Task.Delay 之类的时序赌博。
/// </summary>
public sealed class ProfileServiceBatchTests : IDisposable
{
    // 每个用例类一份独立临时目录。此处曾经写的是"测试宿主进程目录\Data"，
    // 而 ProfileService 的数据目录早已归口到 AppPaths —— 于是测试实际写的是开发者真实的
    // %LOCALAPPDATA%\UEModManager\Data，Dispose 又删不到，每跑一次就留下一批孤儿文件。
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "uemm_profile_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<string> _gameNames = [];

    private async Task<ProfileService> CreateServiceAsync()
    {
        var service = new ProfileService(NullLogger<ProfileService>.Instance, _dataDir);
        await service.SetCurrentGameAsync(NewGameName());
        return service;
    }

    /// <summary>往当前方案塞 N 个包，供 SetPackageEnabledFlagAsync 逐个翻转。</summary>
    private static void SeedPackages(ProfileService service, int count)
    {
        var profile = service.CurrentProfile!;
        var entries = new List<ProfilePackageEntry>();
        for (var i = 0; i < count; i++)
            entries.Add(new ProfilePackageEntry { PackageKey = $"pkg-{i}", IsEnabled = false });
        profile.Packages = entries;
    }

    // ─── 核心诉求：N 次写操作只落盘 1 次 ───

    [Fact]
    public async Task Batch_NWrites_PersistsOnce()
    {
        var service = await CreateServiceAsync();
        SeedPackages(service, 100);
        var before = service.PersistCount;

        await using (await service.BeginBatchAsync())
        {
            for (var i = 0; i < 100; i++)
                await service.SetPackageEnabledFlagAsync($"pkg-{i}", true);
        }

        Assert.Equal(before + 1, service.PersistCount);
    }

    [Fact]
    public async Task WithoutBatch_NWrites_PersistsNTimes()
    {
        // 对照组：不开批处理就是 N 次全量写盘（这正是要消除的写放大）
        var service = await CreateServiceAsync();
        SeedPackages(service, 10);
        var before = service.PersistCount;

        for (var i = 0; i < 10; i++)
            await service.SetPackageEnabledFlagAsync($"pkg-{i}", true);

        Assert.Equal(before + 10, service.PersistCount);
    }

    [Fact]
    public async Task Batch_WritesAreStillVisibleInMemoryDuringBatch()
    {
        // 延迟的只是落盘，不是内存状态——批量中途读到的必须已经是新值
        var service = await CreateServiceAsync();
        SeedPackages(service, 3);

        await using (await service.BeginBatchAsync())
        {
            await service.SetPackageEnabledFlagAsync("pkg-1", true);
            Assert.True(service.CurrentProfile!.Packages.Single(p => p.PackageKey == "pkg-1").IsEnabled);
        }
    }

    [Fact]
    public async Task Batch_NoWrites_DoesNotPersist()
    {
        var service = await CreateServiceAsync();
        var before = service.PersistCount;

        await using (await service.BeginBatchAsync()) { }

        Assert.Equal(before, service.PersistCount);
    }

    [Fact]
    public async Task Batch_NoOpWrites_DoNotPersist()
    {
        // SetPackageEnabledFlagAsync 对"值没变"的情况直接返回，不该标脏
        var service = await CreateServiceAsync();
        SeedPackages(service, 3);
        var before = service.PersistCount;

        await using (await service.BeginBatchAsync())
        {
            // 三个包本来就是 false，再设一次 false 属于空操作
            for (var i = 0; i < 3; i++)
                await service.SetPackageEnabledFlagAsync($"pkg-{i}", false);
        }

        Assert.Equal(before, service.PersistCount);
    }

    // ─── 嵌套 ───

    [Fact]
    public async Task NestedBatches_PersistOnlyWhenOutermostEnds()
    {
        var service = await CreateServiceAsync();
        SeedPackages(service, 4);
        var before = service.PersistCount;

        await using (await service.BeginBatchAsync())
        {
            await service.SetPackageEnabledFlagAsync("pkg-0", true);

            await using (await service.BeginBatchAsync())
            {
                await service.SetPackageEnabledFlagAsync("pkg-1", true);
            }

            // 内层结束不该落盘——外层还开着
            Assert.Equal(before, service.PersistCount);

            await service.SetPackageEnabledFlagAsync("pkg-2", true);
        }

        Assert.Equal(before + 1, service.PersistCount);
    }

    [Fact]
    public async Task BatchScope_DoubleDispose_IsSafe()
    {
        var service = await CreateServiceAsync();
        SeedPackages(service, 2);
        var before = service.PersistCount;

        var batch = await service.BeginBatchAsync();
        await service.SetPackageEnabledFlagAsync("pkg-0", true);
        await batch.DisposeAsync();
        await batch.DisposeAsync();   // 重复 Dispose 不应再落盘一次，也不应把深度压成负数

        Assert.Equal(before + 1, service.PersistCount);

        // 深度必须已归零：批处理结束后的写操作要立即落盘
        await service.SetPackageEnabledFlagAsync("pkg-1", true);
        Assert.Equal(before + 2, service.PersistCount);
    }

    [Fact]
    public async Task AfterBatch_WritesPersistImmediatelyAgain()
    {
        var service = await CreateServiceAsync();
        SeedPackages(service, 3);

        await using (await service.BeginBatchAsync())
        {
            await service.SetPackageEnabledFlagAsync("pkg-0", true);
        }

        var afterBatch = service.PersistCount;
        await service.SetPackageEnabledFlagAsync("pkg-1", true);

        Assert.Equal(afterBatch + 1, service.PersistCount);
    }

    // ─── 落盘内容正确性 ───

    [Fact]
    public async Task Batch_FlushWritesAllAccumulatedChanges()
    {
        // 只写一次，但写进去的必须是全部 N 个改动的最终状态
        var service = await CreateServiceAsync();
        SeedPackages(service, 5);

        await using (await service.BeginBatchAsync())
        {
            for (var i = 0; i < 5; i++)
                await service.SetPackageEnabledFlagAsync($"pkg-{i}", true);
        }

        // 用一个新实例从磁盘读回，验证落盘内容
        var reloaded = new ProfileService(NullLogger<ProfileService>.Instance, _dataDir);
        await reloaded.SetCurrentGameAsync(_gameNames[^1]);

        var packages = reloaded.CurrentProfile!.Packages;
        Assert.Equal(5, packages.Count);
        Assert.All(packages, p => Assert.True(p.IsEnabled));
    }

    private string NewGameName()
    {
        var game = "UEMMBatch_" + Guid.NewGuid().ToString("N");
        _gameNames.Add(game);
        return game;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
