using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using UEModManager.Tests.Themes;

namespace UEModManager.Tests.Views;

/// <summary>
/// 圆角矩形按钮的圆角统一为 8px 的回归。
///
/// 现场反馈："所有的圆角矩形按钮，圆角统一为 8px"。
/// 修复前同一屏上的按钮圆角有 6 / 7 / 10 / 14 四种：主工具栏是 14、
/// 侧栏导航是 10、视图切换是 6，启动中心与设置同理 —— 视觉上像四套不同的控件。
///
/// 这里锁两件事：
/// 1. 主题里的按钮样式全部指向 <c>RadiusButton</c> 令牌（单一真源，改一处全局生效）；
/// 2. 窗口里"按钮角色"的可点击圆角矩形不再写死其它数值。
/// 圆形按钮（圆角 = 边长一半，如头像、圆形图标钮）与卡片容器不在约束范围内 ——
/// 它们不是"圆角矩形按钮"，改成 8 反而会变成方块。
/// </summary>
[Collection(ThemeResourceCollection.Name)]
public sealed class ButtonCornerRadiusTests
{
    /// <summary>按钮圆角的唯一取值。</summary>
    private const int Expected = 8;

    private static string AppSource
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UEModManager.sln"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "UEModManager");
        }
    }

    [Fact]
    public void 主题令牌里的按钮圆角就是8()
    {
        // 取值必须走合并后的字典：Body 里引用的令牌解析不到会直接抛。
        var capture = ThemeResourceLoader.CaptureMerged();
        Assert.True(capture.Values.TryGetValue("RadiusButton", out var rendered),
            "主题里缺少 RadiusButton 令牌");
        Assert.Equal($"Radius {Expected},{Expected},{Expected},{Expected}", rendered);
    }

    [Theory]
    [InlineData("CyberPrimaryButton")]
    [InlineData("CyberSecondaryButton")]
    [InlineData("CyberGhostButton")]
    [InlineData("CyberCompactButton")]
    public void 按钮样式的模板圆角取自RadiusButton令牌(string styleKey)
    {
        var styles = File.ReadAllText(Path.Combine(AppSource, "Themes", "CyberStyles.xaml"));
        var style = Regex.Match(styles,
            $@"x:Key=""{styleKey}""(.*?)(?=<Style x:Key|\z)", RegexOptions.Singleline);

        Assert.True(style.Success, $"找不到样式 {styleKey}");
        Assert.Contains("CornerRadius=\"{StaticResource RadiusButton}\"", style.Value);
    }

    [Fact]
    public void 窗口里的圆角矩形按钮不再写死其它圆角()
    {
        var failures = new StringBuilder();
        var examined = 0;

        foreach (var path in Directory.GetFiles(Path.Combine(AppSource, "Views"), "*.xaml")
                     .Append(Path.Combine(AppSource, "MainWindow.xaml")))
        {
            var text = File.ReadAllText(path);
            foreach (Match m in Regex.Matches(text, @"<Border\b[^>]*>", RegexOptions.Singleline))
            {
                var block = m.Value;
                // 按钮角色：可点击（有鼠标/拖放处理器）+ 手型光标 + 指定了子元素
                if (!Regex.IsMatch(block, @"Cursor=""Hand""")) continue;
                if (!Regex.IsMatch(block, @"(MouseLeftButtonDown|MouseDown|MouseUp|Drop|Click)=")) continue;

                var radius = Regex.Match(block, @"CornerRadius=""(\d+)""");
                if (!radius.Success) continue;

                // 排除"看起来可点击、但不是按钮"的容器 —— 改成 8px 是视觉回退，不是统一：
                //  · 正方形框（宽=高）：图标/头像/缩略图预览框，含 GameIconBorder、圆形按钮
                //  · 卡片：名称含 Card，MOD 卡片与方案卡片的外观成套，不随按钮走
                var width = Regex.Match(block, @"Width=""(\d+)""");
                var height = Regex.Match(block, @"Height=""(\d+)""");
                if (width.Success && height.Success && width.Groups[1].Value == height.Groups[1].Value) continue;
                if (Regex.IsMatch(block, @"x:Name=""[^""]*(Card|Icon|Avatar|Badge|Thumbnail)[^""]*""")) continue;

                var value = int.Parse(radius.Groups[1].Value);
                examined++;
                if (value != Expected)
                    failures.AppendLine($"{Path.GetFileName(path)}: {value}px —— {block.Replace("\n", " ").Trim()}");
            }
        }

        // 防止退化成空转：真实界面里这类按钮有二十多个。
        Assert.True(examined >= 15, $"只检查到 {examined} 个按钮角色元素，扫描可能已失效");
        Assert.True(failures.Length == 0, $"以下按钮的圆角不是 {Expected}px：\n{failures}");
    }
}
