using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UEModManager.Localization;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Promo;

namespace UEModManager.Tests.Views;

/// <summary>
/// 把主窗口连同推广条离屏渲染出来的小工具：测试用它量位置、看像素，
/// 预览图也由它生成（见 <see cref="PromoBarViewTests.生成预览图"/>）。
///
/// <para>
/// 用的是 <see cref="ViewResourceLoader"/>：真实的 MainWindow.xaml + 真实的主题资源，只是不接
/// code-behind。推广条的内容全靠绑定（DataContext 是 Core 里那条活动数据），所以这里把它设成可见、
/// 给上数据，量出来的就是玩家看到的样子。
/// </para>
///
/// <para>
/// 默认不显示窗口，只按给定尺寸排版——量位置、取像素够用，也快。<c>live: true</c> 时窗口真的显示出来
/// （放在屏幕外、不抢焦点）：动效要在挂着窗口句柄的视觉树上才播得起来。
/// </para>
/// </summary>
internal sealed class PromoBarStage : IDisposable
{
    private readonly Window _window;

    public FrameworkElement Root { get; }
    public Border Bar { get; }
    public FrameworkElement Card { get; }
    public double Scale { get; }

    private PromoBarStage(Window window, FrameworkElement root, double scale)
    {
        _window = window;
        Root = root;
        Scale = scale;
        Bar = Find<Border>("PromoBar");
        Card = Find<FrameworkElement>("PromoCouponCard");
    }

    /// <summary>加载主窗口并（默认）让推广条处于显示状态。必须在 STA 线程上调用。</summary>
    /// <param name="english">界面语言。调用方负责事后复原。</param>
    /// <param name="width">窗口内容区宽度（标题栏不在内）。</param>
    /// <param name="height">窗口内容区高度。</param>
    /// <param name="scale">缩放比例（1 / 1.25 / 1.5），布局取整按它来。</param>
    /// <param name="campaign">要展示的活动；不给就用当前那条。</param>
    /// <param name="showPromo">false 时推广条保持 XAML 里的默认状态（折叠）。</param>
    /// <param name="sampleLibrary">给列表填几个示例 MOD，让画面像一个用了一阵子的玩家的主窗口（预览图用）。</param>
    /// <param name="arrange">布局前的额外布置（例如把另外两条提示条也显示出来）。</param>
    /// <param name="live">
    /// 真的把窗口显示出来（屏幕外）。此时 <paramref name="width"/> / <paramref name="height"/> 是整个窗口的大小，
    /// 缩放跟着系统走、<paramref name="scale"/> 不起作用；之后的排版由窗口自己负责，不要再调
    /// <see cref="Resize"/> / <see cref="Relayout"/>。
    /// </param>
    public static PromoBarStage Open(
        bool english, double width = 1440, double height = 864, double scale = 1.0,
        PromoCampaign? campaign = null, bool showPromo = true, bool sampleLibrary = false,
        Action<Window>? arrange = null, bool live = false)
    {
        LanguageManager.SetEnglish(english);
        var window = ViewResourceLoader.Load(LanguageIntegrationTests.AppSource, "MainWindow.xaml");
        try
        {
            window.Width = width;
            window.Height = height;
            VisualTreeHelper.SetRootDpi((Visual)window.Content, new DpiScale(scale, scale));

            // 没接 code-behind，绑定没有数据源：给一份最小的状态，否则「正在处理」的遮罩会一直盖在上面
            var state = new StageState();
            window.DataContext = state;
            if (sampleLibrary) DressAsUsedLibrary(window, state);

            if (showPromo)
            {
                var bar = (FrameworkElement)window.FindName("PromoBar");
                bar.DataContext = campaign ?? PromoCampaigns.GptTopUpCoupon;
                bar.Visibility = Visibility.Visible;
            }

            arrange?.Invoke(window);
            var root = live ? ShowOffscreen(window) : ViewResourceLoader.Layout(window);
            if (sampleLibrary) SizeSampleCards(window);
            // 圆角裁剪挂在 SizeChanged 上，第一次排版之后才有尺寸；再排一次让裁剪生效
            root.UpdateLayout();
            return new PromoBarStage(window, root, scale);
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    /// <summary>把窗口显示在屏幕外：不进任务栏、不抢焦点，但有真的窗口句柄和消息循环。</summary>
    private static FrameworkElement ShowOffscreen(Window window)
    {
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Show();

        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        return root;
    }

    public T Find<T>(string name) where T : FrameworkElement
        => _window.FindName(name) as T
           ?? throw new InvalidOperationException($"MainWindow.xaml 里找不到 {typeof(T).Name}「{name}」");

    /// <summary>把窗口内容区改成另一个尺寸重新排版（相当于玩家拖了窗口边框）。</summary>
    public void Resize(double width, double? height = null)
    {
        _window.Width = width;
        if (height is { } h) _window.Height = h;
        Relayout();
    }

    /// <summary>改了显示状态之后重新排版。</summary>
    public void Relayout()
    {
        ViewResourceLoader.Layout(_window);
        // 尺寸变了之后圆角裁剪、按宽度收起按钮的触发器都要再走一轮
        Root.UpdateLayout();
    }

    /// <summary>元素在窗口内容区里占的矩形。</summary>
    public Rect BoundsOf(FrameworkElement element)
        => element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));

    /// <summary>推广条里看得见的全部文字块。</summary>
    public IReadOnlyList<TextBlock> TextBlocks()
        => Descendants(Bar).OfType<TextBlock>().Where(t => t.IsVisibleInTree()).ToList();

    /// <summary>推广条里看得见的全部文字。</summary>
    public IReadOnlyList<string> Texts() => TextBlocks().Select(t => t.Text).ToList();

    /// <summary>这段文字一行排开、一个字都不省略时要多宽。</summary>
    public double NaturalWidth(TextBlock block)
    {
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        return new FormattedText(
            block.Text, System.Globalization.CultureInfo.InvariantCulture, block.FlowDirection,
            typeface, block.FontSize, Brushes.Black, Scale).WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// 这段文字有没有被省略号截掉。布局取整最多让文字块比文字本身窄半个设备像素，
    /// 而截掉哪怕一个字都是好几个像素的差距，1px 的容差分得清。
    /// </summary>
    public bool IsTrimmed(TextBlock block) => NaturalWidth(block) > block.ActualWidth + 1.0;

    public static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>
    /// 把整个内容区画成位图。<paramref name="background"/> 垫在最底下：
    /// 窗口内容本身是透明的（底色画在窗口外壳上），不垫就看不出票根缺口有没有真的切穿。
    /// </summary>
    public BitmapSource Render(Color background, double? pixelsPerDip = null)
    {
        var ppd = pixelsPerDip ?? Scale;
        var size = Root.RenderSize;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * ppd), (int)Math.Ceiling(size.Height * ppd),
            96 * ppd, 96 * ppd, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(size));
        bitmap.Render(backdrop);
        bitmap.Render(Root);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>位图上某一点（内容区坐标，单位 DIP）的颜色。</summary>
    public static Color PixelAt(BitmapSource bitmap, Point dip, double pixelsPerDip)
    {
        var x = Math.Clamp((int)Math.Round(dip.X * pixelsPerDip), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Round(dip.Y * pixelsPerDip), 0, bitmap.PixelHeight - 1);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    public static BitmapSource Crop(BitmapSource bitmap, Rect dip, double pixelsPerDip)
    {
        var x = Math.Max(0, (int)Math.Floor(dip.X * pixelsPerDip));
        var y = Math.Max(0, (int)Math.Floor(dip.Y * pixelsPerDip));
        var w = Math.Min(bitmap.PixelWidth - x, (int)Math.Ceiling(dip.Width * pixelsPerDip));
        var h = Math.Min(bitmap.PixelHeight - y, (int)Math.Ceiling(dip.Height * pixelsPerDip));
        var cropped = new CroppedBitmap(bitmap, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>把几张同样宽的图自上而下拼成一张（预览图里把动效的几个瞬间摆在一起看）。</summary>
    public static BitmapSource StackVertically(IReadOnlyList<BitmapSource> frames, int gap = 0)
    {
        var width = frames.Max(frame => frame.PixelWidth);
        var height = frames.Sum(frame => frame.PixelHeight) + gap * (frames.Count - 1);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var y = 0;
            foreach (var frame in frames)
            {
                dc.DrawImage(frame, new Rect(0, y, frame.PixelWidth, frame.PixelHeight));
                y += frame.PixelHeight + gap;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose() => _window.Close();

    // ─── 预览图用的示例状态 ───

    /// <summary>
    /// 主窗口绑定要的最小状态（真实的是 MainViewModel，它要一整套服务才建得出来）。
    /// 属性名与 MainViewModel / ModListViewModel 上的一致，XAML 的绑定路径原样可用。
    /// </summary>
    private sealed class StageState
    {
        public bool IsLoading => false;
        public string LoadingMessage => string.Empty;
        public string CurrentProfileName { get; set; } = string.Empty;
        public string CurrentProfileSummary { get; set; } = string.Empty;
        public StageModList ModList { get; } = new();
    }

    private sealed class StageModList
    {
        public ObservableCollection<ModInfo> Mods { get; } = new();
        public string KindFilter { get; set; } = "All";
        public bool ShowKindFilter { get; set; }
        public int AllKindCount => Mods.Count;
        public int ModKindCount => Mods.Count(m => m.Kind == PackageKind.Mod);
        public int PluginKindCount => Mods.Count(m => m.Kind == PackageKind.Plugin);
        public int ConfigKindCount => Mods.Count(m => m.Kind == PackageKind.Config);
        public string SelectAllText => "全选";
        public bool HasSelectedMods => false;
        public int SelectedCount => 0;
    }

    /// <summary>示例卡片的边长（真实窗口里由 MainWindow.CardWidth 按列数算出来，这里没有那段 code-behind）。</summary>
    private const double SampleCardSize = 252;

    private static void DressAsUsedLibrary(Window window, StageState state)
    {
        (string Name, string File, PackageKind Kind, bool Enabled, string Category, long Size)[] samples =
        {
            ("悟空 · 赤金战甲", "WukongGoldArmor_P.pak", PackageKind.Mod, true, "服装", 412_300_000),
            ("金箍棒 · 流光特效", "StaffGlowFx_P.pak", PackageKind.Mod, true, "武器", 86_500_000),
            ("高清贴图包 4K", "HDTextures4K_P.pak", PackageKind.Mod, false, "未分类", 2_310_000_000),
            ("自由视角相机", "FreeCamera", PackageKind.Plugin, true, "未分类", 1_240_000),
            ("画质增强配置", "Engine.ini", PackageKind.Config, true, "未分类", 4_200),
            ("白骨夫人 · 素衣", "BaiguPlainDress_P.pak", PackageKind.Mod, false, "服装", 198_700_000),
            ("天命人 · 黑风披挂", "BlackWindCloak_P.pak", PackageKind.Mod, true, "服装", 305_100_000),
            ("战斗数字显示", "DamageNumbers", PackageKind.Plugin, false, "未分类", 860_000),
        };

        foreach (var s in samples)
        {
            state.ModList.Mods.Add(new ModInfo
            {
                Name = s.Name, RealName = s.File, Kind = s.Kind, IsEnabled = s.Enabled,
                Categories = new List<string> { s.Category }, FileSize = s.Size,
            });
        }

        state.ModList.ShowKindFilter = true;
        state.CurrentProfileName = UiText.Get("默认");
        var enabled = samples.Count(s => s.Enabled);
        state.CurrentProfileSummary = UiText.Interpolate($"{enabled}/{samples.Length} 已启用");

        // 这几处在真实窗口里由 code-behind 写入
        Text(window, "CurrentGameName", GameDisplayNames.For("黑神话·悟空"));
        Text(window, "NavAllCount", samples.Length.ToString());
        Text(window, "NavEnabledCount", enabled.ToString());
        Text(window, "NavDisabledCount", (samples.Length - enabled).ToString());
    }

    private static void Text(Window window, string name, string value)
    {
        if (window.FindName(name) is TextBlock block) block.Text = value;
    }

    /// <summary>
    /// 卡片的宽高绑的是 MainWindow.CardWidth；离屏加载出来的是一个普通 Window，没有这个属性，
    /// 卡片会缩成一团。预览图里按真实窗口常见的尺寸直接给上。
    /// </summary>
    private static void SizeSampleCards(Window window)
    {
        if (window.FindName("ModsCardView") is not ListBox cards) return;
        foreach (var card in Descendants(cards).OfType<Border>().Where(b => b.Name == "CardRoot").ToList())
        {
            card.Width = SampleCardSize;
            card.Height = SampleCardSize;
        }
        cards.InvalidateMeasure();
    }
}

internal static class PromoBarStageExtensions
{
    /// <summary>
    /// 没有挂到真窗口上的视觉树，<c>IsVisible</c> 恒为 false；这里只沿着 Visibility 往上看。
    /// </summary>
    public static bool IsVisibleInTree(this UIElement element)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }
}
