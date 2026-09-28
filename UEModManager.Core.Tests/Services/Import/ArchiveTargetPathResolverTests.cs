using UEModManager.Models;
using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

/// <summary>
/// 包内目标路径推导。旧实现一律 Path.GetFileName，于是
/// <c>b1/Binaries/Win64/ue4ss/Mods/mods.json</c> 会部署成 <c>&lt;gamePath&gt;/mods.json</c>，
/// UE4SS 与 CSharpLoader 都找不到文件，插件因此永远不生效。
/// </summary>
public class ArchiveTargetPathResolverTests
{
    private const string Root = @"C:\temp\import";

    [Fact]
    public void Resolve_ModPackage_UsesBareFileName()
    {
        var resolved = ArchiveTargetPathResolver.Resolve(
            Path.Combine(Root, "SomeMod", "SomeMod.pak"), Root, keepDirectoryStructure: false);

        Assert.Equal("SomeMod.pak", resolved);
    }

    [Fact]
    public void Resolve_PluginPackage_StripsGameAnchorAndKeepsStructure()
    {
        var resolved = ArchiveTargetPathResolver.Resolve(
            Path.Combine(Root, "ModName", "b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json"),
            Root, keepDirectoryStructure: true);

        Assert.Equal(Path.Combine("ue4ss", "Mods", "mods.json"), resolved);
    }

    [Fact]
    public void Resolve_WithoutAnchor_KeepsWholeRelativePath()
    {
        var resolved = ArchiveTargetPathResolver.Resolve(
            Path.Combine(Root, "CSharpLoader", "Mods", "PerfectDodgeV2.4", "config.json"),
            Root, keepDirectoryStructure: true);

        Assert.Equal(Path.Combine("CSharpLoader", "Mods", "PerfectDodgeV2.4", "config.json"), resolved);
    }

    [Fact]
    public void Resolve_FileOutsideArchiveRoot_FallsBackToFileName()
    {
        var resolved = ArchiveTargetPathResolver.Resolve(@"C:\elsewhere\mod.dll", Root, keepDirectoryStructure: true);

        Assert.Equal("mod.dll", resolved);
    }

    [Fact]
    public void Resolve_CommonPrefixBeforeAnchor_StripsModNameFromFileWithoutAnchor()
    {
        // 现场症状：游戏根下多出一个以 MOD 命名的目录，里面只有一个说明文档
        // （Win64\黑神话26年8月最新版自动闪避MOD\说明与事项.txt）。
        // 根因是共有包装目录算得太晚：先剥锚点会把最短的那条路径削成裸文件名
        // （b1/Binaries/Win64/version.dll -> version.dll），共有前缀从此算不出来，
        // 同一个包里没命中锚点的文件就带着 MOD 名目录落进游戏根。
        var prefix = ArchiveTargetPathResolver.StripCommonPrefix(
        [
            ArchiveTargetPathResolver.ResolveRelativePath(
                Path.Combine(Root, "ModName", "b1", "Binaries", "Win64", "version.dll"), Root),
            ArchiveTargetPathResolver.ResolveRelativePath(
                Path.Combine(Root, "ModName", "说明与事项.txt"), Root),
        ]);
        Assert.Equal("ModName", prefix);

        // 先剥共有前缀、再剥锚点：代理 DLL 仍在游戏根，说明文档不再带目录。
        Assert.Equal("version.dll", ArchiveTargetPathResolver.Resolve(
            Path.Combine(Root, "ModName", "b1", "Binaries", "Win64", "version.dll"),
            Root, keepDirectoryStructure: true, commonPrefix: prefix));
        Assert.Equal("说明与事项.txt", ArchiveTargetPathResolver.Resolve(
            Path.Combine(Root, "ModName", "说明与事项.txt"),
            Root, keepDirectoryStructure: true, commonPrefix: prefix));
    }

    [Fact]
    public void ResolveRelativePath_DoesNotStripGameAnchor()
    {
        Assert.Equal(
            Path.Combine("ModName", "b1", "Binaries", "Win64", "version.dll"),
            ArchiveTargetPathResolver.ResolveRelativePath(
                Path.Combine(Root, "ModName", "b1", "Binaries", "Win64", "version.dll"), Root));
    }

    [Theory]
    [InlineData("b1/Binaries/Win64/ue4ss/Mods/mods.json", "ue4ss/Mods/mods.json")]
    [InlineData("Binaries/Win64/version.dll", "version.dll")]
    [InlineData("b1/Content/Paks/~mods/a.pak", "~mods/a.pak")]
    [InlineData("ue4ss/Mods/mods.json", "ue4ss/Mods/mods.json")]
    public void StripLeadingGameAnchor_RemovesAnchorAndEverythingBeforeIt(string input, string expected)
    {
        var actual = ArchiveTargetPathResolver.StripLeadingGameAnchor(input.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), actual);
    }

    [Theory]
    [InlineData("mods.json")]
    [InlineData("a.dll")]
    [InlineData("ue4ss/Mods/mods.json")]
    public void StripPrefix_WholeDirectoryPrefix_IsRemoved(string path)
    {
        var prefixed = Path.Combine("wrapper", path.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(path.Replace('/', Path.DirectorySeparatorChar),
            ArchiveTargetPathResolver.StripPrefix(prefixed, "wrapper"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StripPrefix_EmptyPrefix_ReturnsPathUnchanged(string? prefix)
    {
        var path = Path.Combine("wrapper", "mods.json");

        Assert.Equal(path, ArchiveTargetPathResolver.StripPrefix(path, prefix));
    }

    [Fact]
    public void StripPrefix_PartialNameMatch_IsNotStripped()
    {
        // "wrapperX/mods.json" 不是 "wrapper/" 的子项，不能被误删。
        var path = Path.Combine("wrapperX", "mods.json");

        Assert.Equal(path, ArchiveTargetPathResolver.StripPrefix(path, "wrapper"));
    }

    [Fact]
    public void StripCommonPrefix_SingleWrapperDirectory_IsRemoved()
    {
        var paths = new[]
        {
            Path.Combine("SomeMod", "files", "mods.json"),
            Path.Combine("SomeMod", "files", "config.json"),
        };

        Assert.Equal("SomeMod", ArchiveTargetPathResolver.StripCommonPrefix(paths));
    }

    [Fact]
    public void StripCommonPrefix_InstallAnchorDirectory_IsNeverStripped()
    {
        // 所有文件都在 ue4ss/Mods/ 下时，剥掉前缀会让 mods.json 落到 <gamePath>/mods.json，
        // 而 UE4SS 只读 ue4ss/Mods/mods.json。安装锚点必须原样保留。
        var paths = new[]
        {
            Path.Combine("ue4ss", "Mods", "mods.json"),
            Path.Combine("ue4ss", "Mods", "config.json"),
        };

        Assert.Equal(string.Empty, ArchiveTargetPathResolver.StripCommonPrefix(paths));
    }

    [Theory]
    [InlineData("ue4ss", true)]
    [InlineData("UE4SS", true)]
    [InlineData("CSharpLoader", true)]
    [InlineData("SomeMod", false)]
    [InlineData("wrapper", false)]
    public void IsInstallAnchor_RecognizesAgentDirectories(string name, bool expected)
        => Assert.Equal(expected, ArchiveTargetPathResolver.IsInstallAnchor(name));

    [Fact]
    public void StripCommonPrefix_NothingShared_IsEmpty()
    {
        var paths = new[] { Path.Combine("a", "x.json"), Path.Combine("b", "y.json") };

        Assert.Equal(string.Empty, ArchiveTargetPathResolver.StripCommonPrefix(paths));
    }

    [Fact]
    public void StripCommonPrefix_EmptyList_IsEmpty()
        => Assert.Equal(string.Empty, ArchiveTargetPathResolver.StripCommonPrefix([]));

    [Theory]
    [InlineData("ue4ss/Mods/mods.json", true)]
    [InlineData("CSharpLoader/x/config.json", true)]
    [InlineData("a.dll", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasDirectoryStructure_ReportsNestedPaths(string? path, bool expected)
        => Assert.Equal(expected, ArchiveTargetPathResolver.HasDirectoryStructure(
            path?.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData(PackageKind.Mod, false)]
    [InlineData(PackageKind.Plugin, true)]
    [InlineData(PackageKind.Config, true)]
    public void KeepsDirectoryStructure_OnlyModIsFalse(PackageKind kind, bool expected)
        => Assert.Equal(expected, ArchiveTargetPathResolver.KeepsDirectoryStructure(kind));

    [Theory]
    [InlineData("ue4ss/Mods/mods.json", "ue4ss\\Mods\\mods.json")]
    [InlineData("a.pak", "a.pak")]
    public void NormalizeGameRelativePath_UsesBackslashSeparators(string input, string expected)
        => Assert.Equal(expected, ArchiveTargetPathResolver.NormalizeGameRelativePath(input));

    [Fact]
    public void NormalizeGameRelativePath_AbsolutePath_IsRejected()
        => Assert.Equal(string.Empty, ArchiveTargetPathResolver.NormalizeGameRelativePath(@"C:\windows\system32\x.dll"));
}
