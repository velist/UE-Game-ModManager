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
