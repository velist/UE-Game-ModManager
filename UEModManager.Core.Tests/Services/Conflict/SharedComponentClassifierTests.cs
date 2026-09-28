using UEModManager.Services.Conflict;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// <see cref="SharedComponentClassifier"/> 的判定回归。
///
/// 规则作用于非 MOD 包的条目（相对游戏根的文件路径）。
/// <see cref="SharedComponentClassifier.IsAttachment"/> 的位置条件最容易写错：
/// 只看文件名会把 <c>ue4ss/Mods/readme.txt</c> 也判成说明文件而丢掉，
/// 那是 UE4SS 运行时的一部分。
/// </summary>
public class SharedComponentClassifierTests
{
    private static readonly SharedComponentRule[] Rules =
    [
        new("UE4SS 运行时",
            ["ue4ss/", "binaries/win64/ue4ss/"],
            ["dwmapi.dll", "ue4ss.dll", "ue4ss-settings.ini", "mods.txt", "mods.json", "vtablelayout.ini"]),
        new("CSharpLoader 运行时",
            ["csharploader/"],
            ["version.dll", "b1cs.ini"]),
        new("代理 DLL",
            [],
            ["xinput1_3.dll", "d3d11.dll", "d3d12.dll", "dinput8.dll", "winmm.dll", "dsound.dll"])
    ];

    [Theory]
    [InlineData("ue4ss/Mods/main.lua", "UE4SS 运行时")]
    [InlineData("ue4ss/mods.txt", "UE4SS 运行时")]
    [InlineData("dwmapi.dll", "UE4SS 运行时")]
    [InlineData("binaries/win64/ue4ss/ue4ss.dll", "UE4SS 运行时")]
    [InlineData("csharploader/b1cs.ini", "CSharpLoader 运行时")]
    [InlineData("version.dll", "CSharpLoader 运行时")]
    [InlineData("xinput1_3.dll", "代理 DLL")]
    [InlineData("dinput8.dll", "代理 DLL")]
    public void 命中共享组件规则(string path, string expected)
    {
        Assert.Equal(expected, SharedComponentClassifier.Classify(path, Rules));
    }

    [Theory]
    [InlineData("Content/Paks/~mods/x.pak")]
    [InlineData("b1/Content/Paks/mod_P.pak")]
    [InlineData("Mods/SomeMod/mod.lua")]
    public void 不属于任何共享组件(string path)
    {
        Assert.Null(SharedComponentClassifier.Classify(path, Rules));
    }

    [Fact]
    public void 大小写与反斜杠不影响判定()
    {
        Assert.Equal("UE4SS 运行时", SharedComponentClassifier.Classify(@"UE4SS\Mods\main.lua", Rules));
        Assert.Equal("CSharpLoader 运行时", SharedComponentClassifier.Classify(@"CSharpLoader\b1cs.ini", Rules));
    }

    [Fact]
    public void 空规则表不命中()
    {
        Assert.Null(SharedComponentClassifier.Classify("ue4ss/Mods/main.lua", []));
    }

    [Theory]
    [InlineData("说明与事项.txt")]
    [InlineData("steam属性口令.txt")]
    [InlineData("readme.txt")]
    [InlineData("README.md")]
    public void 根目录下的说明文件是附件(string path)
    {
        Assert.True(SharedComponentClassifier.IsAttachment(path));
    }

    [Fact]
    public void 运行时目录里的readme不是附件()
    {
        // 位置条件不可省：这是 UE4SS 运行时的一部分，丢掉会让运行时少一个文件。
        Assert.False(SharedComponentClassifier.IsAttachment("ue4ss/Mods/readme.txt"));
    }

    [Fact]
    public void 深层目录里的说明文件不是附件()
    {
        Assert.False(SharedComponentClassifier.IsAttachment("Mods/SomeMod/说明.txt"));
    }

    [Theory]
    [InlineData("Content/Paks/mod_P.pak")]
    [InlineData("ue4ss/Mods/main.lua")]
    public void 普通文件不是附件(string path)
    {
        Assert.False(SharedComponentClassifier.IsAttachment(path));
    }

    /// <summary>
    /// 单元被包了一层 MOD 目录时，"单元根"就在那一层之下 ——
    /// 说明文件在单元自己看来仍位于根上。实测 0916.zip 就是这个形状：
    /// <c>红色经文金箍棒/steam属性口令.txt</c>、<c>自动闪避/说明与事项.txt</c>。
    /// </summary>
    [Theory]
    [InlineData("红色经文金箍棒/steam属性口令.txt")]
    [InlineData("自动闪避/说明与事项.txt")]
    [InlineData("三元两刃枪替换为红樱枪_P/readme.txt")]
    public void 包装目录下的说明文件也是附件(string path)
    {
        Assert.True(SharedComponentClassifier.IsAttachment(path, wrapped: true));
    }

    /// <summary>
    /// 运行时文件不能被当成说明丢掉：<c>ue4ss/Mods/mods.txt</c>、
    /// <c>load_order.txt</c> 少了 MOD 就不工作。
    ///
    /// 它们与"包装目录下的说明文件"形状上完全一样（都只低一层、都是 .txt），
    /// 靠"单元里是否只有一个顶层目录"区分：<c>ue4ss/</c> 与 <c>b1/</c> 并列时
    /// 那些是真实安装位置，不是包装，调用方因此传 <c>wrapped: false</c>。
    /// </summary>
    [Theory]
    [InlineData("ue4ss/Mods/mods.txt")]
    [InlineData("ue4ss/Mods/BPModLoaderMod/load_order.txt")]
    [InlineData("ue4ss/Mods/readme.txt")]
    public void 多个顶层目录时运行时文件不是附件(string path)
    {
        Assert.False(SharedComponentClassifier.IsAttachment(path, wrapped: false));
    }

    /// <summary>无论是否包装，都不认超过"单元根"的深度。</summary>
    [Theory]
    [InlineData("包装/子目录/说明.txt")]
    [InlineData("ue4ss/Mods/Sub/readme.txt")]
    public void 深于单元根的路径不是附件(string path)
    {
        Assert.False(SharedComponentClassifier.IsAttachment(path, wrapped: true));
        Assert.False(SharedComponentClassifier.IsAttachment(path));
    }
}
