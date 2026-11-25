using System;
using System.Diagnostics;
using UEModManager.Services.Promo;

namespace UEModManager.Services
{
    /// <summary>点「领取」之后的结果。</summary>
    public enum PromoOpenResult
    {
        /// <summary>已经交给系统浏览器。</summary>
        Opened,

        /// <summary>没有打开：活动已经下线。界面上直接收起即可，不用提示。</summary>
        Expired,

        /// <summary>没有打开：链接不是 https。数据写错了才会发生，不交给系统。</summary>
        Blocked,

        /// <summary>系统没能打开浏览器（没有默认浏览器、被安全软件拦截等）。</summary>
        Failed,
    }

    /// <summary>
    /// 主窗口顶部推广条的 IO 适配层：取今天的日期、读写展示记录、把链接交给系统浏览器。
    ///
    /// <para>
    /// 规则都在 Core（<see cref="PromoSchedule"/> 判定要不要展示，<see cref="PromoLink"/> 管链接放行），
    /// 活动数据也在 Core（<see cref="PromoCampaigns"/>）；这里只接线，不做第二份判断。
    /// </para>
    ///
    /// <para>
    /// <b>全程不联网、不统计。</b>到期日写死在活动数据里，判定只看本机日期；
    /// 展示与点击都不上报，本类也不为它们写日志——诊断包是可以导出发给开发者的，
    /// 日志里留一条"点了广告"就等于变相统计。
    /// </para>
    ///
    /// <para>
    /// 时钟、活动来源、打开浏览器的动作都能从构造函数换掉：测试要能把日期拨到 2027 年，
    /// 也绝不能真的弹出一个浏览器。
    /// </para>
    /// </summary>
    public sealed class PromoBarService
    {
        private readonly Func<DateOnly> _today;
        private readonly Func<PromoCampaign?> _campaign;
        private readonly Action<string> _openInBrowser;

        /// <param name="today">今天的本地日期；默认取系统时钟。</param>
        /// <param name="campaign">当前活动的来源；默认 <see cref="PromoCampaigns.Current"/>。</param>
        /// <param name="openInBrowser">把链接交给系统的动作；默认用系统默认浏览器打开。</param>
        public PromoBarService(
            Func<DateOnly>? today = null,
            Func<PromoCampaign?>? campaign = null,
            Action<string>? openInBrowser = null)
        {
            _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
            _campaign = campaign ?? (() => PromoCampaigns.Current);
            _openInBrowser = openInBrowser ?? OpenWithSystemBrowser;
        }

        /// <summary>现在该不该展示（只判定，不记账）。</summary>
        public PromoDecision Evaluate()
            => PromoSchedule.Decide(
                _campaign(),
                _today(),
                UiPreferences.LoadPromoDisplayRecord(),
                UiPreferences.LoadOnboardingGuideShown());

        /// <summary>
        /// 展示入口：要展示就<b>同时</b>记下「今天展示过」，返回那条活动；不展示返回 null。
        ///
        /// <para>
        /// 调用方拿到非 null 之后应当立刻把推广条显示出来——记账与显示是同一刻的事。
        /// 记不上账（配置读不出、写不进）时返回 null：对玩家说的是"每天最多一次"，
        /// 记不住就宁可这次不出现。
        /// </para>
        /// </summary>
        public PromoCampaign? TryBeginDisplay()
        {
            var decision = Evaluate();
            if (!decision.ShouldShow) return null;

            return UiPreferences.TrySavePromoDisplayRecord(_today(), decision.Campaign.Id)
                ? decision.Campaign
                : null;
        }

        /// <summary>这条活动此刻是否已经下线（软件一直开着跨过了到期日的午夜时用）。</summary>
        public bool IsExpired(PromoCampaign campaign)
        {
            if (campaign == null) throw new ArgumentNullException(nameof(campaign));
            return PromoSchedule.IsExpired(campaign, _today());
        }

        /// <summary>
        /// 用系统默认浏览器打开活动链接。只放行 https，已下线的活动不再打开。
        /// 不抛异常：打不开时返回 <see cref="PromoOpenResult.Failed"/>，由界面决定怎么告诉玩家。
        /// </summary>
        public PromoOpenResult Open(PromoCampaign campaign)
        {
            if (campaign == null) throw new ArgumentNullException(nameof(campaign));

            if (IsExpired(campaign)) return PromoOpenResult.Expired;
            if (!PromoLink.IsAllowed(campaign.Url)) return PromoOpenResult.Blocked;

            try
            {
                _openInBrowser(campaign.Url);
                return PromoOpenResult.Opened;
            }
            catch (Exception ex)
            {
                // 只记失败原因，不记"点了"这件事
                try { Console.WriteLine($"[Promo] 打开浏览器失败: {ex.Message}"); } catch { }
                return PromoOpenResult.Failed;
            }
        }

        /// <summary>
        /// 交给 Shell 打开。<b>调用前必须已经过 <see cref="PromoLink.IsAllowed"/></b>：
        /// Shell 执行什么都肯开，放行检查是这里唯一的闸。
        /// </summary>
        private static void OpenWithSystemBrowser(string url)
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
    }
}
