using System;

namespace UEModManager.Services.Promo;

/// <summary>
/// 一条推广活动：主窗口顶部那张「优惠券」上的全部内容，加上它什么时候下线。
///
/// <para>
/// 界面不写死任何一句文案，全部从这条记录里取；换活动只换这条记录。
/// 文字都是<b>未翻译的中文原文</b>，显示时由界面层按当前语言去查译文（与其它界面文字同一套机制）。
/// </para>
/// </summary>
/// <param name="Id">
/// 活动标识。「今天展示过没有」是按它记的：换一条活动（换一个标识）当天可以再展示一次，
/// 同一条活动一天最多一次。
/// </param>
/// <param name="Title">正文标题，例如「GPT 充值优惠」。</param>
/// <param name="Subtitle">正文副标题，例如「15 元立减优惠券，点击领取」。</param>
/// <param name="CurrencySymbol">票根上金额前的货币符号，例如「¥」。</param>
/// <param name="Amount">票根上的金额数字，例如「15」。</param>
/// <param name="StubCaption">票根上金额下面的小字，例如「立减券」。</param>
/// <param name="ActionText">主按钮文字，例如「立即领取」。</param>
/// <param name="Url">点击后用系统浏览器打开的链接，只允许 https（见 <see cref="PromoLink"/>）。</param>
/// <param name="LastShowDate">
/// 最后一个展示日（<b>本地日期，含当天</b>）。过了这一天就永远不再出现。
/// 到期日跟着数据走、不联网核对：程序里写的是哪天就是哪天。
/// </param>
public sealed record PromoCampaign(
    string Id,
    string Title,
    string Subtitle,
    string CurrencySymbol,
    string Amount,
    string StubCaption,
    string ActionText,
    string Url,
    DateOnly LastShowDate)
{
    /// <summary>
    /// 这条记录能不能拿来展示：标识、要显示的文字都得有，链接必须是 https。
    ///
    /// 现在的数据是写死的、必然合格；这道检查是给"以后数据换了来源"留的——
    /// 一张标题空着、或者按钮点了没反应的优惠券，不如不出现。
    /// </summary>
    public bool IsDisplayable
        => !string.IsNullOrWhiteSpace(Id)
           && !string.IsNullOrWhiteSpace(Title)
           && !string.IsNullOrWhiteSpace(Amount)
           && !string.IsNullOrWhiteSpace(ActionText)
           && PromoLink.IsAllowed(Url);
}

/// <summary>
/// 当前的推广活动从哪来。<b>全程序只有这一个出处。</b>
///
/// <para>
/// 现在是写死在程序里的一条数据：不联网取配置，也不上报展示与点击。
/// 以后要换活动、或者改成从服务端下发，只改 <see cref="Current"/> 的来源，
/// 判定（<see cref="PromoSchedule"/>）、持久化与界面都不用动。
/// </para>
/// </summary>
public static class PromoCampaigns
{
    /// <summary>GPT 充值 15 元立减券。最后展示日 2026-12-31，2027-01-01 起不再出现。</summary>
    public static readonly PromoCampaign GptTopUpCoupon = new(
        Id: "gpt-topup-15off-2026",
        Title: "GPT 充值优惠",
        Subtitle: "15 元立减优惠券，点击领取",
        CurrencySymbol: "¥",
        Amount: "15",
        StubCaption: "立减券",
        ActionText: "立即领取",
        Url: "https://gptpluscz.com/offer/ZpWxYTSoWT_ZMxGDFyLF99zRfeZXecxYa5gm7Zk4ajI",
        LastShowDate: new DateOnly(2026, 12, 31));

    /// <summary>当前这条活动；没有活动时为 null（界面上就什么都不出现）。</summary>
    public static PromoCampaign? Current => GptTopUpCoupon;
}
