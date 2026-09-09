using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 会碰 <see cref="UiPreferences"/> 静态状态的测试类都要挂这个 collection。
///
/// <para>
/// <see cref="UiPreferences"/> 是静态类，配置路径覆盖与内存单例都是进程级的。
/// xUnit 默认让不同测试类并行跑，一旦并行，A 类把路径重定向到临时目录的那一刻，
/// B 类读到的就是 A 的临时配置。同一个 collection 内的类串行执行，环境隔离才成立。
/// </para>
/// </summary>
[CollectionDefinition(UiPreferencesStaticStateCollection.Name)]
public sealed class UiPreferencesStaticStateCollection
{
    public const string Name = "UiPreferences 静态状态";
}

/// <summary>
/// <see cref="UiPreferences"/> 的失败语义：写失败必须让调用方拿得到信号。
///
/// <para>
/// 背景：这里装的是仓库根、背景图、语言、部署后端。写失败此前被一个空 catch 吞掉，
/// 用户在设置界面改完看到界面正常响应，重启后全部还原，日志里也未必查得到——
/// 而同一个根因（目标不可写）下改游戏路径、导入 MOD、改方案却会弹错误框。
/// 本类是项目里最后一处分裂的失败语义，这些用例把统一后的语义钉死。
/// </para>
///
/// <para>
/// <b>绝不触碰开发机真实的 <c>%APPDATA%\UEModManager\ui_config.json</c>。</b>
/// 每条用例都先用 <c>UiPreferences.OverrideConfigPathForTests</c> 把配置文件重定向到
/// <see cref="_root"/> 下的临时目录，Dispose 时复原并清掉内存单例。
/// 这个口子是专门为本类开的：静态类没有"指定路径的测试用构造函数"可用，
/// 而不隔离就会真的改掉开发者的仓库位置偏好（<c>ObjectStore</c> 跟着换目录，MOD 消失）。
/// </para>
///
/// <para>
/// 制造写失败的手法与 <see cref="SaveFailureReportingTests"/> 一致：
/// 让目标文件的父级是一个**文件**，之后 <c>AtomicFileWriter</c> 里的
/// <c>Directory.CreateDirectory</c> 必然抛 IOException，不需要动权限、不留残留、跨机器稳定。
/// </para>
/// </summary>
[Collection(UiPreferencesStaticStateCollection.Name)]
public sealed class UiPreferencesFailureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_pref_" + Guid.NewGuid().ToString("N")[..8]);

    public UiPreferencesFailureTests() => Directory.CreateDirectory(_root);

    /// <summary>可写的临时 ui_config.json 路径。</summary>
    private string WritableConfig(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "ui_config.json");
    }

    /// <summary>注定写不进去的 ui_config.json 路径：它的父目录位置上是一个文件。</summary>
    private string BlockedConfig(string name)
    {
        var blocker = Path.Combine(_root, name);
        File.WriteAllText(blocker, "我是文件，不是目录");
        return Path.Combine(blocker, "ui_config.json");
    }

    // ─── 用户显式发起的设置：写失败必须上抛 ───

    [Fact]
    public void 仓库根写失败时抛给调用方()
    {
        // 此前静默失败的表现：用户在设置界面改完仓库位置，界面正常响应，
        // 重启后位置还原，自定义仓库里的 MOD 在界面上"消失"。
        using var _ = UiPreferences.OverrideConfigPathForTests(BlockedConfig("repo_root"));

        Assert.ThrowsAny<Exception>(() => UiPreferences.SaveRepositoryRoot(@"D:\ModRepo"));
    }

    [Theory]
    [MemberData(nameof(用户发起的保存))]
    public void 每一项用户设置写失败时都抛给调用方(string 名称, Action 保存)
    {
        // 逐项钉死：只要有一项漏网，用户就会因为"别处会报错"而信任这一处的沉默
        using var _ = UiPreferences.OverrideConfigPathForTests(BlockedConfig("each_" + 名称));

        Assert.ThrowsAny<Exception>(保存);
    }

    public static TheoryData<string, Action> 用户发起的保存 => new()
    {
        { "language", () => UiPreferences.SaveEnglish(true) },
        { "background", () => UiPreferences.SaveBackground(new BackgroundSettings()) },
        { "closeaction", () => UiPreferences.SaveCloseAction(UiPreferences.CloseAction.Exit) },
        { "backend", () => UiPreferences.SaveDeployBackend(DeploymentBackendType.HardLink) },
        { "autodeploy", () => UiPreferences.SaveAutoDeploy(false) },
        { "repository", () => UiPreferences.SaveRepositoryRoot(@"D:\ModRepo") },
        { "overwrites", () => UiPreferences.SaveOverwritesRoot(@"D:\Overwrites") },
        { "backups", () => UiPreferences.SaveBackupsRoot(@"D:\Backups") },
        { "telemetry", () => UiPreferences.SaveTelemetryEnabled(false) },
    };

    [Fact]
    public void 写失败时内存里的值保持原样()
    {
        // 只抛异常不回滚的话，用户看到错误框，但本次会话仍按一个没落盘的仓库根继续跑，
        // 重启后又变回去——把"写失败"变成"写失败且行为不一致"，比单纯静默还难排查。
        var config = WritableConfig("rollback");
        using var scope = UiPreferences.OverrideConfigPathForTests(config);
        UiPreferences.SaveRepositoryRoot(@"D:\Good");

        BreakConfigFile(config);

        Assert.ThrowsAny<Exception>(() => UiPreferences.SaveRepositoryRoot(@"D:\Bad"));
        Assert.Equal(@"D:\Good", UiPreferences.LoadRepositoryRoot());
    }

    [Fact]
    public void 写失败的改动不会被下一次成功保存顺带写进去()
    {
        // 同一个理由的另一面：草稿没被提升为内存单例，就不会污染后续的写入。
        var config = WritableConfig("no_bleed");
        using var scope = UiPreferences.OverrideConfigPathForTests(config);
        UiPreferences.SaveRepositoryRoot(@"D:\Good");

        BreakConfigFile(config);
        Assert.ThrowsAny<Exception>(() => UiPreferences.SaveRepositoryRoot(@"D:\Bad"));
        RepairConfigFile(config);

        UiPreferences.SaveAutoDeploy(false);

        Assert.Contains(@"D:\\Good", File.ReadAllText(config));
        Assert.DoesNotContain(@"D:\\Bad", File.ReadAllText(config));
    }

    [Fact]
    public void 写成功时落盘且能读回()
    {
        var config = WritableConfig("ok");
        using var scope = UiPreferences.OverrideConfigPathForTests(config);

        UiPreferences.SaveRepositoryRoot(@"D:\ModRepo");
        UiPreferences.SaveCloseAction(UiPreferences.CloseAction.Minimize);

        Assert.True(File.Exists(config));
        Assert.Equal(@"D:\ModRepo", UiPreferences.LoadRepositoryRoot());
        Assert.Equal(UiPreferences.CloseAction.Minimize, UiPreferences.LoadCloseAction());
    }

    // ─── 隐式落盘：保持静默，但要留痕 ───

    [Fact]
    public void 数据布局版本写失败时不抛()
    {
        // 唯一调用方是 DataLocationMigrator.Run 里没有局部 try/catch 的那一句。
        // 抛出去会让所有搬迁步骤都成功之后半途中断，用户收到一条"迁移异常"的误报，
        // 而搬迁器的铁律是任何失败都不得阻断启动。失败后果本身可承受：
        // 版本标记没写上就是下次启动重新探测一遍磁盘。
        using var _ = UiPreferences.OverrideConfigPathForTests(BlockedConfig("layout_version"));

        UiPreferences.SaveDataLayoutVersion(3);

        Assert.Equal(0, UiPreferences.LoadDataLayoutVersion());
    }

    [Fact]
    public void 迁移器的偏好适配器不因写失败而抛()
    {
        // 生产环境里搬迁器就是通过这个适配器直通到 UiPreferences 的。
        // 它的两个 SaveXxxRoot 会抛（由 DataLocationMigrator.TryExecute 承接，只计一次
        // failed，数据完整留在旧位置），SaveDataLayoutVersion 不会抛。
        using var _ = UiPreferences.OverrideConfigPathForTests(BlockedConfig("adapter"));
        var adapter = UiPreferencesDataMigrationAdapter.Instance;

        adapter.SaveDataLayoutVersion(3);

        Assert.ThrowsAny<Exception>(() => adapter.SaveRepositoryRoot(@"D:\ModRepo"));
    }

    // ─── 读取：失败回落默认值，不抛 ───

    [Fact]
    public void 读失败时回落默认值而不抛()
    {
        // 读取的调用点在窗口构造与启动路径上（AppPaths 解析数据根也要读这里），
        // 让它抛等于让应用启动不了——那比读到默认配置更糟。
        var config = WritableConfig("read_fail");
        File.WriteAllText(config, "{}");
        BreakConfigFile(config);

        using var scope = UiPreferences.OverrideConfigPathForTests(config);

        Assert.Null(UiPreferences.LoadRepositoryRoot());
        Assert.True(UiPreferences.LoadAutoDeploy());
    }

    [Fact]
    public void 读失败不缓存_占用解除后立刻恢复真实值()
    {
        // 读不出来的配置磁盘上很可能仍然完好（被占用 / 杀软扫描 / 同步盘锁定）。
        // 此时那份回落的默认值绝不能进内存单例，否则下一次任何保存都会拿它去
        // 全量覆盖磁盘，用户的全部偏好就此消失且无从恢复。
        var config = WritableConfig("retry");
        File.WriteAllText(config, """{"RepositoryRoot":"D:\\Real"}""");
        using var scope = UiPreferences.OverrideConfigPathForTests(config);

        using (new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(UiPreferences.LoadRepositoryRoot());
        }

        Assert.Equal(@"D:\Real", UiPreferences.LoadRepositoryRoot());
    }

    [Fact]
    public void 写失败时磁盘上的原配置保持完好()
    {
        // 走 AtomicFileWriter（临时文件 + File.Replace）的意义就在这里：
        // 写到一半失败也不会在 ui_config.json 上留下截断的 json 把用户全部偏好带走。
        var config = WritableConfig("intact");
        using var scope = UiPreferences.OverrideConfigPathForTests(config);
        UiPreferences.SaveRepositoryRoot(@"D:\Good");
        var original = File.ReadAllText(config);

        using (new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => UiPreferences.SaveRepositoryRoot(@"D:\Bad"));
        }

        Assert.Equal(original, File.ReadAllText(config));
        Assert.Equal(@"D:\Good", UiPreferences.LoadRepositoryRoot());
    }

    // ─── 测试口子本身 ───

    [Fact]
    public void 覆盖作用域结束后复原到真实路径()
    {
        // 口子漏了就等于所有用例都在写开发者真实的 %APPDATA%——本类最危险的一条。
        using (UiPreferences.OverrideConfigPathForTests(WritableConfig("scope")))
        {
            UiPreferences.SaveRepositoryRoot(@"D:\OnlyInTemp");
        }

        // 复原后读到的是真实配置（本机通常没有这一项，故为 null），绝不是临时目录里那个值
        Assert.NotEqual(@"D:\OnlyInTemp", UiPreferences.LoadRepositoryRoot());
    }

    // ─── 辅助 ───

    /// <summary>把配置文件换成同名目录，让后续的 File.Replace/Move 与读取都必然失败。</summary>
    private static void BreakConfigFile(string configPath)
    {
        if (File.Exists(configPath)) File.Delete(configPath);
        Directory.CreateDirectory(configPath);
    }

    private static void RepairConfigFile(string configPath)
    {
        if (Directory.Exists(configPath)) Directory.Delete(configPath, recursive: true);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不该让测试变红
        }
    }
}
