using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

public class GameOriginalFileDetectorTests
{
    [Theory]
    [InlineData("pakchunk0-Windows.pak")]
    [InlineData("pakchunk0-WindowsNoEditor.ucas")]
    [InlineData("pakchunk12optional-Windows.utoc")]
    [InlineData("global.utoc")]
    [InlineData("re_chunk_000.pak")]
    [InlineData("pakchunk26-Windows_P.pak")]                 // 黑神话官方补丁，带 _P 但是平台名
    [InlineData("pakchunk23-Windows-WinStoreSteam.sig")]
    public void 游戏本体容器被识别(string name) => Assert.True(GameOriginalFileDetector.LooksLikeGameOriginal(name));

    [Theory]
    [InlineData("pakchunk99-Mods_P.pak")]          // MOD 惯例带 _P
    [InlineData("Eve_BodySuit_P.pak")]
    [InlineData("re_chunk_000.pak.patch_002.pak")]  // RE 补丁式 MOD
    [InlineData("readme.txt")]
    [InlineData("")]
    public void 真正的MOD不误伤(string name) => Assert.False(GameOriginalFileDetector.LooksLikeGameOriginal(name));
}
