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
    ///
    /// <para>
    /// 还能在圆角矩形上再切一对<b>票根缺口</b>（<see cref="NotchOffsetProperty"/> +
    /// <see cref="NotchRadiusProperty"/>）：上下边各一个半圆，优惠券的打孔线两头就是它。
    /// 缺口是真的切掉，透出来的是元素后面的东西，而不是拿背景色画一个圆盖上去——
    /// 后者在用户换了背景图之后会露馅。
    /// </para>
    /// </summary>
    public static class RoundedClip
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

        public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

        /// <summary>
        /// 票根缺口的圆心离左边多远。与 <see cref="NotchRadiusProperty"/> 都大于 0、
        /// 且 <see cref="RadiusProperty"/> 已设置时才生效。
        /// </summary>
        public static readonly DependencyProperty NotchOffsetProperty = DependencyProperty.RegisterAttached(
            "NotchOffset", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnNotchChanged));

        public static double GetNotchOffset(DependencyObject d) => (double)d.GetValue(NotchOffsetProperty);
        public static void SetNotchOffset(DependencyObject d, double value) => d.SetValue(NotchOffsetProperty, value);

        /// <summary>票根缺口的半径。圆心压在上、下边线上，所以切进去的是半圆。</summary>
        public static readonly DependencyProperty NotchRadiusProperty = DependencyProperty.RegisterAttached(
            "NotchRadius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnNotchChanged));

        public static double GetNotchRadius(DependencyObject d) => (double)d.GetValue(NotchRadiusProperty);
        public static void SetNotchRadius(DependencyObject d, double value) => d.SetValue(NotchRadiusProperty, value);

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

        private static void OnNotchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // 缺口只是圆角裁剪上的附加形状：没开圆角裁剪时不单独生效
            if (d is FrameworkElement fe && GetRadius(fe) > 0) Apply(fe);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

        private static void Apply(FrameworkElement fe)
        {
            if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
            var clip = BuildClip(new Size(fe.ActualWidth, fe.ActualHeight), GetRadius(fe), GetNotchOffset(fe), GetNotchRadius(fe));
            clip.Freeze();
            fe.Clip = clip;
        }

        /// <summary>
        /// 裁剪形状：圆角矩形，可选再减去上下两个缺口。
        /// 缺口圆心落在元素宽度之外时不切（切了也只是在边角上啃一口，不是票根）。
        /// </summary>
        internal static Geometry BuildClip(Size size, double radius, double notchOffset, double notchRadius)
        {
            var body = new RectangleGeometry(new Rect(size), radius, radius);
            if (notchRadius <= 0 || notchOffset <= 0 || notchOffset >= size.Width) return body;

            var notches = new GeometryGroup { FillRule = FillRule.Nonzero };
            notches.Children.Add(new EllipseGeometry(new Point(notchOffset, 0), notchRadius, notchRadius));
            notches.Children.Add(new EllipseGeometry(new Point(notchOffset, size.Height), notchRadius, notchRadius));
            return new CombinedGeometry(GeometryCombineMode.Exclude, body, notches);
        }
    }
}
