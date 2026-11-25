using System.Globalization;
using UEModManager.Converters;

namespace UEModManager.Tests.Views;

/// <summary>
/// <see cref="LessThanConverter"/>：容器窄到一定程度就收起次要内容的触发器靠它判断。
/// 推广条在券窄于 440 时收起「立即领取」、把位置让给标题，用的就是这个。
/// </summary>
public sealed class LessThanConverterTests
{
    private static readonly LessThanConverter Converter = new();

    private static object Convert(object? value, object? parameter, string culture = "zh-CN")
        => Converter.Convert(value!, typeof(bool), parameter!, CultureInfo.GetCultureInfo(culture));

    [Theory]
    [InlineData(439.9, "440", true)]
    [InlineData(440.0, "440", false)]     // 恰好等于不算小于
    [InlineData(760.0, "440", false)]
    [InlineData(0.0, "440", true)]        // 还没排版出宽度
    [InlineData(-1.0, "0", true)]
    [InlineData(1.0, "1.5", true)]
    public void 小于参数时为真(double value, string parameter, bool expected)
        => Assert.Equal(expected, Convert(value, parameter));

    [Fact]
    public void 参数也可以直接给数字()
    {
        Assert.Equal(true, Convert(1.0, 2.0));
        Assert.Equal(false, Convert(3.0, 2.0));
    }

    [Theory]
    [InlineData("de-DE")]   // 小数点是逗号的区域
    [InlineData("fr-FR")]
    [InlineData("zh-CN")]
    public void XAML里写的小数不跟着系统区域走(string culture)
    {
        // 「1.5」在德语区域按当地习惯会被读成 15：XAML 是程序写死的，不该因为玩家的系统语言变了意思
        Assert.Equal(true, Convert(1.2, "1.5", culture));
        Assert.Equal(false, Convert(1.8, "1.5", culture));
    }

    [Theory]
    [InlineData(null, "440")]
    [InlineData("300", "440")]            // 值不是数字
    [InlineData(300, "440")]              // 值是整数而不是 double（ActualWidth 恒为 double）
    [InlineData(double.NaN, "440")]
    [InlineData(300.0, null)]
    [InlineData(300.0, "")]
    [InlineData(300.0, "宽")]
    public void 认不出来的输入一律为假_照常显示而不是把东西藏掉(object? value, object? parameter)
        => Assert.Equal(false, Convert(value, parameter));

    [Fact]
    public void 只用于单向绑定()
        => Assert.Throws<NotImplementedException>(()
            => Converter.ConvertBack(true, typeof(double), "440", CultureInfo.InvariantCulture));
}
