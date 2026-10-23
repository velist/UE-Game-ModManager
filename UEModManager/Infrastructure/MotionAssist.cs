using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UEModManager.Infrastructure
{
    /// <summary>
    /// 界面动效的附加属性集合：按压回弹、悬停上浮、列表入场。
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
        public static bool IsMotionEnabled => SystemParameters.ClientAreaAnimation;

        internal static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
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

        // ─── 变换辅助 ───

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
