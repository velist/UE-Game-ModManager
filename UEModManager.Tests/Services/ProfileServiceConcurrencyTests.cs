using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// ProfileService 并发保护测试。
///
/// 设计约束：**不用 Task.Delay 赌时序**。这里的做法是放出大量真正并发的操作，
/// 然后只断言"最终状态"这类与交错顺序无关的性质：
///   - 不抛异常（枚举安全）
///   - 没有丢更新（落盘内容包含全部改动）
///
/// 这类测试的失败方向是单向的：**它可能漏报（某次运行恰好没撞上），但不会误报**。
/// 因此不会成为 CI 里的 flaky 测试；而在移除锁之后，它们会以很高的概率变红。
/// </summary>
public sealed class ProfileServiceConcurrencyTests : IDisposable
{
    // 每个用例类一份独立临时目录。此处曾经写的是"测试宿主进程目录\Data"，
    // 而 ProfileService 的数据目录早已归口到 AppPaths —— 于是测试实际写的是开发者真实的
    // %LOCALAPPDATA%\UEModManager\Data，Dispose 又删不到，每跑一次就留下一批孤儿文件。
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "uemm_profile_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<string> _gameNames = [];

    private async Task<ProfileService> CreateServiceAsync(int packageCount)
    {
        var service = new ProfileService(NullLogger<ProfileService>.Instance, _dataDir);
        await service.SetCurrentGameAsync(NewGameName());

        var entries = new List<ProfilePackageEntry>();
        for (var i = 0; i < packageCount; i++)
            entries.Add(new ProfilePackageEntry { PackageKey = $"pkg-{i}", IsEnabled = false });
        service.CurrentProfile!.Packages = entries;

        return service;
    }

    [Fact]
    public async Task ConcurrentFlagWrites_LoseNoUpdates()
    {
        const int count = 64;
        var service = await CreateServiceAsync(count);

        // 全部同时发起：每个写一个不同的包
        var writes = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => service.SetPackageEnabledFlagAsync($"pkg-{i}", true)))
            .ToArray();
        await Task.WhenAll(writes);

        // 内存里必须一个不落
        Assert.All(service.CurrentProfile!.Packages, p => Assert.True(p.IsEnabled));

        // 落盘内容同样一个不落：无锁时两个写入方可能各自序列化再反序写回，后写的把先写的覆盖掉
        var reloaded = new ProfileService(NullLogger<ProfileService>.Instance, _dataDir);
        await reloaded.SetCurrentGameAsync(_gameNames[^1]);
        Assert.Equal(count, reloaded.CurrentProfile!.Packages.Count);
        Assert.All(reloaded.CurrentProfile!.Packages, p => Assert.True(p.IsEnabled));
    }

    [Fact]
    public async Task ConcurrentCreateProfile_AllProfilesSurvive()
    {
        const int count = 32;
        var service = await CreateServiceAsync(0);
        var initial = service.GetProfiles().Count;

        var creates = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => service.CreateProfileAsync($"方案-{i}")))
            .ToArray();
        await Task.WhenAll(creates);

        // 无锁时 _profiles.Add 会并发写同一个 List，轻则丢条目重则抛异常
        Assert.Equal(initial + count, service.GetProfiles().Count);

        var reloaded = new ProfileService(NullLogger<ProfileService>.Instance, _dataDir);
        await reloaded.SetCurrentGameAsync(_gameNames[^1]);
        Assert.Equal(initial + count, reloaded.GetProfiles().Count);
    }

    [Fact]
    public async Task EnumeratingWhileWriting_DoesNotThrow()
    {
        // 这条针对的是 InvalidOperationException("Collection was modified")：
        // 读取方无锁枚举，写入方走 swap-on-write，两者不应互相影响。
        var service = await CreateServiceAsync(0);
        using var cts = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                // 同时枚举外层列表与嵌套的 Packages
                foreach (var p in service.GetProfiles())
                {
                    foreach (var entry in p.Packages)
                        _ = entry.PackageKey;
                }
            }
        });

        var writers = Enumerable.Range(0, 40)
            .Select(i => Task.Run(() => service.CreateProfileAsync($"并发方案-{i}")))
            .ToArray();

        await Task.WhenAll(writers);
        cts.Cancel();

        // 读取线程若抛异常，这里 await 时会重新抛出
        await reader;
    }

    [Fact]
    public async Task ConcurrentBatches_StillPersistConsistentState()
    {
        // 批处理计数受同一把锁保护：并发开关批处理不应把深度算错，
        // 算错的表现是"提前落盘"或"永远不落盘"。
        const int count = 16;
        var service = await CreateServiceAsync(count);

        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await using (await service.BeginBatchAsync())
            {
                await service.SetPackageEnabledFlagAsync($"pkg-{i}", true);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        var reloaded = new ProfileService(NullLogger<ProfileService>.Instance, _dataDir);
        await reloaded.SetCurrentGameAsync(_gameNames[^1]);
        Assert.All(reloaded.CurrentProfile!.Packages, p => Assert.True(p.IsEnabled));
    }

    private string NewGameName()
    {
        var game = "UEMMConc_" + Guid.NewGuid().ToString("N");
        _gameNames.Add(game);
        return game;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
