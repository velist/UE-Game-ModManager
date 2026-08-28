using UEModManager.Services.Migration;

namespace UEModManager.Core.Tests.Services.Migration;

public class AvatarPathClassifierTests
{
    private const string LegacyRoot = @"C:\Program Files\UEModManager\UserData\Avatars";
    private const string CurrentRoot = @"C:\Users\a\AppData\Roaming\UEModManager\Avatars";

    private static AvatarLocation Classify(string? path) =>
        AvatarPathClassifier.Classify(path, LegacyRoot, CurrentRoot);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 未设置头像_判为Empty(string? path)
    {
        Assert.Equal(AvatarLocation.Empty, Classify(path));
    }

    [Fact]
    public void 已在新目录_判为AlreadyCurrent()
    {
        Assert.Equal(AvatarLocation.AlreadyCurrent,
            Classify(@"C:\Users\a\AppData\Roaming\UEModManager\Avatars\1_20260101120000.png"));
    }

    [Fact]
    public void 在旧目录_判为Legacy()
    {
        Assert.Equal(AvatarLocation.Legacy,
            Classify(@"C:\Program Files\UEModManager\UserData\Avatars\1_20260101120000.png"));
    }

    [Fact]
    public void 用户自选的任意位置_判为Foreign()
    {
        Assert.Equal(AvatarLocation.Foreign, Classify(@"D:\Pictures\me.png"));
    }

    /// <summary>
    /// 这是不能裸用 StartsWith 的原因：Avatars 是 Avatars2 的字符串前缀，
    /// 但它们是两个无关的目录。必须补尾分隔符再比。
    /// </summary>
    [Fact]
    public void 同前缀的相邻目录_不误判为归属()
    {
        Assert.Equal(AvatarLocation.Foreign,
            Classify(@"C:\Users\a\AppData\Roaming\UEModManager\Avatars2\1.png"));
        Assert.Equal(AvatarLocation.Foreign,
            Classify(@"C:\Program Files\UEModManager\UserData\AvatarsOld\1.png"));
    }

    [Fact]
    public void 大小写不同_仍判为同一目录()
    {
        Assert.Equal(AvatarLocation.AlreadyCurrent,
            Classify(@"c:\users\a\appdata\roaming\uemodmanager\avatars\1.png"));
        Assert.Equal(AvatarLocation.Legacy,
            Classify(@"C:\PROGRAM FILES\UEMODMANAGER\USERDATA\AVATARS\1.png"));
    }

    [Fact]
    public void 未规范化的路径_先规范化再判定()
    {
        Assert.Equal(AvatarLocation.Legacy,
            Classify(@"C:\Program Files\UEModManager\UserData\Sub\..\Avatars\1.png"));
    }

    [Fact]
    public void 根目录带尾分隔符_判定不受影响()
    {
        var withSlash = AvatarPathClassifier.Classify(
            @"C:\Program Files\UEModManager\UserData\Avatars\1.png",
            LegacyRoot + @"\",
            CurrentRoot + @"\");

        Assert.Equal(AvatarLocation.Legacy, withSlash);
    }

    /// <summary>数据库里存的是历史遗留值，什么形态都可能有，不能因此抛异常。</summary>
    [Theory]
    [InlineData("https://example.com/avatar.png")]
    [InlineData("\u0001\u0002 非法控制字符")]
    [InlineData("|<>?*")]
    public void 无法解析的字符串_判为Foreign且不抛(string path)
    {
        var ex = Record.Exception(() => Classify(path));

        Assert.Null(ex);
        Assert.Equal(AvatarLocation.Foreign, Classify(path));
    }

    /// <summary>
    /// 新旧目录被配置成同一处时（理论上不该发生，但配置可被改），
    /// 应判为"已完成"而不是"要迁移"——后者会让改写器把文件复制给自己。
    /// </summary>
    [Fact]
    public void 新旧目录相同时_优先判为AlreadyCurrent()
    {
        var result = AvatarPathClassifier.Classify(
            @"C:\Same\Avatars\1.png", @"C:\Same\Avatars", @"C:\Same\Avatars");

        Assert.Equal(AvatarLocation.AlreadyCurrent, result);
    }
}
