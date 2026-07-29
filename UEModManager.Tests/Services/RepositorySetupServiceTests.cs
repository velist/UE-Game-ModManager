using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Tests.Services;

/// <summary>
/// 首次运行引导的 IO 编排层。
///
/// <para>
/// <b>本类的绝大多数用例都在证明"不弹"</b>。错弹一次的代价是：一个已经在默认位置攒了
/// 几十 GB 的老用户被问"MOD 放哪个盘"，他认真挑一个大盘之后，引导只改配置指针、不搬数据，
/// 于是他原有的全部 MOD 在界面上当场消失。漏弹的代价只是少一次提醒。
/// </para>
///
/// <para>
/// <b>绝不触碰开发机真实的 <c>%LOCALAPPDATA%</c> / <c>%APPDATA%</c> / 安装目录。</b>
/// 路径与偏好全部由 <see cref="RepositorySetupEnvironment"/> 注入，本类构造的环境只指向
/// <see cref="_root"/> 下的临时目录，偏好用内存替身。这正是把 <c>AppPaths</c> /
/// <c>UiPreferences</c> 两个静态全局挡在服务外面的原因——本项目已经踩过三次这个坑。
/// </para>
/// </summary>
public sealed class RepositorySetupServiceTests : IDisposable
{
    private readonly string _root;

    /// <summary>假的当前仓库位置（默认位置）。</summary>
    private readonly string _repository;

    /// <summary>假的旧默认仓库位置（<c>%APPDATA%\UEModManager\Repository</c>）。</summary>
    private readonly string _legacyRepository;

    /// <summary>假的安装目录。</summary>
    private readonly string _install;

    /// <summary>假的 <c>%LOCALAPPDATA%\UEModManager</c>。</summary>
    private readonly string _local;

    private readonly FakePreferences _prefs = new();

    public RepositorySetupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm_setup_" + Guid.NewGuid().ToString("N")[..8]);
        _repository = Path.Combine(_root, "local", "Repository");
        _legacyRepository = Path.Combine(_root, "roaming", "Repository");
        _install = Path.Combine(_root, "install");
        _local = Path.Combine(_root, "local");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ─── 环境构造 ───

    /// <summary>与 <c>RepositorySetupService.CreateProductionEnvironment</c> 逐项同构，只是根换成临时目录。</summary>
    private RepositorySetupPaths BuildPaths() => new(
        CurrentRepositoryRoot: _repository,
        LegacyRepositoryRoot: _legacyRepository,
        ConfigFile: Path.Combine(_local, "config.json"),
        LegacyConfigFile: Path.Combine(_install, "config.json"),
        LegacyDataDirectory: Path.Combine(_install, "Data"),
        LegacyModBackupsDirectory: Path.Combine(_install, "Backups"),
        InstallDirectory: _install);

    private RepositorySetupService CreateService(IRepositorySetupPreferences? prefs = null)
        => new(NullLogger<RepositorySetupService>.Instance,
            new RepositorySetupEnvironment(BuildPaths(), prefs ?? _prefs));

    private static void Seed(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ─── 全新用户：弹 ───

    [Fact]
    public void 全新安装会弹()
    {
        var decision = CreateService().Decide();

        Assert.True(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.NotSkipped, decision.SkipReason);
    }

    [Fact]
    public void 仓库目录存在但是空的仍然算全新()
    {
        // 目录本身会被 AppPaths.TryEnsureDirectory / ObjectStore.EnsureInitialized 顺手建出来，
        // 拿"目录存在"当判据会让引导对几乎所有人失效
        Directory.CreateDirectory(_repository);
        Directory.CreateDirectory(Path.Combine(_install, "Data"));

        Assert.True(CreateService().Decide().ShouldPrompt);
    }

    // ─── 老用户的每一种形态：不弹 ───

    [Fact]
    public void 默认仓库里已经有包时不弹()
    {
        // 最危险的一种。换位置只改指针不搬数据，这些包会在界面上当场消失。
        Seed(Path.Combine(_repository, "SomeMod", "manifest.json"), "{}");

        var decision = CreateService().Decide();

        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.RepositoryHasContent, decision.SkipReason);
    }

    [Fact]
    public void 旧默认仓库里已经有包时不弹()
    {
        // 搬迁器通常会把它原地登记进配置，但登记本身可能失败（配置不可写）。
        // 那时这一条是唯一还能认出老用户的证据。
        Seed(Path.Combine(_legacyRepository, "SomeMod", "manifest.json"), "{}");

        var decision = CreateService().Decide();

        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.LegacyRepositoryHasContent, decision.SkipReason);
    }

    [Fact]
    public void 配置里已有仓库位置时不弹()
    {
        // 两类人：在设置里改过的，以及被搬迁器原地登记过的老用户
        _prefs.RepositoryRoot = @"D:\MyMods";

        var decision = CreateService().Decide();

        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.RepositoryRootConfigured, decision.SkipReason);
    }

    [Fact]
    public void 搬迁器原地登记之后不弹()
    {
        // 这条钉的是时序：引导跑在搬迁器之后，才看得到它写下的位置。
        // 反过来的话，"老用户 + 旧仓库有数据"会在登记发生前被读成"未配置"。
        Seed(Path.Combine(_legacyRepository, "SomeMod", "manifest.json"), "{}");
        _prefs.RepositoryRoot = _legacyRepository;   // 搬迁器 RegisterInPlace 的效果

        Assert.False(CreateService().Decide().ShouldPrompt);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("install")]
    public void 主配置存在时不弹(string where)
    {
        // config.json 只在用户配置过游戏路径后才被写出，是"真的用过这个软件"最直接的证据。
        // 新旧两个位置都要认：搬移开关至今关着，老用户那份还在安装目录里。
        Seed(where == "local"
            ? Path.Combine(_local, "config.json")
            : Path.Combine(_install, "config.json"), "{}");

        var decision = CreateService().Decide();

        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.AppConfigExists, decision.SkipReason);
    }

    [Theory]
    [InlineData("Data")]
    [InlineData("Backups")]
    public void 安装目录里有旧版数据时不弹(string folder)
    {
        // v1.x 升级上来的用户。生产搬移开关至今是 false，这些数据一直原样躺着。
        Seed(Path.Combine(_install, folder, "悟空_mods.json"), "[]");

        var decision = CreateService().Decide();

        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.LegacyInstallDataExists, decision.SkipReason);
    }

    [Fact]
    public void 同名文件占住仓库路径时不抛()
    {
        // 目录位置上是一个文件属于病态形态，但判定跑在启动早期，
        // 任何形态下都必须给出一个结论而不是把应用带崩。
        Seed(_repository, "我是文件，不是目录");

        var decision = CreateService().Decide();

        Assert.Equal(RepositorySetupSkipReason.NotSkipped, decision.SkipReason);
    }

    [Fact]
    public void 判定出错时不弹()
    {
        // 引导跑在启动早期。一个判定失败把用户挡在主界面之前是完全不成比例的代价。
        // 原因单独一档：日志里要分得清"这台机器有使用痕迹"和"我们根本没判出来"。
        var decision = CreateService(new ThrowingPreferences()).Decide();

        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.ProbeFailed, decision.SkipReason);
    }

    // ─── 跳过之后不再问 ───

    [Fact]
    public void 跳过之后不再弹()
    {
        var service = CreateService();
        Assert.True(service.Decide().ShouldPrompt);

        service.Skip();

        var decision = CreateService().Decide();
        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.AlreadyPrompted, decision.SkipReason);
    }

    [Fact]
    public void 跳过不写任何位置()
    {
        // "以后再说"的语义就是什么都不做，位置保持默认
        CreateService().Skip();

        Assert.Null(_prefs.RepositoryRoot);
        Assert.True(_prefs.Prompted);
    }

    [Fact]
    public void 标记写失败也不抛()
    {
        // 跳过路径上没有 UI 能承接这个错误，而失败后果只是下次再问一次
        CreateService(new ThrowingPreferences { ThrowOnLoad = false }).Skip();
    }

    [Fact]
    public void 选了位置之后不再弹()
    {
        var service = CreateService();
        var target = Path.Combine(_root, "chosen");

        service.Apply(target);

        Assert.False(CreateService().Decide().ShouldPrompt);
    }

    // ─── 落盘 ───

    [Fact]
    public void 采纳位置时先存位置再记标记()
    {
        var service = CreateService();
        var target = Path.Combine(_root, "chosen");

        service.Apply(target);

        Assert.Equal(target, _prefs.RepositoryRoot);
        Assert.True(_prefs.Prompted);
        Assert.True(Directory.Exists(target));
        Assert.Equal(new[] { "SaveRepositoryRoot", "SavePrompted" }, _prefs.Calls);
    }

    [Fact]
    public void 位置写失败时不记标记()
    {
        // 反过来的话，用户既没设置成功、下次也不会再被问，那个选择就永久丢了
        var prefs = new ThrowingPreferences { ThrowOnLoad = false, ThrowOnSaveRoot = true };
        var service = CreateService(prefs);

        Assert.ThrowsAny<Exception>(() => service.Apply(Path.Combine(_root, "chosen")));
        Assert.False(prefs.Prompted);
    }

    [Fact]
    public void 空位置被拒绝()
    {
        Assert.Throws<ArgumentException>(() => CreateService().Apply("   "));
    }

    // ─── 候选位置检查 ───

    [Fact]
    public void 空目录直接当仓库根()
    {
        var target = Path.Combine(_root, "empty");
        Directory.CreateDirectory(target);

        var verdict = CreateService().Inspect(target);

        Assert.True(verdict.CanUse);
        Assert.Equal(target, verdict.ResolvedPath);
    }

    [Fact]
    public void 非空目录落点退到专用子目录()
    {
        // 挡的是 RepositoryReclaimPlanner 第 5 条判据在防的那个场景：
        // 用户把仓库根指到自己的文件夹，里面每个子目录都"没 manifest、不在索引里"。
        // 引导不制造它。
        var target = Path.Combine(_root, "games");
        Seed(Path.Combine(target, "存档.sav"));

        var verdict = CreateService().Inspect(target);

        Assert.True(verdict.CanUse);
        Assert.Equal(Path.Combine(target, "UEModManager", "Repository"), verdict.ResolvedPath);
        Assert.Contains(RepositoryLocationIssueCode.DirectoryNotEmpty,
            verdict.Issues.Select(i => i.Code));
    }

    [Fact]
    public void 不可写的位置被拦下()
    {
        // 制造手法与 SaveFailureReportingTests 一致：在目录该在的位置放一个同名文件，
        // 之后任何 CreateDirectory 都抛 IOException。不需要动权限、跨机器稳定、不留残留。
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "我是文件，不是目录");

        var verdict = CreateService().Inspect(Path.Combine(blocker, "repo"));

        Assert.False(verdict.CanUse);
        Assert.Equal(RepositoryLocationSeverity.Blocked, verdict.Severity);
        Assert.Equal(new[] { RepositoryLocationIssueCode.NotWritable },
            verdict.Issues.Select(i => i.Code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"relative\path")]
    public void 路径本身不成立时不做任何IO(string? path)
    {
        var verdict = CreateService().Inspect(path);

        Assert.False(verdict.CanUse);
        // 一个 IO 都不该发生：相对路径会在当前工作目录下建出目录来
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "relative")));
    }

    [Fact]
    public void 检查完把自己建的目录退回去()
    {
        // 界面上每换一个盘就检查一次。不退回的话，用户点过的每个盘上都会躺着一对
        // 他从没确认过的空目录 —— 那是我们制造的垃圾。
        var target = Path.Combine(_root, "probe", "deep");

        CreateService().Inspect(target);

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(_root, "probe")));
    }

    [Fact]
    public void 检查不会动用户已有的目录()
    {
        // 只退我们自己建的那几层：用户先建好的空文件夹必须原样留着
        var target = Path.Combine(_root, "prepared");
        Directory.CreateDirectory(target);

        CreateService().Inspect(target);

        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void 选到安装目录里会给警告()
    {
        // 卸载与覆盖安装会清空安装目录。把用户数据放进去正是数据搬迁那一整轮要根治的病根。
        Directory.CreateDirectory(_install);
        var target = Path.Combine(_install, "MyRepo");

        var verdict = CreateService().Inspect(target);

        Assert.True(verdict.CanUse);
        Assert.True(verdict.NeedsConfirmation);
        Assert.Contains(RepositoryLocationIssueCode.InsideInstallDirectory,
            verdict.Issues.Select(i => i.Code));
    }

    [Fact]
    public void 安装目录的兄弟目录不算在安装目录里()
    {
        // 前缀比较不带分隔符的话，C:\App 会把 C:\AppData 也算成自己的子目录
        var sibling = _install + "Data";
        Directory.CreateDirectory(sibling);

        var verdict = CreateService().Inspect(sibling);

        Assert.DoesNotContain(RepositoryLocationIssueCode.InsideInstallDirectory,
            verdict.Issues.Select(i => i.Code));
    }

    // ─── 磁盘列表 ───

    [Fact]
    public void 磁盘列表不含未就绪的卷且已排好序()
    {
        // 真机上至少有一个系统盘。这里只确认它不抛、不返回未就绪的卷，
        // 排序规则本身由 Core 的 RepositoryDriveAdvisorTests 覆盖。
        var drives = CreateService().ListDrives();

        Assert.All(drives, d => Assert.False(string.IsNullOrWhiteSpace(d.RootPath)));
        Assert.Equal(
            RepositoryDriveAdvisor.Rank(drives).Select(d => d.RootPath),
            drives.Select(d => d.RootPath));
    }

    // ─── 偏好替身 ───

    private sealed class FakePreferences : IRepositorySetupPreferences
    {
        public string? RepositoryRoot { get; set; }
        public bool Prompted { get; private set; }

        /// <summary>调用顺序。<c>Apply</c> 必须先存位置再记标记，这里是唯一能钉住它的地方。</summary>
        public List<string> Calls { get; } = new();

        public string? LoadRepositoryRoot() => RepositoryRoot;

        public void SaveRepositoryRoot(string? path)
        {
            Calls.Add(nameof(SaveRepositoryRoot));
            RepositoryRoot = path;
        }

        public bool LoadPrompted() => Prompted;

        public void SavePrompted()
        {
            Calls.Add(nameof(SavePrompted));
            Prompted = true;
        }
    }

    private sealed class ThrowingPreferences : IRepositorySetupPreferences
    {
        public bool ThrowOnLoad { get; init; } = true;
        public bool ThrowOnSaveRoot { get; init; }
        public bool Prompted { get; private set; }

        public string? LoadRepositoryRoot() => null;

        public void SaveRepositoryRoot(string? path)
        {
            if (ThrowOnSaveRoot) throw new IOException("配置写不进去");
        }

        public bool LoadPrompted()
            => ThrowOnLoad ? throw new IOException("配置读不出来") : Prompted;

        public void SavePrompted() => throw new IOException("配置写不进去");
    }
}
