using UEModManager.Services.Promo;

namespace UEModManager.Core.Tests.Services.Promo;

/// <summary>
/// 当前这条推广活动的数据，以及链接的放行规则。
///
/// <para>
/// 数据是应用所有者原话里的事实：GPT 充值优惠、15 元立减优惠券、12 月 31 日下线、那一条链接。
/// 这里逐项钉住，防止以后改文案时顺手改掉金额或日期，或者给链接"规范化"一下。
/// </para>
/// </summary>
public class PromoCampaignTests
{
    private const string OwnerLink = "https://gptpluscz.com/offer/ZpWxYTSoWT_ZMxGDFyLF99zRfeZXecxYa5gm7Zk4ajI";

    [Fact]
    public void 当前活动就是那条GPT充值立减券()
    {
        var campaign = PromoCampaigns.Current;

        Assert.NotNull(campaign);
        Assert.Same(PromoCampaigns.GptTopUpCoupon, campaign);
        Assert.Equal("GPT 充值优惠", campaign!.Title);
        Assert.Equal("15 元立减优惠券，点击领取", campaign.Subtitle);
        Assert.Equal("¥", campaign.CurrencySymbol);
        Assert.Equal("15", campaign.Amount);
        Assert.Equal("立减券", campaign.StubCaption);
        Assert.Equal("立即领取", campaign.ActionText);
    }

    [Fact]
    public void 链接与所有者给的一字不差()
    {
        Assert.Equal(OwnerLink, PromoCampaigns.GptTopUpCoupon.Url);
        Assert.True(PromoLink.IsAllowed(PromoCampaigns.GptTopUpCoupon.Url));
    }

    [Fact]
    public void 最后展示日是2026年12月31日()
        => Assert.Equal(new DateOnly(2026, 12, 31), PromoCampaigns.GptTopUpCoupon.LastShowDate);

    [Fact]
    public void 当前活动数据完整_可以展示()
        => Assert.True(PromoCampaigns.GptTopUpCoupon.IsDisplayable);

    /// <summary>
    /// 文案只用所有者说过的事实。「官方」「限时」「有效期」「独家」这类词他没说过，
    /// 写上去就是替他做了承诺。
    /// </summary>
    [Fact]
    public void 文案里没有所有者没说过的话()
    {
        var campaign = PromoCampaigns.GptTopUpCoupon;
        var copy = string.Concat(campaign.Title, campaign.Subtitle, campaign.StubCaption, campaign.ActionText);

        foreach (var word in new[] { "官方", "限时", "有效期", "独家", "最低", "仅限", "免费", "保证" })
        {
            Assert.DoesNotContain(word, copy);
        }
    }

    // ─── 链接放行 ───

    [Theory]
    [InlineData("https://gptpluscz.com/offer/abc")]
    [InlineData("https://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/Path?x=1&y=2")]
    public void https链接放行(string url) => Assert.True(PromoLink.IsAllowed(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://gptpluscz.com/offer/abc")]
    [InlineData("gptpluscz.com/offer/abc")]
    [InlineData("//gptpluscz.com/offer/abc")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/a")]
    [InlineData("https://")]
    [InlineData("https:///nohost")]
    [InlineData(" https://example.com")]
    [InlineData("https://example.com ")]
    [InlineData("https://example.com\n--flag")]
    [InlineData("https://example.com\" & calc.exe")]
    [InlineData("https://exam ple.com")]
    public void 其余一律不放行(string? url) => Assert.False(PromoLink.IsAllowed(url));
}
