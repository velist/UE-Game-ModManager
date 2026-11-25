using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Localization;
using UEModManager.Services;
using UEModManager.Services.Promo;
using UEModManager.Views;

namespace UEModManager
{
    /// <summary>
    /// 主窗口顶部的推广条：一张优惠券。
    ///
    /// <para>
    /// 这里只管<b>什么时候出现、点了之后怎样</b>。要不要展示由 Core 的 <c>PromoSchedule</c> 判定，
    /// 活动内容来自 Core 的 <c>PromoCampaigns</c>，读写展示记录与打开浏览器在 <see cref="PromoBarService"/>，
    /// 动效全部走 <see cref="MotionAssist"/>（<c>Reveal</c> / <c>Shine</c> / <c>Conceal</c>），样子在 MainWindow.xaml。
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>时机</b>：启动流程走完后等 <see cref="PromoBarDelay"/> 出现。这次启动时新手引导还没看过、
    /// 今天已经展示过、或者活动已下线，就什么都不安排。到点时窗口正最小化着就再等等，回来了才出现。</item>
    /// <item><b>记账</b>：出现的那一刻就记下「今天展示过」，所以无论点没点、关没关，同一天重启都不会再出现。</item>
    /// <item><b>收起</b>：点券面任意处或「立即领取」→ 打开链接后收起；点「×」→ 直接收起。</item>
    /// <item><b>不联网、不统计</b>：到期日写在活动数据里；展示与点击都不上报，也不写日志。</item>
    /// </list>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>启动流程走完后，等这么久推广条才出现：先让玩家看清自己的 MOD 列表。</summary>
        internal static readonly TimeSpan PromoBarDelay = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 推广条出现后这么久之内的点击不作数。
        ///
        /// 它出现时会把下面的工具栏往下推：玩家正要点「导入」，手指落下的那一瞬间那个位置换成了广告——
        /// 没有这段保护，这一下就会替他打开一个网页。宁可少一次点击，也不要一次不是他本意的点击。
        /// </summary>
        internal static readonly TimeSpan PromoClickGuard = TimeSpan.FromMilliseconds(800);

        /// <summary>开头连扫几次高光；之后降为每隔几秒一次的淡高光（节奏见 <see cref="MotionAssist.Shine"/>）。</summary>
        private const int PromoShineBursts = 3;

        /// <summary>高光等入场滑完（320ms）再开始扫。</summary>
        private const int PromoShineDelayMs = 420;

        private DispatcherTimer? _promoDelayTimer;
        private DispatcherTimer? _promoExpiryTimer;

        /// <summary>正在展示的那条活动；没在展示时为 null。</summary>
        private PromoCampaign? _promoCampaign;

        private long _promoShownAtTick;

        /// <summary>鼠标是在券面上按下的。按下与松开都落在券上才算一次点击。</summary>
        private bool _promoPressed;

        private PromoBarService? PromoBarService
            => ((App)Application.Current).ServiceProvider?.GetService<PromoBarService>();

        /// <summary>
        /// 启动流程走完后调用一次：这次启动该展示的话，<see cref="PromoBarDelay"/> 之后让推广条出现。
        /// </summary>
        private void SchedulePromoBar()
        {
            try
            {
                // 一次启动只安排一次（Loaded 理论上可能再触发，那样会多出一个计时器）
                if (_promoDelayTimer != null) return;

                var service = PromoBarService;

                // 这次启动用不上就什么都不安排——活动下线之后，这里就是一次判定、没有任何后续。
                // 新手引导的状态也在这一刻定下：第一次打开软件的人，哪怕两秒内就点了「跳过」，这次也不出现。
                if (service == null || !service.Evaluate().ShouldShow) return;

                _promoDelayTimer = new DispatcherTimer { Interval = PromoBarDelay };
                _promoDelayTimer.Tick += PromoDelay_Tick;
                _promoDelayTimer.Start();
                Closed += PromoBar_WindowClosed;
            }
            catch (Exception ex)
            {
                // 推广是我们的需求，不是玩家的：它出任何问题都不该连累启动，也不该弹任何东西
                _logger?.LogDebug(ex, "[Promo] 安排推广条失败");
            }
        }

        private void PromoDelay_Tick(object? sender, EventArgs e)
        {
            // 窗口这会儿看不见（刚启动就被最小化了）：先不出现，计时器照常走，过两秒再看。
            // 「今天展示过」要记在玩家真的看得到的那一刻，不能记在一个没人看见的窗口上。
            if (WindowState == WindowState.Minimized || !IsVisible) return;

            _promoDelayTimer?.Stop();
            try
            {
                // 重新判定并记下「今天展示过」，两件事在同一刻完成；记不上账就不展示
                var campaign = PromoBarService?.TryBeginDisplay();
                if (campaign != null) ShowPromoBar(campaign);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Promo] 显示推广条失败");
            }
        }

        private void ShowPromoBar(PromoCampaign campaign)
        {
            _promoCampaign = campaign;
            _promoPressed = false;
            _promoShownAtTick = Environment.TickCount64;

            PromoBar.DataContext = campaign;
            MotionAssist.Reveal(PromoBar);
            MotionAssist.Shine(PromoShine, PromoAmount, PromoShineBursts, PromoShineDelayMs);

            // 软件可能一直开到活动下线之后：每分钟看一眼，到期了就自己收起
            _promoExpiryTimer ??= CreatePromoExpiryTimer();
            _promoExpiryTimer.Start();
        }

        private DispatcherTimer CreatePromoExpiryTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += PromoExpiry_Tick;
            return timer;
        }

        private void PromoExpiry_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_promoCampaign is not { } campaign || PromoBarService is not { } service || service.IsExpired(campaign))
                    HidePromoBar();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Promo] 检查推广条是否到期失败");
                HidePromoBar();
            }
        }

        /// <summary>收起推广条。「今天展示过」在出现时就记下了，这里不用再记。</summary>
        private void HidePromoBar()
        {
            _promoCampaign = null;
            _promoPressed = false;
            _promoExpiryTimer?.Stop();
            MotionAssist.StopShine(PromoShine);
            MotionAssist.Conceal(PromoBar);
        }

        private void PromoBar_WindowClosed(object? sender, EventArgs e)
        {
            _promoDelayTimer?.Stop();
            _promoExpiryTimer?.Stop();
            MotionAssist.StopShine(PromoShine);
        }

        private bool IsWithinPromoClickGuard()
            => Environment.TickCount64 - _promoShownAtTick < PromoClickGuard.TotalMilliseconds;

        // ─── 点击 ───

        private void PromoCoupon_MouseDown(object sender, MouseButtonEventArgs e) => _promoPressed = true;

        private void PromoCoupon_MouseLeave(object sender, MouseEventArgs e) => _promoPressed = false;

        private void PromoCoupon_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // 在别处按住、拖到券上才松手的不算：那多半是在拖别的东西
            if (!_promoPressed) return;
            _promoPressed = false;
            ClaimPromo();
        }

        private void PromoClaim_Click(object sender, RoutedEventArgs e) => ClaimPromo();

        /// <summary>领取：用系统默认浏览器打开活动链接，然后收起。</summary>
        private void ClaimPromo()
            => SafeEvent.Run(this, () =>
            {
                if (_promoCampaign is not { } campaign || IsWithinPromoClickGuard()) return Task.CompletedTask;

                // 只放行 https、已下线的活动不再打开，这两条都在服务里把关
                var result = PromoBarService?.Open(campaign) ?? PromoOpenResult.Blocked;
                HidePromoBar();

                if (result == PromoOpenResult.Failed)
                {
                    CyberMessageBox.Show(this,
                        UiText.Interpolate($"无法打开默认浏览器，请手动复制此链接到浏览器访问：\n\n{campaign.Url}"),
                        UiText.Get(campaign.Title), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return Task.CompletedTask;
            }, _logger, "打开推广链接");

        private void PromoClose_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, () =>
            {
                if (!IsWithinPromoClickGuard()) HidePromoBar();
                return Task.CompletedTask;
            }, _logger, "关闭推广条");
    }
}
