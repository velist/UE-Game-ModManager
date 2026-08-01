using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services.Backends;

namespace UEModManager.Tests.Services;

/// <summary>
/// 硬链接后端的真实行为。
///
/// <para>
/// 这里只跑得了"同一个盘"这一半：跨盘要两个真实的卷，CI 与开发机上都保证不了。
/// 另一半（跨盘失败怎么分类、上万条降级怎么聚合、什么时候值得说一次）已经被拆成纯函数
/// 压在 Core 的 <c>DeploymentDegradationTests</c> 里——那正是把判据从后端里拆出去的收益。
/// </para>
///
/// <para>
/// <b>只在系统临时目录下建文件</b>，绝不碰 <c>%LOCALAPPDATA%\UEModManager</c> /
/// <c>%APPDATA%\UEModManager</c>：本后端不读任何配置，也就没有静态全局要挡。
/// </para>
/// </summary>
public sealed class HardLinkBackendTests : IDisposable
{
    private readonly string _root;
    private readonly HardLinkBackend _backend = new(NullLogger<HardLinkBackend>.Instance);

    public HardLinkBackendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm_hardlink_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    [Fact]
    public void 后端实现了降级上报能力()
    {
        // 没有这一条，DeploymentService 里的 `backend as IDeploymentDegradationReporter`
        // 会静默地探测不到，降级又回到"只写一条日志"的状态
        Assert.IsAssignableFrom<IDeploymentDegradationReporter>(_backend);
    }

    [Fact]
    public async Task 同一个盘真的建了硬链接而且不上报降级()
    {
        var source = Path.Combine(_root, "repo", "mod.pak");
        var target = Path.Combine(_root, "game", "Paks", "mod.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "原始内容");

        var reported = new List<DeploymentDegradation>();
        _backend.Degraded += reported.Add;
        try
        {
            await _backend.DeployFileAsync(source, target);
        }
        finally
        {
            _backend.Degraded -= reported.Add;
        }

        Assert.Empty(reported);
        Assert.True(File.Exists(target));

        // 硬链接与复制在"文件存在"这一层看不出区别，唯一的判据是二者指向同一份数据：
        // 改源文件，目标必须跟着变。这条断言就是"省空间"这件事是否真的发生了。
        File.WriteAllText(source, "改过之后");
        Assert.Equal("改过之后", File.ReadAllText(target));
    }

    [Fact]
    public async Task 目标已存在时先删再链不报降级()
    {
        var source = Path.Combine(_root, "repo", "mod.pak");
        var target = Path.Combine(_root, "game", "mod.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(source, "新的");
        File.WriteAllText(target, "旧的");

        var reported = new List<DeploymentDegradation>();
        _backend.Degraded += reported.Add;
        try
        {
            await _backend.DeployFileAsync(source, target);
        }
        finally
        {
            _backend.Degraded -= reported.Add;
        }

        // "已存在"曾被注释写成错误码 17（那其实是 ERROR_NOT_SAME_DEVICE）。
        // 真实行为是部署前就把目标删掉了，根本走不到那条降级分支。
        Assert.Empty(reported);
        Assert.Equal("新的", File.ReadAllText(target));
    }

    [Fact]
    public async Task 移除只删链接不动源文件()
    {
        var source = Path.Combine(_root, "repo", "mod.pak");
        var target = Path.Combine(_root, "game", "mod.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "内容");

        await _backend.DeployFileAsync(source, target);
        await _backend.RemoveFileAsync(target);

        Assert.False(File.Exists(target));
        Assert.True(File.Exists(source));
    }
}
