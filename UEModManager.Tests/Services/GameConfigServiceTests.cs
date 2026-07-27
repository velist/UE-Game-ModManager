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

    // ─── SelectExecutable（纯逻辑：排除辅助程序 + 按游戏名匹配 + 体积回退）───

    [Fact]
    public void SelectExecutable_ExcludesUninstallerAndLauncher()
    {
        var candidates = new[]
        {
            @"C:\Game\unins000.exe",
            @"C:\Game\launcher.exe",
            @"C:\Game\vcredist_x64.exe",
            @"C:\Game\StellarBlade.exe"
        };

        var picked = GameConfigService.SelectExecutable(candidates, "剑星", allowLargestFallback: true, _ => 1);

        Assert.Equal(@"C:\Game\StellarBlade.exe", picked);
    }

    [Fact]
    public void SelectExecutable_AllCandidatesAreAuxiliary_ReturnsNull()
    {
        var candidates = new[] { @"C:\Game\unins000.exe", @"C:\Game\setup.exe" };

        var picked = GameConfigService.SelectExecutable(candidates, "剑星", allowLargestFallback: true, _ => 1);

        Assert.Null(picked);
    }

    [Fact]
    public void SelectExecutable_CrashReporter_ExcludedExceptForBorderlands()
    {
        var candidates = new[] { @"C:\Game\CrashReporter.exe", @"C:\Game\Wukong.exe" };

        Assert.Equal(@"C:\Game\Wukong.exe",
            GameConfigService.SelectExecutable(candidates, "黑神话·悟空", allowLargestFallback: false));

        // 无主之地的主程序本身带 CrashReporter 字样，不能被排除
        var borderlands = new[] { @"C:\Game\Borderlands4CrashReporter.exe" };
        Assert.Equal(@"C:\Game\Borderlands4CrashReporter.exe",
            GameConfigService.SelectExecutable(borderlands, "无主之地4", allowLargestFallback: false));
    }

    [Fact]
    public void SelectExecutable_KnownGame_PrefersShippingBinaryOverGenericName()
    {
        var candidates = new[]
        {
            @"C:\Game\StellarBlade.exe",
            @"C:\Game\SB\Binaries\Win64\SB-Win64-Shipping.exe"
        };

        var picked = GameConfigService.SelectExecutable(candidates, "剑星 (CNS)", allowLargestFallback: false);

        Assert.Equal(@"C:\Game\SB\Binaries\Win64\SB-Win64-Shipping.exe", picked);
    }

    [Fact]
    public void SelectExecutable_Expedition33_DoesNotPickUnrelatedRootExecutable()
    {
        var candidates = new[]
        {
            @"C:\Game\enshrouded.exe",
            @"C:\Game\Sandfall\Binaries\Win64\Expedition33Steam-Win64-Shipping.exe"
        };

        var picked = GameConfigService.SelectExecutable(candidates, "光与影：33号远征队", allowLargestFallback: false);

        Assert.Equal(@"C:\Game\Sandfall\Binaries\Win64\Expedition33Steam-Win64-Shipping.exe", picked);
    }

    [Fact]
    public void SelectExecutable_SingleCandidate_ReturnedWithoutFallback()
    {
        var candidates = new[] { @"C:\Game\SomeGame.exe" };

        var picked = GameConfigService.SelectExecutable(candidates, "完全不匹配的游戏名", allowLargestFallback: false);

        Assert.Equal(@"C:\Game\SomeGame.exe", picked);
    }

    [Fact]
    public void SelectExecutable_NoNameMatch_WithoutFallback_ReturnsNull()
    {
        // 按目录约定探测时候选集不完整，猜错的代价是启动错误的程序，所以宁可返回 null 让上层继续扫描
        var candidates = new[] { @"C:\Game\a.exe", @"C:\Game\b.exe" };

        var picked = GameConfigService.SelectExecutable(candidates, "完全不匹配的游戏名", allowLargestFallback: false);

        Assert.Null(picked);
    }

    [Fact]
    public void SelectExecutable_NoNameMatch_WithFallback_PicksLargest()
    {
        var candidates = new[] { @"C:\Game\a.exe", @"C:\Game\b.exe" };
        var sizes = new Dictionary<string, long> { [@"C:\Game\a.exe"] = 10, [@"C:\Game\b.exe"] = 999 };

        var picked = GameConfigService.SelectExecutable(candidates, "完全不匹配的游戏名", allowLargestFallback: true, p => sizes[p]);

        Assert.Equal(@"C:\Game\b.exe", picked);
    }

    [Fact]
    public void SelectExecutable_EmptyGameName_DoesNotPickArbitraryFirst()
    {
        // 归一化后的游戏名为空时，"互相包含"对任何文件名都成立，
        // 那等于按目录枚举顺序随便挑一个——必须走体积回退而不是取第一个
        var candidates = new[] { @"C:\Game\aaa.exe", @"C:\Game\bbb.exe" };
        var sizes = new Dictionary<string, long> { [@"C:\Game\aaa.exe"] = 1, [@"C:\Game\bbb.exe"] = 500 };

        Assert.Null(GameConfigService.SelectExecutable(candidates, "", allowLargestFallback: false));
        Assert.Equal(@"C:\Game\bbb.exe",
            GameConfigService.SelectExecutable(candidates, "", allowLargestFallback: true, p => sizes[p]));
    }

    [Fact]
    public void SelectExecutable_DuplicatePaths_DeduplicatedIgnoringCase()
    {
        // 约定路径探测可能把同一个文件收集两次，去重后只剩一个候选就应直接采信
        var candidates = new[] { @"C:\Game\Game.exe", @"c:\game\GAME.EXE" };

        var picked = GameConfigService.SelectExecutable(candidates, "完全不匹配的游戏名", allowLargestFallback: false);

        Assert.Equal(@"C:\Game\Game.exe", picked);
    }

    [Fact]
    public void SelectExecutable_NoCandidates_ReturnsNull()
    {
        Assert.Null(GameConfigService.SelectExecutable([], "剑星", allowLargestFallback: true));
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
