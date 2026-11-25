using UEModManager.Services;
using UEModManager.Services.Promo;

namespace UEModManager.Tests.Services;

/// <summary>
/// 推广条从出现到收起这一段：点击算不算数、会不会多开网页、到期了怎么办。
///
/// <para>
/// 这些是主窗口 code-behind 里原本没法测的部分（主窗口要一整套服务才构造得出来）。
/// 拆进 <see cref="PromoBarSession"/> 之后，连着真实的 <see cref="PromoBarService"/> 与
/// <see cref="UiPreferences"/> 一起跑：时钟、毫秒计数都是假的，浏览器换成只记录不执行的假实现。
/// </para>
///
/// <para><b>绝不触碰开发机真实的 <c>ui_config.json</c>，也绝不真的打开浏览器。</b></para>
/// </summary>
[Collection(UiPreferencesStaticStateCollection.Name)]
public sealed class PromoBarSessionTests : IDisposable
{
    private static readonly DateOnly Nov25 = new(2026, 11, 25);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);
    private static readonly DateOnly Jan01 = new(2027, 1, 1);

    private static readonly long Guard = (long)PromoBarSession.ClickGuard.TotalMilliseconds;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_promo_session_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly IDisposable _config;

    /// <summary>假浏览器收到的链接。</summary>
    private readonly List<string> _opened = new();

    /// <summary>假的今天与假的毫秒计数：用例自己拨。</summary>
    private DateOnly _today = Nov25;
    private long _tick = 5_000_000;

    private Action<string>? _browser;

    public PromoBarSessionTests()
    {
        Directory.CreateDirectory(_root);
        _config = UiPreferences.OverrideConfigPathForTests(Path.Combine(_root, "ui_config.json"));
        UiPreferences.SaveOnboardingGuideShown(true);   // 默认是一个用过软件的老玩家
    }

    public void Dispose()
    {
        _config.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private PromoBarSession Session()
        => new(
            new PromoBarService(
                today: () => _today,
                campaign: () => PromoCampaigns.GptTopUpCoupon,
                openInBrowser: url => (_browser ?? _opened.Add)(url)),
            tickCount: () => _tick);

    /// <summary>一条已经显示出来、并且过了误点保护期的推广条。</summary>
    private PromoBarSession Shown()
    {
        var session = Session();
        Assert.NotNull(session.Begin());
        _tick += Guard;
        return session;
    }

    // ─── 安排与出现 ───

    [Fact]
    public void 老玩家当天第一次启动_要安排_到点后进入展示状态并记账()
    {
        var session = Session();

        Assert.True(session.ShouldSchedule);
        Assert.Null(session.Showing);   // 只是问了一句，还没出现
        Assert.Equal(PromoDisplayRecord.Never, UiPreferences.LoadPromoDisplayRecord());

        var campaign = session.Begin();

        Assert.Same(PromoCampaigns.GptTopUpCoupon, campaign);
        Assert.Same(campaign, session.Showing);
        Assert.Equal(new PromoDisplayRecord(Nov25, campaign!.Id), UiPreferences.LoadPromoDisplayRecord());
    }

    [Fact]
    public void 新手引导还没看过_这次启动不安排()
    {
        UiPreferences.SaveOnboardingGuideShown(false);
        var session = Session();

        Assert.False(session.ShouldSchedule);
        Assert.Null(session.Begin());
        Assert.Null(session.Showing);
    }

    [Fact]
    public void 今天已经展示过_这次启动不安排()
    {
        Assert.NotNull(Session().Begin());   // 上一次启动

        var session = Session();             // 同一天再启动

        Assert.False(session.ShouldSchedule);
        Assert.Null(session.Begin());
        Assert.Null(session.Showing);
    }

    [Theory]
    [InlineData(2026, 12, 31, true)]    // 最后展示日当天
    [InlineData(2027, 1, 1, false)]     // 下线第一天
    [InlineData(2028, 3, 9, false)]
    public void 到期日当天还安排_之后不再安排(int year, int month, int day, bool expected)
    {
        _today = new DateOnly(year, month, day);
        var session = Session();

        Assert.Equal(expected, session.ShouldSchedule);
        Assert.Equal(expected, session.Begin() != null);
    }

    [Fact]
    public void 安排之后到点之前跨过了到期日_到点时不出现()
    {
        // 12-31 深夜启动，窗口一直最小化着，过了零点才回来
        _today = Dec31;
        var session = Session();
        Assert.True(session.ShouldSchedule);

        _today = Jan01;

        Assert.Null(session.Begin());
        Assert.Null(session.Showing);
        Assert.Equal(PromoDisplayRecord.Never, UiPreferences.LoadPromoDisplayRecord());
    }

    // ─── 误点保护 ───

    [Fact]
    public void 刚出现的那一小段时间里_点券面和点领取都不作数()
    {
        var session = Session();
        session.Begin();

        foreach (var elapsed in new[] { 0L, 1, 300, Guard - 1 })
        {
            _tick = 5_000_000 + elapsed;
            Assert.Equal(PromoBarAction.None, session.Claim());
        }

        Assert.Empty(_opened);               // 一个网页都没开
        Assert.NotNull(session.Showing);     // 推广条还在

        // 过了保护期，这一下才算
        _tick = 5_000_000 + Guard;
        Assert.Equal(PromoBarAction.DismissAfterOpening, session.Claim());
        Assert.Single(_opened);
    }

    [Fact]
    public void 刚出现的那一小段时间里_点关闭也不作数()
    {
        var session = Session();
        session.Begin();

        _tick += Guard - 1;
        Assert.Equal(PromoBarAction.None, session.Close());
        Assert.NotNull(session.Showing);

        _tick += 1;
        Assert.Equal(PromoBarAction.Dismiss, session.Close());
        Assert.Null(session.Showing);
    }

    [Fact]
    public void 误点保护是八百毫秒_盖得住整个滑入过程()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(800), PromoBarSession.ClickGuard);
        // 滑入那 320ms 里工具栏还在往下走，保护期必须比它长；又不能长到让人觉得点不动
        Assert.True(PromoBarSession.ClickGuard > TimeSpan.FromMilliseconds(UEModManager.Infrastructure.MotionAssist.RevealMs));
        Assert.True(PromoBarSession.ClickGuard <= TimeSpan.FromSeconds(1));
    }

    // ─── 领取 ───

    [Fact]
    public void 领取_把活动链接原样交给浏览器_然后收起()
    {
        var session = Shown();

        Assert.Equal(PromoBarAction.DismissAfterOpening, session.Claim());

        Assert.Equal(new[] { PromoCampaigns.GptTopUpCoupon.Url }, _opened);
        Assert.Null(session.Showing);
    }

    [Fact]
    public void 连点好几下_只开一个网页()
    {
        var session = Shown();

        Assert.Equal(PromoBarAction.DismissAfterOpening, session.Claim());
        // 推广条正在收起的那一小段动画里又点了几下
        Assert.Equal(PromoBarAction.None, session.Claim());
        Assert.Equal(PromoBarAction.None, session.Claim());
        Assert.Equal(PromoBarAction.None, session.Close());

        Assert.Single(_opened);
    }

    [Fact]
    public void 浏览器还没打开完又点了一下_也只开一个网页()
    {
        var session = Shown();
        var reentered = PromoBarAction.DismissAfterOpening;

        // 打开浏览器的过程中界面又收到一次点击（Shell 调用期间消息循环可能被重入）
        _browser = url =>
        {
            _opened.Add(url);
            reentered = session.Claim();
        };

        Assert.Equal(PromoBarAction.DismissAfterOpening, session.Claim());
        Assert.Equal(PromoBarAction.None, reentered);
        Assert.Single(_opened);
    }

    [Fact]
    public void 浏览器打不开_收起并把链接留给界面去提示()
    {
        var session = Shown();
        _browser = _ => throw new System.ComponentModel.Win32Exception(1155);

        Assert.Equal(PromoBarAction.DismissAndOfferLink, session.Claim());

        Assert.Null(session.Showing);   // 不管打没打开，推广条都收起，不会卡在界面上让人反复点
    }

    [Fact]
    public void 软件开到了活动下线之后_点领取不打开网页_只是收起()
    {
        _today = Dec31;
        var session = Shown();

        _today = Jan01;   // 过了零点，推广条还留在界面上

        Assert.Equal(PromoBarAction.Dismiss, session.Claim());
        Assert.Empty(_opened);
        Assert.Null(session.Showing);
    }

    // ─── 关闭与到期 ───

    [Fact]
    public void 关闭_收起_不打开任何东西_之后再点领取也不作数()
    {
        var session = Shown();

        Assert.Equal(PromoBarAction.Dismiss, session.Close());

        Assert.Null(session.Showing);
        Assert.Equal(PromoBarAction.None, session.Claim());
        Assert.Equal(PromoBarAction.None, session.Close());
        Assert.Empty(_opened);
    }

    [Fact]
    public void 关闭之后同一天重启_不再出现()
    {
        var session = Shown();
        session.Close();

        var restarted = Session();

        Assert.False(restarted.ShouldSchedule);
        Assert.Null(restarted.Begin());
    }

    [Fact]
    public void 到期检查_没到期不动_到期了收起()
    {
        _today = Dec31;
        var session = Shown();

        Assert.Equal(PromoBarAction.None, session.CheckExpiry());
        Assert.NotNull(session.Showing);

        _today = Jan01;

        Assert.Equal(PromoBarAction.Dismiss, session.CheckExpiry());
        Assert.Null(session.Showing);
        // 已经收起了，再来问就没什么可做的
        Assert.Equal(PromoBarAction.None, session.CheckExpiry());
        Assert.Empty(_opened);
    }

    [Fact]
    public void 还没出现的时候_点击_关闭_到期检查都不作数()
    {
        var session = Session();
        _tick += Guard * 10;

        Assert.Equal(PromoBarAction.None, session.Claim());
        Assert.Equal(PromoBarAction.None, session.Close());
        Assert.Equal(PromoBarAction.None, session.CheckExpiry());
        session.End();   // 没在展示时结束也不出错

        Assert.Empty(_opened);
        Assert.Equal(PromoDisplayRecord.Never, UiPreferences.LoadPromoDisplayRecord());
    }

    // ─── 鼠标在券面上的按下与松开 ───

    [Fact]
    public void 按下与松开都在券上_才算一次点击()
    {
        var session = Shown();

        // 在别处按住、拖到券上才松手
        Assert.False(session.PointerUp());

        // 在券上按下，拖出去了
        session.PointerDown();
        session.PointerLeave();
        Assert.False(session.PointerUp());

        // 按下、松开都在券上
        session.PointerDown();
        Assert.True(session.PointerUp());

        // 一次按下只算一次
        Assert.False(session.PointerUp());
    }

    [Fact]
    public void 没在展示的时候按下不记()
    {
        var session = Session();

        // 还没出现
        session.PointerDown();
        Assert.False(session.PointerUp());

        // 已经关掉、正在收起的那一小段动画里
        session.Begin();
        _tick += Guard;
        session.Close();
        session.PointerDown();
        Assert.False(session.PointerUp());
    }

    [Fact]
    public void 结束展示会把按下的状态一起清掉()
    {
        var session = Shown();

        session.PointerDown();
        session.End();

        Assert.False(session.PointerUp());
        Assert.Null(session.Showing);
    }

    [Fact]
    public void 没有服务就建不出来()
        => Assert.Throws<ArgumentNullException>(() => new PromoBarSession(null!));
}
