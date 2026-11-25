using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UEModManager.Infrastructure;
using UEModManager.Tests.Themes;
using UEModManager.Tests.Views;

namespace UEModManager.Tests.Infrastructure;

/// <summary>
/// 提示条的三个动效：<see cref="MotionAssist.Reveal"/>（滑入）、<see cref="MotionAssist.Conceal"/>（收起）、
/// <see cref="MotionAssist.Shine"/>（高光扫过）。主窗口顶部的推广条用的就是这三个。
///
/// <para>
/// 两条要求在这里钉住：<b>系统关闭动画时界面是静态的</b>（滑入就是直接显示、收起就是立即折叠、高光根本不启动），
/// 以及<b>高光的节奏</b>（先连扫几次并带着金额脉冲，之后降为每隔几秒一次的淡高光）。
/// </para>
///
/// <para>
/// 窗口是真的显示出来的（放在屏幕外、不抢焦点）：没有挂到窗口句柄上的视觉树 <c>IsVisible</c> 恒为 false，
/// 而高光「没人看得见就不播」正是看这个。节奏用 <see cref="MotionAssist.AdvanceShine"/> 一步一步核对，不等真实时间；
/// 只有「动画播完才折叠」「计时器到点真的会扫」这类接线才等时钟，等的都是几十毫秒的事、超时给到十秒。
/// </para>
/// </summary>
// MotionAssist.OverrideMotionForTests 是进程级的：必须独占运行（这个 collection 禁用了并行）。
[Collection(LanguageIntegrationCollection.Name)]
public sealed class MotionAssistBannerTests
{
    /// <summary>扫光计时器的首次延迟给到一小时：测试期间它自己不会响，节奏由测试一步一步推。</summary>
    private const int NeverByItself = 3_600_000;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ─── 滑入 ───

    [Fact]
    public void 系统关闭动画时_滑入就是直接显示()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(false);
            using var banner = Banner.Show(visible: false);

            MotionAssist.Reveal(banner.Host);

            Assert.Equal(Visibility.Visible, banner.Host.Visibility);
            Assert.False(banner.Host.HasAnimatedProperties);   // 一个动画都没挂
            banner.Host.UpdateLayout();
            Assert.Equal(Banner.NaturalHeight, banner.Host.ActualHeight);
            Assert.Equal(1.0, banner.Host.Opacity);
        });

    [Fact]
    public void 系统允许动画时_滑入是从零高度展开并淡入_播完回到自然高度()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: false);
            var host = banner.Host;

            // 放慢到 5 秒：途中的状态有充足的时间被看到
            MotionAssist.Reveal(host, ms: 5000);
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.True(host.HasAnimatedProperties);
            if (!AnimationClockTicks()) return;

            Assert.True(
                PumpUntil(() => host.ActualHeight > 0 && host.ActualHeight < Banner.NaturalHeight && host.Opacity is > 0 and < 1,
                    TimeSpan.FromSeconds(4)),
                $"没有看到展开途中的状态：高度 {host.ActualHeight}，不透明度 {host.Opacity}");

            // 正常速度再滑一次（途中再叫一次不会出错）。播完后动画不再压着属性：
            // 高度回到 Auto，之后内容变高变矮（换语言、窗口变窄）都能跟着走
            MotionAssist.Reveal(host, ms: 80);
            Assert.True(PumpUntil(() => double.IsNaN(host.Height) && host.Opacity == 1.0, Patience),
                $"滑入播完后高度应回到 Auto、完全不透明：高度 {host.Height}，不透明度 {host.Opacity}");
            host.UpdateLayout();
            Assert.Equal(Banner.NaturalHeight, host.ActualHeight);
        });

    // ─── 收起 ───

    [Fact]
    public void 系统关闭动画时_收起就是立即折叠()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(false);
            using var banner = Banner.Show(visible: true);
            var hidden = 0;

            MotionAssist.Conceal(banner.Host, () => hidden++);

            Assert.Equal(Visibility.Collapsed, banner.Host.Visibility);
            Assert.Equal(1, hidden);
            Assert.False(banner.Host.HasAnimatedProperties);

            // 已经折叠了再收一次：什么都不做，也不再通知
            MotionAssist.Conceal(banner.Host, () => hidden++);
            Assert.Equal(1, hidden);
        });

    [Fact]
    public void 系统允许动画时_收起播完才折叠_只通知一次_不留下动画()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);
            var host = banner.Host;
            if (!AnimationClockTicks()) return;
            var hidden = 0;

            MotionAssist.Conceal(host, () => hidden++, ms: 120);

            // 刚开始收：还看得见，还没通知
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal(0, hidden);
            Assert.True(host.HasAnimatedProperties);

            // 点「×」之后提示条必须真的消失 —— 这是整条推广条最不能坏的一处
            Assert.True(PumpUntil(() => host.Visibility == Visibility.Collapsed, Patience), "收起动画播完后提示条没有折叠");
            Assert.Equal(1, hidden);
            Assert.False(host.HasAnimatedProperties);
            Assert.True(double.IsNaN(host.Height));
            Assert.Equal(1.0, host.Opacity);

            // 之后再滑出来仍是完整的一条，没有被上一次收起留下的高度 0 / 透明卡住
            using (MotionAssist.OverrideMotionForTests(false))
            {
                MotionAssist.Reveal(host);
                host.UpdateLayout();
                Assert.Equal(Banner.NaturalHeight, host.ActualHeight);
                Assert.Equal(1.0, host.Opacity);
            }
        });

    [Fact]
    public void 收起途中又被叫出来_旧的收尾不会把它折叠掉()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);
            var host = banner.Host;
            var hidden = 0;

            // 收起放得极慢（测试期间播不完），收到一半又滑出来
            MotionAssist.Conceal(host, () => hidden++, ms: NeverByItself);
            var stale = MotionAssist.CurrentRevealToken(host);
            MotionAssist.Reveal(host, ms: NeverByItself);

            // 这时旧的收起动画「播完」了：对不上号，什么都不做
            Assert.False(MotionAssist.FinishConceal(host, stale, () => hidden++));
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal(0, hidden);

            // 对得上号的收尾照常生效
            Assert.True(MotionAssist.FinishConceal(host, MotionAssist.CurrentRevealToken(host), () => hidden++));
            Assert.Equal(Visibility.Collapsed, host.Visibility);
            Assert.Equal(1, hidden);
            Assert.False(host.HasAnimatedProperties);
        });

    // ─── 高光 ───

    [Fact]
    public void 系统关闭动画时_高光根本不启动_界面是静态的()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(false);
            using var banner = Banner.Show(visible: true);

            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 3, delayMs: 0);

            Assert.False(MotionAssist.IsShining(banner.Band));
            Assert.Null(MotionAssist.AdvanceShine(banner.Band));
            // 给计时器（如果有的话）充分的机会：什么都不会发生
            Assert.False(PumpUntil(() => banner.Band.HasAnimatedProperties, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(0, banner.Band.Opacity);
            Assert.Equal(Transform.Identity, banner.Pulse.RenderTransform);   // 金额没被碰过
        });

    [Fact]
    public void 高光的节奏_先连扫三次并带着金额脉冲_然后降为六秒一次不带脉冲()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);

            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 3, delayMs: NeverByItself);
            Assert.True(MotionAssist.IsShining(banner.Band));

            var burst = MotionAssist.ShineBurstInterval;
            var idle = MotionAssist.ShineIdleInterval;
            var steps = Enumerable.Range(0, 7).Select(_ => MotionAssist.AdvanceShine(banner.Band)).ToArray();
            Assert.Equal(new MotionAssist.ShineStep?[]
            {
                new MotionAssist.ShineStep(burst, Swept: true, Pulsed: true),    // 第 1 次，1.4 秒后下一次
                new MotionAssist.ShineStep(burst, Swept: true, Pulsed: true),    // 第 2 次
                new MotionAssist.ShineStep(idle, Swept: true, Pulsed: true),     // 第 3 次：连扫结束，下一次在 6 秒后
                new MotionAssist.ShineStep(idle, Swept: true, Pulsed: false),    // 之后：淡高光，不再脉冲
                new MotionAssist.ShineStep(idle, Swept: true, Pulsed: false),
                new MotionAssist.ShineStep(idle, Swept: true, Pulsed: false),
                new MotionAssist.ShineStep(idle, Swept: true, Pulsed: false),
            }, steps);

            // 节奏的具体数字（写进了更新说明与汇报，改了要一起改）
            Assert.Equal(TimeSpan.FromMilliseconds(1400), burst);
            Assert.Equal(TimeSpan.FromSeconds(6), idle);
            Assert.Equal(900, MotionAssist.ShineBurstSweepMs);
            Assert.Equal(1300, MotionAssist.ShineIdleSweepMs);
            Assert.Equal(0.45, MotionAssist.ShineIdleOpacity);
            Assert.Equal(1.1, MotionAssist.ShinePulseScale);

            // 连扫时两次之间有停顿；平静阶段大部分时间是完全静止的（不占渲染时钟）
            Assert.True(MotionAssist.ShineBurstSweepMs < burst.TotalMilliseconds);
            Assert.True(MotionAssist.ShineIdleSweepMs * 4 < idle.TotalMilliseconds);
        });

    [Fact]
    public void 扫光与脉冲各挂在各的元素上_停掉时一起撤干净()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);

            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 1, delayMs: NeverByItself);
            Assert.False(banner.Band.HasAnimatedProperties);   // 还没到点：什么都没在播
            MotionAssist.AdvanceShine(banner.Band);

            var move = MotionAssist.EnsureTransform<TranslateTransform>(banner.Band);
            var scale = MotionAssist.EnsureTransform<ScaleTransform>(banner.Pulse);
            Assert.True(banner.Band.HasAnimatedProperties, "高光的不透明度应在动");
            Assert.True(move.HasAnimatedProperties, "高光应在水平移动");
            Assert.True(scale.HasAnimatedProperties, "金额应在脉冲");

            MotionAssist.StopShine(banner.Band);

            Assert.False(MotionAssist.IsShining(banner.Band));
            Assert.Null(MotionAssist.AdvanceShine(banner.Band));
            Assert.False(banner.Band.HasAnimatedProperties);
            Assert.False(move.HasAnimatedProperties);
            Assert.False(scale.HasAnimatedProperties);
            Assert.Equal(0, banner.Band.Opacity);    // 高光回到透明
            Assert.Equal(1.0, scale.ScaleX);         // 金额回到原大小
            Assert.Equal(1.0, scale.ScaleY);

            // 停了之后再停一次、没开始就停：都不出错
            MotionAssist.StopShine(banner.Band);
            MotionAssist.StopShine(new Border());
        });

    [Fact]
    public void 高光原有的倾斜在扫光时保留()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);

            MotionAssist.Shine(banner.Band, null, bursts: 1, delayMs: NeverByItself);
            MotionAssist.AdvanceShine(banner.Band);

            // XAML 里给高光写的是一个 SkewTransform；扫光要加位移，但不能把倾斜顶掉
            var group = Assert.IsType<TransformGroup>(banner.Band.RenderTransform);
            var skew = Assert.Single(group.Children.OfType<SkewTransform>());
            Assert.Equal(-20, skew.AngleX);
            Assert.Single(group.Children.OfType<TranslateTransform>());
        });

    [Fact]
    public void 没人看得见时_这一步不播也不消耗连扫次数()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: false);   // 提示条还折叠着
            var burst = MotionAssist.ShineBurstInterval;
            var unseen = new MotionAssist.ShineStep(burst, Swept: false, Pulsed: false);
            var seen = new MotionAssist.ShineStep(burst, Swept: true, Pulsed: true);

            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 3, delayMs: NeverByItself);
            for (var i = 0; i < 5; i++) Assert.Equal(unseen, MotionAssist.AdvanceShine(banner.Band));
            Assert.False(banner.Band.HasAnimatedProperties);

            // 显示出来了：开始扫
            banner.Host.Visibility = Visibility.Visible;
            banner.Host.UpdateLayout();
            Assert.Equal(seen, MotionAssist.AdvanceShine(banner.Band));

            // 窗口最小化：又停下，不白扫
            banner.Window.WindowState = WindowState.Minimized;
            for (var i = 0; i < 5; i++) Assert.Equal(unseen, MotionAssist.AdvanceShine(banner.Band));

            // 回来之后，三次连扫一次都没少：前面只扫过 1 次，还剩 2 次
            banner.Window.WindowState = WindowState.Normal;
            Assert.Equal(seen, MotionAssist.AdvanceShine(banner.Band));
            Assert.Equal(new MotionAssist.ShineStep(MotionAssist.ShineIdleInterval, Swept: true, Pulsed: true),
                MotionAssist.AdvanceShine(banner.Band));
            Assert.Equal(new MotionAssist.ShineStep(MotionAssist.ShineIdleInterval, Swept: true, Pulsed: false),
                MotionAssist.AdvanceShine(banner.Band));
        });

    [Fact]
    public void 途中关掉了系统动画_高光就此停下()
        => OnSta(() =>
        {
            using var banner = Banner.Show(visible: true);
            using (MotionAssist.OverrideMotionForTests(true))
            {
                MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 3, delayMs: NeverByItself);
                Assert.NotNull(MotionAssist.AdvanceShine(banner.Band));
            }

            using (MotionAssist.OverrideMotionForTests(false))
            {
                Assert.Null(MotionAssist.AdvanceShine(banner.Band));
                Assert.False(MotionAssist.IsShining(banner.Band));
                Assert.False(banner.Band.HasAnimatedProperties);
                Assert.Equal(0, banner.Band.Opacity);
            }
        });

    [Fact]
    public void 重新开始扫光时_先停掉上一轮()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);

            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 3, delayMs: NeverByItself);
            MotionAssist.AdvanceShine(banner.Band);   // 第一轮用掉 1 次
            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 2, delayMs: NeverByItself);

            // 第二轮从头数：2 次连扫，不是接着第一轮剩下的
            Assert.False(banner.Band.HasAnimatedProperties);   // 第一轮正在播的那次也撤了
            Assert.Equal(new MotionAssist.ShineStep(MotionAssist.ShineBurstInterval, Swept: true, Pulsed: true),
                MotionAssist.AdvanceShine(banner.Band));
            Assert.Equal(new MotionAssist.ShineStep(MotionAssist.ShineIdleInterval, Swept: true, Pulsed: true),
                MotionAssist.AdvanceShine(banner.Band));
        });

    [Fact]
    public void 高光离开界面时自己停下()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);

            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 3, delayMs: NeverByItself);
            banner.Track.Children.Remove(banner.Band);

            // Unloaded 是排进消息队列里异步触发的
            Assert.True(PumpUntil(() => !MotionAssist.IsShining(banner.Band), Patience),
                "高光已经不在界面上了，计时器还挂着");
        });

    [Fact]
    public void 计时器到点会真的扫一次()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);

            MotionAssist.Shine(banner.Band, banner.Pulse, bursts: 1, delayMs: 40);

            Assert.False(banner.Band.HasAnimatedProperties);
            Assert.True(PumpUntil(() => banner.Band.HasAnimatedProperties, Patience), "到点了高光没有扫");
        });

    [Fact]
    public void 连扫时高光是全亮的_平静阶段只有四成半()
        => OnSta(() =>
        {
            using var motion = MotionAssist.OverrideMotionForTests(true);
            using var banner = Banner.Show(visible: true);
            if (!AnimationClockTicks()) return;

            MotionAssist.Shine(banner.Band, null, bursts: 1000, delayMs: NeverByItself);
            Assert.Equal(1.0, PeakOpacity(banner.Band));

            MotionAssist.Shine(banner.Band, null, bursts: 0, delayMs: NeverByItself);
            Assert.Equal(MotionAssist.ShineIdleOpacity, PeakOpacity(banner.Band));

            // 扫完自己回到透明，不留下一条停在右边的高光
            Assert.True(PumpUntil(() => banner.Band.Opacity == 0, Patience), "扫完之后高光没有回到透明");
        });

    [Theory]
    [InlineData(150, 72, 760)]    // 推广条：高光宽 150、券高 72、最宽 760
    [InlineData(150, 72, 344)]    // 券最窄的时候
    [InlineData(80, 40, 1200)]
    public void 扫光的起点和终点上_整条高光都在容器外面(double bandWidth, double bandHeight, double trackWidth)
    {
        var (from, to) = MotionAssist.ShineTravel(bandWidth, bandHeight, trackWidth);

        // 高光斜 20°（绕中心）：上下两头比中线各多探出 tan20° × 半个高度
        var reach = Math.Tan(20 * Math.PI / 180) * bandHeight / 2;
        Assert.True(from + bandWidth + reach < 0, $"起点上高光的右端 {from + bandWidth + reach} 还在容器里");
        Assert.True(to - reach > trackWidth, $"终点上高光的左端 {to - reach} 还在容器里");

        // 让出的余量够用到 45°
        var limit = bandHeight / 2;
        Assert.True(from + bandWidth + limit <= 0);
        Assert.True(to - limit >= trackWidth);
    }

    // ─── 辅助 ───

    /// <summary>
    /// 一条与推广条同构的提示条：外壳（滑入 / 收起的对象）里是一个被裁剪的容器，
    /// 容器里有跟着脉冲的文字和那条高光。窗口真的显示在屏幕外。
    /// </summary>
    private sealed class Banner : IDisposable
    {
        /// <summary>内容高 72 + 上边距 16，与推广条一致。</summary>
        public const double NaturalHeight = 88;

        public Window Window { get; }
        public Border Host { get; }
        public Grid Track { get; }
        public Border Band { get; }
        public TextBlock Pulse { get; }

        private Banner(Window window, Border host, Grid track, Border band, TextBlock pulse)
        {
            Window = window;
            Host = host;
            Track = track;
            Band = band;
            Pulse = pulse;
        }

        public static Banner Show(bool visible)
        {
            var band = new Border
            {
                Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, IsHitTestVisible = false,
                Background = Brushes.White, RenderTransform = new SkewTransform(-20, 0),
            };
            var pulse = new TextBlock
            {
                Text = "¥15", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            var track = new Grid { Height = 72, Margin = new Thickness(32, 16, 32, 0), VerticalAlignment = VerticalAlignment.Bottom };
            track.Children.Add(pulse);
            track.Children.Add(band);

            var host = new Border
            {
                ClipToBounds = true, Child = track,
                Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
            };
            DockPanel.SetDock(host, Dock.Top);
            var panel = new DockPanel { LastChildFill = true };
            panel.Children.Add(host);
            panel.Children.Add(new Border());

            var window = new Window
            {
                Content = panel,
                ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                Width = 640, Height = 360,
            };
            window.Show();
            panel.UpdateLayout();
            return new Banner(window, host, track, band, pulse);
        }

        public void Dispose()
        {
            MotionAssist.StopShine(Band);
            Window.Close();
        }
    }

    private static void OnSta(Action work)
        => ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            work();
            return null;
        });

    /// <summary>
    /// 这个环境里 WPF 的动画时钟走不走。正常的桌面与 CI 都走；极少数没有合成器的宿主里不走，
    /// 那里「等动画播完」的断言无从验证，相关用例直接返回，而不是报一个与被测代码无关的失败。
    /// </summary>
    private static bool AnimationClockTicks()
    {
        var probe = new TranslateTransform();
        var completed = false;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(30)) { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => completed = true;
        probe.BeginAnimation(TranslateTransform.XProperty, animation);
        return PumpUntil(() => completed, TimeSpan.FromSeconds(5));
    }

    /// <summary>推一步扫光，读它播放时的不透明度。机器卡顿错过了这一次就再推一步。</summary>
    private static double PeakOpacity(FrameworkElement band)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Assert.Equal(true, MotionAssist.AdvanceShine(band)?.Swept);
            var seen = 0.0;
            if (PumpUntil(() => (seen = band.Opacity) > 0, TimeSpan.FromMilliseconds(600))) return seen;
        }
        Assert.Fail("扫光时高光一直是透明的");
        return 0;
    }

    /// <summary>处理消息队列（含计时器与动画时钟），直到条件成立或超时。</summary>
    private static bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (done()) return true;
            if (DateTime.UtcNow >= deadline) return false;

            // 退出帧排在最低一档：比它优先的（计时器、渲染、布局、Unloaded）都处理完才返回
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (!done()) Thread.Sleep(5);
        }
    }
}
