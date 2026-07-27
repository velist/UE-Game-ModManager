using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class GameConfigServiceTests : IDisposable
{
    private readonly string _gameRoot = Path.Combine(
        Path.GetTempPath(),
        "UEModManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AutoDetectExecutable_Expedition33_PrefersShippingExecutableOverEnshrouded()
    {
        var shippingDirectory = Path.Combine(_gameRoot, "Sandfall", "Binaries", "Win64");
        Directory.CreateDirectory(shippingDirectory);
        File.WriteAllText(Path.Combine(_gameRoot, "enshrouded.exe"), string.Empty);
        File.WriteAllText(Path.Combine(shippingDirectory, "Expedition33Steam-Win64-Shipping.exe"), string.Empty);

        var service = new GameConfigService(NullLogger<GameConfigService>.Instance);

        var executable = service.AutoDetectExecutable(_gameRoot, "光与影：33号远征队");

        Assert.Equal("Expedition33Steam-Win64-Shipping.exe", executable);
    }

    [Fact]
    public async Task LoadConfigAsync_ValidFile_LoadsConfigWithoutBackup()
    {
        var configPath = CreateConfigFile(
            """{"GameName":"剑星","GamePath":"D:\\Games\\StellarBlade"}""");
        var service = new GameConfigService(NullLogger<GameConfigService>.Instance, configPath);

        await service.LoadConfigAsync();

        Assert.Equal("剑星", service.CurrentGameName);
        Assert.Equal(@"D:\Games\StellarBlade", service.CurrentGamePath);
        Assert.Empty(FindBackups(configPath));
    }

    [Fact]
    public async Task LoadConfigAsync_CorruptFile_BacksUpOriginalBeforeFallingBackToDefaults()
    {
        // 截断的 json（进程被杀 / 断电留下的典型形态）
        const string corrupt = """{"GameName":"剑星","GamePath":"D:\\Games\\Stellar""";
        var configPath = CreateConfigFile(corrupt);
        var service = new GameConfigService(NullLogger<GameConfigService>.Instance, configPath);

        await service.LoadConfigAsync();

        var backup = Assert.Single(FindBackups(configPath));
        Assert.Equal(corrupt, File.ReadAllText(backup));
        Assert.Equal(string.Empty, service.CurrentGameName);
    }

    [Fact]
    public async Task SaveConfigAsync_AfterCorruptLoad_OriginalConfigSurvivesInBackup()
    {
        // 数据丢失链：config.json 损坏 → Config 停在默认空对象 → 下一次保存用空配置全量覆盖。
        const string corrupt = """{"GameName":"剑星","GamePath":"D:\\Games\\Stellar""";
        var configPath = CreateConfigFile(corrupt);
        var service = new GameConfigService(NullLogger<GameConfigService>.Instance, configPath);

        await service.LoadConfigAsync();
        await service.SaveConfigAsync();

        Assert.DoesNotContain("剑星", File.ReadAllText(configPath));
        var backup = Assert.Single(FindBackups(configPath));
        Assert.Equal(corrupt, File.ReadAllText(backup));
    }

    [Fact]
    public async Task SaveConfigAsync_AfterUnreadableLoad_BacksUpFileBeforeOverwriting()
    {
        // 读盘失败（文件被占用）时磁盘上的配置仍然完好，覆盖前必须留下备份。
        const string original = """{"GameName":"剑星","GamePath":"D:\\Games\\StellarBlade"}""";
        var configPath = CreateConfigFile(original);
        var service = new GameConfigService(NullLogger<GameConfigService>.Instance, configPath);

        using (new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await service.LoadConfigAsync();
        }

        Assert.Equal(string.Empty, service.CurrentGameName);
        Assert.Empty(FindBackups(configPath));

        await service.SaveConfigAsync();

        var backup = Assert.Single(FindBackups(configPath));
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void Resolve_FromContainer_PicksLoggerOnlyConstructor()
    {
        // 新增的 (ILogger, string) 构造函数只给测试用：容器无法解析 string，
        // 必须仍然选中单参数构造函数，否则应用启动即失败。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<GameConfigService>();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<GameConfigService>());
    }

    private string CreateConfigFile(string content)
    {
        Directory.CreateDirectory(_gameRoot);
        var configPath = Path.Combine(_gameRoot, "config.json");
        File.WriteAllText(configPath, content);
        return configPath;
    }

    private static string[] FindBackups(string configPath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(configPath)!,
            $"{Path.GetFileName(configPath)}.corrupt-*.bak");

    public void Dispose()
    {
        if (Directory.Exists(_gameRoot))
        {
            Directory.Delete(_gameRoot, recursive: true);
        }
    }
}
