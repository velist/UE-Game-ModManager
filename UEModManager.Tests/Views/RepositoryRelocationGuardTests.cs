using System.Text.RegularExpressions;
using UEModManager.Tests.Themes;

namespace UEModManager.Tests.Views;

/// <summary>
/// "换个地方存 MOD"的源码守卫。
///
/// <para>
/// 这条功能要防的事故没有运行期信号：改仓库位置只改指针不搬数据，程序不会抛异常、
/// 不会写日志，用户看到的只是"我的 MOD 全没了"。行为测试盖不住"下次重构又写回去"，
/// 本项目已经因为丢接线出过三次事故，所以对源码本身断言。
/// </para>
/// </summary>
[Collection(ThemeResourceCollection.Name)]
public class RepositoryRelocationGuardTests
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

    private static IEnumerable<string> AllProductionSources()
        => Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "UEModManager"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// 去掉注释再断言。
    ///
    /// <para>
    /// 这些守卫查的是"代码里还有没有这一行"，而这轮改动恰恰在注释里<b>反复引用</b>了
    /// 被删掉的那个方法名（"此前这里是一行 SetRepositoryRoot——只改指针不搬数据"）。
    /// 不剥注释的话，守卫会被自己要求写下的解释绊倒，而真正的修法是删掉那句解释
    /// ——那等于为了让测试变绿删掉最该留下的说明。
    /// </para>
    /// </summary>
    private static string StripComments(string source)
    {
        // 先块注释后行注释；字符串字面量里出现 "//" 的情况在本项目的这几个文件里不存在，
        // 且误剥的后果只是守卫少看几个字符，不会放过真正的调用。
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    // ═══════════════════════════════════════
    //  一个功能一条路径
    // ═══════════════════════════════════════

    [Fact]
    public void 不存在只改指针的换位置方法()
    {
        // 这是整轮改动的核心。ObjectStore 此前有一个 SetRepositoryRoot(path)：
        // 写配置 + 改内存，一行就能换掉仓库位置——而设置界面正是照着它写的。
        // 用户改完位置，已导入的包实体还躺在旧位置、新仓库是空的，界面上 MOD 全没了。
        //
        // 不是"约定只让某个服务调用"，是**整个删掉**：只要它还在，早晚会有第二处照着它写。
        foreach (var file in AllProductionSources())
        {
            var text = StripComments(File.ReadAllText(file));
            var calls = Regex.Matches(text, @"\.SetRepositoryRoot\s*\(");

            Assert.True(calls.Count == 0,
                $"{Path.GetFileName(file)} 里出现了 SetRepositoryRoot 调用。"
                + "换仓库位置必须连着搬数据，只改指针会让用户的 MOD 从界面上消失。"
                + "请走 RepositoryRelocationService。");
        }
    }

    [Fact]
    public void 界面层不直接写仓库位置偏好()
    {
        // 绕开服务直接写偏好等价于只改指针——同一个事故换了个写法。
        // 落盘那一半必须和搬移日记写在同一次原子写里（CommitRepositoryRelocation），
        // 否则断电恢复读到的两个值会互相矛盾。
        foreach (var file in AllProductionSources()
                     .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Views{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("SaveRepositoryRoot", text);
        }
    }

    [Fact]
    public void 设置界面改位置走的是搬移服务()
    {
        var settings = ReadSource("Views", "SettingsWindow.xaml.cs");

        Assert.Contains("RepositoryRelocationService", settings);
        Assert.Contains("RepositoryRelocationWindow", settings);
    }

    [Fact]
    public void 降级提示与设置界面用同一个服务和同一个窗口()
    {
        // 一个功能一条路径：两个入口若各写一套，其中一套迟早会漏掉搬数据那一步
        var main = ReadSource("MainWindow.xaml.cs");
        var settings = ReadSource("Views", "SettingsWindow.xaml.cs");

        foreach (var source in new[] { main, settings })
        {
            Assert.Contains("RepositoryRelocationService", source);
            Assert.Contains("RepositoryRelocationWindow", source);
        }
    }

    // ═══════════════════════════════════════
    //  一键：发现问题的位置就要能解决
    // ═══════════════════════════════════════

    [Fact]
    public void 降级提示里给的是动作按钮而不是只有知道了()
    {
        // 只告知不给动作，对相当一部分玩家等于没告知——他们会关掉弹窗然后放弃
        var main = ReadSource("MainWindow.xaml.cs");
        var handler = Regex.Match(main,
            @"private void ShowDeploymentDegradationNotice.*?\n        \}", RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 ShowDeploymentDegradationNotice");
        Assert.Contains("CanFixInPlace", handler.Value);
        Assert.Contains("MessageBoxButton.YesNo", handler.Value);
        Assert.Contains("FixButtonText", handler.Value);
    }

    [Fact]
    public void 搬移界面不用原生MessageBox()
    {
        // 审计项 P1-7 正在清理原生弹窗对暗色主题的破坏，这里不能再添一处
        var window = ReadSource("Views", "RepositoryRelocationWindow.xaml.cs");

        Assert.DoesNotMatch(@"(?<!Cyber)MessageBox\.Show", window);
    }

    [Fact]
    public void 搬移不占UI线程()
    {
        // 几十 GB 的复制跑在 UI 线程上就是"程序未响应"。服务侧用 Task.Run 把四步搬移
        // 整个丢到线程池，界面侧 await 它。
        var service = ReadSource("Services", "RepositoryRelocationService.cs");
        var window = ReadSource("Views", "RepositoryRelocationWindow.xaml.cs");

        Assert.Contains("Task.Run", service);
        Assert.Contains("await _service.ExecuteAsync", window);
        // UI 事件处理器统一用 SafeEvent.Run 包裹，不写裸 async void。
        // 匹配的是方法声明而不是"async void"这三个词——那三个词正好出现在解释
        // 为什么要用 SafeEvent 的注释里。
        Assert.Contains("SafeEvent.Run", window);
        Assert.DoesNotMatch(@"\basync\s+void\s+\w+\s*\(", StripComments(window));
    }

    [Fact]
    public void 搬移途中不许关窗口()
    {
        // 关掉窗口不会让后台那个复制停下来，而窗口一关主界面就解除了模态封锁——
        // 用户可以立刻去导入 MOD，导进去的包正好会随旧位置一起被清空。
        var window = ReadSource("Views", "RepositoryRelocationWindow.xaml.cs");
        var closing = Regex.Match(window,
            @"protected override void OnClosing\(CancelEventArgs e\).*?\n        \}",
            RegexOptions.Singleline);

        Assert.True(closing.Success, "找不到 OnClosing 拦截");
        Assert.Contains("e.Cancel = true", closing.Value);
        Assert.Contains("RequestCancel()", closing.Value);
    }

    // ═══════════════════════════════════════
    //  顺序不变量
    // ═══════════════════════════════════════

    [Fact]
    public void 搬移不绕开校验与墓碑()
    {
        // 四步搬移的顺序只有 DataRelocationExecutor.Execute 一处。服务侧若自己去调
        // CopyAndVerify / WriteTombstone / DeleteLegacy 编排一遍，就有了第二份必须
        // 永远保持一致的顺序表——而这种表迟早会漏掉墓碑或漏掉标记。
        var service = ReadSource("Services", "RepositoryRelocationService.cs");
        var runFourSteps = Regex.Match(service,
            @"private void RunFourSteps\(.*?\n        \}", RegexOptions.Singleline);

        Assert.True(runFourSteps.Success, "找不到 RunFourSteps");
        Assert.Contains("executor.Execute(", runFourSteps.Value);
        Assert.DoesNotContain("CopyAndVerify(", runFourSteps.Value);
        Assert.DoesNotContain("WriteTombstone(", runFourSteps.Value);
        Assert.DoesNotContain("DeleteLegacy(", runFourSteps.Value);
    }

    [Fact]
    public void Execute保持非虚以锁住顺序()
    {
        // 顺序正是这个类要守住的不变量，能被覆盖就守不住了。
        // 四个步骤方法是 virtual（换"某一步做了什么"），Execute 不是（不换顺序）。
        var executor = ReadSource("Services", "DataRelocationExecutor.cs");

        Assert.Matches(@"public void Execute\(RelocationStep", executor);
        Assert.DoesNotMatch(@"public virtual void Execute\(RelocationStep", executor);
    }

    [Fact]
    public void 落定是一次原子写()
    {
        // 存放位置与搬移日记必须一起落盘。分成两次写就必然存在一个中间态，
        // 而那一刻断电正是恢复判定唯一读不懂的状态。
        var prefs = ReadSource("Services", "UiPreferences.cs");
        var commit = Regex.Match(prefs,
            @"public static void CommitRepositoryRelocation\(.*?\n        \}", RegexOptions.Singleline);

        Assert.True(commit.Success, "找不到 CommitRepositoryRelocation");
        // 一个 Write 调用里同时改两样东西
        Assert.Contains("cfg.RepositoryRoot", commit.Value);
        Assert.Contains("cfg.RepositoryRelocationPhase", commit.Value);
        Assert.Single(Regex.Matches(commit.Value, @"\bWrite\("));
    }

    // ═══════════════════════════════════════
    //  启动时序
    // ═══════════════════════════════════════

    [Fact]
    public void 断电恢复排在数据搬迁之后_首次运行引导之前()
    {
        // 排在搬迁器之后：它的原地登记会写存放位置。
        // 排在引导之前：引导判定"老用户"最主要的两条判据是"配置里有没有仓库位置"
        // 和"当前仓库里有没有包"，而一次被中断的搬移恰好会让这两条都读成 false——
        // 于是一个 MOD 正躺在半搬完状态的老用户会被弹窗问"MOD 放哪个盘"。
        var app = ReadSource("App.xaml.cs");
        var body = Regex.Match(app,
            @"private async void ShowAuthenticationWindow\(\).*?\n        \}", RegexOptions.Singleline);

        Assert.True(body.Success, "找不到 ShowAuthenticationWindow");

        var migration = body.Value.IndexOf("migrator.RunAsync()", StringComparison.Ordinal);
        var recovery = body.Value.IndexOf(
            "RecoverInterruptedRepositoryRelocation()", StringComparison.Ordinal);
        var setup = body.Value.IndexOf("ShowRepositorySetupIfNeeded()", StringComparison.Ordinal);
        var firstResolve = body.Value.IndexOf(
            "GetRequiredService<LocalDbContext>()", StringComparison.Ordinal);

        Assert.True(recovery >= 0, "启动流程里找不到仓库搬移恢复");
        Assert.True(migration < recovery, "搬移恢复必须排在数据搬迁之后");
        Assert.True(recovery < setup, "搬移恢复必须排在首次运行引导之前");
        Assert.True(recovery < firstResolve,
            "搬移恢复必须早于任何业务服务解析：ObjectStore 构造时读一次存放位置就记进字段，"
            + "晚一步的话恢复推过去的新位置这次会话根本不生效，用户看到的是一个空仓库。");
    }

    [Fact]
    public void 恢复自己不解析ObjectStore()
    {
        // 解析它就等于把它构造出来，正好把"第一次读存放位置"的时机提前到恢复内部
        var app = ReadSource("App.xaml.cs");
        var handler = Regex.Match(app,
            @"private void RecoverInterruptedRepositoryRelocation\(\).*?\n        \}",
            RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 RecoverInterruptedRepositoryRelocation");
        Assert.DoesNotContain("ObjectStore", handler.Value);
    }

    [Fact]
    public void 恢复失败不阻断启动()
    {
        var app = ReadSource("App.xaml.cs");
        var handler = Regex.Match(app,
            @"private void RecoverInterruptedRepositoryRelocation\(\).*?\n        \}",
            RegexOptions.Singleline);

        Assert.True(handler.Success, "找不到 RecoverInterruptedRepositoryRelocation");
        Assert.Matches(@"catch\s*\(Exception", handler.Value);
        Assert.DoesNotContain("Shutdown()", handler.Value);
        Assert.DoesNotContain("throw", handler.Value);
    }

    [Fact]
    public void 服务用工厂拿ObjectStore而不是构造注入()
    {
        // 直接注入的话，"解析本服务"就等于"构造 ObjectStore"，
        // 而恢复恰恰要在 ObjectStore 构造之前跑完
        var app = ReadSource("App.xaml.cs");

        Assert.Contains("sp.GetRequiredService<ObjectStore>)", app);

        var service = ReadSource("Services", "RepositoryRelocationService.cs");
        Assert.Contains("Func<ObjectStore>", service);
    }

    // ═══════════════════════════════════════
    //  并发写入
    // ═══════════════════════════════════════

    [Theory]
    [InlineData("PackageRepository.cs")]
    [InlineData("RepositoryReclaimService.cs")]
    [InlineData("ProfileLockService.cs")]
    public void 不经过ObjectStore写方法的写入方都接了闸门(string fileName)
    {
        // 它们直接往仓库根下写文件，不走 ObjectStore 的写方法。漏掉任何一个的后果是：
        // 搬移期间写下的东西落在旧位置，搬完随旧位置一起被清空。
        var source = ReadSource("Services", fileName);

        Assert.Contains("ThrowIfRelocating", source);
    }

    [Fact]
    public void 闸门挡写但不挡读()
    {
        // 挡读会把一次正常的搬移变成满屏错误框：界面刷新、统计仓库大小、切游戏都是读。
        // 而读本来就是安全的——旧位置的数据在墓碑落地之前一个字节都不会少。
        var store = ReadSource("Services", "ObjectStore.cs");

        foreach (var reader in new[]
                 {
                     "public List<string> GetPackageFiles",
                     "public List<string> EnumeratePackageKeys",
                     "public long GetTotalSize",
                     "public bool PackageExists",
                 })
        {
            var method = Regex.Match(store,
                Regex.Escape(reader) + @".*?\n        \}", RegexOptions.Singleline);
            if (!method.Success) continue;

            Assert.DoesNotContain("ThrowIfRelocating", method.Value);
        }
    }

    // ═══════════════════════════════════════
    //  视觉：与既有暗色主题同源
    // ═══════════════════════════════════════

    [Fact]
    public void 搬移界面引用的每个主题令牌都真实存在()
    {
        // 错一个资源 key 的表现是 XamlParseException —— 被 SafeEvent 兜住不至于崩，
        // 但用户点了"帮我搬过去"之后什么都不会发生，而无头环境点不了 WPF
        var xaml = ReadSource("Views", "RepositoryRelocationWindow.xaml");
        var available = ThemeResourceLoader.CaptureMerged().Keys.ToHashSet(StringComparer.Ordinal);

        var missing = Regex.Matches(xaml, @"\{StaticResource\s+(\w+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !available.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0,
            "搬移界面引用了不存在的主题资源: " + string.Join(", ", missing));
    }

    [Fact]
    public void 搬移界面代码里用的主题令牌也真实存在()
    {
        // 三个状态的图标颜色是在 code-behind 里按状态切的（SetResourceReference），
        // XAML 那条测试盖不到它们
        var code = ReadSource("Views", "RepositoryRelocationWindow.xaml.cs");
        var available = ThemeResourceLoader.CaptureMerged().Keys.ToHashSet(StringComparer.Ordinal);

        var missing = Regex.Matches(code, @"SetResourceReference\(\w+Property,\s*""(\w+)""\)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !available.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0,
            "搬移界面代码里引用了不存在的主题资源: " + string.Join(", ", missing));
    }

    [Fact]
    public void 搬移界面不写死颜色()
    {
        var xaml = ReadSource("Views", "RepositoryRelocationWindow.xaml");

        var hardcoded = Regex.Matches(xaml, @"""#[0-9A-Fa-f]{3,8}""")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(hardcoded.Count == 0,
            "搬移界面写死了颜色，应改用 CyberDarkTheme 的令牌: " + string.Join(", ", hardcoded));
    }

    // ═══════════════════════════════════════
    //  文案不能再说"自己搬"
    // ═══════════════════════════════════════

    [Fact]
    public void 首次运行引导不再说要用户自己搬()
    {
        // 这句话曾经是对的（那时改位置确实只改指针），现在是错的，
        // 而且它出现在用户第一次见到本程序的那个窗口上
        var xaml = ReadSource("Views", "RepositorySetupWindow.xaml");

        Assert.DoesNotContain("需要自己搬过去", xaml);
        Assert.Contains("空间充足", xaml);
    }
}
