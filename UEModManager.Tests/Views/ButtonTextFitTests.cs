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

    /// <remarks>
    /// 缩放比例由用例自己钉死（<see cref="VisualTreeHelper.SetRootDpi"/>），不跟着跑测试的机器走。
    /// 以前不钉：没挂到窗口句柄上的视觉树用的是进程启动时的系统缩放，同一份代码在 100% 的屏幕上通过、
    /// 切到 125%（比如换了远程桌面的虚拟显示器）就失败 —— 布局取整到设备像素后，按钮比
    /// 「理想文字宽度 + 内边距」窄不到 2 个设备像素，而原来的容差是固定的 0.5。
    /// 现在 100% 仍按 0.5 卡；其它缩放放宽到 2 个设备像素（左右内边距、边框、总宽各自取整的上限）。
    /// 本用例要拦的是「内边距被模板整个丢掉」，那是几十像素的差距，这个容差放不过它。
    ///
    /// 钉要钉两遍（<see cref="LayoutAtPinnedScale"/>）：只在布局前钉一次的话，按钮模板里的边框和文字
    /// 那时还没生成，生成之后用的仍是进程的系统缩放，机器不是 100% 时 100% 这一档照样翻红。
    /// </remarks>
    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 1.25)]
    [InlineData(true, 1.25)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.5)]
    public void 所有窗口的按钮文字都不被挤压(bool english, double scale)
    {
        var tolerance = scale == 1.0 ? 0.5 : 2.0 / scale;
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
                        var root = LayoutAtPinnedScale(window, scale);
                        foreach (var button in Descendants(root).OfType<Button>())
                        {
                            if (button.Visibility != Visibility.Visible || button.ActualWidth <= 0) continue;

                            var content = ContentWidth(button);
                            if (content <= 0) continue;
                            examined++;

                            var needed = content
                                + button.Padding.Left + button.Padding.Right
                                + button.BorderThickness.Left + button.BorderThickness.Right;

                            if (needed > button.ActualWidth + tolerance)
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
            $"语言={(english ? "en" : "zh")}、缩放 {scale:P0} 下按钮文字被挤压：\n{failures}");
    }

    /// <summary>
    /// 按钉死的缩放比例把窗口内容排一遍版，<b>整棵视觉树</b>都用这个比例，不只是根节点。
    ///
    /// <para>
    /// <see cref="VisualTreeHelper.SetRootDpi"/> 只给调用那一刻已经存在的元素打标记。
    /// 按钮模板里的边框、文字要到第一次布局才生成；而新生成的子元素只有在进程开启了
    /// 「按显示器缩放」时才继承父元素的比例（<c>Visual.AddVisualChild</c>）——测试宿主没有开，
    /// 它们用的是进程的系统缩放。于是机器是 125% 时，钉成 100% 的按钮里装着按 125% 取整的边框：
    /// 1px 的边框取整成 0.8，按钮比「文字 + 内边距 + 边框」窄 1px，超过 100% 这一档 0.5 的容差。
    /// 同一批用例单独跑能过（那时进程还没被别的用例变成感知缩放的，系统缩放读到的是 100%），
    /// 跟着全量跑就翻红。
    /// </para>
    ///
    /// <para>
    /// 所以排完第一遍、模板都展开之后再钉一遍，整棵树重新量。先换成另一个比例再换回来：
    /// 比例没变的话 WPF 不会通知各元素（<c>OnDpiChanged</c>），缓存着旧比例的元素不会跟着变。
    /// 重排可能又展开出新的元素，重复到元素数不再变化为止（实际一轮就稳定，上限三轮）。
    /// </para>
    /// </summary>
    private static FrameworkElement LayoutAtPinnedScale(Window window, double scale)
    {
        var content = (Visual)window.Content;
        var pinned = new DpiScale(scale, scale);
        VisualTreeHelper.SetRootDpi(content, pinned);
        var root = ViewResourceLoader.Layout(window);

        for (var pass = 0; pass < 3; pass++)
        {
            var before = Descendants(content).Count();
            VisualTreeHelper.SetRootDpi(content, new DpiScale(scale * 2, scale * 2));
            VisualTreeHelper.SetRootDpi(content, pinned);
            foreach (var element in Descendants(content).Prepend(content).OfType<UIElement>())
                element.InvalidateMeasure();
            root = ViewResourceLoader.Layout(window);
            if (Descendants(content).Count() == before) break;
        }

        return root;
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
    /// 扫描 Views 下的窗口与主窗口。排除 <see cref="UEModManager.Views.LoadingOverlay"/> 与 <see cref="UEModManager.Views.OnboardingOverlay"/>：
    /// 它们是 UserControl 不是 Window，由宿主窗口提供资源，不单独作为窗口加载。
    /// </summary>
    private static IEnumerable<string> WindowPaths()
        => Directory.GetFiles(Path.Combine(AppSource, "Views"), "*.xaml")
            .Where(path => Path.GetFileNameWithoutExtension(path) is not ("LoadingOverlay" or "OnboardingOverlay"))
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