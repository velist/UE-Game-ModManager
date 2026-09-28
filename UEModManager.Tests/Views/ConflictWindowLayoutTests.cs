using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UEModManager.Models;
using UEModManager.Services.Conflict.Model;
using UEModManager.Tests.Themes;
using UEModManager.ViewModels;

namespace UEModManager.Tests.Views;

/// <summary>
/// 冲突面板的离屏渲染回归。面板只表达一件事：**哪几个 MOD 改到了同一批资源、我该留哪个**。
///
/// 这里固定三件容易在重构里丢掉的东西：
/// ① 两成员组给"禁用 X"、三成员及以上给"只保留 X"（一个组 = 一个决策）；
/// ② 面板里不出现"胜者 / 败者 / 应用 / 优先级"这类内部词；
/// ③ 长 MOD 名不把卡片撑出面板宽度 —— 用水平 StackPanel 装可变长文本就会这样。
///
/// DataContext 用真实的面板条目 ViewModel（<see cref="ConflictViewModel"/> 的嵌套类型），
/// 它们只依赖委托、不碰服务，所以能直接构造；外层要拿 <c>ConflictModuleService</c>
/// 才能建，而本用例关心的只是模板渲染，不需要真去分析一遍。
/// </summary>
[Collection(ThemeResourceCollection.Name)]
public sealed class ConflictWindowLayoutTests
{
    /// <summary>一个够长的 MOD 名，足以在 720 宽的面板里把标题行撑爆。</summary>
    private const string LongName = "指头大佬-披风（搭配任意贴图使用，效果最好~）-横跨两代的超长名字版本";

    [Fact]
    public void 两成员组给禁用按钮_三成员组给只保留按钮()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            var window = LoadWindow();
            try
            {
                var content = ViewResourceLoader.Layout(window);
                var labels = Buttons(content).Select(b => b.Content?.ToString() ?? string.Empty).ToList();

                // 两成员组：每个成员一个"禁用 X"。
                Assert.Equal(2, labels.Count(l => l.StartsWith("禁用 ", StringComparison.Ordinal)));
                // 三成员组：每个成员一个"只保留 X"（不是三个"禁用"）。
                Assert.Equal(3, labels.Count(l => l.StartsWith("只保留 ", StringComparison.Ordinal)));
                Assert.DoesNotContain(labels, l => l.StartsWith("禁用 ", StringComparison.Ordinal)
                                                   && l.Contains("三人大乱斗", StringComparison.Ordinal));

                // 重复区：一份可以删掉的副本。
                Assert.Single(labels, l => l.StartsWith("删除 ", StringComparison.Ordinal));

                // 底部只有一个"关闭"。
                Assert.Single(labels, l => l == "关闭");
                // 以及"一键处理"，它排在关闭左边。
                var resolveAll = Assert.Single(Buttons(content), b => b.Content?.ToString() == "一键处理");
                var close = Assert.Single(Buttons(content), b => b.Content?.ToString() == "关闭");
                Assert.True(
                    resolveAll.TransformToAncestor(content).Transform(new Point(0, 0)).X
                    < close.TransformToAncestor(content).Transform(new Point(0, 0)).X,
                    "「一键处理」应排在「关闭」左侧");
            }
            finally { window.Close(); }
            return null;
        });
    }

    [Fact]
    public void 面板里不出现内部词汇()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            var window = LoadWindow();
            try
            {
                var content = ViewResourceLoader.Layout(window);
                var texts = Texts(content).Select(t => t.Text).ToList();
                // 按钮文案与可展开的资源清单也算：它们同样面向玩家。
                texts.AddRange(Buttons(content).Select(b => b.Content?.ToString() ?? string.Empty));

                foreach (var forbidden in new[] { "胜者", "败者", "应用", "优先级" })
                    Assert.DoesNotContain(texts, t => t.Contains(forbidden, StringComparison.Ordinal));
            }
            finally { window.Close(); }
            return null;
        });
    }

    [Fact]
    public void 有冲突组时一键处理可用()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            var window = LoadWindow();
            try
            {
                // 夹具里两组冲突都在，按钮可用。
                var content = ViewResourceLoader.Layout(window);
                Assert.True(Assert.Single(Buttons(content), b => b.Content?.ToString() == "一键处理").IsEnabled);
            }
            finally { window.Close(); }
            return null;
        });
    }

    [Fact]
    public void 没有冲突组时一键处理置灰()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            var window = LoadWindow(withGroups: false);
            try
            {
                // 没有可处理的对象时按钮必须置灰，而不是点了什么都不发生。
                var content = ViewResourceLoader.Layout(window);
                Assert.False(Assert.Single(Buttons(content), b => b.Content?.ToString() == "一键处理").IsEnabled);
            }
            finally { window.Close(); }
            return null;
        });
    }

    [Fact]
    public void 长MOD名的标题不超出面板宽度()
    {
        ThemeResourceLoader.RunOnSta<object?>(() =>
        {
            var window = LoadWindow();
            try
            {
                var content = ViewResourceLoader.Layout(window);

                var heading = Texts(content).First(t => t.Text.Contains(LongName, StringComparison.Ordinal));

                // 标题的自然宽度（不换行、不裁剪时需要的宽度）确实超过了卡片能给它的宽度 ——
                // 否则这条断言只是碰巧成立，撑不撑出去都看不出来。
                var natural = new FormattedText(heading.Text, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(heading.FontFamily, heading.FontStyle, heading.FontWeight, heading.FontStretch),
                    heading.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(heading).PixelsPerDip).Width;
                Assert.True(natural > heading.ActualWidth,
                    $"这条用例失去了判别力：标题自然宽 {natural} 没有超过实际宽 {heading.ActualWidth}");

                var right = heading.TransformToAncestor(content)
                    .Transform(new Point(heading.ActualWidth, 0)).X;
                Assert.True(right <= window.Width + 1,
                    $"标题右边界 {right} 超出了面板宽度 {window.Width}");
            }
            finally { window.Close(); }
            return null;
        });
    }

    // ─── 夹具 ───

    private static readonly Func<string, Task<OperationResult>> NoOpAction
        = _ => Task.FromResult(OperationResult.Ok());

    private static readonly Func<IReadOnlyList<string>, Task<OperationResult>> NoOpActions
        = _ => Task.FromResult(OperationResult.Ok());

    private static Window LoadWindow(bool withGroups = true)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "UEModManager.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var window = ViewResourceLoader.Load(
            Path.Combine(directory.FullName, "UEModManager"), "Views/ConflictWindow.xaml");
        window.DataContext = BuildDataContext(withGroups);
        return window;
    }

    private static object BuildDataContext(bool withGroups)
    {
        var texture = new ConflictAsset(
            "b1/content/.../t_wukong_born_cloth_ring_d",
            "b1/content/.../T_WuKong_Born_Cloth_Ring_D.uasset",
            AssetCategory.Texture, "WuKong_Born_Cloth_Ring");
        var summary = new AssetCategorySummary(
            new Dictionary<AssetCategory, int> { [AssetCategory.Texture] = 1 }, "1 张贴图");

        var twoMember = new ConflictGroup(
            [new ConflictMember("指头大佬-披风", LongName),
             new ConflictMember("红底龙纹披风", "红底龙纹披风(替换神珍大圣胸甲)")],
            [texture], summary);
        var threeMember = new ConflictGroup(
            [new ConflictMember("三人大乱斗A", "三人大乱斗-A"),
             new ConflictMember("三人大乱斗B", "三人大乱斗-B"),
             new ConflictMember("三人大乱斗C", "三人大乱斗-C")],
            [texture], summary);

        var duplicate = new DuplicateGroup(
            new ConflictMember("红色经文金箍棒", "红色经文金箍棒"),
            [new ConflictMember("红色经文金箍棒_2", "红色经文金箍棒_2")]);

        var shared = new SharedComponent("UE4SS 运行时",
            [new SharedComponentProvider("超级筋斗云", "超级筋斗云", null, null, true),
             new SharedComponentProvider("超级识破", "超级识破", null, null, true)],
            "超级筋斗云", "内容相同");

        return new
        {
            HeaderText = "2 组冲突 · 1 处重复 · 1 个共享组件",
            StatusText = string.Empty,
            IsEmpty = false,
            HasDuplicates = true,
            HasSharedComponents = true,
            HasUnanalyzable = false,
            HasGroups = withGroups,
            Groups = withGroups ? new[]
            {
                new ConflictViewModel.ConflictGroupItemViewModel(twoMember, NoOpAction, NoOpActions),
                new ConflictViewModel.ConflictGroupItemViewModel(threeMember, NoOpAction, NoOpActions)
            } : [],
            Duplicates = duplicate.Copies
                .Select(c => new ConflictViewModel.DuplicateItemViewModel(duplicate.Primary, c, NoOpAction))
                .ToList(),
            SharedComponents = new[] { new ConflictViewModel.SharedComponentItemViewModel(shared, null!) },
            Unanalyzable = Array.Empty<ConflictViewModel.UnanalyzableItemViewModel>()
        };
    }

    private static IEnumerable<TextBlock> Texts(DependencyObject root)
        => Descendants(root).OfType<TextBlock>();

    private static IEnumerable<Button> Buttons(DependencyObject root)
        => Descendants(root).OfType<Button>();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandChild in Descendants(child)) yield return grandChild;
        }
    }
}
