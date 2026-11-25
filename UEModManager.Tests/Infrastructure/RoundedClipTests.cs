using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UEModManager.Infrastructure;
using UEModManager.Tests.Themes;

namespace UEModManager.Tests.Infrastructure;

/// <summary>
/// <see cref="RoundedClip"/> 的裁剪形状：圆角矩形，以及推广优惠券用的那对票根缺口。
///
/// <para>
/// 缺口必须是<b>真的切掉</b>（裁剪形状里没有那两块），而不是拿背景色画个圆盖上去——
/// 玩家换了背景图之后，后者会露出两个深色的圆。
/// </para>
/// </summary>
public sealed class RoundedClipTests
{
    private static readonly Size Coupon = new(760, 72);

    [Fact]
    public void 不带缺口时就是一个圆角矩形()
    {
        var clip = RoundedClip.BuildClip(new Size(200, 80), radius: 12, notchOffset: 0, notchRadius: 0);

        var rectangle = Assert.IsType<RectangleGeometry>(clip);
        Assert.Equal(new Rect(0, 0, 200, 80), rectangle.Rect);
        Assert.Equal(12, rectangle.RadiusX);
        Assert.Equal(12, rectangle.RadiusY);

        Assert.True(clip.FillContains(new Point(100, 40)));
        Assert.False(clip.FillContains(new Point(1, 1)));       // 圆角外侧
        Assert.False(clip.FillContains(new Point(199, 79)));
        Assert.True(clip.FillContains(new Point(6, 6)));        // 圆角内侧
    }

    [Fact]
    public void 带缺口时_上下边各切掉一个半圆_其余部分原样保留()
    {
        var clip = RoundedClip.BuildClip(Coupon, radius: 12, notchOffset: 112, notchRadius: 7);

        // 上缺口（圆心在上边线上，半径 7）
        Assert.False(clip.FillContains(new Point(112, 1)));
        Assert.False(clip.FillContains(new Point(112, 6)));
        Assert.False(clip.FillContains(new Point(107, 3)));
        Assert.False(clip.FillContains(new Point(117, 3)));
        // 下缺口
        Assert.False(clip.FillContains(new Point(112, 71)));
        Assert.False(clip.FillContains(new Point(112, 66)));
        Assert.False(clip.FillContains(new Point(107, 69)));

        // 半径之外马上又是券面：缺口正下方、缺口两侧的边缘、两个缺口之间
        Assert.True(clip.FillContains(new Point(112, 8)));
        Assert.True(clip.FillContains(new Point(112, 64)));
        Assert.True(clip.FillContains(new Point(104, 1)));
        Assert.True(clip.FillContains(new Point(120, 1)));
        Assert.True(clip.FillContains(new Point(104, 71)));
        Assert.True(clip.FillContains(new Point(120, 71)));
        Assert.True(clip.FillContains(new Point(112, 36)));

        // 四个圆角还在
        Assert.False(clip.FillContains(new Point(1, 1)));
        Assert.False(clip.FillContains(new Point(759, 1)));
        Assert.False(clip.FillContains(new Point(1, 71)));
        Assert.False(clip.FillContains(new Point(759, 71)));
        Assert.True(clip.FillContains(new Point(700, 36)));

        // 外轮廓没变大也没变小
        AssertBounds(new Rect(Coupon), clip.Bounds);
    }

    [Theory]
    [InlineData(0, 7)]       // 没给位置
    [InlineData(112, 0)]     // 没给半径
    [InlineData(112, -3)]
    [InlineData(-10, 7)]
    [InlineData(760, 7)]     // 圆心压在右边线上：切了也只是在角上啃一口，不是票根
    [InlineData(900, 7)]     // 圆心在元素外面
    public void 缺口参数不成立时不切(double notchOffset, double notchRadius)
    {
        var clip = RoundedClip.BuildClip(Coupon, radius: 12, notchOffset, notchRadius);

        Assert.IsType<RectangleGeometry>(clip);
        Assert.True(clip.FillContains(new Point(112, 3)));
    }

    [Fact]
    public void 挂到元素上_排出尺寸后裁剪生效_尺寸和参数变了跟着变()
        => ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            var element = new Border();
            RoundedClip.SetRadius(element, 12);
            RoundedClip.SetNotchOffset(element, 112);
            RoundedClip.SetNotchRadius(element, 7);
            Assert.Null(element.Clip);   // 还没有尺寸：没什么可裁的

            Layout(element, new Size(760, 72));
            Assert.NotNull(element.Clip);
            Assert.True(element.Clip.IsFrozen);
            AssertBounds(new Rect(0, 0, 760, 72), element.Clip.Bounds);
            Assert.False(element.Clip.FillContains(new Point(112, 3)));
            Assert.True(element.Clip.FillContains(new Point(300, 3)));

            // 窗口变窄，券跟着变窄：裁剪形状跟着走，缺口位置不变（离左边仍是 112）
            Layout(element, new Size(344, 72));
            AssertBounds(new Rect(0, 0, 344, 72), element.Clip.Bounds);
            Assert.False(element.Clip.FillContains(new Point(112, 69)));
            Assert.False(element.Clip.FillContains(new Point(343, 1)));   // 右上角的圆角跟到了新的右边

            // 去掉缺口：回到普通的圆角矩形
            RoundedClip.SetNotchRadius(element, 0);
            Assert.IsType<RectangleGeometry>(element.Clip);
            Assert.True(element.Clip.FillContains(new Point(112, 3)));

            // 去掉圆角裁剪：整个不裁了
            RoundedClip.SetRadius(element, 0);
            Assert.Null(element.Clip);
            return null;
        });

    [Fact]
    public void 只给缺口不给圆角_不单独生效()
        => ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            var element = new Border();
            RoundedClip.SetNotchOffset(element, 112);
            RoundedClip.SetNotchRadius(element, 7);

            Layout(element, new Size(760, 72));

            // 缺口只是圆角裁剪上的附加形状；原来只用 Radius 的卡片不会因为多了这两个属性而变样
            Assert.Null(element.Clip);
            return null;
        });

    /// <summary>几何运算（圆角矩形减去两个圆）会带出 1e-6 量级的浮点误差，按千分之一像素比。</summary>
    private static void AssertBounds(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }

    private static void Layout(FrameworkElement element, Size size)
    {
        element.Width = size.Width;
        element.Height = size.Height;
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }
}
