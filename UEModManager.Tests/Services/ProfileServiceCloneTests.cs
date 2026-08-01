using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// ProfileService.CloneProfileAsync 的字段保真测试。
///
/// 背景：审计曾把"克隆丢 TargetRootPath"列为缺陷，实际不成立——
/// <c>ProfilePackageEntry.PluginTargetPath</c> 是 <c>TargetRootPath</c> 的 get/set 转发
/// （见 UEModManager.Core/Models/InstanceProfile.cs），拷旧别名等价于拷规范字段。
/// 但"只是碰巧没坏"不是可依赖的性质：一旦别名被拆开或加上 JsonIgnore，
/// entry 级覆盖就会静默丢失。这里把行为锁死。
/// </summary>
public sealed class ProfileServiceCloneTests : IDisposable
{
    // 每个用例类一份独立临时目录。此处曾经写的是"测试宿主进程目录\Data"，
    // 而 ProfileService 的数据目录早已归口到 AppPaths —— 于是测试实际写的是开发者真实的
    // %LOCALAPPDATA%\UEModManager\Data，Dispose 又删不到，每跑一次就留下一批孤儿文件。
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "uemm_profile_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<string> _gameNames = [];

    private async Task<(ProfileService Service, InstanceProfile Source)> CreateWithSourceAsync(
        Action<ProfilePackageEntry> configureEntry)
    {
        var service = new ProfileService(NullLogger<ProfileService>.Instance, _dataDir);
        await service.SetCurrentGameAsync(NewGameName());

        var source = await service.CreateProfileAsync("源方案");
        var entry = new ProfilePackageEntry
        {
            PackageKey = "pkg-a",
            IsEnabled = true,
            Priority = 7,
            Kind = PackageKind.Config
        };
        configureEntry(entry);
        source.Packages.Add(entry);

        return (service, source);
    }

    [Fact]
    public async Task CloneProfileAsync_PreservesEntryLevelTargetRootPath()
    {
        var (service, source) = await CreateWithSourceAsync(
            e => e.TargetRootPath = "Engine/Config/Override");

        var clone = await service.CloneProfileAsync(source.Id, "克隆方案");

        var cloned = Assert.Single(clone.Packages);
        Assert.Equal("Engine/Config/Override", cloned.TargetRootPath);
    }

    [Fact]
    public async Task CloneProfileAsync_PreservesValueWrittenThroughLegacyAlias()
    {
        // 老数据可能是通过 PluginTargetPath 这个旧别名写进去的，克隆同样不能丢
        var (service, source) = await CreateWithSourceAsync(
            e => e.PluginTargetPath = "Binaries/Win64/Plugins");

        var clone = await service.CloneProfileAsync(source.Id, "克隆方案");

        var cloned = Assert.Single(clone.Packages);
        Assert.Equal("Binaries/Win64/Plugins", cloned.TargetRootPath);
        Assert.Equal("Binaries/Win64/Plugins", cloned.PluginTargetPath);
    }

    [Fact]
    public async Task CloneProfileAsync_NullTargetRootPath_StaysNull()
    {
        var (service, source) = await CreateWithSourceAsync(e => e.TargetRootPath = null);

        var clone = await service.CloneProfileAsync(source.Id, "克隆方案");

        Assert.Null(Assert.Single(clone.Packages).TargetRootPath);
    }

    [Fact]
    public async Task CloneProfileAsync_PreservesAllOtherEntryFields()
    {
        // 顺带把整份字段清单锁住：将来给 ProfilePackageEntry 加字段却忘了改克隆，
        // 这条会失败（虽然它测不到"新加的字段"，但把现有 5 项固定下来仍有价值）。
        var (service, source) = await CreateWithSourceAsync(
            e => e.TargetRootPath = "Some/Path");

        var clone = await service.CloneProfileAsync(source.Id, "克隆方案");

        var cloned = Assert.Single(clone.Packages);
        Assert.Equal("pkg-a", cloned.PackageKey);
        Assert.True(cloned.IsEnabled);
        Assert.Equal(7, cloned.Priority);
        Assert.Equal(PackageKind.Config, cloned.Kind);
    }

    [Fact]
    public async Task CloneProfileAsync_ClonedEntryIsIndependentOfSource()
    {
        var (service, source) = await CreateWithSourceAsync(e => e.TargetRootPath = "Original");

        var clone = await service.CloneProfileAsync(source.Id, "克隆方案");
        source.Packages[0].TargetRootPath = "Mutated";

        Assert.Equal("Original", Assert.Single(clone.Packages).TargetRootPath);
    }

    private string NewGameName()
    {
        var game = "UEMMTest_" + Guid.NewGuid().ToString("N");
        _gameNames.Add(game);
        return game;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
