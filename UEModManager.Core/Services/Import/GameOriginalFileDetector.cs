using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UEModManager.Services.Import;

/// <summary>
/// 识别"看起来是游戏自带原版文件"的导入项（纯函数）。
///
/// 玩家常把游戏目录里的本体容器误当 MOD 拖进来，导入后一启用就覆盖原版文件，游戏可能起不来。
/// 只认把握很大的命名，宁可漏判也不误伤真正的 MOD：
/// <list type="bullet">
/// <item>UE：<c>pakchunk0-Windows.pak</c> 一类本体容器及其 .ucas/.utoc/.sig，以及 <c>global.ucas/utoc</c>。
/// MOD 惯例带 <c>_P</c> 后缀（<c>pakchunk99-Mods_P.pak</c>），带 _P 的不算。</item>
/// <item>RE 引擎：<c>re_chunk_000.pak</c>。补丁式 MOD 的 <c>re_chunk_000.pak.patch_001.pak</c> 不算。</item>
/// </list>
/// </summary>
public static class GameOriginalFileDetector
{
    private static readonly Regex UePakChunk = new(
        @"^pakchunk\d+(?:optional)?(?:-[A-Za-z0-9]+)?\.(?:pak|ucas|utoc|sig)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 官方补丁容器：平台名 + 可选 _P，例如黑神话的 <c>pakchunk26-Windows_P.pak</c>。
    /// 不能被"带 _P 就是 MOD"的规则放过——MOD 惯例是自己的名字加 _P（<c>pakchunk99-Mods_P</c>），不用平台名。
    /// </summary>
    private static readonly Regex UePlatformPatch = new(
        @"^pakchunk\d+(?:optional)?-(?:Windows|WindowsNoEditor|WinGDK|Windows-WinStoreSteam|WinStoreSteam)(?:_P)?\.(?:pak|ucas|utoc|sig)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReChunk = new(
        @"^re_chunk_\d+\.pak$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksLikeGameOriginal(string filePath)
    {
        var name = Path.GetFileName(filePath ?? string.Empty);
        if (string.IsNullOrEmpty(name)) return false;

        if (UePlatformPatch.IsMatch(name)) return true;

        if (Path.GetFileNameWithoutExtension(name).EndsWith("_p", StringComparison.OrdinalIgnoreCase))
            return false;

        return UePakChunk.IsMatch(name)
            || ReChunk.IsMatch(name)
            || name.Equals("global.ucas", StringComparison.OrdinalIgnoreCase)
            || name.Equals("global.utoc", StringComparison.OrdinalIgnoreCase);
    }
}
