using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services.Backends;

namespace UEModManager.Tests.Services;

/// <summary>
/// 复制部署不得穿透硬链接改写仓库源文件。
///
/// 事故形态：先用硬链接模式部署（目标 = 仓库里包 B 文件的硬链接），之后改回复制模式，
/// 任何一次"替换"（调优先级、冲突面板改胜者）都会把包 A 的内容
/// <c>File.Copy(overwrite: true)</c> 到目标上——CREATE_ALWAYS 原地改写，
/// 仓库里包 B 的源文件跟着变成了 A 的内容。这里建真实硬链接验证。
/// </summary>
public sealed class CopyBackendHardLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uemm-copylink-" + Guid.NewGuid().ToString("N"));

    public CopyBackendHardLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败无所谓 */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [Fact]
    public async Task 覆盖硬链接目标时不改写仓库里的另一份源文件()
    {
        var repoB = Path.Combine(_root, "repo-b.pak");
        var repoA = Path.Combine(_root, "repo-a.pak");
        var target = Path.Combine(_root, "game", "x.pak");
        File.WriteAllText(repoB, "BBBB");
        File.WriteAllText(repoA, "AAAAAAAA");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Assert.True(CreateHardLink(target, repoB, IntPtr.Zero), "当前文件系统不支持硬链接");

        await new CopyBackend(NullLogger<CopyBackend>.Instance).DeployFileAsync(repoA, target);

        Assert.Equal("AAAAAAAA", File.ReadAllText(target));
        Assert.Equal("BBBB", File.ReadAllText(repoB));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp"));
    }

    [Fact]
    public async Task 目标不存在时正常复制并建目录()
    {
        var src = Path.Combine(_root, "src.pak");
        var target = Path.Combine(_root, "deep", "dir", "x.pak");
        File.WriteAllText(src, "data");

        await new CopyBackend(NullLogger<CopyBackend>.Instance).DeployFileAsync(src, target);

        Assert.Equal("data", File.ReadAllText(target));
    }
}
