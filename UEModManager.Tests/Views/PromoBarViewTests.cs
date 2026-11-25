using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UEModManager.Infrastructure;
using UEModManager.Localization;
using UEModManager.Services;
using UEModManager.Services.Promo;
using UEModManager.Tests.Themes;

namespace UEModManager.Tests.Views;

/// <summary>
/// 主窗口顶部推广条的界面回归（离屏渲染，不弹窗、不联网、不打开浏览器）。
///
/// <para>
/// 这里看的是<b>样子</b>：文字齐不齐、挤不挤、和另外两条提示条打不打架、票根是不是真的切出了缺口。
/// 什么时候出现、到期下线、每天一次——在 <c>PromoScheduleTests</c>（Core）与 <c>PromoBarServiceTests</c>；
/// 动效的节奏——在 <c>MotionAssistBannerTests</c>。
/// </para>
/// </summary>
// 要切界面语言：进程级状态，必须和其它改语言的测试一起独占运行。
[Collection(LanguageIntegrationCollection.Name)]
public sealed class PromoBarViewTests : IDisposable
{
    /// <summary>垫在窗口后面的颜色。界面里没有任何地方用品红，所以哪里透出了它一眼就能认出来。</summary>
    private static readonly Color Backdrop = Color.FromRgb(0xFF, 0x00, 0xFF);

    /// <summary>窗口允许缩到的最小尺寸（MainWindow 的 MinWidth × MinHeight 减去 36px 标题栏）。</summary>
    private const double MinWindowWidth = 1024;
    private const double MinContentHeight = 680 - 36;

    /// <summary>右侧 MOD 详情面板展开后的宽度。</summary>
    private const double DetailPanelWidth = 360;

    private readonly bool _previousLanguage = LanguageManager.IsEnglish;

    public void Dispose() => LanguageManager.SetEnglish(_previousLanguage);

    // ─── 出现与否 ───

    [Fact]
    public void 没有活动要展示时推广条是折叠的_不占任何位置()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            double toolbarTopWithout, toolbarTopWith, barHeight;
            using (var stage = PromoBarStage.Open(english: false, showPromo: false))
            {
                // XAML 里的默认状态就是折叠：没有 code-behind 去叫它，它就永远不出现
                Assert.Equal(Visibility.Collapsed, stage.Bar.Visibility);
                Assert.Equal(0, stage.Bar.ActualHeight);
                toolbarTopWithout = stage.BoundsOf(stage.Find<FrameworkElement>("MainToolbar")).Top;
            }
            using (var stage = PromoBarStage.Open(english: false))
            {
                barHeight = stage.Bar.ActualHeight;
                toolbarTopWith = stage.BoundsOf(stage.Find<FrameworkElement>("MainToolbar")).Top;
            }

            // 出现时自己占一行，把工具栏整体往下推这么多——而不是浮在工具栏上面
            Assert.Equal(88, barHeight);
            Assert.Equal(toolbarTopWithout + barHeight, toolbarTopWith);
            return null;
        });
    }

    // ─── 文字 ───

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 券面上的文字齐全_而且只有这些(bool english)
    {
        var expected = english
            ? new[] { "¥", "15", "OFF", "GPT top-up deal", "Ad", "¥15 off coupon — click to claim", "Claim now" }
            : new[] { "¥", "15", "立减券", "GPT 充值优惠", "广告", "15 元立减优惠券，点击领取", "立即领取" };

        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            using var stage = PromoBarStage.Open(english);

            // 逐字相等：应用所有者没说过的话（「官方」「限时」「有效期至…」）一个字都不能多
            Assert.Equal(expected, stage.Texts().Where(text => !IsIconGlyph(text)).ToArray());

            // 「×」是图标字体画的，读屏软件念不出来：名字和悬停提示要有，而且跟着语言走
            var close = stage.Find<Button>("PromoCloseButton");
            Assert.Equal(english ? "Close" : "关闭", AutomationProperties.GetName(close));
            Assert.Equal(english ? "Close" : "关闭", close.ToolTip);
            return null;
        });
    }

    [Fact]
    public void 推广条用到的每一句话都有英文译文()
    {
        var campaign = PromoCampaigns.GptTopUpCoupon;
        foreach (var source in new[]
        {
            campaign.Title, campaign.Subtitle, campaign.StubCaption, campaign.ActionText,
            "广告", "关闭",
            // 这两句是出错弹窗里的操作名（SafeEvent.Run 的最后一个参数）
            "打开推广链接", "关闭推广条",
            "无法打开默认浏览器，请手动复制此链接到浏览器访问：\n\n{0}",
        })
            Assert.True(UiText.HasEnglish(source), $"缺英文译文：{source}");
    }

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 1.25)]
    [InlineData(true, 1.25)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.5)]
    public void 两个按钮的文字不被挤压(bool english, double scale)
    {
        // 判据与 ButtonTextFitTests 完全一致。那边扫的是 XAML 默认状态，推广条是折叠的、按钮量不到，
        // 所以这里让它显示出来再量一遍。
        var tolerance = scale == 1.0 ? 0.5 : 2.0 / scale;

        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            using var stage = PromoBarStage.Open(english, scale: scale);
            var card = stage.BoundsOf(stage.Card);

            foreach (var name in new[] { "PromoClaimButton", "PromoCloseButton" })
            {
                var button = stage.Find<Button>(name);
                Assert.True(button.IsVisibleInTree() && button.ActualWidth > 0, $"{name} 没有显示出来");

                var content = ContentWidth(button);
                Assert.True(content > 0, $"{name} 没有内容");
                var needed = content
                    + button.Padding.Left + button.Padding.Right
                    + button.BorderThickness.Left + button.BorderThickness.Right;
                Assert.True(needed <= button.ActualWidth + tolerance,
                    $"{name} 需要 {needed:F1}px（内容 {content:F1}），实际只有 {button.ActualWidth:F1}px");

                // 整个按钮都在券里面，上下留有余地
                var bounds = stage.BoundsOf(button);
                Assert.True(card.Contains(bounds), $"{name} 伸到了券外面：{bounds} 不在 {card} 里");
                Assert.True(bounds.Top - card.Top >= 12 && card.Bottom - bounds.Bottom >= 12,
                    $"{name} 离券的上下边太近：{bounds}");
            }

            // 「×」要够大，点得中
            var close = stage.Find<Button>("PromoCloseButton");
            Assert.True(close.ActualWidth >= 28 && close.ActualHeight >= 28);
            return null;
        });
    }

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 1.25)]
    [InlineData(true, 1.5)]
    public void 常见窗口宽度下文字完整显示_不越界也不互相压住(bool english, double scale)
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            using var stage = PromoBarStage.Open(english, scale: scale);

            // 从允许的最小宽度到 4K 屏最大化
            foreach (var width in new[] { MinWindowWidth, 1280, 1366, 1440, 1920, 2560, 3840 })
            {
                stage.Resize(width);
                var card = stage.BoundsOf(stage.Card);
                var blocks = stage.TextBlocks();

                foreach (var block in blocks)
                {
                    Assert.False(stage.IsTrimmed(block),
                        $"宽 {width} 时「{block.Text}」被截断：要 {stage.NaturalWidth(block):F1}px，只有 {block.ActualWidth:F1}px");
                    var bounds = stage.BoundsOf(block);
                    Assert.True(card.Contains(bounds), $"宽 {width} 时「{block.Text}」伸到了券外面：{bounds} 不在 {card} 里");
                }

                // 文字块两两不重叠（行框之间允许 2px 的贴边，字形本身不会碰到）
                for (var i = 0; i < blocks.Count; i++)
                for (var j = i + 1; j < blocks.Count; j++)
                {
                    var overlap = Rect.Intersect(stage.BoundsOf(blocks[i]), stage.BoundsOf(blocks[j]));
                    Assert.True(overlap.IsEmpty || overlap.Width <= 2 || overlap.Height <= 2,
                        $"宽 {width} 时「{blocks[i].Text}」与「{blocks[j].Text}」叠在了一起：{overlap}");
                }

                // 券居中，最宽 760，两侧至少留 32
                Assert.True(card.Width <= 760 + 0.01);
                var bar = stage.BoundsOf(stage.Bar);
                Assert.True(card.Left - bar.Left >= 32 - 0.01 && bar.Right - card.Right >= 32 - 0.01);
                Assert.Equal(card.Left - bar.Left, bar.Right - card.Right, 1.0);
            }
            return null;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 窗口缩到最小又开着详情面板时_收起领取按钮保住标题(bool english)
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            using var stage = PromoBarStage.Open(english, width: MinWindowWidth,
                arrange: window => ((FrameworkElement)window.FindName("DetailPanelHost")).Width = DetailPanelWidth);

            var claim = stage.Find<Button>("PromoClaimButton");
            var close = stage.Find<Button>("PromoCloseButton");
            var title = stage.TextBlocks().Single(block => block.Text == UiText.Get(PromoCampaigns.GptTopUpCoupon.Title));

            // 从最窄一路拉宽：标题在任何宽度下都不能被截成「GPT 充…」
            var sawCollapsed = false;
            var sawVisible = false;
            for (var width = MinWindowWidth; width <= 1400; width += 4)
            {
                stage.Resize(width);
                var card = stage.BoundsOf(stage.Card);

                Assert.False(stage.IsTrimmed(title),
                    $"宽 {width}（券宽 {card.Width}）时标题被截断：要 {stage.NaturalWidth(title):F1}px，只有 {title.ActualWidth:F1}px");

                // 「×」和「广告」角标任何时候都完整可见
                Assert.True(card.Contains(stage.BoundsOf(close)), $"宽 {width} 时「×」伸到了券外面");
                var badge = stage.TextBlocks().Single(block => block.Text == UiText.Get("广告"));
                Assert.False(stage.IsTrimmed(badge), $"宽 {width} 时「广告」角标被截断");

                // 窄于 440 收起按钮，其余时候按钮都在，而且完整
                var expectCollapsed = card.Width < 440;
                Assert.Equal(expectCollapsed ? Visibility.Collapsed : Visibility.Visible, claim.Visibility);
                if (expectCollapsed) sawCollapsed = true;
                else
                {
                    sawVisible = true;
                    Assert.True(card.Contains(stage.BoundsOf(claim)), $"宽 {width} 时「立即领取」伸到了券外面");
                    Assert.True(claim.ActualWidth >= ContentWidth(claim) + claim.Padding.Left + claim.Padding.Right - 1,
                        $"宽 {width} 时「立即领取」被挤窄");
                }
            }

            // 两种状态都真的走到过，否则上面的循环就没检查到要检查的东西
            Assert.True(sawCollapsed && sawVisible);
            return null;
        });
    }

    // ─── 与另外两条提示条的关系 ───

    [Theory]
    [InlineData(false, 1440, 864)]
    [InlineData(true, 1440, 864)]
    [InlineData(false, MinWindowWidth, MinContentHeight)]
    [InlineData(true, MinWindowWidth, MinContentHeight)]
    public void 三条提示条同时出现时自上而下排开_互不遮挡(bool english, double width, double height)
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            using var stage = PromoBarStage.Open(english, width: width, height: height, arrange: ShowTheOtherTwoBars);

            var promo = stage.BoundsOf(stage.Card);
            var toolbar = stage.BoundsOf(stage.Find<FrameworkElement>("MainToolbar"));
            var loader = stage.BoundsOf(stage.Find<FrameworkElement>("LoaderStatusBar"));
            var update = stage.BoundsOf(stage.Find<FrameworkElement>("UpdateNoticeBar"));

            var all = new (string Name, Rect Bounds)[]
            {
                ("推广条", promo), ("工具栏", toolbar), ("加载器状态条", loader), ("新版本提示条", update),
            };
            foreach (var (name, bounds) in all)
                Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{name} 没有显示出来：{bounds}");

            for (var i = 0; i < all.Length; i++)
            for (var j = i + 1; j < all.Length; j++)
            {
                var overlap = Rect.Intersect(all[i].Bounds, all[j].Bounds);
                Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < 0.5,
                    $"{all[i].Name} 与 {all[j].Name} 叠在了一起：{all[i].Bounds} ∩ {all[j].Bounds} = {overlap}");
            }

            // 顺序：推广条 → 工具栏 → 加载器状态条 → 新版本提示条
            Assert.True(promo.Bottom <= toolbar.Top, $"推广条 {promo} 应在工具栏 {toolbar} 上方");
            Assert.True(toolbar.Bottom <= loader.Top, $"工具栏 {toolbar} 应在加载器状态条 {loader} 上方");
            Assert.True(loader.Bottom <= update.Top, $"加载器状态条 {loader} 应在新版本提示条 {update} 上方");

            // 推广条不超出内容区
            var root = new Rect(stage.Root.RenderSize);
            Assert.True(root.Contains(promo), $"推广条伸到了窗口外面：{promo}");

            if (height > MinContentHeight)
            {
                // 默认大小的窗口：三条都在的时候，MOD 列表仍然放得下一整排卡片
                var list = stage.BoundsOf(stage.Find<FrameworkElement>("ModsCardView"));
                Assert.True(list.Height >= 300, $"三条提示条把 MOD 列表挤得只剩 {list.Height}px");
                Assert.True(root.Contains(update), $"新版本提示条伸到了窗口外面：{update}");
            }
            return null;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 只有推广条时_最小的窗口里MOD列表仍然放得下一排卡片(bool english)
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            using var stage = PromoBarStage.Open(english, width: MinWindowWidth, height: MinContentHeight);
            var list = stage.BoundsOf(stage.Find<FrameworkElement>("ModsCardView"));
            Assert.True(list.Height >= 280, $"推广条把 MOD 列表挤得只剩 {list.Height}px");
            return null;
        });
    }

    // ─── 优惠券的样子 ───

    [Fact]
    public void 票根的缺口和四个圆角是真的切掉的_透出的是后面的东西()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            const double ppd = 2.0;
            using var stage = PromoBarStage.Open(english: false);
            var bitmap = stage.Render(Backdrop, ppd);
            var card = stage.BoundsOf(stage.Card);
            var notchX = card.Left + RoundedClip.GetNotchOffset(stage.Card);

            Color At(double x, double y) => PromoBarStage.PixelAt(bitmap, new Point(x, y), ppd);

            // 上下两个缺口：缺口里是背景，不是拿深色画了个圆盖上去
            Assert.Equal(Backdrop, At(notchX, card.Top + 2.5));
            Assert.Equal(Backdrop, At(notchX, card.Bottom - 2.5));
            Assert.Equal(Backdrop, At(notchX - 4, card.Top + 1.5));
            Assert.Equal(Backdrop, At(notchX + 4, card.Bottom - 1.5));

            // 四个圆角外侧也是背景
            Assert.Equal(Backdrop, At(card.Left + 1, card.Top + 1));
            Assert.Equal(Backdrop, At(card.Right - 1.5, card.Top + 1));
            Assert.Equal(Backdrop, At(card.Left + 1, card.Bottom - 1.5));
            Assert.Equal(Backdrop, At(card.Right - 1.5, card.Bottom - 1.5));

            // 券面本身是深色的，没被切掉；缺口往里一点就又是券面
            foreach (var point in new[]
            {
                new Point(card.Left + 300, card.Top + 8), new Point(card.Left + 8, card.Top + 8),
                new Point(notchX, card.Top + 11), new Point(notchX, card.Bottom - 11),
                new Point(card.Right - 8, card.Bottom - 8),
            })
            {
                var color = At(point.X, point.Y);
                Assert.True(color != Backdrop && (color.R + color.G + color.B) / 3 < 90 && color.B < 70,
                    $"券面 {point} 处应是深色，实际 {color}");
            }

            // 票根比券面暖：左边这一块罩了琥珀色
            var stub = At(card.Left + 56, card.Top + 6);
            var face = At(card.Left + 500, card.Top + 6);
            Assert.True(stub.R - stub.B >= face.R - face.B + 20, $"票根 {stub} 应比券面 {face} 更暖");

            // 打孔线：票根与正文的分界上有一列琥珀色圆点
            var dots = 0;
            for (var y = card.Top + 12; y <= card.Bottom - 12; y += 0.5)
            {
                var color = At(notchX, y);
                if (color.R > 150 && color.R - color.B > 80) dots++;
            }
            Assert.True(dots >= 8, $"分界线上只找到 {dots} 个琥珀色采样点，打孔线没画出来");
            return null;
        });
    }

    [Fact]
    public void 金额是券面上最大的字_领取按钮是金色的()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            using var stage = PromoBarStage.Open(english: false);
            var campaign = PromoCampaigns.GptTopUpCoupon;
            var blocks = stage.TextBlocks();

            var amount = blocks.Single(block => block.Text == campaign.Amount);
            Assert.True(blocks.All(block => block == amount || block.FontSize < amount.FontSize),
                "金额应是整张券上最大的字");
            Assert.True(amount.FontSize >= 2 * blocks.Single(block => block.Text == campaign.Title).FontSize,
                "金额至少是标题的两倍大，一眼先看到优惠多少");

            // 金额在票根里，票根在打孔线左边
            var notchX = stage.BoundsOf(stage.Card).Left + RoundedClip.GetNotchOffset(stage.Card);
            Assert.True(stage.BoundsOf(stage.Find<FrameworkElement>("PromoAmount")).Right < notchX);
            Assert.True(stage.BoundsOf(blocks.Single(block => block.Text == campaign.Title)).Left > notchX);

            // 按钮底色是暖色渐变（不是主题里的青色主按钮）
            var claim = stage.Find<Button>("PromoClaimButton");
            var fill = Assert.IsType<LinearGradientBrush>(claim.Background);
            Assert.All(fill.GradientStops, stop => Assert.True(stop.Color.R > 200 && stop.Color.B < 80, $"{stop.Color} 不是暖色"));
            return null;
        });
    }

    [Fact]
    public void 高光平时看不见也不挡鼠标_扫过时被裁在券的形状里()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            const double ppd = 2.0;
            using var stage = PromoBarStage.Open(english: false);
            var shine = stage.Find<Border>("PromoShine");
            var card = stage.BoundsOf(stage.Card);
            var notchX = card.Left + RoundedClip.GetNotchOffset(stage.Card);

            // 静止状态：完全透明、不接鼠标 —— 系统关闭动画时界面就是这个样子
            Assert.Equal(0, shine.Opacity);
            Assert.False(shine.IsHitTestVisible);
            var still = stage.Render(Backdrop, ppd);

            // 把高光摆到扫光途中的某个位置（走的是 MotionAssist 自己的那个位移变换）。
            // 换了变换对象要重新排版一次才生效；之后只改位移的数值就不用了
            var move = MotionAssist.EnsureTransform<TranslateTransform>(shine);
            stage.Root.UpdateLayout();
            BitmapSource FrameAt(double bandCenterX)
            {
                move.X = bandCenterX - card.Left - shine.ActualWidth / 2;
                shine.Opacity = 1;
                var frame = stage.Render(Backdrop, ppd);
                shine.Opacity = 0;
                return frame;
            }

            // 扫到券中间：那一片变亮了
            var probe = new Point(card.Left + 420, card.Top + 8);
            var lit = PromoBarStage.PixelAt(FrameAt(probe.X), probe, ppd);
            var dark = PromoBarStage.PixelAt(still, probe, ppd);
            Assert.True(lit.R >= dark.R + 30 && lit.G >= dark.G + 30,
                $"高光扫过处应明显变亮：{dark} → {lit}");

            // 扫到右边缘：券外面一丝都不亮，右上角的圆角外侧也不亮
            var atRightEdge = FrameAt(card.Right);
            Assert.Equal(Backdrop, PromoBarStage.PixelAt(atRightEdge, new Point(card.Right + 3, card.Top + 36), ppd));
            Assert.Equal(Backdrop, PromoBarStage.PixelAt(atRightEdge, new Point(card.Right - 1.5, card.Top + 1), ppd));

            // 扫过票根缺口：缺口里仍然是背景
            var atNotch = FrameAt(notchX);
            Assert.Equal(Backdrop, PromoBarStage.PixelAt(atNotch, new Point(notchX, card.Top + 2.5), ppd));
            Assert.Equal(Backdrop, PromoBarStage.PixelAt(atNotch, new Point(notchX, card.Bottom - 2.5), ppd));

            // 起点与终点：整条高光都在券外面，画面与静止时一模一样
            var (from, to) = MotionAssist.ShineTravel(shine.ActualWidth, shine.ActualHeight, stage.Card.ActualWidth);
            foreach (var x in new[] { from, to })
            {
                move.X = x;
                shine.Opacity = 1;
                var frame = stage.Render(Backdrop, ppd);
                shine.Opacity = 0;
                for (var px = card.Left + 2; px < card.Right; px += 6)
                for (var py = card.Top + 2; py < card.Bottom; py += 6)
                {
                    var point = new Point(px, py);
                    Assert.Equal(PromoBarStage.PixelAt(still, point, ppd), PromoBarStage.PixelAt(frame, point, ppd));
                }
            }
            return null;
        });
    }

    // ─── 预览图 ───

    /// <summary>
    /// 生成预览图。平时什么都不写；设了环境变量 <c>UEMM_PROMO_PREVIEW_DIR</c> 才把 PNG 存过去：
    /// <code>UEMM_PROMO_PREVIEW_DIR=D:\previews dotnet test --filter "FullyQualifiedName~PromoBarViewTests.生成预览图"</code>
    /// 预览是静态的：主图里高光不在画面上（平时就是这样），另出一张把扫光的三个瞬间摆出来的图。
    /// 动起来的观感要在真机上看。
    /// </summary>
    [Fact]
    public void 生成预览图()
    {
        var directory = Environment.GetEnvironmentVariable("UEMM_PROMO_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            const double closeup = 3.0;
            var canvas = Color.FromRgb(0x03, 0x03, 0x03);   // 窗口底色 BgBase
            foreach (var english in new[] { false, true })
            {
                var suffix = english ? "en" : "zh";
                using var stage = PromoBarStage.Open(english, scale: 2.0, sampleLibrary: true);
                PromoBarStage.SavePng(stage.Render(canvas), Path.Combine(directory, $"promo-bar-{suffix}.png"));

                var card = stage.BoundsOf(stage.Card);
                var crop = card;
                crop.Inflate(20, 14);
                BitmapSource Closeup() => PromoBarStage.Crop(stage.Render(canvas, closeup), crop, closeup);
                PromoBarStage.SavePng(Closeup(),
                    Path.Combine(directory, english ? "promo-bar-closeup-en.png" : "promo-bar-closeup.png"));

                if (english) continue;

                // 扫光的三个瞬间：刚扫过票根（金额脉冲到最大）、扫到正文、扫到按钮
                var shine = stage.Find<Border>("PromoShine");
                var amount = stage.Find<FrameworkElement>("PromoAmount");
                var move = MotionAssist.EnsureTransform<TranslateTransform>(shine);
                var pulse = MotionAssist.EnsureTransform<ScaleTransform>(amount);
                stage.Root.UpdateLayout();   // 换了变换对象，排版一次才生效
                var (from, to) = MotionAssist.ShineTravel(shine.ActualWidth, shine.ActualHeight, stage.Card.ActualWidth);
                var frames = new List<BitmapSource>();
                foreach (var (progress, scale) in new[] { (0.22, MotionAssist.ShinePulseScale), (0.5, 1.0), (0.86, 1.0) })
                {
                    move.X = from + (to - from) * progress;
                    pulse.ScaleX = pulse.ScaleY = scale;
                    shine.Opacity = 1;
                    frames.Add(Closeup());
                }
                shine.Opacity = 0;
                PromoBarStage.SavePng(PromoBarStage.StackVertically(frames),
                    Path.Combine(directory, "promo-bar-closeup-shine.png"));
            }
            return null;
        });
    }

    // ─── 辅助 ───

    /// <summary>把加载器状态条和新版本提示条也显示出来，文案用它们在真实窗口里的那几句。</summary>
    private static void ShowTheOtherTwoBars(Window window)
    {
        const int scriptMods = 2;
        var sample = string.Join(UiText.Get("、"), "FreeCamera", "DamageNumbers");
        Text(window, "LoaderStatusTitle", UiText.Get("已启用的脚本 MOD 需要 UE4SS 才能生效"));
        Text(window, "LoaderStatusNotes",
            UiText.Interpolate($"没有检测到 UE4SS，{scriptMods} 个脚本 MOD（{sample}）装好了也不会被游戏加载。从 UE4SS 官方发布页下载，下载后把压缩包拖进来即可安装。")
            + UiText.Get("部分游戏需要 MOD 作者指定的 UE4SS 版本，以 MOD 说明为准。"));
        ((FrameworkElement)window.FindName("LoaderStatusBar")).Visibility = Visibility.Visible;

        const string latest = "2.1.6";
        Text(window, "UpdateNoticeTitle", UiText.Interpolate($"新版本 v{latest} 已发布"));
        ((FrameworkElement)window.FindName("UpdateNoticeBar")).Visibility = Visibility.Visible;
    }

    private static void Text(Window window, string name, string value)
        => ((TextBlock)window.FindName(name)).Text = value;

    /// <summary>图标字体里的字形（Segoe MDL2 Assets 用的是私用区码位），不算文案。</summary>
    private static bool IsIconGlyph(string text)
        => text.Length > 0 && text.All(c => c is >= '\uE000' and <= '\uF8FF');

    /// <summary>按钮内容的自然宽度，不含内边距（与 ButtonTextFitTests 同一量法）。</summary>
    private static double ContentWidth(Button button)
    {
        switch (button.Content)
        {
            case FrameworkElement element:
                return element.DesiredSize.Width;

            case string text when text.Length > 0:
                var typeface = new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch);
                return new FormattedText(
                    text, System.Globalization.CultureInfo.CurrentCulture, button.FlowDirection,
                    typeface, button.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(button).PixelsPerDip)
                    .WidthIncludingTrailingWhitespace;

            default:
                return 0;
        }
    }
}
