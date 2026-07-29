using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Infrastructure;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 首次运行引导的时序为什么必须排在 <see cref="ObjectStore"/> 之前 —— 用行为把理由钉住。
///
/// <para>
/// <see cref="StartupSequenceGuardTests"/> 从源码上保证了顺序，本类证明<b>顺序错了会怎样</b>：
/// <see cref="ObjectStore"/> 在构造时读一次 <see cref="AppPaths.RepositoryRoot"/> 并记进字段，
/// 之后本次会话不再回头看配置。它是 DI 单例，第一次被解析时构造 —— 引导写在它之后，
/// 用户这次选的位置要等下次启动才生效，而界面上不会有任何异常。
/// </para>
///
/// <para>
/// <b>不碰开发机真实的 <c>%APPDATA%\UEModManager\ui_config.json</c>。</b>
/// 每条用例都用 <c>UiPreferences.OverrideConfigPathForTests</c> 把配置重定向到临时目录，
/// Dispose 时复原并清掉内存单例。<see cref="ObjectStore"/> 只读路径字符串，
/// 不创建任何目录（建目录在 <c>EnsureInitialized</c> 里，本类不调它）。
/// </para>
/// </summary>
[Collection(UiPreferencesStaticStateCollection.Name)]
public sealed class RepositoryRootTimingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_timing_" + Guid.NewGuid().ToString("N")[..8]);

    public RepositoryRootTimingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不该让测试变红 */ }
    }

    private string ConfigPath() => Path.Combine(_root, "ui_config.json");

    private static ObjectStore NewStore() => new(NullLogger<ObjectStore>.Instance);

    [Fact]
    public void 先写偏好再构造_仓库指向用户选的位置()
    {
        // 这就是引导排在前面时发生的事
        using var _ = UiPreferences.OverrideConfigPathForTests(ConfigPath());
        var chosen = Path.Combine(_root, "chosen");

        UiPreferences.SaveRepositoryRoot(chosen);

        Assert.Equal(chosen, NewStore().RepositoryRoot);
    }

    [Fact]
    public void 先构造再写偏好_本次会话仍指向旧位置()
    {
        // 这就是引导排在后面时发生的事：配置已经改了，界面上没有任何异常，
        // 但这次会话读的还是旧仓库，用户要重启一次才看得到效果。
        using var _ = UiPreferences.OverrideConfigPathForTests(ConfigPath());
        var before = Path.Combine(_root, "before");
        UiPreferences.SaveRepositoryRoot(before);

        var store = NewStore();
        UiPreferences.SaveRepositoryRoot(Path.Combine(_root, "after"));

        Assert.Equal(before, store.RepositoryRoot);
    }

    [Fact]
    public void AppPaths不缓存_配置一改立刻生效()
    {
        // ObjectStore 拿到新值的前提是 AppPaths 每次现算。它要是缓存了，
        // 引导排在前面也没用 —— 那正是 AppPaths 类注释里"不缓存布局"的理由。
        using var _ = UiPreferences.OverrideConfigPathForTests(ConfigPath());
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");

        UiPreferences.SaveRepositoryRoot(first);
        Assert.Equal(first, AppPaths.RepositoryRoot);

        UiPreferences.SaveRepositoryRoot(second);
        Assert.Equal(second, AppPaths.RepositoryRoot);
    }

    [Fact]
    public void 引导跳过时仓库落在默认位置()
    {
        // "以后再说"不写位置，AppPaths 回落到 %LOCALAPPDATA%\UEModManager\Repository
        using var _ = UiPreferences.OverrideConfigPathForTests(ConfigPath());

        Assert.Null(UiPreferences.LoadRepositoryRoot());
        Assert.Equal(Path.Combine(AppPaths.LocalRoot, "Repository"), AppPaths.RepositoryRoot);
        Assert.Equal(AppPaths.RepositoryRoot, NewStore().RepositoryRoot);
    }

    [Fact]
    public void 已问过标记与仓库位置互不影响()
    {
        // 两个键各管各的：跳过的人只有标记、没有位置；
        // 拿"有没有位置"推"问没问过"，跳过的人每次启动都会被拦一次。
        using var _ = UiPreferences.OverrideConfigPathForTests(ConfigPath());

        Assert.False(UiPreferences.LoadRepositoryLocationPrompted());

        UiPreferences.SaveRepositoryLocationPrompted();

        Assert.True(UiPreferences.LoadRepositoryLocationPrompted());
        Assert.Null(UiPreferences.LoadRepositoryRoot());
    }

    [Fact]
    public void 已问过标记会真的落盘()
    {
        // 只存在内存单例里的话，重启之后又会被问一次
        using var scope = UiPreferences.OverrideConfigPathForTests(ConfigPath());
        UiPreferences.SaveRepositoryLocationPrompted();

        Assert.Contains("\"RepositoryLocationPrompted\": true", File.ReadAllText(ConfigPath()));
    }
}
