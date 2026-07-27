using System.Collections.Generic;
using System.IO;
using UEModManager.Services.Deployment;

namespace UEModManager.Core.Tests.Services.Deployment;

public class EmptyDirectoryCleanerTests
{
    // ─── ResolveDeploymentRoot ───

    [Fact]
    public void ResolveDeploymentRoot_ModLayout_StripsRelativeSuffix()
    {
        var root = EmptyDirectoryCleaner.ResolveDeploymentRoot(
            @"D:\Game\Content\Paks\~mods\MyMod\foo.pak",
            @"MyMod\foo.pak");

        Assert.Equal(@"D:\Game\Content\Paks\~mods", root);
    }

    [Fact]
    public void ResolveDeploymentRoot_ForwardSlashRelativePath_StillMatches()
    {
        var root = EmptyDirectoryCleaner.ResolveDeploymentRoot(
            @"D:\Game\Content\Paks\~mods\MyMod\sub\foo.pak",
            "MyMod/sub/foo.pak");

        Assert.Equal(@"D:\Game\Content\Paks\~mods", root);
    }

    [Fact]
    public void ResolveDeploymentRoot_SuffixDoesNotMatch_ReturnsNull()
    {
        // 数据不一致时必须返回 null：没有边界就不能向上删
        Assert.Null(EmptyDirectoryCleaner.ResolveDeploymentRoot(
            @"D:\Game\~mods\MyMod\foo.pak", @"Other\bar.pak"));
    }

    [Theory]
    [InlineData(null, "a.pak")]
    [InlineData(@"D:\Game\a.pak", null)]
    [InlineData("", "")]
    public void ResolveDeploymentRoot_MissingInput_ReturnsNull(string? targetPath, string? relativePath)
    {
        Assert.Null(EmptyDirectoryCleaner.ResolveDeploymentRoot(targetPath, relativePath));
    }

    [Fact]
    public void ResolveDeploymentRoot_RelativePathIsWholePath_ReturnsNull()
    {
        // 去掉后缀只剩盘符，反推结果不可信
        Assert.Null(EmptyDirectoryCleaner.ResolveDeploymentRoot(@"D:\foo.pak", "foo.pak"));
    }

    // ─── CleanUpwards（用注入的委托模拟文件系统）───

    [Fact]
    public void CleanUpwards_DeletesEmptyDirectoriesUpToButNotIncludingRoot()
    {
        var fs = new FakeDirectories(
            @"D:\Game\~mods",
            @"D:\Game\~mods\MyMod",
            @"D:\Game\~mods\MyMod\sub");

        var deleted = fs.Clean(@"D:\Game\~mods\MyMod\sub", @"D:\Game\~mods");

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { @"D:\Game\~mods\MyMod\sub", @"D:\Game\~mods\MyMod" }, fs.Deleted);
        Assert.Contains(@"D:\Game\~mods", fs.Existing); // 部署根本身保留
    }

    [Fact]
    public void CleanUpwards_StopsAtNonEmptyDirectory()
    {
        var fs = new FakeDirectories(@"D:\Game\~mods", @"D:\Game\~mods\MyMod", @"D:\Game\~mods\MyMod\sub");
        fs.NonEmpty.Add(@"D:\Game\~mods\MyMod");

        var deleted = fs.Clean(@"D:\Game\~mods\MyMod\sub", @"D:\Game\~mods");

        Assert.Equal(1, deleted);
        Assert.Equal(new[] { @"D:\Game\~mods\MyMod\sub" }, fs.Deleted);
    }

    [Fact]
    public void CleanUpwards_StartAtRoot_DeletesNothing()
    {
        var fs = new FakeDirectories(@"D:\Game\~mods");

        var deleted = fs.Clean(@"D:\Game\~mods", @"D:\Game\~mods");

        Assert.Equal(0, deleted);
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void CleanUpwards_StartOutsideRoot_DeletesNothing()
    {
        // 旧实现会一路向上删到游戏目录甚至更外层，这里必须一个都不删
        var fs = new FakeDirectories(@"D:\Game", @"D:\Game\Content");

        var deleted = fs.Clean(@"D:\Game\Content", @"D:\Game\~mods");

        Assert.Equal(0, deleted);
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void CleanUpwards_RootWithTrailingSeparator_TreatedSame()
    {
        var fs = new FakeDirectories(@"D:\Game\~mods", @"D:\Game\~mods\MyMod");

        var deleted = fs.Clean(@"D:\Game\~mods\MyMod", @"D:\Game\~mods\");

        Assert.Equal(1, deleted);
    }

    [Fact]
    public void CleanUpwards_CaseInsensitiveBoundary()
    {
        var fs = new FakeDirectories(@"D:\Game\~mods", @"D:\Game\~mods\MyMod");

        var deleted = fs.Clean(@"d:\game\~MODS\MyMod", @"D:\Game\~mods");

        Assert.Equal(1, deleted);
    }

    [Fact]
    public void CleanUpwards_DeleteThrows_StopsWithoutPropagating()
    {
        var fs = new FakeDirectories(@"D:\Game\~mods", @"D:\Game\~mods\MyMod", @"D:\Game\~mods\MyMod\sub");
        fs.Locked.Add(@"D:\Game\~mods\MyMod\sub");

        var deleted = fs.Clean(@"D:\Game\~mods\MyMod\sub", @"D:\Game\~mods");

        Assert.Equal(0, deleted);
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void CleanUpwards_MissingDirectory_DeletesNothing()
    {
        var fs = new FakeDirectories(@"D:\Game\~mods");

        var deleted = fs.Clean(@"D:\Game\~mods\Gone", @"D:\Game\~mods");

        Assert.Equal(0, deleted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CleanUpwards_MissingStart_ReturnsZero(string? start)
    {
        var fs = new FakeDirectories(@"D:\Game\~mods");

        Assert.Equal(0, fs.Clean(start, @"D:\Game\~mods"));
    }

    /// <summary>内存中的目录树替身，避免测试碰真实文件系统。</summary>
    private sealed class FakeDirectories
    {
        public FakeDirectories(params string[] directories)
        {
            Existing = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        }

        public HashSet<string> Existing { get; }
        public HashSet<string> NonEmpty { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Deleted { get; } = new();

        public int Clean(string? start, string root) =>
            EmptyDirectoryCleaner.CleanUpwards(
                start,
                root,
                directoryExists: Existing.Contains,
                isDirectoryEmpty: d => !NonEmpty.Contains(d),
                deleteDirectory: d =>
                {
                    if (Locked.Contains(d)) throw new IOException($"目录被占用: {d}");
                    Existing.Remove(d);
                    Deleted.Add(d);
                });
    }
}
