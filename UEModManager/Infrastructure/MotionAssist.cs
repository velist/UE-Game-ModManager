using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UEModManager.Infrastructure
{
    /// <summary>
    /// 界面动效的附加属性集合：按压回弹、悬停上浮、列表入场、提示条滑入收起、高光扫过。
    ///
    /// <para>
    /// 动效统一挂在这里，而不是散落在各窗口的 code-behind：XAML 里一行
    /// <c>infra:MotionAssist.Press="True"</c> 就能用，节奏（时长 / 缓动）只在这一处调。
    /// </para>
    ///
    /// <para>
    /// 尊重系统设置：Windows「显示动画」关闭时（<see cref="SystemParameters.ClientAreaAnimation"/> 为 false）
    /// 一律直接跳到终态，不播动画。
    /// </para>
    /// </summary>
    public static class MotionAssist
    {
        /// <summary>系统允许动画时才播放。</summary>
        public static bool IsMotionEnabled => _motionOverride ?? SystemParameters.ClientAreaAnimation;

        private static bool? _motionOverride;

        /// <summary>
        /// 把「系统是否允许动画」钉成固定值，返回的对象 Dispose 时复原。<b>生产恒不调用。</b>
        ///
        /// <para>
        /// 没有这个口子，「关掉动画时静态显示」这条要求就只能在跑测试的那台机器恰好关了动画时才验得到。
        /// 进程级静态状态：消费它的测试类必须放进禁用并行的 collection。
        /// </para>
        /// </summary>
        internal static IDisposable OverrideMotionForTests(bool enabled)
        {
            var scope = new MotionOverrideScope(_motionOverride);
            _motionOverride = enabled;
            return scope;
        }

        private sealed class MotionOverrideScope(bool? previous) : IDisposable
        {
            public void Dispose() => _motionOverride = previous;
        }

        internal static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
        internal static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
        internal static readonly IEasingFunction EaseInOut = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseInOut });
        internal static readonly IEasingFunction EaseOutBack = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });

        private static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

        // ─── 按压回弹 ───

        public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
            "Press", typeof(bool), typeof(MotionAssist), new PropertyMetadata(false, OnPressChanged));

        public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);
        public static void SetPress(DependencyObject d, bool value) => d.SetValue(PressProperty, value);

        private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            if ((bool)e.NewValue)
            {
                fe.PreviewMouseLeftButtonDown += Press_Down;
                fe.PreviewMouseLeftButtonUp += Press_Up;
                fe.MouseLeave += Press_Up;
            }
            else
            {
                fe.PreviewMouseLeftButtonDown -= Press_Down;
                fe.PreviewMouseLeftButtonUp -= Press_Up;
                fe.MouseLeave -= Press_Up;
            }
        }

        private static void Press_Down(object sender, MouseButtonEventArgs e) => AnimateScale((FrameworkElement)sender, 0.96, 90, EaseOut);

        private static void Press_Up(object sender, MouseEventArgs e) => AnimateScale((FrameworkElement)sender, 1.0, 180, EaseOutBack);

        private static void AnimateScale(FrameworkElement fe, double to, int ms, IEasingFunction ease)
        {
            var scale = EnsureTransform<ScaleTransform>(fe);
            if (!IsMotionEnabled)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = to;
                return;
            }
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        // ─── 悬停上浮 ───

        /// <summary>悬停时向上浮起的像素数；0 表示关闭。</summary>
        public static readonly DependencyProperty LiftProperty = DependencyProperty.RegisterAttached(
            "Lift", typeof(double), typeof(MotionAssist), new PropertyMetadata(0.0, OnLiftChanged));

        public static double GetLift(DependencyObject d) => (double)d.GetValue(LiftProperty);
        public static void SetLift(DependencyObject d, double value) => d.SetValue(LiftProperty, value);

        private static void OnLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.MouseEnter -= Lift_Enter;
            fe.MouseLeave -= Lift_Leave;
            if ((double)e.NewValue > 0)
            {
                fe.MouseEnter += Lift_Enter;
                fe.MouseLeave += Lift_Leave;
            }
        }

        private static void Lift_Enter(object sender, MouseEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            AnimateY(fe, -GetLift(fe), 160, EaseOut);
        }

        private static void Lift_Leave(object sender, MouseEventArgs e) => AnimateY((FrameworkElement)sender, 0, 200, EaseOut);

        private static void AnimateY(FrameworkElement fe, double to, int ms, IEasingFunction ease)
        {
            var move = EnsureTransform<TranslateTransform>(fe);
            if (!IsMotionEnabled)
            {
                move.BeginAnimation(TranslateTransform.YProperty, null);
                move.Y = to;
                return;
            }
            move.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease },
                HandoffBehavior.SnapshotAndReplace);
        }

        // ─── 列表入场 ───

        /// <summary>
        /// 列表刷新后逐个淡入上浮。只在 <see cref="ArmEntrance"/> 之后的短窗口内生效：
        /// 普通滚动时容器实现/复用不播动画，避免滚动发涩。
        /// </summary>
        public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
            "Enter", typeof(bool), typeof(MotionAssist), new PropertyMetadata(false, OnEnterChanged));

        public static bool GetEnter(DependencyObject d) => (bool)d.GetValue(EnterProperty);
        public static void SetEnter(DependencyObject d, bool value) => d.SetValue(EnterProperty, value);

        private static DateTime _armedUntil = DateTime.MinValue;
        private static int _enterIndex;

        /// <summary>列表即将整体重建时调用（例如筛选、刷新）；之后 600ms 内实现的卡片依次入场。</summary>
        public static void ArmEntrance()
        {
            _armedUntil = DateTime.UtcNow.AddMilliseconds(600);
            _enterIndex = 0;
        }

        private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.Loaded -= Enter_Trigger;
            fe.DataContextChanged -= Enter_DataContextChanged;
            if ((bool)e.NewValue)
            {
                fe.Loaded += Enter_Trigger;
                // 虚拟化回收复用容器时不会再触发 Loaded，换数据才是"新卡片出现"的信号
                fe.DataContextChanged += Enter_DataContextChanged;
            }
        }

        private static void Enter_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is FrameworkElement { IsLoaded: true } fe) PlayEnter(fe);
        }

        private static void Enter_Trigger(object sender, RoutedEventArgs e) => PlayEnter((FrameworkElement)sender);

        private static void PlayEnter(FrameworkElement fe)
        {
            if (!IsMotionEnabled || DateTime.UtcNow > _armedUntil) return;

            // 前 12 张错开 28ms，之后同时进，整批最多拖 ~0.6s
            var delay = TimeSpan.FromMilliseconds(Math.Min(_enterIndex++, 12) * 28);
            var duration = TimeSpan.FromMilliseconds(260);

            var fade = new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
            fe.Opacity = 1;
            fe.BeginAnimation(UIElement.OpacityProperty, fade);

            var move = EnsureTransform<TranslateTransform>(fe);
            var rise = new DoubleAnimation(14, 0, duration) { BeginTime = delay, EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
            move.BeginAnimation(TranslateTransform.YProperty, rise);
        }

        // ─── 弹窗出现 ───

        /// <summary>
        /// 加载时淡入并轻微上浮放大到位（220ms）。挂在窗口外壳模板的内容区上，
        /// 所有使用 CyberModalWindow / CyberDarkWindow 的对话框都会生效。
        /// 窗口本身不是透明窗口，没法整体淡入，所以动的是内容。
        /// </summary>
        public static readonly DependencyProperty AppearProperty = DependencyProperty.RegisterAttached(
            "Appear", typeof(bool), typeof(MotionAssist), new PropertyMetadata(false, OnAppearChanged));

        public static bool GetAppear(DependencyObject d) => (bool)d.GetValue(AppearProperty);
        public static void SetAppear(DependencyObject d, bool value) => d.SetValue(AppearProperty, value);

        private static void OnAppearChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.Loaded -= Appear_Loaded;
            if ((bool)e.NewValue) fe.Loaded += Appear_Loaded;
        }

        private static void Appear_Loaded(object sender, RoutedEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            fe.Loaded -= Appear_Loaded;   // 只在第一次显示时播放
            if (!IsMotionEnabled) return;

            var duration = TimeSpan.FromMilliseconds(220);
            fe.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
            EnsureTransform<TranslateTransform>(fe).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
            var scale = EnsureTransform<ScaleTransform>(fe);
            var grow = new DoubleAnimation(0.985, 1, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        // ─── 报错抖动 ───

        /// <summary>左右轻抖两下（320ms），用于失败提示，告诉用户"这次没成功"。</summary>
        public static void Shake(UIElement element, int delayMs = 0)
        {
            if (!IsMotionEnabled) return;
            var move = EnsureTransform<TranslateTransform>(element);
            var shake = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(320), FillBehavior = FillBehavior.Stop,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            foreach (var (ms, x) in new[] { (0, 0.0), (60, -7.0), (120, 6.0), (180, -4.0), (240, 2.0), (320, 0.0) })
                shake.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            move.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        // ─── 数字滚动 ───

        private static readonly DependencyProperty CountValueProperty = DependencyProperty.RegisterAttached(
            "CountValue", typeof(double), typeof(MotionAssist), new PropertyMetadata(0.0, OnCountValueChanged));

        private static readonly DependencyProperty CountFormatProperty = DependencyProperty.RegisterAttached(
            "CountFormat", typeof(Func<long, string>), typeof(MotionAssist));

        private static void OnCountValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is System.Windows.Controls.TextBlock tb && tb.GetValue(CountFormatProperty) is Func<long, string> format)
                tb.Text = format((long)Math.Round((double)e.NewValue));
        }

        /// <summary>
        /// 数字从 0 滚动到目标值（约 600ms，先快后慢）；系统关闭动画时直接显示终值。
        /// <paramref name="format"/> 决定显示形式，例如字节数格式化成"2.4 GB"。
        /// </summary>
        public static void CountUp(System.Windows.Controls.TextBlock target, long value, Func<long, string>? format = null, int ms = 600)
        {
            format ??= v => v.ToString();
            target.SetValue(CountFormatProperty, format);
            if (!IsMotionEnabled || value == 0)
            {
                target.BeginAnimation(CountValueProperty, null);
                target.Text = format(value);
                return;
            }
            target.BeginAnimation(CountValueProperty,
                new DoubleAnimation(0, value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
        }

        // ─── 颜色过渡（给 code-behind 里改背景色的地方用）───

        /// <summary>把元素的纯色背景平滑过渡到目标颜色；背景若是冻结的共享画刷会先换成本地实例。</summary>
        public static void AnimateBackground(System.Windows.Controls.Border border, Color to, int ms = 180)
        {
            if (border.Background is not SolidColorBrush current || current.IsFrozen)
            {
                var from = (border.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
                current = new SolidColorBrush(from);
                border.Background = current;
            }
            if (!IsMotionEnabled)
            {
                current.BeginAnimation(SolidColorBrush.ColorProperty, null);
                current.Color = to;
                return;
            }
            current.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        }

        // ─── 提示条滑入 / 收起 ───

        /// <summary>
        /// 每次滑入 / 收起递增一次。收起动画结束时对不上号，说明途中又被叫出来了，旧的收尾不再执行。
        /// </summary>
        private static readonly DependencyProperty RevealTokenProperty = DependencyProperty.RegisterAttached(
            "RevealToken", typeof(int), typeof(MotionAssist), new PropertyMetadata(0));

        internal static int CurrentRevealToken(DependencyObject host) => (int)host.GetValue(RevealTokenProperty);

        private static int NextRevealToken(DependencyObject host)
        {
            var token = CurrentRevealToken(host) + 1;
            host.SetValue(RevealTokenProperty, token);
            return token;
        }

        /// <summary>
        /// 占一行位置的提示条滑入：高度从 0 展开到内容的自然高度（320ms），同时淡入。
        /// 下面的内容被平滑推开，而不是突然往下跳一截。
        ///
        /// <para>
        /// <paramref name="host"/> 应是一个不带 Margin 的外壳并设 <c>ClipToBounds="True"</c>，间距放在里面的内容上；
        /// 里面的内容设 <c>VerticalAlignment="Bottom"</c> 时，看起来是从上方滑下来。
        /// 系统关闭动画时直接显示。
        /// </para>
        /// </summary>
        public static void Reveal(FrameworkElement host, int ms = 320)
        {
            NextRevealToken(host);   // 让还没播完的收起作废
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            host.BeginAnimation(UIElement.OpacityProperty, null);
            host.Visibility = Visibility.Visible;
            if (!IsMotionEnabled) return;

            // 先按自然尺寸排一次版，量出要展开到多高。这一帧还没画出来，用户看不到"先撑满再缩回去"
            host.UpdateLayout();
            var target = host.ActualHeight;
            if (double.IsNaN(target) || target <= 0) return;

            var duration = TimeSpan.FromMilliseconds(ms);
            // FillBehavior.Stop：播完后高度回到 Auto，之后内容变高变矮（换语言、窗口变窄）都能跟着走
            host.BeginAnimation(FrameworkElement.HeightProperty,
                new DoubleAnimation(0, target, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
            host.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        }

        /// <summary>
        /// 提示条收起：高度收回到 0 并淡出（220ms），结束后折叠，再调用 <paramref name="onHidden"/>。
        /// 系统关闭动画时立即折叠。已经是折叠状态时什么都不做（也不调用 <paramref name="onHidden"/>）。
        /// </summary>
        public static void Conceal(FrameworkElement host, Action? onHidden = null, int ms = 220)
        {
            if (host.Visibility != Visibility.Visible) return;

            var token = NextRevealToken(host);
            var from = host.ActualHeight;
            if (!IsMotionEnabled || double.IsNaN(from) || from <= 0)
            {
                FinishConceal(host, token, onHidden);
                return;
            }

            var duration = TimeSpan.FromMilliseconds(ms);
            var shrink = new DoubleAnimation(from, 0, duration) { EasingFunction = EaseIn };
            shrink.Completed += (_, _) => FinishConceal(host, token, onHidden);
            host.BeginAnimation(FrameworkElement.HeightProperty, shrink);
            host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = EaseIn });
        }

        /// <summary>
        /// 收起的收尾：折叠、撤动画、回调。<paramref name="token"/> 对不上号（收起途中又被
        /// <see cref="Reveal"/> 叫了出来）时什么都不做，返回 false。
        /// 单独拆出来是为了测试能不等动画时钟、直接核对「过期的收尾不会把刚滑出来的提示条折叠掉」。
        /// </summary>
        internal static bool FinishConceal(FrameworkElement host, int token, Action? onHidden)
        {
            if (CurrentRevealToken(host) != token) return false;

            // 先折叠、再撤动画：反过来的话，高度会在折叠前弹回原样闪一帧
            host.Visibility = Visibility.Collapsed;
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            host.BeginAnimation(UIElement.OpacityProperty, null);
            onHidden?.Invoke();
            return true;
        }

        // ─── 高光扫过 ───

        /// <summary>连扫阶段：一次扫过 900ms。</summary>
        internal const int ShineBurstSweepMs = 900;

        /// <summary>连扫阶段：两次之间歇 500ms。</summary>
        internal const int ShineBurstGapMs = 500;

        /// <summary>平静阶段：一次扫过 1300ms，比连扫时慢。</summary>
        internal const int ShineIdleSweepMs = 1300;

        /// <summary>平静阶段：高光只有连扫时的 45% 亮。</summary>
        internal const double ShineIdleOpacity = 0.45;

        /// <summary>平静阶段：每 6 秒扫一次。</summary>
        internal static readonly TimeSpan ShineIdleInterval = TimeSpan.FromSeconds(6);

        /// <summary>连扫阶段：相邻两次扫光的起点相隔 1.4 秒（扫 900ms + 歇 500ms）。</summary>
        internal static readonly TimeSpan ShineBurstInterval = TimeSpan.FromMilliseconds(ShineBurstSweepMs + ShineBurstGapMs);

        /// <summary>脉冲放大到的倍数。</summary>
        internal const double ShinePulseScale = 1.1;

        /// <summary>扫光推进一步的结果。</summary>
        /// <param name="Next">距下一步多久。</param>
        /// <param name="Swept">这一步有没有播扫光（没人看得见时不播）。</param>
        /// <param name="Pulsed">这一步有没有带脉冲（只有连扫阶段带）。</param>
        internal readonly record struct ShineStep(TimeSpan Next, bool Swept, bool Pulsed);

        private sealed class ShineState(System.Windows.Threading.DispatcherTimer timer, UIElement? pulse, int bursts)
        {
            public System.Windows.Threading.DispatcherTimer Timer { get; } = timer;
            public UIElement? Pulse { get; } = pulse;
            public int BurstsLeft { get; set; } = bursts;
        }

        private static readonly DependencyProperty ShineStateProperty = DependencyProperty.RegisterAttached(
            "ShineState", typeof(ShineState), typeof(MotionAssist));

        /// <summary>
        /// 高光扫过：一条斜着的高光从容器左侧外扫到右侧外。
        ///
        /// <para>
        /// 节奏分两段：先<b>连扫 <paramref name="bursts"/> 次</b>（每次 900ms、间歇 500ms），每次都让
        /// <paramref name="pulse"/> 轻轻放大再回弹；之后<b>降为每 6 秒一次的淡高光</b>
        /// （亮度 45%、扫得更慢、不再脉冲），直到 <see cref="StopShine"/> 或元素离开界面。
        /// </para>
        ///
        /// <para>
        /// <paramref name="band"/> 是那条高光本身：放在被裁剪的容器里、靠左对齐、<c>Opacity="0"</c>。
        /// 不扫的时候它保持透明，所以系统关闭动画时这里什么都不播，界面就是静态的。
        /// </para>
        ///
        /// <para>
        /// 两次扫光之间没有任何动画在跑：间隔由计时器掐，而不是一段一直循环的长动画——
        /// 提示条可能在界面上挂几个小时，不能为它一直占着每秒 60 次的渲染时钟。
        /// </para>
        /// </summary>
        /// <param name="band">高光元素。</param>
        /// <param name="pulse">连扫时跟着脉冲的元素（例如金额）；不需要传 null。</param>
        /// <param name="bursts">开头连扫几次。</param>
        /// <param name="delayMs">第一次扫光前等多久（等入场动效播完）。</param>
        public static void Shine(FrameworkElement band, UIElement? pulse = null, int bursts = 3, int delayMs = 0)
        {
            StopShine(band);
            if (!IsMotionEnabled) return;

            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, band.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)),
            };
            band.SetValue(ShineStateProperty, new ShineState(timer, pulse, Math.Max(0, bursts)));
            timer.Tick += (_, _) =>
            {
                if (AdvanceShine(band) is { } step) timer.Interval = step.Next;
            };
            band.Unloaded += Shine_Unloaded;
            timer.Start();
        }

        /// <summary>高光是否还在按节奏播（<see cref="Shine"/> 之后、<see cref="StopShine"/> 之前）。</summary>
        internal static bool IsShining(FrameworkElement band) => band.GetValue(ShineStateProperty) is ShineState;

        /// <summary>停掉高光：计时器、正在播的那一次扫光和脉冲一起撤掉，高光回到透明。</summary>
        public static void StopShine(FrameworkElement band)
        {
            if (band.GetValue(ShineStateProperty) is ShineState state)
            {
                state.Timer.Stop();
                band.Unloaded -= Shine_Unloaded;
                band.ClearValue(ShineStateProperty);
                if (state.Pulse != null && FindTransform<ScaleTransform>(state.Pulse) is { } scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
            }

            band.BeginAnimation(UIElement.OpacityProperty, null);
            FindTransform<TranslateTransform>(band)?.BeginAnimation(TranslateTransform.XProperty, null);
        }

        private static void Shine_Unloaded(object sender, RoutedEventArgs e) => StopShine((FrameworkElement)sender);

        /// <summary>
        /// 推进一步：该播就播一次扫光，返回这一步做了什么、距下一步多久；已经停了返回 null。
        /// 计时器每响一次调一次；单独拆出来是为了测试能不等时间、一步一步核对节奏。
        /// </summary>
        internal static ShineStep? AdvanceShine(FrameworkElement band)
        {
            if (band.GetValue(ShineStateProperty) is not ShineState state) return null;

            // 途中关掉了系统动画：到此为止，界面回到静态
            if (!IsMotionEnabled)
            {
                StopShine(band);
                return null;
            }

            var bursting = state.BurstsLeft > 0;
            var wait = bursting ? ShineBurstInterval : ShineIdleInterval;

            // 没人看得见（窗口最小化、藏到托盘、还没排出尺寸）：这一步不播，也不消耗连扫次数
            var unseen = !band.IsVisible || Window.GetWindow(band) is { WindowState: WindowState.Minimized };
            if (unseen || !PlayShineSweep(band, bursting)) return new ShineStep(wait, Swept: false, Pulsed: false);
            if (!bursting) return new ShineStep(ShineIdleInterval, Swept: true, Pulsed: false);

            state.BurstsLeft--;
            if (state.Pulse != null) PlayShinePulse(state.Pulse);
            return new ShineStep(
                state.BurstsLeft > 0 ? ShineBurstInterval : ShineIdleInterval, Swept: true, Pulsed: state.Pulse != null);
        }

        /// <summary>
        /// 一次扫光里高光的水平位移：从哪儿到哪儿（相对它靠左对齐时的原位）。
        ///
        /// <para>
        /// 高光是斜的，上下两头比中线各多探出一截；两端各多让出半个高度，
        /// 斜度不超过 45° 时，起点和终点上整条高光都在容器外面——不会开场就亮着一角，也不会收尾时留一角。
        /// </para>
        /// </summary>
        internal static (double From, double To) ShineTravel(double bandWidth, double bandHeight, double trackWidth)
        {
            var slack = bandHeight / 2;
            return (-(bandWidth + slack), trackWidth + slack);
        }

        /// <summary>高光从左侧外扫到右侧外一次；容器还没排版出尺寸时返回 false。</summary>
        private static bool PlayShineSweep(FrameworkElement band, bool strong)
        {
            if (VisualTreeHelper.GetParent(band) is not FrameworkElement track) return false;
            if (track.ActualWidth <= 0 || band.ActualWidth <= 0) return false;

            var (from, to) = ShineTravel(band.ActualWidth, band.ActualHeight, track.ActualWidth);
            var duration = TimeSpan.FromMilliseconds(strong ? ShineBurstSweepMs : ShineIdleSweepMs);
            var peak = strong ? 1.0 : ShineIdleOpacity;

            // 都用 FillBehavior.Stop：扫完自动回到透明，不留下一条停在右边的高光
            EnsureTransform<TranslateTransform>(band).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(from, to, duration) { EasingFunction = EaseInOut, FillBehavior = FillBehavior.Stop });
            band.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(peak, peak, duration) { FillBehavior = FillBehavior.Stop });
            return true;
        }

        /// <summary>
        /// 轻轻放大 10% 再回弹（620ms）。峰值卡在 280ms 前后：连扫的高光这时正扫过靠左的那块内容。
        /// </summary>
        private static void PlayShinePulse(UIElement pulse)
        {
            var beat = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(620), FillBehavior = FillBehavior.Stop,
            };
            beat.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            beat.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            beat.KeyFrames.Add(new EasingDoubleKeyFrame(ShinePulseScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)), EaseOut));
            beat.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), EaseInOut));

            var scale = EnsureTransform<ScaleTransform>(pulse);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
        }

        // ─── 变换辅助 ───

        /// <summary>取元素上已有的指定类型变换，没有就返回 null（不新建）。</summary>
        private static T? FindTransform<T>(UIElement element) where T : Transform
        {
            switch (element.RenderTransform)
            {
                case T existing when !existing.IsFrozen:
                    return existing;
                case TransformGroup group when !group.IsFrozen:
                    foreach (var child in group.Children)
                        if (child is T match && !match.IsFrozen) return match;
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 取元素上指定类型的变换；没有就建一个。已有单个变换时并入 TransformGroup，
        /// 让按压缩放、悬停上浮、入场位移可以同时作用在同一元素上。
        /// </summary>
        internal static T EnsureTransform<T>(UIElement element) where T : Transform, new()
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            switch (element.RenderTransform)
            {
                case T existing when !existing.IsFrozen:
                    return existing;
                case TransformGroup group when !group.IsFrozen:
                    foreach (var child in group.Children)
                        if (child is T match && !match.IsFrozen) return match;
                    var added = new T();
                    group.Children.Add(added);
                    return added;
                case null:
                case MatrixTransform { Matrix.IsIdentity: true }:
                    var created = new T();
                    element.RenderTransform = created;
                    return created;
                default:
                    var wrap = new TransformGroup();
                    if (!element.RenderTransform.Value.IsIdentity) wrap.Children.Add(element.RenderTransform.CloneCurrentValue());
                    var fresh = new T();
                    wrap.Children.Add(fresh);
                    element.RenderTransform = wrap;
                    return fresh;
            }
        }
    }
}
