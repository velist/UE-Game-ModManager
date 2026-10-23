using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UEModManager.Infrastructure;
using UEModManager.Localization;

namespace UEModManager.Views
{
    /// <summary>引导的一步：圈出哪个控件、说什么（未翻译源串）。</summary>
    public sealed record GuideStep(FrameworkElement Target, string Title, string Body);

    /// <summary>
    /// 首次使用引导：聚光灯 + 说明卡，带用户走一遍主流程。
    ///
    /// <para>
    /// 只负责"怎么演示"，不决定"什么时候演示"——是否已看过由调用方用
    /// <c>UiPreferences.LoadOnboardingGuideShown</c> 判断，结束时触发 <see cref="Finished"/>。
    /// </para>
    /// </summary>
    public partial class OnboardingOverlay : UserControl
    {
        private IReadOnlyList<GuideStep> _steps = Array.Empty<GuideStep>();
        private int _index;

        private const double HolePadding = 6;
        private static readonly Duration MoveDuration = TimeSpan.FromMilliseconds(320);

        /// <summary>看完或跳过时触发；参数 true 表示走完全部步骤，false 表示中途跳过。</summary>
        public event Action<bool>? Finished;

        public OnboardingOverlay()
        {
            InitializeComponent();
            SizeChanged += (_, _) => { if (IsVisible) Layout(animate: false); };
            KeyDown += OnKeyDown;
        }

        public void Start(IReadOnlyList<GuideStep> steps)
        {
            _steps = steps;
            _index = -1;
            Visibility = Visibility.Visible;
            Opacity = 1;
            if (MotionAssist.IsMotionEnabled)
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            Advance(animate: false);
            // 首次定位放到布局完成之后（此刻自身尺寸还是 0，Layout 会直接返回）
            Dispatcher.BeginInvoke(() => { Layout(animate: false); Focus(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void Advance(bool animate)
        {
            // 跳过当前不可见的目标（例如窗口太窄被挤没的按钮），不能把聚光灯打到空处
            do { _index++; }
            while (_index < _steps.Count && !IsShowable(_steps[_index].Target));

            if (_index >= _steps.Count)
            {
                Finish(completed: true);
                return;
            }

            var step = _steps[_index];
            StepNumber.Text = (_index + 1).ToString();
            StepCounter.Text = UiText.Interpolate($"第 {_index + 1} 步，共 {_steps.Count} 步");
            StepTitle.Text = UiText.Get(step.Title);
            StepBody.Text = UiText.Get(step.Body);
            NextButton.Content = UiText.Get(_index == _steps.Count - 1 ? "开始使用" : "下一步");
            SkipButton.Visibility = _index == _steps.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
            Layout(animate);
        }

        private static bool IsShowable(FrameworkElement target)
            => target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0;

        /// <summary>按当前步骤的目标重新摆放洞、描边与说明卡。</summary>
        private void Layout(bool animate)
        {
            // 刚从 Collapsed 变为可见的那一刻还没布局，宽高为 0：此时求出的洞是 Rect.Empty（坐标为 ∞），
            // 拿去设 Canvas.Left 会抛异常。等 SizeChanged 带着真实尺寸再来。
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            FullRect.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            if (_index < 0 || _index >= _steps.Count) return;

            var target = _steps[_index].Target;
            if (!IsShowable(target)) return;
            var topLeft = target.TranslatePoint(new Point(0, 0), this);
            var hole = new Rect(topLeft.X - HolePadding, topLeft.Y - HolePadding,
                target.ActualWidth + HolePadding * 2, target.ActualHeight + HolePadding * 2);
            hole.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
            if (hole.IsEmpty) return;

            animate &= MotionAssist.IsMotionEnabled;
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (animate) Hole.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(hole, MoveDuration) { EasingFunction = ease });
            else { Hole.BeginAnimation(RectangleGeometry.RectProperty, null); Hole.Rect = hole; }

            MoveTo(HoleRing, hole.X, hole.Y, animate, ease);
            Size(HoleRing, hole.Width, hole.Height, animate, ease);

            // 说明卡：右侧 → 左侧 → 下方，最后夹在窗口内
            Card.Measure(new Size(Card.Width, double.PositiveInfinity));
            var cardW = Card.Width;
            var cardH = Card.DesiredSize.Height;
            const double gap = 18, edge = 16;
            double x, y;
            if (hole.Right + gap + cardW + edge <= ActualWidth) { x = hole.Right + gap; y = hole.Top; }
            else if (hole.Left - gap - cardW - edge >= 0) { x = hole.Left - gap - cardW; y = hole.Top; }
            else { x = hole.Left; y = hole.Bottom + gap; }
            if (y + cardH + edge > ActualHeight) y = Math.Max(edge, hole.Top - gap - cardH);
            x = Math.Clamp(x, edge, Math.Max(edge, ActualWidth - cardW - edge));
            y = Math.Clamp(y, edge, Math.Max(edge, ActualHeight - cardH - edge));
            MoveTo(Card, x, y, animate, ease);

            if (animate)
            {
                Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(280)));
            }
        }

        private static void MoveTo(UIElement e, double x, double y, bool animate, IEasingFunction ease)
        {
            if (animate)
            {
                e.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, MoveDuration) { EasingFunction = ease });
                e.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y, MoveDuration) { EasingFunction = ease });
            }
            else
            {
                e.BeginAnimation(Canvas.LeftProperty, null);
                e.BeginAnimation(Canvas.TopProperty, null);
                Canvas.SetLeft(e, x);
                Canvas.SetTop(e, y);
            }
        }

        private static void Size(FrameworkElement e, double w, double h, bool animate, IEasingFunction ease)
        {
            if (animate)
            {
                e.BeginAnimation(WidthProperty, new DoubleAnimation(w, MoveDuration) { EasingFunction = ease });
                e.BeginAnimation(HeightProperty, new DoubleAnimation(h, MoveDuration) { EasingFunction = ease });
            }
            else
            {
                e.BeginAnimation(WidthProperty, null);
                e.BeginAnimation(HeightProperty, null);
                e.Width = w;
                e.Height = h;
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e) => Advance(animate: true);

        private void Skip_Click(object sender, RoutedEventArgs e) => Finish(completed: false);

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Finish(completed: false); e.Handled = true; }
            else if (e.Key == Key.Enter) { Advance(animate: true); e.Handled = true; }
        }

        private void Finish(bool completed)
        {
            if (Visibility != Visibility.Visible) return;
            _index = _steps.Count;
            if (MotionAssist.IsMotionEnabled)
            {
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
                fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
                BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                Visibility = Visibility.Collapsed;
            }
            Finished?.Invoke(completed);
        }
    }
}
