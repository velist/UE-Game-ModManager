using UEModManager.Services;
using UEModManager.Services.Telemetry;

namespace UEModManager.Tests.Services;

/// <summary>
/// 匿名统计的同意状态在 <see cref="UiPreferences"/> 里的存取。
///
/// <para>
/// <b>绝不触碰开发机真实的 <c>%APPDATA%\UEModManager\ui_config.json</c>。</b>
/// 每条用例都先用 <c>UiPreferences.OverrideConfigPathForTests</c> 把配置文件重定向到
/// <see cref="_root"/> 下的临时目录，Dispose 时复原并清掉内存单例。理由见
/// <see cref="UiPreferencesFailureTests"/> 的类注释——这个静态类没有构造函数可以做隔离。
/// </para>
///
/// <para>
/// 这些用例钉的是三件"错了不会报错、只会让软件在用户被告知之前就开始发数据"的事：
/// 默认值、两个字段的原子落盘、以及"在设置里改过 = 知情"这条推论。
/// </para>
/// </summary>
[Collection(UiPreferencesStaticStateCollection.Name)]
public sealed class TelemetryConsentPreferenceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_tele_" + Guid.NewGuid().ToString("N")[..8]);

    public TelemetryConsentPreferenceTests() => Directory.CreateDirectory(_root);

    private string Config(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "ui_config.json");
    }

    /// <summary>
    /// 全新安装：开关默认是开的，但"没问过"，所以判定结果是**不上报、先去问**。
    /// 这两条必须同时成立——默认开是为了数字有代表性，没问过不上报是合规底线。
    /// </summary>
    [Fact]
    public void 全新安装_默认开但因为没问过而不上报()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("fresh"));

        var state = UiPreferences.LoadTelemetryConsent();
        Assert.False(state.Asked);
        Assert.True(state.Enabled);

        var decision = TelemetryConsent.Decide(state);
        Assert.False(decision.ShouldReport);
        Assert.True(decision.ShouldAsk);
    }

    [Fact]
    public void 用户在告知框里选了参与()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("opt_in"));

        UiPreferences.SaveTelemetryConsent(enabled: true);

        var decision = TelemetryConsent.Decide(UiPreferences.LoadTelemetryConsent());
        Assert.True(decision.ShouldReport);
        Assert.False(decision.ShouldAsk);
    }

    [Fact]
    public void 用户在告知框里选了不参与_之后不再上报也不再问()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("opt_out"));

        UiPreferences.SaveTelemetryConsent(enabled: false);

        var decision = TelemetryConsent.Decide(UiPreferences.LoadTelemetryConsent());
        Assert.False(decision.ShouldReport);
        Assert.False(decision.ShouldAsk);
    }

    /// <summary>
    /// 两个字段必须在同一次写里落盘。分两次写就存在一个中间态：已标记问过、选择还没写上。
    /// 那一刻断电，用户明明选了"不参与"，下次启动却读到"问过 + 默认开"，于是开始上报。
    /// 这条用例直接读磁盘上的 json 文本，确认落盘时两个字段是一起出现的。
    /// </summary>
    [Fact]
    public void 已问过与选择在同一次写里落盘()
    {
        var config = Config("atomic");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);

        UiPreferences.SaveTelemetryConsent(enabled: false);

        var json = File.ReadAllText(config);
        Assert.Contains("\"TelemetryAsked\": true", json);
        Assert.Contains("\"TelemetryEnabled\": false", json);
    }

    /// <summary>
    /// 在设置界面里主动改过这个开关，等同于知情。不把"已问过"一起钉上的话，一个刚刚在设置里
    /// 明确关掉统计的用户，下次启动还会被弹一次"我们要开始统计了"——而他刚表示过不要。
    /// </summary>
    [Fact]
    public void 在设置里改过开关就算知情_不会再被弹窗问一次()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("settings_implies_asked"));

        UiPreferences.SaveTelemetryEnabled(false);

        var state = UiPreferences.LoadTelemetryConsent();
        Assert.True(state.Asked);
        Assert.False(state.Enabled);
        Assert.False(TelemetryConsent.Decide(state).ShouldAsk);
    }

    /// <summary>用户关掉之后再打开，应当恢复上报——开关是双向的，不是一次性的。</summary>
    [Fact]
    public void 关掉之后还能在设置里重新打开()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("toggle_back"));

        UiPreferences.SaveTelemetryConsent(enabled: false);
        Assert.False(TelemetryConsent.Decide(UiPreferences.LoadTelemetryConsent()).ShouldReport);

        UiPreferences.SaveTelemetryEnabled(true);
        Assert.True(TelemetryConsent.Decide(UiPreferences.LoadTelemetryConsent()).ShouldReport);
    }

    /// <summary>
    /// 老用户升级上来：配置文件里没有这两个键。反序列化后 Asked 为 false（C# 的默认值），
    /// 于是他会被问一次——这正是想要的：他此前从没被告知过。
    /// </summary>
    [Fact]
    public void 老配置文件里没有这两个键时_视为没问过()
    {
        var config = Config("legacy");
        File.WriteAllText(config, "{ \"Language\": \"zh-CN\", \"AutoDeploy\": true }");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);

        var state = UiPreferences.LoadTelemetryConsent();
        Assert.False(state.Asked);
        Assert.True(TelemetryConsent.Decide(state).ShouldAsk);
        Assert.False(TelemetryConsent.Decide(state).ShouldReport);
    }

    /// <summary>
    /// 配置读不出来时（文件损坏 / 被占用）回落到"没问过"，也就是**不上报**。
    /// 读取失败的最坏结果必须是少统计，绝不能是偷偷上报。
    /// </summary>
    [Fact]
    public void 配置损坏时回落到不上报()
    {
        var config = Config("corrupt");
        File.WriteAllText(config, "{ 这不是 json");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);

        Assert.False(TelemetryConsent.Decide(UiPreferences.LoadTelemetryConsent()).ShouldReport);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
