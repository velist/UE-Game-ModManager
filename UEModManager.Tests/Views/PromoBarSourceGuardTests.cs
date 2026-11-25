using System.Text.RegularExpressions;
using UEModManager.Services.Promo;

namespace UEModManager.Tests.Views;

/// <summary>
/// 对推广条相关源码本身的静态检查。
///
/// <para>
/// 主窗口的 code-behind 要一整套服务才构造得出来，测试里跑不起来；
/// 所以「写法层面」的几条约定在这里直接对源文件断言——它们都是应用所有者明确提过的要求，
/// 而且都属于"下次有人顺手一改就破、破了也没有任何测试会响"的那一类：
/// 动效只走 <c>MotionAssist</c>、处理器只走 <c>SafeEvent.Run</c>、不联网不统计、活动内容只有一份。
/// </para>
/// </summary>
public sealed class PromoBarSourceGuardTests
{
    private static string AppSource => LanguageIntegrationTests.AppSource;

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { AppSource }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    private static string ReadCore(string fileName)
    {
        var path = Path.Combine(AppSource, "..", "UEModManager.Core", "Services", "Promo", fileName);
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    /// <summary>去掉整行的注释（文档注释里会提到被禁止的写法本身）。行尾注释与字符串里的 // 不动。</summary>
    private static string Code(string source)
        => string.Join("\n", source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string CodeBehind => Code(Read("MainWindow.PromoBar.cs"));

    /// <summary>MainWindow.xaml 里推广条那一段（从 PromoBar 外壳到工具栏之前），不含注释。</summary>
    private static string PromoBarXaml()
    {
        var xaml = Read("MainWindow.xaml");
        var start = xaml.IndexOf("<Border x:Name=\"PromoBar\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("x:Name=\"MainToolbar\"", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到推广条在 MainWindow.xaml 里的位置");
        return StripXmlComments(xaml[start..end]);
    }

    /// <summary>MainWindow.xaml 的窗口资源里推广条用的画刷与样式那一段，不含注释。</summary>
    private static string PromoResourcesXaml()
    {
        var xaml = Read("MainWindow.xaml");
        var start = xaml.IndexOf("x:Key=\"PromoGoldBrush\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("x:Key=\"MoveToCategoryMenuItem\"", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到推广条的窗口资源");
        return StripXmlComments(xaml[start..end]);
    }

    private static string StripXmlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?(-->|$)", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void 推广条的动效全部走MotionAssist_自己不写动画()
    {
        var code = CodeBehind;
        foreach (var forbidden in new[] { "BeginAnimation", "Storyboard", "DoubleAnimation", "ColorAnimation", "System.Windows.Media.Animation" })
            Assert.DoesNotContain(forbidden, code);

        // 入场、高光、收起各调一次库里的方法
        Assert.Contains("MotionAssist.Reveal(PromoBar)", code);
        Assert.Contains("MotionAssist.Shine(PromoShine, PromoAmount,", code);
        Assert.Contains("MotionAssist.Conceal(PromoBar)", code);
        Assert.Contains("MotionAssist.StopShine(PromoShine)", code);

        // XAML 这边也一样：推广条那一段和它的样式里都没有 Storyboard
        foreach (var xaml in new[] { PromoBarXaml(), PromoResourcesXaml() })
        {
            Assert.DoesNotContain("Storyboard", xaml);
            Assert.DoesNotContain("Animation ", xaml);
        }
    }

    [Fact]
    public void 推广条的事件处理器都走SafeEvent_没有裸async_void()
    {
        var code = CodeBehind;
        Assert.DoesNotMatch(@"\basync\s+void\b", code);

        // 会干活的两个动作（领取、关闭）都包在 SafeEvent.Run 里，出错时带着操作名弹给玩家
        Assert.Equal(2, Regex.Matches(code, @"SafeEvent\.Run\(this,").Count);
        Assert.Contains("_logger, \"打开推广链接\");", code);
        Assert.Contains("_logger, \"关闭推广条\");", code);

        // XAML 里接的每个处理器在分部文件里都有（接了不存在的处理器，运行时才会炸）
        var handlers = Regex.Matches(PromoBarXaml(), @"\b(?:Click|MouseLeftButtonDown|MouseLeftButtonUp|MouseLeave)=""(\w+)""")
            .Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.Equal(5, handlers.Count);
        foreach (var handler in handlers)
            Assert.Contains($"private void {handler}(object sender,", code);
    }

    [Fact]
    public void 启动流程只安排一次推广条_排在新手引导之后_别处不碰它()
    {
        var view = Read("MainWindow.xaml.cs");
        Assert.Single(Regex.Matches(view, @"\bSchedulePromoBar\(\)"));

        var initialize = Regex.Match(view,
            @"private async Task InitializeAsync\(\).*?\n        \}", RegexOptions.Singleline);
        Assert.True(initialize.Success, "找不到 MainWindow.InitializeAsync");
        var onboarding = initialize.Value.IndexOf("StartOnboardingIfNeeded", StringComparison.Ordinal);
        var promo = initialize.Value.IndexOf("SchedulePromoBar()", StringComparison.Ordinal);
        Assert.True(onboarding >= 0 && promo > onboarding, "推广条应在 InitializeAsync 里、排在新手引导之后安排");

        // 主窗口的主文件只有那一行调用：推广条的控件只由 MainWindow.PromoBar.cs 操作
        Assert.DoesNotMatch(@"\bPromo(Bar|Shine|Amount|CouponCard|ClaimButton|CloseButton|BarService)\b", Code(view));

        // 新手引导没看过时不出现，这一条由判定守着：安排之前先问过服务
        Assert.Matches(@"!service\.Evaluate\(\)\.ShouldShow\)\s*return;", CodeBehind);
        // 显示之前必须经过「判定 + 记账」那一个入口，没有别的路能让它出现
        Assert.Single(Regex.Matches(CodeBehind, @"\bShowPromoBar\(campaign\)"));
        Assert.Matches(@"TryBeginDisplay\(\);\s*if \(campaign != null\) ShowPromoBar\(campaign\);", CodeBehind);
    }

    [Fact]
    public void 推广相关的代码不联网不上报_展示和点击不留日志()
    {
        var sources = new Dictionary<string, string>
        {
            ["MainWindow.PromoBar.cs"] = CodeBehind,
            ["PromoBarService.cs"] = Code(Read("Services", "PromoBarService.cs")),
            ["PromoCampaign.cs"] = Code(ReadCore("PromoCampaign.cs")),
            ["PromoLink.cs"] = Code(ReadCore("PromoLink.cs")),
            ["PromoSchedule.cs"] = Code(ReadCore("PromoSchedule.cs")),
        };

        foreach (var (file, code) in sources)
        {
            // 到期日写死在程序里：没有任何取远程配置的路
            foreach (var network in new[] { "HttpClient", "WebClient", "WebRequest", "System.Net", "Socket", "GetAsync", "PostAsync" })
                Assert.False(code.Contains(network, StringComparison.Ordinal), $"{file} 里出现了 {network}");

            // 不碰统计上报那一套
            Assert.False(code.Contains("Telemetry", StringComparison.Ordinal), $"{file} 里出现了 Telemetry");

            // 诊断包是可以导出发给开发者的：日志里留一条「点了广告」就等于变相统计
            foreach (var log in new[] { "LogInformation", "LogWarning", "LogError", "LogCritical", "LogTrace" })
                Assert.False(code.Contains(log, StringComparison.Ordinal), $"{file} 里出现了 {log}");
        }

        // 界面这边只在出异常时记一笔调试日志，每一条都带着异常
        Assert.DoesNotMatch(@"LogDebug\((?!ex,)", sources["MainWindow.PromoBar.cs"]);

        // 服务里唯一的一条输出是「打开浏览器失败」
        var service = sources["PromoBarService.cs"];
        Assert.DoesNotContain("ILogger", service);
        var writes = Regex.Matches(service, @"Console\.WriteLine\(([^\n]*)");
        Assert.Single(writes);
        Assert.Contains("打开浏览器失败", writes[0].Value);
    }

    [Fact]
    public void 打开链接只有一处_先过https放行检查_再交给系统浏览器()
    {
        var service = Code(Read("Services", "PromoBarService.cs"));

        Assert.Single(Regex.Matches(service, @"Process\.Start\("));
        Assert.Contains("UseShellExecute = true", service);

        // 放行检查在把链接交出去之前
        var gate = service.IndexOf("if (!PromoLink.IsAllowed(campaign.Url)) return PromoOpenResult.Blocked;", StringComparison.Ordinal);
        var open = service.IndexOf("_openInBrowser(campaign.Url);", StringComparison.Ordinal);
        Assert.True(gate >= 0 && open > gate, "交给浏览器之前必须先过 PromoLink.IsAllowed");

        // 界面不自己开进程，只走服务
        Assert.DoesNotContain("Process.Start", CodeBehind);
        Assert.DoesNotContain("ProcessStartInfo", CodeBehind);
        Assert.Contains("PromoBarService?.Open(campaign)", CodeBehind);
    }

    [Fact]
    public void 活动的文案和链接只有Core里那一份()
    {
        var campaign = PromoCampaigns.GptTopUpCoupon;

        // 链接：主程序的任何源文件里都不出现，只在 Core 的活动数据里
        var host = new Uri(campaign.Url).Host;
        var offenders = Directory.EnumerateFiles(AppSource, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".json")
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(host, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(AppSource, path))
            .ToList();
        Assert.True(offenders.Count == 0, "活动链接应只写在 Core 的 PromoCampaigns 里，这些文件里又出现了一份：" + string.Join(", ", offenders));

        // 文案：XAML 全靠绑定，不写死任何一句——换活动只改 Core 一处
        var xaml = PromoBarXaml();
        foreach (var binding in new[] { "CurrencySymbol", "Amount", "StubCaption", "Title", "Subtitle", "ActionText" })
            Assert.Contains("{Binding " + binding + "}", xaml);
        foreach (var literal in new[] { campaign.Title, campaign.Subtitle, campaign.StubCaption, campaign.ActionText })
            Assert.DoesNotContain(literal, xaml);

        // code-behind 也不写文案（出错提示复用的是已有的那句通用话）
        foreach (var literal in new[] { campaign.Title, campaign.Subtitle, campaign.StubCaption, campaign.ActionText })
            Assert.DoesNotContain(literal, CodeBehind);
    }

    [Fact]
    public void 推广用的画刷和样式留在主窗口里_不进主题字典()
    {
        // 这是一张到期就下线的券：不给全局设计系统留下永久的键（主题快照也因此不用改）
        foreach (var theme in new[] { "CyberDarkTheme.xaml", "CyberStyles.xaml" })
            Assert.DoesNotContain("Promo", Read("Themes", theme));

        var resources = PromoResourcesXaml();
        foreach (var key in new[] { "PromoGoldTextBrush", "PromoFaceBrush", "PromoStubBrush", "PromoEdgeBrush", "PromoInkBrush", "PromoClaimButtonStyle", "PromoCloseButtonStyle" })
            Assert.Contains($"x:Key=\"{key}\"", resources);
    }
}
