using System.Collections.Generic;
using UEModManager.Services.Import;
using Xunit;

namespace UEModManager.Core.Tests.Services.Import;

/// <summary>
/// 引擎自己的安装锚点（<c>EngineProfile.InstallAnchors</c>）必须真的被 Core 的纯函数吃进去。
///
/// 通用锚点表只认 UE 的词汇（ue4ss / Binaries / Content / b1 / Mods …）。RE 引擎的
/// <c>natives/</c>、Unity 的 <c>BepInEx/</c>、暗黑4 的 <c>WTF/</c> 不在里面，于是
/// <c>natives/stm/x.tex</c> 会被判成"natives 是包装目录"：包名变成 natives、
/// natives/ 那一层被剥掉、文件落到游戏根 —— 游戏永远读不到，界面还显示导入成功。
/// </summary>
public class EngineAnchorTests
{
    /// <summary>RE 引擎的锚点（与 EngineProfile 里的取值一致）。</summary>
    private static readonly HashSet<string> ReAnchors = new() { "natives", "reframework", "pak_mods" };

    [Theory]
    [InlineData("natives")]
    [InlineData("reframework")]
    [InlineData("pak_mods")]
    public void 传了引擎锚点才算安装位置(string directory)
    {
        Assert.True(ArchiveTargetPathResolver.IsInstallAnchor(directory, ReAnchors));
        Assert.False(ArchiveTargetPathResolver.IsInstallAnchor(directory));
    }

    [Fact]
    public void 通用锚点不受引擎表影响()
    {
        // 引擎表是"额外"的，不能把通用表挤掉。
        Assert.True(ArchiveTargetPathResolver.IsInstallAnchor("ue4ss"));
        Assert.True(ArchiveTargetPathResolver.IsInstallAnchor("ue4ss", ReAnchors));
    }

    [Fact]
    public void RE的松散资源不会被当成包装目录剥掉()
    {
        List<string> paths = ["natives/stm/sub/x.tex", "natives/stm/y.tex"];

        Assert.Equal(string.Empty, ArchiveTargetPathResolver.StripCommonPrefix(paths, ReAnchors));
        // 没有引擎锚点时 natives 被当成包装目录 —— 这正是修之前的行为。
        Assert.Equal("natives", ArchiveTargetPathResolver.StripCommonPrefix(paths));
    }

    [Fact]
    public void 包装目录仍然照剥()
    {
        // ModName/natives/... 这种形状：包装目录该剥，natives 该留。
        List<string> paths = ["ModName/natives/stm/x.tex", "ModName/natives/stm/y.tex"];

        Assert.Equal("ModName", ArchiveTargetPathResolver.StripCommonPrefix(paths, ReAnchors));
    }

    [Fact]
    public void 顶层就是natives时按单个MOD处理()
    {
        List<ArchiveImportUnit> units = [new("natives", ["root/natives/stm/x.tex"])];

        Assert.True(ArchiveImportUnitResolver.LooksLikeSingleModLayout(units, ReAnchors));
        // 认不出 natives 时这里会切出一个名叫 natives 的包，MOD 真名被丢掉。
        Assert.False(ArchiveImportUnitResolver.LooksLikeSingleModLayout(units));
    }

    [Fact]
    public void 多个MOD文件夹仍然按多单元处理()
    {
        List<ArchiveImportUnit> units =
        [
            new("超级识破", ["root/超级识破/natives/stm/x.tex"]),
            new("无限弹药", ["root/无限弹药/natives/stm/y.tex"]),
        ];

        Assert.False(ArchiveImportUnitResolver.LooksLikeSingleModLayout(units, ReAnchors));
    }
}
