using System;
using UEModManager.Services.Promo;

namespace UEModManager.Services
{
    /// <summary>玩家点了推广条之后，界面该怎么办。</summary>
    public enum PromoBarAction
    {
        /// <summary>不作数：没在展示，或者还在刚出现的误点保护期内。界面保持原样。</summary>
        None,

        /// <summary>收起。</summary>
        Dismiss,

        /// <summary>链接已经交给浏览器；收起。</summary>
        DismissAfterOpening,

        /// <summary>浏览器没能打开；收起，并把链接给玩家让他自己复制。</summary>
        DismissAndOfferLink,
    }

    /// <summary>
    /// 推广条从出现到收起这一段的交互状态：正在展示哪条活动、出现了多久、鼠标是不是在券上按下的。
    ///
    /// <para>
    /// 不碰任何界面类型。<c>MainWindow.PromoBar.cs</c> 把鼠标事件和计时器转给它，再按它的回答去动界面。
    /// 拆出来是因为主窗口在测试里构造不出来（要一整套服务，启动还会联网查更新），而
    /// 「出现后 0.8 秒内的点击不作数」「连点两下只开一个网页」「活动下线后点了也不打开」
    /// 这些规矩恰恰是最不能错的——留在 code-behind 里就只能靠真机去试。
    /// </para>
    ///
    /// <para>不联网、不统计；点没点、关没关都不落盘也不写日志（「今天展示过」在出现时由服务记下）。</para>
    /// </summary>
    public sealed class PromoBarSession
    {
        /// <summary>
        /// 推广条出现后这么久之内的点击不作数。
        ///
        /// 它出现时会把下面的工具栏往下推：玩家正要点「导入」，手指落下的那一瞬间那个位置换成了广告——
        /// 没有这段保护，这一下就会替他打开一个网页。宁可少一次点击，也不要一次不是他本意的点击。
        /// </summary>
        public static readonly TimeSpan ClickGuard = TimeSpan.FromMilliseconds(800);

        private readonly PromoBarService _service;
        private readonly Func<long> _tickCount;
        private long _shownAtTick;

        /// <summary>鼠标是在券面上按下的。按下与松开都落在券上才算一次点击。</summary>
        private bool _pressed;

        /// <param name="service">判定、记账与打开链接。</param>
        /// <param name="tickCount">单调递增的毫秒计数；默认 <see cref="Environment.TickCount64"/>（不受改系统时间影响）。</param>
        public PromoBarSession(PromoBarService service, Func<long>? tickCount = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _tickCount = tickCount ?? (() => Environment.TickCount64);
        }

        /// <summary>正在展示的那条活动；没在展示时为 null。</summary>
        public PromoCampaign? Showing { get; private set; }

        /// <summary>
        /// 这次启动要不要安排推广条出现（只判定，不记账）。
        /// 新手引导没看过、今天已经展示过、活动已下线时为 false——那样的话这次启动什么都不用做。
        /// </summary>
        public bool ShouldSchedule => _service.Evaluate().ShouldShow;

        /// <summary>
        /// 到点了，推广条即将显示：重新判定并记下「今天展示过」，两件事在同一刻完成。
        /// 返回要显示的活动，调用方应当立刻把它显示出来；返回 null 就不显示（记不上账也算）。
        /// </summary>
        public PromoCampaign? Begin()
        {
            var campaign = _service.TryBeginDisplay();
            if (campaign == null) return null;

            Showing = campaign;
            _pressed = false;
            _shownAtTick = _tickCount();
            return campaign;
        }

        private bool IsWithinClickGuard => _tickCount() - _shownAtTick < ClickGuard.TotalMilliseconds;

        // ─── 鼠标在券面上的按下 / 松开 ───

        /// <summary>鼠标在券面上按下。没在展示（比如正在收起的那一小段动画里）时不记。</summary>
        public void PointerDown() => _pressed = Showing != null;

        /// <summary>鼠标离开了券。</summary>
        public void PointerLeave() => _pressed = false;

        /// <summary>
        /// 鼠标在券面上松开。返回 true 表示这是一次完整的点击（按下也在券上）；
        /// 在别处按住、拖到券上才松手的不算——那多半是在拖别的东西。
        /// </summary>
        public bool PointerUp()
        {
            var pressed = _pressed;
            _pressed = false;
            return pressed;
        }

        // ─── 领取 / 关闭 / 到期 ───

        /// <summary>
        /// 领取（点了券面或「立即领取」）：用系统默认浏览器打开活动链接，然后收起。
        /// 只放行 https、已下线的活动不再打开，这两条由服务把关——那两种情况下不打开，直接收起。
        /// </summary>
        public PromoBarAction Claim()
        {
            if (Showing is not { } campaign || IsWithinClickGuard) return PromoBarAction.None;

            // 先结束展示再去开浏览器：打开要花一点时间，这期间再点一下不能再开一个网页
            End();
            return _service.Open(campaign) switch
            {
                PromoOpenResult.Opened => PromoBarAction.DismissAfterOpening,
                PromoOpenResult.Failed => PromoBarAction.DismissAndOfferLink,
                _ => PromoBarAction.Dismiss,
            };
        }

        /// <summary>关闭（点了「×」）。</summary>
        public PromoBarAction Close()
        {
            if (Showing == null || IsWithinClickGuard) return PromoBarAction.None;

            End();
            return PromoBarAction.Dismiss;
        }

        /// <summary>
        /// 软件可能一直开到活动下线之后：隔一阵来问一次，到期了就收起。
        /// 没在展示、或者还没到期时返回 <see cref="PromoBarAction.None"/>。
        /// </summary>
        public PromoBarAction CheckExpiry()
        {
            if (Showing is not { } campaign || !_service.IsExpired(campaign)) return PromoBarAction.None;

            End();
            return PromoBarAction.Dismiss;
        }

        /// <summary>结束展示（界面那边已经、或者马上就要把推广条收起来）。可以重复调用。</summary>
        public void End()
        {
            Showing = null;
            _pressed = false;
        }
    }
}
