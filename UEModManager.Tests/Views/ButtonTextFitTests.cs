using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UEModManager.Localization;
using UEModManager.Services;
using UEModManager.Tests.Themes;

namespace UEModManager.Tests.Views;

/// <summary>
/// 按钮文字不被挤压回归。
///
/// 现场症状：弹窗（导入确认等）的确认按钮"样式没显示完全，文字溢出" ——
/// 文字顶到圆角上，首尾字符被圆角切掉。
///
/// 根因不在某个窗口，而在主题：<c>CyberPrimaryButton</c> / <c>CyberSecondaryButton</c>
/// 的 <c>ControlTemplate</c> 里 Border 上既没有 <c>TemplateBinding Padding</c>、
/// 样式也没有默认 Padding，于是调用方写的 <c>Padding="16,0"</c> 被静默忽略
/// （<c>CyberMessageBox.CreateButton</c> 早就写了 Padding 与 MinWidth，一直没生效）。
/// 各弹窗又把按钮宽度写成固定值（60~140），长文案与英文译文直接顶边。
///
/// 修法：模板消费 Padding + 给默认值；固定 <c>Width</c> 改 <c>MinWidth</c>，让按钮按内容撑开。
///
/// 判据：按钮实际宽度必须容得下"内容 + 内边距 + 边框"。内边距被模板丢弃时，
/// 按钮会缩到只有内容那么宽，这个不等式立刻不成立。
/// 注意**不能**把按钮的 Content 挂到探针控件上测量：内联内容（如确认按钮里的
/// StackPanel）已有父级，重新挂载会得到 0 宽度，恰好漏掉出问题的那个按钮。
/// 所以这里直接取内容在现有视觉树中的自然尺寸，用 FormattedText 量纯文本。
/// </summary>
// 英文用例会把进程级的界面语言切成英文：必须和其他改语言的测试一起独占运行。
// 放在可并行的主题集合里时，同一时刻别的测试拿到的报错文案会变成英文，
// 断言中文文案的用例（如冲突模块的"找不到 MOD"）就会偶发失败。
// 语言集合禁用了并行，也就同时满足了主题解析必须串行的要求。
[Collection(LanguageIntegrationCollection.Name)]
public sealed class ButtonTextFitTests
{
    private static string AppSource
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UEModManager.sln"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "UEModManager");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 所有窗口的按钮文字都不被挤压(bool english)
    {
        var previous = LanguageManager.IsEnglish;
        var failures = new StringBuilder();
        var examined = 0;

        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            LanguageManager.SetEnglish(english);
            try
            {
                foreach (var path in WindowPaths())
                {
                    Window window;
                    try { window = ViewResourceLoader.Load(AppSource, path); }
                    catch (Exception ex)
                    {
                        failures.AppendLine($"{path} 加载失败: {ex.Message}");
                        continue;
                    }
                    try
                    {
                        var root = ViewResourceLoader.Layout(window);
                        foreach (var button in Descendants(root).OfType<Button>())
                        {
                            if (button.Visibility != Visibility.Visible || button.ActualWidth <= 0) continue;

                            var content = ContentWidth(button);
                            if (content <= 0) continue;
                            examined++;

                            var needed = content
                                + button.Padding.Left + button.Padding.Right
                                + button.BorderThickness.Left + button.BorderThickness.Right;

                            if (needed > button.ActualWidth + 0.5)
                                failures.AppendLine(
                                    $"{path} 的按钮 '{Label(button)}' 需要 {needed:F1}px（内容 {content:F1} + 内边距 "
                                    + $"{button.Padding.Left:F0}/{button.Padding.Right:F0}），实际只有 {button.ActualWidth:F1}px");
                        }
                    }
                    finally { window.Close(); }
                }
            }
            finally { LanguageManager.SetEnglish(previous); }
            return null;
        });

        // 防止测试退化成空转：窗口加载失败或遍历失效时，上面的循环会一个按钮都不看，
        // 那样任何回归都会被"通过"。真实窗口里可检查的按钮远多于这个下限。
        Assert.True(examined >= 20, $"只检查到 {examined} 个按钮，窗口加载或遍历可能已失效");

        Assert.True(failures.Length == 0,
            $"语言={(english ? "en" : "zh")} 下按钮文字被挤压：\n{failures}");
    }

    /// <summary>内容的自然宽度，不包含内边距。不重新挂载已有父级的元素。</summary>
    private static double ContentWidth(Button button)
    {
        switch (button.Content)
        {
            case FrameworkElement element:
                // 内联内容：取它在当前视觉树中量出的自然尺寸
                return element.DesiredSize.Width;

            case string text when text.Length > 0:
                var typeface = new Typeface(
                    button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch);
                var formatted = new FormattedText(
                    text, System.Globalization.CultureInfo.CurrentCulture, button.FlowDirection,
                    typeface, button.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(button).PixelsPerDip);
                return formatted.WidthIncludingTrailingWhitespace;

            default:
                return 0;
        }
    }

    /// <summary>
    /// 扫描 Views 下的窗口与主窗口。排除 <see cref="UEModManager.Views.LoadingOverlay"/>：
    /// 它是 UserControl 不是 Window，由宿主窗口提供资源，不单独作为窗口加载。
    /// </summary>
    private static IEnumerable<string> WindowPaths()
        => Directory.GetFiles(Path.Combine(AppSource, "Views"), "*.xaml")
            .Where(path => Path.GetFileNameWithoutExtension(path) != "LoadingOverlay")
            .Select(path => "Views/" + Path.GetFileName(path))
            .Prepend("MainWindow.xaml")
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string Label(Button button) => button.Content switch
    {
        string text => text,
        Panel panel => string.Join("+", panel.Children.OfType<TextBlock>().Select(t => t.Text)),
        _ => button.Content?.GetType().Name ?? "(空)",
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}