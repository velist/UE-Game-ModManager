using UEModManager.Services.Promo;

namespace UEModManager.Core.Tests.Services.Promo;

/// <summary>
/// 推广条"现在该不该出现"的判定。
///
/// <para>
/// 这里钉的是应用所有者的三句话：<b>每天展示一次</b>、<b>12 月 31 日下线</b>、<b>到期就不再展示</b>，
/// 再加一条我们自己的：<b>新手引导还没看过时不出现</b>。
/// 这几条错了都不会报错——只会表现成"今天怎么弹了两次"或"过年了广告还在"，所以全靠用例守着。
/// </para>
/// </summary>
public class PromoScheduleTests
{
    private static readonly PromoCampaign Campaign = PromoCampaigns.GptTopUpCoupon;

    private static readonly DateOnly Nov25 = new(2026, 11, 25);
    private static readonly DateOnly Nov26 = new(2026, 11, 26);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);
    private static readonly DateOnly Jan01 = new(2027, 1, 1);

    private static PromoDisplayRecord ShownOn(DateOnly date, string? campaignId = null)
        => new(date, campaignId ?? Campaign.Id);

    // ─── 每天一次 ───

    [Fact]
    public void 当天第一次启动_展示()
    {
        var decision = PromoSchedule.Decide(Campaign, Nov25, PromoDisplayRecord.Never, onboardingSeen: true);

        Assert.True(decision.ShouldShow);
        Assert.Same(Campaign, decision.Campaign);
        Assert.Equal(PromoSkipReason.NotSkipped, decision.SkipReason);
    }

    [Fact]
    public void 同一天再启动_不展示()
    {
        var decision = PromoSchedule.Decide(Campaign, Nov25, ShownOn(Nov25), onboardingSeen: true);

        Assert.False(decision.ShouldShow);
        Assert.Null(decision.Campaign);
        Assert.Equal(PromoSkipReason.AlreadyShownToday, decision.SkipReason);
    }

    [Fact]
    public void 第二天_又展示()
    {
        var decision = PromoSchedule.Decide(Campaign, Nov26, ShownOn(Nov25), onboardingSeen: true);

        Assert.True(decision.ShouldShow);
    }

    [Fact]
    public void 换了活动标识_当天可以再展示一次()
    {
        // 今天已经展示过另一条活动：记的是那条，不妨碍这条
        var shownOther = ShownOn(Nov25, campaignId: "another-campaign");

        Assert.True(PromoSchedule.Decide(Campaign, Nov25, shownOther, onboardingSeen: true).ShouldShow);

        // 反过来也一样：同一天换成一条新活动，新活动照常展示
        var replacement = Campaign with { Id = "gpt-topup-20off-2027" };
        Assert.True(PromoSchedule.Decide(replacement, Nov25, ShownOn(Nov25), onboardingSeen: true).ShouldShow);
    }

    [Fact]
    public void 活动标识区分大小写_不会把两条活动当成一条()
    {
        var shownUpper = ShownOn(Nov25, campaignId: Campaign.Id.ToUpperInvariant());

        Assert.True(PromoSchedule.Decide(Campaign, Nov25, shownUpper, onboardingSeen: true).ShouldShow);
    }

    // ─── 到期 ───

    [Fact]
    public void 最后展示日当天_仍然展示()
    {
        Assert.Equal(Dec31, Campaign.LastShowDate);

        var decision = PromoSchedule.Decide(Campaign, Dec31, ShownOn(new DateOnly(2026, 12, 30)), onboardingSeen: true);

        Assert.True(decision.ShouldShow);
    }

    [Theory]
    [InlineData(2027, 1, 1)]
    [InlineData(2027, 1, 2)]
    [InlineData(2027, 12, 31)]
    [InlineData(2030, 6, 15)]
    public void 最后展示日之后_永不展示(int year, int month, int day)
    {
        var today = new DateOnly(year, month, day);

        // 从没展示过的全新用户也一样：过期压过一切
        var decision = PromoSchedule.Decide(Campaign, today, PromoDisplayRecord.Never, onboardingSeen: true);

        Assert.False(decision.ShouldShow);
        Assert.Equal(PromoSkipReason.Expired, decision.SkipReason);
    }

    [Fact]
    public void 到期判定的边界正好卡在最后展示日与次日之间()
    {
        Assert.False(PromoSchedule.IsExpired(Campaign, Dec31));
        Assert.True(PromoSchedule.IsExpired(Campaign, Jan01));
        Assert.False(PromoSchedule.IsExpired(Campaign, Nov25));
    }

    [Fact]
    public void 过期后即使记录显示今天没展示过_也不展示()
    {
        // 12-31 展示过，01-01 是"新的一天"——按"每天一次"本该再出现，但已经下线了
        var decision = PromoSchedule.Decide(Campaign, Jan01, ShownOn(Dec31), onboardingSeen: true);

        Assert.False(decision.ShouldShow);
        Assert.Equal(PromoSkipReason.Expired, decision.SkipReason);
    }

    // ─── 新手引导 ───

    [Fact]
    public void 新手引导还没看过_不展示()
    {
        var decision = PromoSchedule.Decide(Campaign, Nov25, PromoDisplayRecord.Never, onboardingSeen: false);

        Assert.False(decision.ShouldShow);
        Assert.Equal(PromoSkipReason.OnboardingPending, decision.SkipReason);
    }

    [Fact]
    public void 过期的原因排在新手引导之前()
    {
        // 两个条件都不满足时报"已下线"：日志里看到的应该是那个不会再变的原因
        var decision = PromoSchedule.Decide(Campaign, Jan01, PromoDisplayRecord.Never, onboardingSeen: false);

        Assert.Equal(PromoSkipReason.Expired, decision.SkipReason);
    }

    // ─── 系统时间被改过 ───

    [Fact]
    public void 上次展示日期在未来_不崩_今天照常展示()
    {
        // 玩家把系统时间调到过 12-20、看过一次，又调回 11-25
        var future = ShownOn(new DateOnly(2026, 12, 20));

        var decision = PromoSchedule.Decide(Campaign, Nov25, future, onboardingSeen: true);

        // 判据是"恰好等于今天"：未来的日期不算今天展示过，展示时会把记录写回今天，自己纠正
        Assert.True(decision.ShouldShow);
    }

    [Fact]
    public void 上次展示日期在很远的未来_仍按到期日判定()
    {
        var farFuture = ShownOn(new DateOnly(9999, 12, 31));

        Assert.True(PromoSchedule.Decide(Campaign, Dec31, farFuture, onboardingSeen: true).ShouldShow);
        Assert.False(PromoSchedule.Decide(Campaign, Jan01, farFuture, onboardingSeen: true).ShouldShow);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(9999, 12, 31)]
    public void 极端的今天也不崩(int year, int month, int day)
    {
        var today = new DateOnly(year, month, day);

        var decision = PromoSchedule.Decide(Campaign, today, ShownOn(today), onboardingSeen: true);

        Assert.False(decision.ShouldShow);
    }

    // ─── 活动本身 ───

    [Fact]
    public void 没有活动_不展示()
    {
        var decision = PromoSchedule.Decide(null, Nov25, PromoDisplayRecord.Never, onboardingSeen: true);

        Assert.False(decision.ShouldShow);
        Assert.Equal(PromoSkipReason.NoCampaign, decision.SkipReason);
    }

    [Theory]
    [InlineData("http://gptpluscz.com/offer/x")]
    [InlineData("")]
    [InlineData("gptpluscz.com/offer/x")]
    public void 链接不是https的活动_不展示(string url)
    {
        var broken = Campaign with { Url = url };

        var decision = PromoSchedule.Decide(broken, Nov25, PromoDisplayRecord.Never, onboardingSeen: true);

        Assert.False(decision.ShouldShow);
        Assert.Equal(PromoSkipReason.InvalidCampaign, decision.SkipReason);
    }

    [Fact]
    public void 缺标题或缺标识的活动_不展示()
    {
        foreach (var broken in new[]
                 {
                     Campaign with { Id = " " },
                     Campaign with { Title = "" },
                     Campaign with { Amount = "" },
                     Campaign with { ActionText = "" },
                 })
        {
            var decision = PromoSchedule.Decide(broken, Nov25, PromoDisplayRecord.Never, onboardingSeen: true);
            Assert.Equal(PromoSkipReason.InvalidCampaign, decision.SkipReason);
        }
    }

    [Fact]
    public void 是否到期_空活动抛异常()
        => Assert.Throws<ArgumentNullException>(() => PromoSchedule.IsExpired(null!, Nov25));

    // ─── 一路走下来 ───

    /// <summary>
    /// 把"判定 → 展示了就记账"连起来跑一遍：从今天到明年元旦，每天启动三次。
    /// 结果必须是：到 12-31 为止每天恰好一次，之后一次都没有。
    /// </summary>
    [Fact]
    public void 从今天到下线_每天恰好展示一次_之后一次都没有()
    {
        var record = PromoDisplayRecord.Never;
        var shownDays = new List<DateOnly>();

        for (var day = Nov25; day <= new DateOnly(2027, 1, 10); day = day.AddDays(1))
        {
            for (var launch = 0; launch < 3; launch++)
            {
                var decision = PromoSchedule.Decide(Campaign, day, record, onboardingSeen: true);
                if (!decision.ShouldShow) continue;

                shownDays.Add(day);
                record = new PromoDisplayRecord(day, decision.Campaign.Id);
            }
        }

        var expected = Enumerable.Range(0, Dec31.DayNumber - Nov25.DayNumber + 1).Select(Nov25.AddDays).ToList();
        Assert.Equal(expected, shownDays);
        Assert.Equal(37, shownDays.Count);
        Assert.Equal(Dec31, shownDays[^1]);
    }

    // ─── 展示记录的落盘格式 ───

    [Fact]
    public void 日期落盘后读得回来()
    {
        Assert.Equal("2026-11-25", PromoSchedule.FormatDate(Nov25));
        Assert.Equal(Nov25, PromoSchedule.ParseDate("2026-11-25"));
        Assert.Equal(Dec31, PromoSchedule.ParseDate(PromoSchedule.FormatDate(Dec31)));
    }

    [Fact]
    public void 日期格式不跟系统区域设置走()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // 泰国佛历的年份是 2569、阿拉伯区域的数字不是 0-9：落盘格式都不该受影响
            foreach (var name in new[] { "th-TH", "ar-SA", "fa-IR", "zh-CN" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(name);
                Assert.Equal("2026-11-25", PromoSchedule.FormatDate(Nov25));
                Assert.Equal(Nov25, PromoSchedule.ParseDate("2026-11-25"));
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("昨天")]
    [InlineData("2026-13-40")]
    [InlineData("2026/11/25")]
    [InlineData("25-11-2026")]
    [InlineData("2026-11-25T08:00:00")]
    public void 认不出的日期_当作从没展示过(string? text)
    {
        Assert.Null(PromoSchedule.ParseDate(text));

        var record = PromoDisplayRecord.Parse(text, Campaign.Id);
        Assert.Null(record.LastShownDate);
        Assert.True(PromoSchedule.Decide(Campaign, Nov25, record, onboardingSeen: true).ShouldShow);
    }

    [Fact]
    public void 落盘记录带首尾空白也读得回来()
    {
        var record = PromoDisplayRecord.Parse(" 2026-11-25 ", $" {Campaign.Id} ");

        Assert.Equal(Nov25, record.LastShownDate);
        Assert.Equal(Campaign.Id, record.LastShownCampaignId);
        Assert.False(PromoSchedule.Decide(Campaign, Nov25, record, onboardingSeen: true).ShouldShow);
    }

    [Fact]
    public void 只有日期没有活动标识_不算展示过()
    {
        var record = PromoDisplayRecord.Parse("2026-11-25", null);

        Assert.Null(record.LastShownCampaignId);
        Assert.True(PromoSchedule.Decide(Campaign, Nov25, record, onboardingSeen: true).ShouldShow);
    }

    [Fact]
    public void 结论能直接写进日志()
    {
        var show = PromoSchedule.Decide(Campaign, Nov25, PromoDisplayRecord.Never, onboardingSeen: true);
        var expired = PromoSchedule.Decide(Campaign, Jan01, PromoDisplayRecord.Never, onboardingSeen: true);

        Assert.Contains(Campaign.Id, show.ToString());
        Assert.Contains("2026-12-31", expired.ToString());
        Assert.Contains(nameof(PromoSkipReason.Expired), expired.ToString());
    }
}
