using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services.Conflict;
using UEModManager.Tests.Infrastructure;
using UEModManager.Tests.Services;
using UEModManager.ViewModels;
using UEModManager.Views;

namespace UEModManager.Tests.Views;

/// <summary>
/// 冲突面板的接线回归。
///
/// <para>
/// 事故原型：<c>OpenConflictPanel</c> 曾从 DI 容器取 <c>ConflictWindow</c>；窗口构造函数要一个
/// <c>MainViewModel</c>，而容器把它注册成 Transient，于是面板拿到的是<b>另一个</b>
/// MainViewModel —— 它的 <c>AllMods</c> 是空的，禁用动作在空列表里找不到目标，
/// 表现成"面板显示已禁用 X 并重新部署，主列表和游戏目录都不动"。
/// </para>

/// <para>
/// 这条错误在运行期没有别的信号：面板照常显示、日志里也没有异常。所以两头都钉住 ——
/// 窗口持有的必须是调用方传进来的那个实例，且容器里不能再有这条注册。
/// </para>
///
/// <para>
/// 跑在<see cref="StaApplication"/> 上而不是 <c>ThemeResourceLoader.RunOnSta</c>：前者是常驻的
/// 单个 Application，而本用例要建的是**真的窗口**（<c>InitializeComponent</c> 会解析 XAML），
/// 需要在 Application 级的主题字典里查 <c>CyberModalWindow</c> 之类的样式。
/// </para>
/// </summary>
public sealed class ConflictWindowWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ConflictWindowWiring", Guid.NewGuid().ToString("N"));

    public ConflictWindowWiringTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task 面板持有的就是调用方传进来的那个主视图模型()
        => await StaApplication.RunAsync(async () =>
        {
            // 装配一份真的 MainViewModel —— 只有真对象才能证明"面板拿到的就是这个"。
            // 必须在派发线程上 await（不能 GetResult）：装配过程里的续体会回到这个线程。
            var host = await ConflictTestHost.OpenAsync(_root);
            WithAppTheme(() =>
            {
                var window = NewWindow(host.Conflicts, host.Main);
                try { Assert.Same(host.Main, window.ViewModelForTests.MainForTests); }
                finally { window.Close(); }
            });
            return true;
        });

    [Fact]
    public void 容器里不再有冲突面板的注册()
    {
        // 有注册就说明有人还能从容器取到"另一个 MainViewModel 的窗口"。
        var app = File.ReadAllText(Path.Combine(LanguageIntegrationTests.AppSource, "App.xaml.cs"));
        Assert.DoesNotContain("AddTransient<Views.ConflictWindow>", app);
        Assert.DoesNotContain("AddTransient<ConflictWindow>", app);
    }

    private static ConflictWindow NewWindow(ConflictModuleService conflictModule, MainViewModel main)
        => new(conflictModule, main, NullLogger<ConflictWindow>.Instance);

    /// <summary>
    /// 把 App.xaml 的两件事补到当前 Application 上：主题字典与那几个转换器资源。
    ///
    /// 重复调用无副作用。窗口的 XAML 会在解析期用 StaticResource 查它们 ——
    /// 查不到是 <c>XamlParseException</c>，所以这里必须补齐，不能只挂主题。
    /// </summary>
    private static void WithAppTheme(Action check)
    {
        var application = Application.Current;
        if (application.Resources.MergedDictionaries.Count == 0)
            foreach (var name in new[] { "CyberDarkTheme", "CyberStyles" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/UEModManager;component/Themes/{name}.xaml")
                });

        // App.xaml 在主题之外还声明了这几个转换器；面板的 XAML 解析期就要查 BoolToVisibilityConverter。
        if (!application.Resources.Contains("BoolToVisibilityConverter"))
        {
            application.Resources["BoolToVisibilityConverter"] =
                new global::UEModManager.Converters.BooleanToVisibilityConverter();
        }

        check();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
