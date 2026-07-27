using System.Globalization;
using System.Windows.Data;
using UEModManager.Views;

namespace UEModManager.Tests.Views;

/// <summary>
/// 优先级列的显示转换。原实现是 code-behind 里的 $"#{entry.Priority + 1}" 字符串拼接，
/// 迁到 DataTemplate 后由本转换器承担，行为必须逐字符一致。
/// </summary>
public class PriorityDisplayConverterTests
{
    private readonly PriorityDisplayConverter _converter = new();

    [Theory]
    [InlineData(0, "#1")]
    [InlineData(1, "#2")]
    [InlineData(9, "#10")]
    [InlineData(98, "#99")]
    public void Convert_Priority_IsOneBased(int priority, string expected)
    {
        Assert.Equal(expected, _converter.Convert(priority, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Convert_NonInteger_ReturnsEmpty()
    {
        // 绑定在 SelectedProfile 为空等时机可能拿到 null/占位值，不能抛异常
        Assert.Equal(string.Empty, _converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, _converter.Convert("x", typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_IsNoOp()
    {
        // 单向显示用，不参与回写
        Assert.Equal(Binding.DoNothing,
            _converter.ConvertBack("#1", typeof(int), null!, CultureInfo.InvariantCulture));
    }
}
