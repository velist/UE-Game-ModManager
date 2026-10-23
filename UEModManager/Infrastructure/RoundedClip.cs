using System.Windows;
using System.Windows.Media;

namespace UEModManager.Infrastructure
{
    /// <summary>
    /// 按圆角矩形裁剪元素内容。
    ///
    /// <para>
    /// WPF 的 <c>Border.CornerRadius</c> 只画圆角，不裁子元素：卡片里直角的预览图区域会盖住外框四角的弧线，
    /// 悬停时青色边框亮起来就露出缺角。把它挂在 Border 的直接子元素上，半径取外框圆角减去边框宽度。
    /// </para>
    /// </summary>
    public static class RoundedClip
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

        public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.SizeChanged -= OnSizeChanged;
            if ((double)e.NewValue > 0)
            {
                fe.SizeChanged += OnSizeChanged;
                Apply(fe);
            }
            else
            {
                fe.Clip = null;
            }
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

        private static void Apply(FrameworkElement fe)
        {
            if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
            var r = GetRadius(fe);
            var clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
            clip.Freeze();
            fe.Clip = clip;
        }
    }
}
