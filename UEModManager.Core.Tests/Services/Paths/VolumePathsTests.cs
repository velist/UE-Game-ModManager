using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// "两个路径在不在同一个盘上"的判定。
///
/// <para>
/// 这条判据同时决定两件面向用户的事：部署降级时告诉他"仓库和游戏不在一个盘"还是
/// "这个盘不支持硬链接"（两句解法完全不同），以及引导界面上哪个盘会被标成
/// "游戏就装在这个盘"。说反了比不说更糟——用户会照着搬一遍几十 GB 的仓库，
/// 然后发现什么都没变。
/// </para>
/// </summary>
public class VolumePathsTests
{
    // ─── 盘根 ───

    [Theory]
    [InlineData(@"D:\Games\Wukong\Binaries\a.pak", @"D:\")]
    [InlineData(@"C:\Users\a\AppData\Local\UEModManager", @"C:\")]
    [InlineData(@"d:\games", @"d:\")]
    public void 能取到盘根(string path, string expected)
    {
        Assert.Equal(expected, VolumePaths.TryGetVolumeRoot(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Games\Wukong")]
    [InlineData(@"..\Mods")]
    public void 相对路径与空路径一律说不清(string? path)
    {
        // 相对路径会按当前工作目录展开，而 WPF 进程的工作目录不受控（快捷方式启动、
        // 拖拽启动、开机自启各不相同）。拿它兜底会让"同不同盘"随启动方式漂移。
        Assert.Null(VolumePaths.TryGetVolumeRoot(path));
    }

    [Fact]
    public void 网络共享取到共享根()
    {
        // 注意 UNC 根不带末尾分隔符（盘符根带），两侧走的是同一个方法所以比较仍然成立
        Assert.Equal(@"\\nas\games", VolumePaths.TryGetVolumeRoot(@"\\nas\games\Wukong"));
        Assert.True(VolumePaths.AreSameVolume(@"\\nas\games\repo\x", @"\\nas\games\Wukong\x"));
        Assert.False(VolumePaths.AreSameVolume(@"\\nas\games\x", @"\\nas\backup\x"));
    }

    // ─── 同盘判定 ───

    [Fact]
    public void 同一个盘上的两个路径算同盘()
    {
        Assert.True(VolumePaths.AreSameVolume(
            @"D:\UEModManager\Repository\ModA\x.pak", @"D:\Games\Wukong\Content\Paks\x.pak"));
    }

    [Fact]
    public void 大小写不同的盘符仍算同盘()
    {
        Assert.True(VolumePaths.AreSameVolume(@"d:\repo\x", @"D:\game\x"));
    }

    [Fact]
    public void 不同盘不算同盘()
    {
        // 这正是默认配置下的形态：仓库在 %LOCALAPPDATA%（C 盘），游戏装在 D 盘。
        Assert.False(VolumePaths.AreSameVolume(
            @"C:\Users\a\AppData\Local\UEModManager\Repository\x.pak",
            @"D:\Games\Wukong\Content\Paks\x.pak"));
    }

    [Theory]
    [InlineData(null, @"D:\game")]
    [InlineData(@"D:\repo", null)]
    [InlineData(@"repo\x", @"D:\game")]
    public void 说不清的一侧一律不算同盘(string? left, string? right)
    {
        // 证明不了就不说同盘：误报"同盘"会让降级提示整句说反
        Assert.False(VolumePaths.AreSameVolume(left, right));
    }

    // ─── 说人话 ───

    [Theory]
    [InlineData(@"D:\Games\Wukong", "D 盘")]
    [InlineData(@"c:\users\a", "C 盘")]
    public void 盘符说成几盘(string path, string expected)
    {
        // 面向普通玩家的文案里不能出现"卷"，只能出现"盘"
        Assert.Equal(expected, VolumePaths.TryDescribeVolume(path));
    }

    [Fact]
    public void 网络位置没有盘符就原样给共享根()
    {
        Assert.Equal(@"\\nas\games", VolumePaths.TryDescribeVolume(@"\\nas\games\Wukong"));
    }

    [Fact]
    public void 说不清时返回空而不是编一个未知盘()
    {
        // 调用方要据此换一句不点名的说法，绝不能把"未知盘"塞进用户看的句子里
        Assert.Null(VolumePaths.TryDescribeVolume(@"relative\path"));
        Assert.Null(VolumePaths.TryDescribeVolume(null));
    }

    // ─── 包含关系：两处消费者，算错都是丢数据 ───

    [Theory]
    [InlineData(@"D:\Mods", @"D:\Mods")]
    [InlineData(@"D:\Mods", @"D:\Mods\")]
    [InlineData(@"D:\Mods\", @"D:\Mods")]
    [InlineData(@"D:\Mods", @"d:\mods")]
    [InlineData(@"D:\Mods", @"D:\Mods\Repo")]
    [InlineData(@"D:\Mods", @"D:\Mods\Repo\files\a.pak")]
    public void 自己和子孙都算在里面(string parent, string candidate)
    {
        Assert.True(VolumePaths.IsSameOrInside(parent, candidate));
    }

    [Theory]
    [InlineData(@"D:\Mods", @"D:\ModsBackup")]
    [InlineData(@"C:\App", @"C:\AppData")]
    [InlineData(@"D:\Mods\Repo", @"D:\Mods")]
    [InlineData(@"D:\Mods", @"E:\Mods\Repo")]
    public void 同名前缀与反方向都不算(string parent, string candidate)
    {
        // C:\App 把 C:\AppData 算成自己的子目录，会让引导对一整类用户凭空多一条
        // "这是安装文件夹"的警告，也会让一次完全合法的搬移被误拒
        Assert.False(VolumePaths.IsSameOrInside(parent, candidate));
    }

    [Theory]
    [InlineData(null, @"D:\Mods")]
    [InlineData(@"D:\Mods", null)]
    [InlineData("", @"D:\Mods")]
    [InlineData(@"D:\Mods", "   ")]
    public void 判不出来时按不包含处理(string? parent, string? candidate)
    {
        // 两处消费者都是"证明得了才拦"，证明不了就放行，
        // 让后续真实的 IO 去失败并给出真正的原因
        Assert.False(VolumePaths.IsSameOrInside(parent, candidate));
    }
}
