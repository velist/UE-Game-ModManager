using System.Text.RegularExpressions;
using System.Xml.Linq;
using UEModManager.Tests.Themes;

namespace UEModManager.Tests.Views;

/// <summary>
/// 启动时序的源码守卫。
///
/// <para>
/// 首次运行的仓库位置引导必须夹在两件事之间，两头都是硬约束，而且<b>两头都没有运行期信号</b>
/// ——顺序错了不会抛异常、不会有日志，只会表现成"某类用户被莫名其妙地问了一次"
/// 或"用户选的位置这次会话不生效"。这种错误行为测试盖不住，只能对源码本身断言。
/// </para>
///
/// <list type="number">
/// <item><b>上界：搬迁器之后。</b><c>DataLocationMigrator</c> 的原地登记会把老用户的旧仓库
/// 位置写进配置，而"配置里已有仓库位置"正是判定老用户最主要的一条判据。抢在它前面判，
/// 装满 MOD 的老用户会被读成"未配置"、被弹窗问一次 —— 他很可能会认真挑一个大盘，
/// 而引导只改指针不搬数据，他的 MOD 当场"消失"。</item>
/// <item><b>下界：任何会拖出 <c>ObjectStore</c> 的解析之前。</b><c>ObjectStore</c> 是 DI 单例，
/// 构造时读一次 <c>AppPaths.RepositoryRoot</c> 记进字段，之后本次会话不再回头看配置。
/// 引导写在它之后，用户这次选的位置要等下次启动才生效。</item>
/// </list>
///
/// <para>
/// 本项目已经因为重构丢接线出过三次事故，行为测试盖不住"下次又写回去"。
/// </para>
/// </summary>
[Collection(ThemeResourceCollection.Name)]
public class StartupSequenceGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UEModManager.sln")))
            dir = dir.Parent;

        Assert.True(dir != null, "找不到仓库根目录（UEModManager.sln）");
        return dir!.FullName;
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { RepoRoot(), "UEModManager" }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    /// <summary><c>ShowAuthenticationWindow</c> 的方法体——启动时序的全部现场都在这里。</summary>
    private static string StartupBody()
    {
        var app = ReadSource("App.xaml.cs");
        var body = Regex.Match(app,
            @"private async void ShowAuthenticationWindow\(\).*?\n        \}", RegexOptions.Singleline);

        Assert.True(body.Success, "找不到 ShowAuthenticationWindow");
        return body.Value;
    }

    [Fact]
    public void 引导排在数据搬迁之后()
    {
        var body = StartupBody();

        var migration = body.IndexOf("migrator.RunAsync()", StringComparison.Ordinal);
        var setup = body.IndexOf("ShowRepositorySetupIfNeeded()", StringComparison.Ordinal);

        Assert.True(migration >= 0, "启动流程里找不到数据搬迁");
        Assert.True(setup >= 0, "启动流程里找不到仓库位置引导");
        Assert.True(migration < setup,
            "仓库位置引导必须排在数据搬迁之后：搬迁器的原地登记会把老用户的旧仓库位置写进配置，"
            + "那是判定老用户最主要的一条判据。抢在它前面判，装满 MOD 的老用户会被弹窗问一次。");
    }

    [Fact]
    public void 引导排在任何业务服务解析之前()
    {
        // ObjectStore 是 DI 单例，第一次被解析时才构造，而它构造时就把 AppPaths.RepositoryRoot
        // 记进了字段。ShowAuthenticationWindow 里第一处从容器解析业务服务的是 LocalDbContext，
        // 真正拖出 ObjectStore 的是 MainWindow —— 引导必须早于其中最早的那一处。
        var body = StartupBody();

        var setup = body.IndexOf("ShowRepositorySetupIfNeeded()", StringComparison.Ordinal);
        var firstResolve = body.IndexOf("GetRequiredService<LocalDbContext>()", StringComparison.Ordinal);
        var mainWindow = body.IndexOf("ShowMainWindow()", StringComparison.Ordinal);

        Assert.True(firstResolve >= 0, "启动流程里找不到 LocalDbContext 解析");
        Assert.True(mainWindow >= 0, "启动流程里找不到 ShowMainWindow");
        Assert.True(setup < firstResolve && setup < mainWindow,
            "仓库位置引导必须早于任何业务服务解析：ObjectStore 构造时读一次 AppPaths.RepositoryRoot，"
            + "之后本次会话不再回头看配置，晚一步用户选的位置就要等下次启动才生效。");
    }

    [Fact]
    public void 引导自己不解析仓库相关服务()
    {
        // 解析 ObjectStore 就等于把它构造出来 —— 那正好把"第一次读配置"的时机提前到引导内部，
        // 反过来制造出这段代码要防的问题。引导只写偏好。
        var app = ReadSource("App.xaml.cs");
        var handler = Regex.Match(app,
            @"private void ShowRepositorySetupIfNeeded\(\).*?\n        \}", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 ShowRepositorySetupIfNeeded");
        foreach (var forbidden in new[] { "ObjectStore", "PackageRepository", "PackageImportService" })
        {
            Assert.DoesNotContain(forbidden, handler.Value);
        }
    }

    [Fact]
    public void 引导失败不阻断启动()
    {
        // 一个可选的引导把用户挡在主界面之外是完全不成比例的代价
        var app = ReadSource("App.xaml.cs");
        var handler = Regex.Match(app,
            @"private void ShowRepositorySetupIfNeeded\(\).*?\n        \}", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 ShowRepositorySetupIfNeeded");
        Assert.Matches(@"catch\s*\(Exception", handler.Value);
        Assert.DoesNotContain("Shutdown()", handler.Value);
        Assert.DoesNotContain("throw", handler.Value);
    }

    [Fact]
    public void 引导界面不用原生MessageBox()
    {
        // 审计项 P1-7 正在清理原生弹窗对暗色主题的破坏，这里不能再添一处
        var window = ReadSource("Views", "RepositorySetupWindow.xaml.cs");

        Assert.DoesNotMatch(@"(?<!Cyber)MessageBox\.Show", window);
        Assert.Contains("CyberMessageBox.Show", window);
    }

    [Fact]
    public void 引导窗口的每一条离开路径都会记账()
    {
        // "跳过的人不该被问第二次"是硬要求。关闭窗口的路径数不清（标题栏按钮、Alt+F4、
        // Owner 关闭、系统菜单），逐个按钮记账必然漏，所以统一由 OnClosed 兜底。
        var window = ReadSource("Views", "RepositorySetupWindow.xaml.cs");

        var onClosed = Regex.Match(window,
            @"protected override void OnClosed\(EventArgs e\).*?\n        \}", RegexOptions.Singleline);

        Assert.True(onClosed.Success, "找不到 OnClosed 兜底");
        Assert.Contains("_service.Skip()", onClosed.Value);
        Assert.Contains("_settled", onClosed.Value);
    }

    [Fact]
    public void 引导只碰仓库位置_不动生成物与备份根()
    {
        // 三个可自定义的数据根里只有仓库可能到几十 GB。备份根下面是部署事务备份，
        // 崩溃回滚依赖它；把它引到可移动盘上，U 盘一拔就等于回滚能力消失。
        // 这两项目前在设置界面上也没有入口，问了用户连改回来的地方都没有。
        var service = ReadSource("Services", "RepositorySetupService.cs");
        var window = ReadSource("Views", "RepositorySetupWindow.xaml.cs");

        foreach (var source in new[] { service, window })
        {
            Assert.DoesNotContain("SaveOverwritesRoot", source);
            Assert.DoesNotContain("SaveBackupsRoot", source);
        }
    }

    [Fact]
    public void 已问过是独立标记而不是靠有没有选过位置推出来()
    {
        // 拿"配置里有没有仓库位置"当判据的话，点了"以后再说"的人每次启动都会被拦一次
        var prefs = ReadSource("Services", "UiPreferences.cs");

        Assert.Contains("RepositoryLocationPrompted", prefs);
        Assert.Contains("LoadRepositoryLocationPrompted", prefs);
        Assert.Contains("SaveRepositoryLocationPrompted", prefs);

        // 标记走静默写入：它不是设置界面上的一项，用户看不见，
        // 弹一个"保存标记失败"只会让人莫名其妙，而失败后果只是下次再问一次
        var save = Regex.Match(prefs,
            @"public static void SaveRepositoryLocationPrompted\(\).*?\n        \}", RegexOptions.Singleline);
        Assert.True(save.Success, "找不到 SaveRepositoryLocationPrompted");
        Assert.Contains("WriteQuietly", save.Value);
    }

    // ─── 视觉：必须与既有暗色主题同源 ───

    [Fact]
    public void 引导界面引用的每个主题令牌都真实存在()
    {
        // 引导跑在首次启动，界面上一个错的资源 key 会让整个窗口 XamlParseException。
        // 它被 App 的 try/catch 兜住不至于崩，但表现是"这个引导对所有新用户永远不出现"，
        // 而无头环境点不了 WPF，行为测试盖不住。这里直接对着 XAML 的引用做静态核对。
        //
        // 新版引导的样式、几何图标和转换器定义在 Window.Resources，
        // 颜色由全局主题提供；两类资源都需要核对，包含延迟解析的 DynamicResource。
        var xaml = ReadSource("Views", "RepositorySetupWindow.xaml");
        var available = ThemeResourceLoader.CaptureMerged().Keys.ToHashSet(StringComparer.Ordinal);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace markup = "http://schemas.microsoft.com/winfx/2006/xaml";
        var localResources = XDocument.Parse(xaml).Root!.Element(presentation + "Window.Resources");
        if (localResources != null)
            available.UnionWith(localResources.Elements().Attributes(markup + "Key").Select(key => key.Value));

        var missing = Regex.Matches(xaml, @"\{(?:StaticResource|DynamicResource)\s+(\w+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !available.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0,
            "引导界面引用了不存在的主题资源: " + string.Join(", ", missing));
    }

    [Fact]
    public void 引导界面不写死颜色()
    {
        // 视觉必须与现有暗色主题同源。写死一个 #RRGGBB 意味着换主题时这一个窗口不跟着变，
        // 而且它是新加的窗口，最容易成为"主题里唯一一块对不上的地方"。
        var xaml = ReadSource("Views", "RepositorySetupWindow.xaml");

        var hardcoded = Regex.Matches(xaml, @"""#[0-9A-Fa-f]{3,8}""")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(hardcoded.Count == 0,
            "引导界面写死了颜色，应改用 CyberDarkTheme 的令牌: " + string.Join(", ", hardcoded));
    }
}
