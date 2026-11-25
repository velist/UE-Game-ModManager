using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using UEModManager.Services;
using UEModManager.Tests.Infrastructure;
using UEModManager.Tests.Services;

namespace UEModManager.Tests.Views;

/// <summary>
/// 「刷新」按钮：在不在该在的位置、接没接上线、窄窗口里会不会把别的控件挤出去。
///
/// <para>
/// 接线用源码断言：按钮在 XAML 里、处理器在分部文件里、F5 在主窗口的按键处理里，
/// 三处少了哪一处，程序都不会报错，只会表现成"点了没反应"。
/// </para>
/// </summary>
// 布局用例会切换进程级的界面语言，必须和其他改语言的测试一起独占运行
[Collection(LanguageIntegrationCollection.Name)]
public sealed class RefreshButtonTests : IDisposable
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly bool _previous = LanguageManager.IsEnglish;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "refresh-button-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        LanguageManager.SetEnglish(_previous);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private static string AppSource => LanguageIntegrationTests.AppSource;

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { AppSource }.Concat(parts).ToArray()));

    // ═══════════════════════════════════════
    //  接线
    // ═══════════════════════════════════════

    [Fact]
    public void Button_SitsInTheListToolbarNextToSelectAllAndSort()
    {
        var document = XDocument.Load(Path.Combine(AppSource, "MainWindow.xaml"));
        var button = Assert.Single(document.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "RefreshModsBtn");

        // 和「全选」「排序」在同一组里：这是 MOD 列表自己的工具栏
        var toolbar = button.Parent!;
        Assert.Contains(toolbar.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "SelectAllText");
        Assert.Contains(toolbar.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "SortText");

        Assert.Equal("RefreshMods_Click", (string?)button.Attribute("MouseLeftButtonDown"));
        Assert.Equal("{loc:Text '重新检查 MOD 文件是否还在（F5）'}", (string?)button.Attribute("ToolTip"));
        Assert.Equal("Hand", (string?)button.Attribute("Cursor"));
        var label = Assert.Single(button.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "RefreshModsText");
        Assert.Equal("{loc:Text '刷新'}", (string?)label.Attribute("Text"));
        // 进行中的状态绑在视图模型上，不靠代码隐藏去改按钮
        Assert.Contains("{Binding IsRefreshingFromDisk}", button.ToString());
    }

    [Fact]
    public void ClickAndF5_GoThroughTheSameGuardedEntryPoint()
    {
        var refresh = Source("MainWindow.Refresh.cs");
        var main = Source("MainWindow.xaml.cs");

        // 点击与 F5 最后都落到 StartDiskRefresh，它包在 SafeEvent.Run 里
        Assert.Matches(@"void RefreshMods_Click\(object sender, MouseButtonEventArgs e\)\s*\{[^}]*StartDiskRefresh\(\);", refresh);
        Assert.Matches(@"bool HandleRefreshShortcut\(KeyEventArgs e\)\s*\{[^}]*Key\.F5[^}]*StartDiskRefresh\(\);", refresh);
        Assert.Matches(@"void StartDiskRefresh\(\)\s*=>\s*SafeEvent\.Run\(", refresh);
        Assert.Contains("_vm.RefreshFromDiskAsync(", refresh);
        Assert.DoesNotContain("async void", refresh);

        // 主窗口的按键处理里接了 F5，构造函数里接了初始化
        var keyDown = Regex.Match(main, @"private void MainWindow_KeyDown\(object sender, KeyEventArgs e\).*?\n        \}",
            RegexOptions.Singleline);
        Assert.True(keyDown.Success, "找不到 MainWindow_KeyDown");
        Assert.Contains("HandleRefreshShortcut(e)", keyDown.Value);
        Assert.Contains("InitializeDiskRefresh();", main);
    }

    [Fact]
    public void RefreshFlow_NeverDeletesFilesFromTheView()
    {
        // 界面这一层只负责弹框和显示：删什么、留什么全部在视图模型和服务里决定（那里有测试）
        var refresh = Source("MainWindow.Refresh.cs");

        foreach (var forbidden in new[] { "File.Delete", "Directory.Delete", "DeletePackage", "ReleaseDeployedFiles" })
            Assert.DoesNotContain(forbidden, refresh);
    }

    // ═══════════════════════════════════════
    //  布局
    // ═══════════════════════════════════════

    [Theory]
    [InlineData(1440, false, true)]
    [InlineData(1440, true, true)]
    [InlineData(1280, false, true)]
    [InlineData(1280, true, true)]
    [InlineData(1024, false, false)]
    [InlineData(1024, true, false)]
    public void Button_FitsEverySupportedWidth_AndDropsItsLabelOnlyWhenTheWindowIsNarrow(
        int width, bool english, bool labelVisible)
    {
        StaApplication.Run(() =>
        {
            LanguageManager.SetEnglish(english);
            var window = ViewResourceLoader.Load(AppSource, "MainWindow.xaml");
            try
            {
                window.Width = width;
                var root = ViewResourceLoader.Layout(window);
                // 文字的显隐跟着这一排排出来的宽度走：第一遍排完才知道宽度，再排一遍才是最终样子
                root.UpdateLayout();
                var button = (FrameworkElement)window.FindName("RefreshModsBtn");
                var label = (TextBlock)window.FindName("RefreshModsText");
                var sort = (FrameworkElement)window.FindName("SortText");

                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Width > 0 && bounds.Height > 0, "刷新按钮没有显示出来");
                Assert.True(bounds.Left >= 256 && bounds.Right <= root.ActualWidth + 1,
                    $"{width}px 下刷新按钮超出了内容区：{bounds}");

                Assert.Equal(labelVisible ? Visibility.Visible : Visibility.Collapsed, label.Visibility);
                Assert.Equal(english ? "Refresh" : "刷新", label.Text);
                if (!labelVisible)
                    // 只剩图标：和旁边的图标按钮一样是个小方块，不占一个文字按钮的位置
                    Assert.True(bounds.Width < 40, $"收起文字后按钮仍有 {bounds.Width:F0}px 宽");

                // 不和排序按钮叠在一起
                var sortBounds = sort.TransformToAncestor(root).TransformBounds(new Rect(sort.RenderSize));
                Assert.True(bounds.Right <= sortBounds.Left, $"刷新按钮压到了排序按钮上：{bounds} / {sortBounds}");

                // 鼠标停上去有完整说明（窄窗口里只剩图标时全靠它）
                Assert.Equal(
                    english ? "Check again whether your mod files are still there (F5)" : "重新检查 MOD 文件是否还在（F5）",
                    button.ToolTip);
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Button_IsGreyedOutAndIgnoresClicksWhileARefreshIsRunning()
    {
        StaApplication.Run(() =>
        {
            var window = ViewResourceLoader.Load(AppSource, "MainWindow.xaml");
            try
            {
                var state = new RefreshState();
                window.DataContext = state;
                ViewResourceLoader.Layout(window);
                var button = (FrameworkElement)window.FindName("RefreshModsBtn");
                Assert.Equal(1.0, button.Opacity);
                Assert.True(button.IsHitTestVisible);

                state.IsRefreshingFromDisk = true;
                window.DataContext = null;
                window.DataContext = state;
                ViewResourceLoader.Layout(window);

                Assert.True(button.Opacity < 0.6);
                Assert.False(button.IsHitTestVisible);
                return true;
            }
            finally { window.Close(); }
        });
    }

    private sealed class RefreshState
    {
        public bool IsRefreshingFromDisk { get; set; }
    }

    // ═══════════════════════════════════════
    //  整窗渲染（也是人工看图的出口）
    // ═══════════════════════════════════════

    /// <summary>
    /// 把主窗口连同几张 MOD 卡片渲染成位图，确认刷新按钮真的画出来了。
    ///
    /// <para>
    /// 设了环境变量 <c>UEMM_PREVIEW_DIR</c> 时，同时把图存成
    /// <c>refresh-button-zh.png</c> / <c>refresh-button-en.png</c>，供人工看按钮实际长什么样：
    /// <code>UEMM_PREVIEW_DIR=D:\somewhere dotnet test --filter "FullyQualifiedName~RefreshButtonTests"</code>
    /// 数据全部来自临时目录里的一套假仓库，不读任何真实的 MOD 库。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, "refresh-button-zh.png")]
    [InlineData(true, "refresh-button-en.png")]
    public async Task MainWindow_RendersWithTheRefreshButtonVisible(bool english, string fileName)
    {
        var host = await StaApplication.RunAsync(async () =>
        {
            var opened = await DiskRefreshHost.OpenAsync(_root);
            await opened.AddPackageAsync("Character_Outfit_P", ("Character_Outfit_P.bin", new string('A', 4096)));
            await opened.AddPackageAsync("Weapon_Glow_P", ("Weapon_Glow_P.bin", new string('B', 2048)));
            await opened.AddPackageAsync("HUD_Clean_P", ("HUD_Clean_P.bin", new string('C', 1024)));
            await opened.EnableAsync("Character_Outfit_P", "Weapon_Glow_P");
            return opened;
        });

        var (pixels, buttonBounds, scale) = StaApplication.Run(() =>
        {
            LanguageManager.SetEnglish(english);
            var window = ViewResourceLoader.Load(AppSource, "MainWindow.xaml");
            try
            {
                window.DataContext = host.Main;
                // 没有代码隐藏：把它平时会填的几处文字和卡片尺寸补上，图才像真的
                ((TextBlock)window.FindName("CurrentGameName")).Text = english ? "Sample Game" : "示例游戏";
                ((TextBlock)window.FindName("NavAllCount")).Text = "3";
                ((TextBlock)window.FindName("NavEnabledCount")).Text = "2";
                ((TextBlock)window.FindName("NavDisabledCount")).Text = "1";
                var root = ViewResourceLoader.Layout(window);
                // 窗口的深色底画在窗口自己身上，这里渲染的是窗口内容：把底色铺到内容上，
                // 否则半透明的按钮和浅色文字会落在一张透明（看图软件里是白色）的底上，完全不是实际的样子
                if (root is Panel { Background: null } panel) panel.Background = window.Background;
                foreach (var card in Descendants(root).OfType<Border>().Where(b => b.Name == "CardRoot"))
                {
                    card.Width = 265;
                    card.Height = 265;
                }
                root.InvalidateMeasure();
                root = ViewResourceLoader.Layout(window);

                const double renderScale = 2;
                var bitmap = new RenderTargetBitmap(
                    (int)(root.ActualWidth * renderScale), (int)(root.ActualHeight * renderScale),
                    96 * renderScale, 96 * renderScale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                bitmap.Freeze();

                var button = (FrameworkElement)window.FindName("RefreshModsBtn");
                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));

                var directory = Environment.GetEnvironmentVariable("UEMM_PREVIEW_DIR");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, fileName));
                    encoder.Save(stream);
                }

                var stride = bitmap.PixelWidth * 4;
                var buffer = new byte[stride * bitmap.PixelHeight];
                bitmap.CopyPixels(buffer, stride, 0);
                return ((buffer, bitmap.PixelWidth, bitmap.PixelHeight), bounds, renderScale);
            }
            finally { window.Close(); }
        });

        // 按钮所在的那一小块里确实画了东西（图标的描边、文字），不是一片纯色
        var colors = new HashSet<uint>();
        for (var y = (int)(buttonBounds.Top * scale); y < (int)(buttonBounds.Bottom * scale); y++)
        for (var x = (int)(buttonBounds.Left * scale); x < (int)(buttonBounds.Right * scale); x++)
            colors.Add(BitConverter.ToUInt32(pixels.buffer, (y * pixels.PixelWidth + x) * 4));

        Assert.True(buttonBounds.Width > 40, $"按钮只有 {buttonBounds.Width:F0}px 宽，文字没显示出来");
        Assert.True(colors.Count > 8, $"按钮区域只有 {colors.Count} 种颜色，看起来没有画出图标和文字");
        Assert.Equal(1440 * 2, pixels.PixelWidth);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
