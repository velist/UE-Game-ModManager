using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UEModManager.Tests.Views;

/// <summary>
/// 对 MainWindow 源码本身的静态检查。
///
/// 这些是"写法层面"的回归防线：行为测试证明了拔插会丢选中项，但挡不住有人
/// 下次又把 ItemsSource 赋值写回 code-behind。这里直接对源文件断言。
/// </summary>
public class MainWindowSourceGuardTests
{
    private static string RepoRoot()
    {
        // 从测试程序集位置向上找到含 UEModManager.sln 的目录
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UEModManager.sln")))
            dir = dir.Parent;

        Assert.True(dir != null, "找不到仓库根目录（UEModManager.sln）");
        return dir!.FullName;
    }

    private static string ReadMainWindow(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "UEModManager", fileName);
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ModSelected只有一个订阅者()
    {
        // 事故原型：MainViewModel 和 MainWindow 各订阅一次 ModList.ModSelected，
        // 且对 mod == null 的处理相反（VM"只开不关"，View"没选中就关"）。
        // 谁生效完全取决于订阅顺序——调一下构造顺序，"详情面板关不掉"就会冒出来，
        // 而这种偶发抽风事后根本查不出条件。语义唯一交给 VM。
        var vm = ReadMainWindow(Path.Combine("ViewModels", "MainViewModel.cs"));
        var view = ReadMainWindow("MainWindow.xaml.cs");

        var vmSubscriptions = Regex.Matches(vm, @"ModList\.ModSelected\s*\+=").Count;
        Assert.True(vmSubscriptions == 1,
            $"MainViewModel 应恰好订阅一次 ModList.ModSelected，实际 {vmSubscriptions} 次");

        // code-behind 侧一个订阅/退订都不许有（只匹配代码，注释里提到名字不算）
        var viewSubscriptions = Regex.Matches(view, @"ModSelected\s*[+\-]=").Count;
        Assert.True(viewSubscriptions == 0,
            $"MainWindow.xaml.cs 里还有 {viewSubscriptions} 处 ModSelected 订阅——两份语义相反的逻辑会靠订阅顺序决胜负");

        // 保留下来的必须是"没有选中项就收起面板"那一份，
        // 否则删除 MOD 后面板会停在一个已经不存在的 MOD 上
        Assert.Contains("IsDetailPanelOpen = mod != null;", vm);
    }

    [Fact]
    public void 方案选择器的文字只由绑定驱动()
    {
        // 事故原型：VM 写 CurrentProfileName/CurrentProfileSummary（当时没人绑定，纯空转），
        // MainWindow 另写一份 ProfileSelectorName.Text，连字符串模板都逐字重复。
        // 改 VM 不生效，是 MVVM 迁移最容易踩的坑。
        var xaml = ReadMainWindow("MainWindow.xaml");
        var view = ReadMainWindow("MainWindow.xaml.cs");

        Assert.Contains("Text=\"{Binding CurrentProfileName}\"", xaml);
        Assert.Contains("Text=\"{Binding CurrentProfileSummary}\"", xaml);

        // x:Name 已去掉，code-behind 再想直接写 Text 会编译不过；
        // 这里连名字出现都拦住，防止有人把 x:Name 加回来
        Assert.DoesNotContain("ProfileSelectorName", xaml);
        Assert.DoesNotContain("ProfileSelectorSummary", xaml);
        Assert.DoesNotContain("ProfileSelectorName", view);
        Assert.DoesNotContain("ProfileSelectorSummary", view);

        // 绑定路径必须在 VM 上真实存在
        var vmType = typeof(UEModManager.ViewModels.MainViewModel);
        foreach (var name in new[] { "CurrentProfileName", "CurrentProfileSummary" })
        {
            var prop = vmType.GetProperty(name);
            Assert.True(prop != null, $"MainViewModel.{name} 不存在，方案选择器会一直显示 XAML 默认值");
            Assert.True(prop!.GetMethod?.IsPublic == true, $"{name} 必须是 public，否则绑定取不到");
        }
    }

    [Fact]
    public void 方案改名后侧栏文字会跟着刷新()
    {
        // ProfileService.RenameProfileAsync 只发 ProfileListChanged、不发 ProfileChanged。
        // 只在 ProfileChanged 里刷新显示，就是"改完名字侧栏还显示旧名"。
        var vm = ReadMainWindow(Path.Combine("ViewModels", "MainViewModel.cs"));

        var handler = Regex.Match(vm,
            @"private void OnProfileListChanged\(\).*?\n        \}", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 OnProfileListChanged");
        Assert.Contains("RefreshProfileDisplay", handler.Value);
    }

    [Fact]
    public void 冲突分析失败必须弹给用户而不是静默()
    {
        // 事故原型：catch 里"回退到旧版冲突检测"，而回退目标已被重构掏空成空方法，
        // 分析失败后界面毫无反馈。用户会把"没检查成功"读成"没有冲突"——
        // 对一个以冲突处理为核心能力的软件，这比直接报错危险得多。
        var view = ReadMainWindow("MainWindow.xaml.cs");
        var vm = ReadMainWindow(Path.Combine("ViewModels", "MainViewModel.cs"));

        var handler = Regex.Match(view,
            @"private void OpenConflictPanel\(\).*?, _logger, ""冲突检测""\);", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到走 SafeEvent.Run 的 OpenConflictPanel");
        Assert.Contains("AnalyzeAsync()", handler.Value);
        Assert.DoesNotMatch(@"\bcatch\s*\(", handler.Value);

        // VM 侧也不许再有"catch 后 return null"的第二条冲突分析入口：
        // 失败与"零冲突"在返回值上分不出来，接上按钮就等于把静默失效再造一遍
        Assert.DoesNotMatch(@"Task<ConflictAnalysisResult\?>\s+AnalyzeConflictsAsync", vm);
    }

    [Fact]
    public void 启动中心的冲突预检失败不会被显示成检查通过()
    {
        // BuildPreCheckSteps 把第 3 步预置成绿色的"无文件冲突"，
        // RunConflictPreCheckAsync 抛异常时若只记日志，清单就一直停在那个绿勾上。
        var launchVm = File.ReadAllText(Path.Combine(
            RepoRoot(), "UEModManager", "ViewModels", "LaunchViewModel.cs"));
        var launchWindow = File.ReadAllText(Path.Combine(
            RepoRoot(), "UEModManager", "Views", "LaunchCenterWindow.xaml.cs"));

        var handler = Regex.Match(launchVm,
            @"public async Task RunConflictPreCheckAsync\(\).*?\n        \}", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 RunConflictPreCheckAsync");
        Assert.Contains("ConflictPreCheckError", handler.Value);
        Assert.Contains("StepItemStatus.Warning", handler.Value);

        // 界面必须监听这个属性重画清单，否则 VM 改了也没人看
        Assert.Contains("nameof(LaunchViewModel.ConflictPreCheckError)", launchWindow);
    }

    [Fact]
    public void 动态菜单项的点击处理器没有裸async_void()
    {
        // 动态 new 出来的 MenuItem 没有 XAML 事件属性可查，是最容易漏掉 SafeEvent.Run 的地方。
        // 裸 async void 抛出去只能落到全局 DispatcherUnhandledException：能弹窗，
        // 但不带操作上下文，日志里看不出是"添加新游戏"还是"退出登录"失败了。
        var view = ReadMainWindow("MainWindow.xaml.cs");

        Assert.DoesNotContain(".Click += async", view);

        var addItem = Regex.Match(view,
            @"addItem\.Click \+=.*?, _logger, ""添加新游戏""\);", RegexOptions.Singleline);
        Assert.True(addItem.Success, "「添加新游戏」的点击处理器必须包进 SafeEvent.Run");
        Assert.Contains("SafeEvent.Run", addItem.Value);
    }

    [Fact]
    public void 每条切换游戏的路径都带上了分类服务()
    {
        // 分类文件按游戏名分片。少调一次 SetCurrentGameAsync，当前游戏名就恒为空：
        // 所有游戏的分类挤进同一份文件，加载路径永不触发，用户新建的分类重启即消失。
        // 这正是它当初被漏掉的方式——MVVM 重构时其他几个服务都接上了，唯独它没有。
        var source = ReadMainWindow(Path.Combine("ViewModels", "MainViewModel.cs"));

        var profileCalls = Regex.Matches(source, @"_profileService\.SetCurrentGameAsync").Count;
        var categoryCalls = Regex.Matches(source, @"_categoryService\.SetCurrentGameAsync").Count;

        Assert.True(categoryCalls >= profileCalls,
            $"_profileService.SetCurrentGameAsync 被调用 {profileCalls} 次，"
            + $"_categoryService 只有 {categoryCalls} 次——有切换游戏的路径漏了分类服务");
    }

    [Fact]
    public void 分类拖拽排序落盘而不是只改内存()
    {
        // 直接 Categories.Move 只改内存，用户排好的顺序重启就弹回去了；
        // 且落盘失败必须被 SafeEvent.Run 接住弹窗，否则又是一次静默失败。
        var source = ReadMainWindow("MainWindow.xaml.cs");

        var handler = Regex.Match(source,
            @"private void CategoryList_Drop\(.*?\n        \}", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 CategoryList_Drop");
        Assert.Contains("ReorderCategoryAsync", handler.Value);
        Assert.Contains("SafeEvent.Run", handler.Value);
        Assert.DoesNotContain("items.Move(", handler.Value);
    }

    [Fact]
    public void code_behind_不再手工赋值列表控件的ItemsSource()
    {
        var source = ReadMainWindow("MainWindow.xaml.cs");

        var offenders = Regex.Matches(source, @"(ModsCardView|ModsListView|CategoryList)\.ItemsSource\s*=")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            "列表数据源应只在 MainWindow.xaml 里绑定。code-behind 里重新赋值会绕过绑定机制，"
            + "拔插式赋值更会导致滚动位置归零和选中项丢失。发现: " + string.Join(", ", offenders));
    }

    [Fact]
    public void XAML里三个列表控件都声明了ItemsSource绑定()
    {
        var xaml = ReadMainWindow("MainWindow.xaml");

        Assert.Contains("ItemsSource=\"{Binding ModList.Mods}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding Categories.Categories}\"", xaml);

        // ModsCardView 与 ModsListView 各一处
        var modBindings = Regex.Matches(xaml, Regex.Escape("ItemsSource=\"{Binding ModList.Mods}\"")).Count;
        Assert.Equal(2, modBindings);
    }

    [Fact]
    public void 搜索防抖不再每次击键新建计时器()
    {
        var source = ReadMainWindow("MainWindow.xaml.cs");

        // TextChanged 处理器内不应出现 new DispatcherTimer
        var handler = Regex.Match(source,
            @"private void SearchBox_TextChanged\(.*?\n        \}", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 SearchBox_TextChanged");
        Assert.DoesNotContain("new DispatcherTimer", handler.Value);
    }

    [Fact]
    public void 已删除的空事件处理器不再被XAML引用()
    {
        var xaml = ReadMainWindow("MainWindow.xaml");
        var source = ReadMainWindow("MainWindow.xaml.cs");

        string[] removed =
        {
            "MainWindow_PreviewMouseDown",
            "CategoryContextMenu_Opened",
            "CategoryList_ContextMenuOpening",
            "ModContextMenu_Opened",
            "MainContentArea_PreviewMouseDown",
            "ConflictCheckButton_Click",
            "DisposeTrayIcon",
            "NormalizeGameName",
            "_statsTimer",
        };

        var stillReferenced = removed
            .Where(name => xaml.Contains(name) || source.Contains(name))
            .ToList();

        Assert.True(stillReferenced.Count == 0,
            "以下已删除的成员仍被引用（XAML 引用不存在的处理器会在运行时抛 XamlParseException）: "
            + string.Join(", ", stillReferenced));
    }

    [Fact]
    public void 移动到分类菜单真的接了线_而不是挂在那里没人管()
    {
        // 事故原型：v2.0 基线把 <MenuItem Header="移动到分类"> 从 v1.7 抄进了新 XAML，
        // 却没抄它的填充逻辑，C# 里对它零引用。菜单点开是空的，而"移动 MOD 到分类"
        // 恰恰没有任何替代路径（拖拽只处理分类之间的排序，不接受 MOD）。
        // 这里同时钉住三件事：菜单项存在、子项由数据驱动、点击有处理器。
        var xaml = ReadMainWindow("MainWindow.xaml");
        var source = ReadMainWindow("MainWindow.xaml.cs");

        Assert.Contains("MoveToCategoryMenuItem", xaml);

        // 子项必须来自 AssignableCategories：绑到 Categories 会把三个系统筛选视图
        // 也列出来，它们不存储归属，移进去等于什么都没发生。
        Assert.Contains("AssignableCategories", xaml);
        Assert.DoesNotContain("Value=\"{Binding PlacementTarget.Tag.Categories.Categories", xaml);

        // 点击必须有处理器，且走 SafeEvent.Run（落盘失败要弹给用户）
        Assert.Contains("MoveToCategoryMenuItem_Click", source);
        var handler = Regex.Match(source,
            @"private void MoveToCategoryMenuItem_Click\(.*?\n            \}, _logger", RegexOptions.Singleline);
        Assert.True(handler.Success, "找不到 MoveToCategoryMenuItem_Click");
        Assert.Contains("SafeEvent.Run", handler.Value);
        Assert.Contains("MoveModToCategoryAsync", handler.Value);
        Assert.Contains("ShowOperationFailure", handler.Value);
    }

    [Fact]
    public void 两个模式的右键菜单都有移动到分类()
    {
        // 卡片模式和列表模式各有一份 ContextMenu。列表模式那份此前压根没有这一项，
        // 于是"切到列表视图就没法改分类"。共用同一个 Style 是为了不再各写各的。
        var xaml = ReadMainWindow("MainWindow.xaml");

        var uses = Regex.Matches(xaml, Regex.Escape("Style=\"{StaticResource MoveToCategoryMenuItem}\"")).Count;

        Assert.True(uses == 2,
            $"应有卡片模式和列表模式两处引用 MoveToCategoryMenuItem 样式，实际 {uses} 处");
    }

    [Fact]
    public void 带子菜单的菜单项没有套用不能显示子项的样式()
    {
        // CyberMenuItem 的 ControlTemplate 里只有 Border > Grid > Header，
        // 没有 Popup 也没有 IsItemsHost——套着它的 MenuItem 哪怕 Items 填满了也弹不出东西。
        // "移动到分类"当初就是这么双重失效的。带子项的菜单项必须用 SubmenuHeader 样式。
        var xaml = ReadMainWindow("MainWindow.xaml");

        var style = Regex.Match(xaml,
            @"<Style x:Key=""MoveToCategoryMenuItem"".*?</Style>\s*</Window.Resources>",
            RegexOptions.Singleline);

        Assert.True(style.Success, "找不到 MoveToCategoryMenuItem 样式定义");
        Assert.Contains("CyberMenuItemSubmenuHeader", style.Value);
    }

    [Fact]
    public void 右键菜单取MOD时会一路向上找ContextMenu()
    {
        // 只看一层 Parent 有两个后果：子菜单项（"移动到分类"下的分类）的 Parent
        // 是父 MenuItem 而非 ContextMenu，永远取不到 MOD；列表模式的菜单挂在
        // ListView 上，PlacementTarget 给不出具体某一行，也必须回落到选中项。
        var source = ReadMainWindow("MainWindow.xaml.cs");

        var helper = Regex.Match(source,
            @"private ModInfo\? GetModFromContextMenu\(.*?\n        \}", RegexOptions.Singleline);

        Assert.True(helper.Success, "找不到 GetModFromContextMenu");
        Assert.Contains("while", helper.Value);
        Assert.Contains("_vm.ModList.SelectedMod", helper.Value);
    }

    [Fact]
    public void 系统分类名单只有一份事实来源()
    {
        // 名单散成多份拷贝时，加一个系统分类漏改一处的表现是：
        // 某个筛选视图变成了可写的真分类，用户能把 MOD 移进"已启用"。
        var categoryItem = File.ReadAllText(
            Path.Combine(RepoRoot(), "UEModManager", "Models", "CategoryItem.cs"));
        var categoryService = File.ReadAllText(
            Path.Combine(RepoRoot(), "UEModManager", "Services", "NewCategoryService.cs"));

        Assert.Contains("ModCategoryAssignment.SystemCategoryNames", categoryItem);
        Assert.Contains("ModCategoryAssignment.SystemCategoryNames", categoryService);

        // 运行时再确认一次两边确实一致
        Assert.Equal(
            UEModManager.Services.Categories.ModCategoryAssignment.SystemCategoryNames.OrderBy(n => n),
            UEModManager.Models.CategoryItem.SystemNames.OrderBy(n => n));
    }

    [Fact]
    public void 绑定路径在ViewModel上真实存在_改名不会静默断掉绑定()
    {
        // XAML 绑定路径不参与编译检查：把 ModList 改名，绑定会静默失效、列表变空，
        // 且只有运行起来才看得见。这里用反射把三条路径钉住。
        var vm = typeof(UEModManager.ViewModels.MainViewModel);

        var modList = vm.GetProperty("ModList");
        Assert.True(modList != null, "MainViewModel.ModList 不存在，{Binding ModList.Mods} 会失效");
        Assert.True(modList!.GetMethod?.IsPublic == true, "ModList 必须是 public，否则绑定取不到");

        var mods = modList.PropertyType.GetProperty("Mods");
        Assert.True(mods != null, "ModListViewModel.Mods 不存在，{Binding ModList.Mods} 会失效");
        Assert.True(mods!.GetMethod?.IsPublic == true, "Mods 必须是 public");

        var categories = vm.GetProperty("Categories");
        Assert.True(categories != null, "MainViewModel.Categories 不存在");
        Assert.True(categories!.GetMethod?.IsPublic == true, "Categories 必须是 public");

        var categoryItems = categories.PropertyType.GetProperty("Categories");
        Assert.True(categoryItems != null,
            "CategoryViewModel.Categories 不存在，{Binding Categories.Categories} 会失效");
        Assert.True(categoryItems!.GetMethod?.IsPublic == true, "Categories 必须是 public");

        // 右键"移动到分类"的子项绑的是这条路径
        var assignable = categories.PropertyType.GetProperty("AssignableCategories");
        Assert.True(assignable != null,
            "CategoryViewModel.AssignableCategories 不存在，右键菜单的分类子项会全部消失");
        Assert.True(assignable!.GetMethod?.IsPublic == true, "AssignableCategories 必须是 public");
        Assert.Null(assignable.SetMethod);
    }

    [Fact]
    public void 绑定的集合是实例不变的ObservableCollection()
    {
        // 绑定一次到位的前提：集合实例从不被替换（只有 getter、没有 setter），
        // 且实现 INotifyCollectionChanged。任一条不成立，绑定都会退化。
        var mods = typeof(UEModManager.ViewModels.MainViewModel)
            .GetProperty("ModList")!.PropertyType.GetProperty("Mods")!;

        Assert.Null(mods.SetMethod);
        Assert.True(
            typeof(System.Collections.Specialized.INotifyCollectionChanged).IsAssignableFrom(mods.PropertyType),
            "Mods 必须实现 INotifyCollectionChanged，否则界面不会自动跟随变更");
    }
}
