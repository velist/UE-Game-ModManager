using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace UEModManager.Services.Promo;

/// <summary>这次不展示推广条的原因。每一条都要能在日志里一眼看懂。</summary>
public enum PromoSkipReason
{
    /// <summary>不适用（可以展示）。</summary>
    NotSkipped,

    /// <summary>当前没有任何推广活动。</summary>
    NoCampaign,

    /// <summary>活动数据不完整，或者链接不是 https。</summary>
    InvalidCampaign,

    /// <summary>已经过了最后展示日。</summary>
    Expired,

    /// <summary>首次使用引导还没看过：新玩家第一次打开先学怎么用，不看广告。</summary>
    OnboardingPending,

    /// <summary>这条活动今天已经展示过一次。</summary>
    AlreadyShownToday,
}

/// <summary>
/// 上一次<b>真正把推广条显示出来</b>的记录：哪一天、哪条活动。
/// 两项一起才能回答「这条活动今天展示过没有」。
/// </summary>
/// <param name="LastShownDate">上次显示的本地日期；从没显示过（或落盘的日期认不出来）为 null。</param>
/// <param name="LastShownCampaignId">上次显示的活动标识。</param>
public readonly record struct PromoDisplayRecord(DateOnly? LastShownDate, string? LastShownCampaignId)
{
    /// <summary>从没显示过。</summary>
    public static PromoDisplayRecord Never => default;

    /// <summary>
    /// 从落盘的两个字符串还原。日期认不出来（被手改过、文件损坏）就当从没显示过——
    /// 最坏的结果是今天多出现一次，不值得为它报错。
    /// </summary>
    public static PromoDisplayRecord Parse(string? lastShownDate, string? lastShownCampaignId)
        => new(
            PromoSchedule.ParseDate(lastShownDate),
            string.IsNullOrWhiteSpace(lastShownCampaignId) ? null : lastShownCampaignId.Trim());

    /// <summary>
    /// <paramref name="campaign"/> 在 <paramref name="today"/> 这一天是否已经展示过。
    ///
    /// <para>
    /// 判据是<b>日期恰好等于今天</b>，不是"不早于今天"。玩家把系统时间调到过未来再调回来，
    /// 记录里会留下一个未来的日期：按"不早于"算，推广条要一直等到那一天才再出现；
    /// 按"等于"算，今天照常展示一次，展示时把记录写回今天，自己就纠正过来了。
    /// </para>
    /// </summary>
    public bool CoversToday(PromoCampaign campaign, DateOnly today)
        => LastShownDate == today
           && string.Equals(LastShownCampaignId, campaign.Id, StringComparison.Ordinal);
}

/// <summary>一次"要不要展示推广条"的结论，带可直接进日志的理由。</summary>
/// <param name="Campaign">要展示的那条活动；不展示时为 null。</param>
/// <param name="SkipReason">不展示的原因。</param>
/// <param name="Reason">给人看的理由。</param>
public sealed record PromoDecision(PromoCampaign? Campaign, PromoSkipReason SkipReason, string Reason)
{
    /// <summary>是否展示。为 true 时 <see cref="Campaign"/> 一定有值。</summary>
    [MemberNotNullWhen(true, nameof(Campaign))]
    public bool ShouldShow => Campaign != null && SkipReason == PromoSkipReason.NotSkipped;

    /// <inheritdoc/>
    public override string ToString()
        => ShouldShow ? $"展示推广条「{Campaign.Id}」：{Reason}" : $"不展示推广条（{SkipReason}）：{Reason}";
}

/// <summary>
/// 推广条"现在该不该出现"的判定（纯函数：不看时钟、不碰磁盘、不碰界面，今天是哪天由调用方给）。
///
/// <para>三个条件同时成立才展示：</para>
/// <list type="number">
/// <item><b>没到期</b>：今天 ≤ 最后展示日（含当天）。</item>
/// <item><b>新手引导已经看过</b>：第一次打开软件的玩家先学怎么用。</item>
/// <item><b>这条活动今天还没展示过</b>：同一条活动一天最多一次；换一条活动当天可以再展示一次。</item>
/// </list>
///
/// <para>
/// 到期与否<b>只看调用方给的本地日期</b>，不联网核对。玩家把系统时间调回去能让一条过期的活动
/// 再出现——这是"到期日写死、不联网"这个方案本身的边界，不在这里绕。
/// </para>
/// </summary>
public static class PromoSchedule
{
    /// <summary>展示日期落盘的格式。与系统区域设置无关，换语言、换日历都读得回来。</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>判定。</summary>
    /// <param name="campaign">当前活动；没有活动传 null。</param>
    /// <param name="today">今天的本地日期。</param>
    /// <param name="record">上一次显示的记录。</param>
    /// <param name="onboardingSeen">首次使用引导是否已经看过（看完或跳过都算）。</param>
    public static PromoDecision Decide(
        PromoCampaign? campaign, DateOnly today, PromoDisplayRecord record, bool onboardingSeen)
    {
        if (campaign == null)
        {
            return Skip(PromoSkipReason.NoCampaign, "当前没有推广活动");
        }

        if (!campaign.IsDisplayable)
        {
            return Skip(PromoSkipReason.InvalidCampaign, $"活动「{campaign.Id}」数据不完整或链接不是 https");
        }

        // 顺序即优先级：到期排在最前，过期之后不管别的条件怎样都不再出现
        if (IsExpired(campaign, today))
        {
            return Skip(PromoSkipReason.Expired,
                $"活动「{campaign.Id}」已于 {FormatDate(campaign.LastShowDate)} 下线");
        }

        if (!onboardingSeen)
        {
            return Skip(PromoSkipReason.OnboardingPending, "首次使用引导还没看过");
        }

        if (record.CoversToday(campaign, today))
        {
            return Skip(PromoSkipReason.AlreadyShownToday, $"活动「{campaign.Id}」今天已经展示过");
        }

        return new PromoDecision(campaign, PromoSkipReason.NotSkipped, "今天还没展示过");
    }

    /// <summary>是否已经过了最后展示日。最后展示日当天仍算有效。</summary>
    public static bool IsExpired(PromoCampaign campaign, DateOnly today)
    {
        if (campaign == null) throw new ArgumentNullException(nameof(campaign));
        return today > campaign.LastShowDate;
    }

    /// <summary>把日期写成落盘格式。</summary>
    public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>读回落盘的日期；空的、格式不对的一律返回 null。</summary>
    public static DateOnly? ParseDate(string? text)
        => DateOnly.TryParseExact(text?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static PromoDecision Skip(PromoSkipReason reason, string why) => new(null, reason, why);
}
