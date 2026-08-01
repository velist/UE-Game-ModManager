using UEModManager.Services.Telemetry;

namespace UEModManager.Core.Tests.Services.Telemetry;

/// <summary>
/// "这次该不该上报"的判定。
///
/// <para>
/// 全部用例服务于同一条硬规则：<b>没告知过就绝不上报</b>。用户主要在中国大陆，
/// 收集邮箱哈希 + 设备标识属于个人信息处理，PIPL 要求告知并取得同意。
/// 这条规则一旦被"顺手优化"掉（比如有人觉得 Enabled 默认为 true 就够了），
/// 软件会在用户被告知之前就开始发数据——这是合规意义上最严重的一种回归，
/// 而它在功能测试里完全看不出来。
/// </para>
/// </summary>
public class TelemetryConsentTests
{
    [Fact]
    public void 没问过时不上报_并要求先问一次()
    {
        var d = TelemetryConsent.Decide(new TelemetryConsentState(Asked: false, Enabled: true));

        Assert.True(d.ShouldAsk);
        Assert.False(d.ShouldReport);
        Assert.Equal(TelemetrySkipReason.NotAskedYet, d.SkipReason);
    }

    /// <summary>
    /// 关键的一条：Enabled 的默认值是 true（默认关会让样本只剩"愿意主动去设置里打开的人"，
    /// 数字失真到没有参考价值）。正因为默认是 true，"没问过"这一条必须能压过它，
    /// 否则默认值本身就成了一条绕过告知的路径。
    /// </summary>
    [Fact]
    public void 默认开也压不过没问过这一条()
    {
        var neverAskedButDefaultOn = new TelemetryConsentState(Asked: false, Enabled: true);

        Assert.False(TelemetryConsent.Decide(neverAskedButDefaultOn).ShouldReport);
    }

    [Fact]
    public void 用户关掉后不上报_也不再问()
    {
        var d = TelemetryConsent.Decide(new TelemetryConsentState(Asked: true, Enabled: false));

        Assert.False(d.ShouldAsk);
        Assert.False(d.ShouldReport);
        Assert.Equal(TelemetrySkipReason.OptedOut, d.SkipReason);
    }

    [Fact]
    public void 已告知且未关闭时才上报()
    {
        var d = TelemetryConsent.Decide(new TelemetryConsentState(Asked: true, Enabled: true));

        Assert.False(d.ShouldAsk);
        Assert.True(d.ShouldReport);
        Assert.Equal(TelemetrySkipReason.NotSkipped, d.SkipReason);
    }

    /// <summary>问过一次之后不再问——无论用户当时选了参与还是不参与。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 问过一次就不再打扰(bool enabled)
    {
        Assert.False(TelemetryConsent.Decide(new TelemetryConsentState(Asked: true, Enabled: enabled)).ShouldAsk);
    }

    /// <summary>四种状态里"要问"和"要报"永不同时成立：问的那一刻还没有同意可依据。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 要问和要报互斥(bool asked, bool enabled)
    {
        var d = TelemetryConsent.Decide(new TelemetryConsentState(asked, enabled));

        Assert.False(d.ShouldAsk && d.ShouldReport);
    }

    [Fact]
    public void 结论的文字描述能进日志()
    {
        Assert.Contains("上报", TelemetryConsent.Decide(new TelemetryConsentState(true, true)).ToString());
        Assert.Contains("先问用户", TelemetryConsent.Decide(new TelemetryConsentState(false, true)).ToString());
        Assert.Contains("OptedOut", TelemetryConsent.Decide(new TelemetryConsentState(true, false)).ToString());
    }
}
