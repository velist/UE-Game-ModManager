using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Tests.Services;

/// <summary>
/// "首次运行"判据的取舍依据 —— 把两条<b>被否决的判据</b>为什么行不通用行为钉住，
/// 顺带证明引导排在数据搬迁器之后是必需的。
///
/// <para>
/// 这三条用例都是<b>把真实的 <see cref="DataLocationMigrator"/> 与
/// <see cref="RepositorySetupService"/> 串起来跑一遍</b>，搬移开关取生产值（false），
/// 两者共用同一份偏好 —— 与真机启动时序完全一致，只是路径换成临时目录。
/// </para>
///
/// <para>
/// <b>绝不触碰开发机真实的 <c>%LOCALAPPDATA%</c> / <c>%APPDATA%</c> / 安装目录。</b>
/// 两个服务的路径与偏好都是注入的，本类构造的环境只指向 <see cref="_root"/>。
/// </para>
/// </summary>
public sealed class FirstRunCriteriaRationaleTests : IDisposable
{
    private readonly string _root;
    private readonly string _install;
    private readonly string _local;
    private readonly string _roaming;
    private readonly SharedPreferences _prefs = new();

    public FirstRunCriteriaRationaleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm_first_" + Guid.NewGuid().ToString("N")[..8]);
        _install = Path.Combine(_root, "install");
        _local = Path.Combine(_root, "local");
        _roaming = Path.Combine(_root, "roaming");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ─── 环境 ───

    private string LegacyRepository => Path.Combine(_roaming, "Repository");

    /// <summary>与 <c>DataLocationMigrator.CreateProductionEnvironment</c> 同构。</summary>
    private DataMigrationEnvironment MigrationEnvironment() => new(
        new DataMigrationPaths(
            LegacyConfigFile: Path.Combine(_install, "config.json"),
            ConfigFile: Path.Combine(_local, "config.json"),
            LegacyDeploymentBackupsDirectory: Path.Combine(_install, "Data", "Backups"),
            DeploymentBackupsDirectory: Path.Combine(_local, "Backups", "Deployments"),
            LegacyDataDirectory: Path.Combine(_install, "Data"),
            DataDirectory: Path.Combine(_local, "Data"),
            LegacyModBackupsDirectory: Path.Combine(_install, "Backups"),
            ModBackupsDirectory: Path.Combine(_local, "Backups", "Mods"),
            LegacyRepositoryRoot: LegacyRepository,
            LegacyOverwritesRoot: Path.Combine(_roaming, "Overwrites")),
        _prefs,
        // 生产值。整条链路的行为都取决于它，改成 true 这三条用例就不再代表真机。
        RelocationExecutionEnabled: false);

    /// <summary>与 <c>RepositorySetupService.CreateProductionEnvironment</c> 同构。</summary>
    private RepositorySetupEnvironment SetupEnvironment() => new(
        new RepositorySetupPaths(
            CurrentRepositoryRoot: _prefs.RepositoryRoot ?? Path.Combine(_local, "Repository"),
            LegacyRepositoryRoot: LegacyRepository,
            ConfigFile: Path.Combine(_local, "config.json"),
            LegacyConfigFile: Path.Combine(_install, "config.json"),
            LegacyDataDirectory: Path.Combine(_install, "Data"),
            LegacyModBackupsDirectory: Path.Combine(_install, "Backups"),
            InstallDirectory: _install),
        _prefs);

    /// <summary>按真机时序跑一遍：先搬迁器，再引导判定。</summary>
    private RepositorySetupDecision RunStartup()
    {
        new DataLocationMigrator(NullLogger<DataLocationMigrator>.Instance, MigrationEnvironment())
            .RunAsync().GetAwaiter().GetResult();

        return new RepositorySetupService(
            NullLogger<RepositorySetupService>.Instance, SetupEnvironment()).Decide();
    }

    private static void Write(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ─── 被否决的判据 ①：ui_config.json / DataLayoutVersion ───

    [Fact]
    public void 全新安装跑完搬迁器后版本标记已经是1_所以它不能当首次运行判据()
    {
        // 全新安装时搬迁器每一项都是"没东西要搬"，于是 completed = true、ShouldStampVersion = true，
        // 它会写下 DataLayoutVersion = 1 —— 而写这一下正好把 ui_config.json 创建出来。
        //
        // 结论：引导排在搬迁器之后（这是硬约束），就绝不能拿
        // "ui_config.json 不存在" 或 "DataLayoutVersion == 0" 当"首次运行"的判据 ——
        // 那样判的话，引导对<b>所有</b>全新用户都永远不会出现。
        var decision = RunStartup();

        Assert.Equal(1, _prefs.DataLayoutVersion);
        Assert.True(decision.ShouldPrompt, "全新安装必须弹");
    }

    [Fact]
    public void 老用户跑完搬迁器后版本标记反而是0_判据方向正好相反()
    {
        // 老用户的旧数据还在安装目录里，而搬移开关是 false，于是这些项被计为"推迟"，
        // completed = false，版本标记不写。也就是说 DataLayoutVersion 对老用户是 0、
        // 对新用户是 1 —— 拿它当"首次运行"判据不只是无效，方向还是反的。
        Write(Path.Combine(_install, "Data", "悟空_mods.json"), "[]");
        Write(Path.Combine(_local, "Data", "悟空_mods.json"), "[]");

        var decision = RunStartup();

        Assert.Equal(0, _prefs.DataLayoutVersion);
        Assert.False(decision.ShouldPrompt, "老用户不许被打扰");
        Assert.Equal(RepositorySetupSkipReason.LegacyInstallDataExists, decision.SkipReason);
    }

    // ─── 引导为什么必须排在搬迁器之后 ───

    [Fact]
    public void 搬迁器的原地登记让老用户的旧仓库被认出来()
    {
        // 这是"不打扰老用户"最主要的一条判据的来源，也是引导必须排在搬迁器之后的理由：
        // 旧仓库在 %APPDATA% 下、配置里原本什么都没有，只有搬迁器跑过之后
        // RepositoryRoot 才会被填上。抢在它前面判，这台机器会被读成"未配置"。
        Write(Path.Combine(LegacyRepository, "SomeMod", "manifest.json"), "{}");

        // 跑之前：配置里确实是空的
        Assert.Null(_prefs.RepositoryRoot);

        var decision = RunStartup();

        Assert.Equal(LegacyRepository, _prefs.RepositoryRoot);
        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.RepositoryRootConfigured, decision.SkipReason);
    }

    [Fact]
    public void 就算原地登记失败_旧仓库里的包本身也能兜住()
    {
        // 登记会写配置，配置不可写时它会失败（迁移器只计一次 failed，不阻断启动）。
        // 那时 RepositoryRootConfigured 是 false —— 必须还有第二条判据认得出这台机器有数据，
        // 否则一个装满 MOD 的老用户会因为"配置写不进去"而被弹窗问一次。
        Write(Path.Combine(LegacyRepository, "SomeMod", "manifest.json"), "{}");
        _prefs.ThrowOnSaveRepositoryRoot = true;

        var decision = RunStartup();

        Assert.Null(_prefs.RepositoryRoot);
        Assert.False(decision.ShouldPrompt);
        Assert.Equal(RepositorySetupSkipReason.LegacyRepositoryHasContent, decision.SkipReason);
    }

    // ─── 偏好替身：两个服务共用同一份，与生产里同为 UiPreferences 一致 ───

    private sealed class SharedPreferences : IDataMigrationPreferences, IRepositorySetupPreferences
    {
        public int DataLayoutVersion { get; private set; }
        public string? RepositoryRoot { get; private set; }
        public string? OverwritesRoot { get; private set; }
        public bool Prompted { get; private set; }

        /// <summary>模拟配置不可写（磁盘满 / 权限 / 杀软锁定）。生产实现是直通转发，异常真的会传出去。</summary>
        public bool ThrowOnSaveRepositoryRoot { get; set; }

        public int LoadDataLayoutVersion() => DataLayoutVersion;

        public void SaveDataLayoutVersion(int version) => DataLayoutVersion = version;

        public string? LoadRepositoryRoot() => RepositoryRoot;

        public void SaveRepositoryRoot(string? path)
        {
            if (ThrowOnSaveRepositoryRoot) throw new IOException("模拟：配置写不进去");
            RepositoryRoot = path;
        }

        public string? LoadOverwritesRoot() => OverwritesRoot;

        public void SaveOverwritesRoot(string? path) => OverwritesRoot = path;

        public bool LoadPrompted() => Prompted;

        public void SavePrompted() => Prompted = true;
    }
}
