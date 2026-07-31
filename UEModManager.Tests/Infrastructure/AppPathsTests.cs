using UEModManager.Infrastructure;
using UEModManager.Services;
using UEModManager.Tests.Services;

namespace UEModManager.Tests.Infrastructure;

/// <summary>
/// AppPaths 与 UiPreferences 之间那条环的看门测试。
///
/// AppPaths 解析可覆盖的数据根时要读 UiPreferences，UiPreferences 定位自己的配置文件时
/// 又要问 AppPaths。环不闭合的唯一理由是 <c>AppPaths.UiConfigFile</c> 用的是不含用户覆盖的
/// 布局；改成读覆盖的版本就会无限递归。StackOverflowException 在 .NET 里 catch 不住，
/// 一旦发生是整个进程当场消失，故这里用最直接的方式看住：真的调一次，能返回就说明环是断的。
///
/// 本文件只读取路径与既有配置，不写入任何文件（UiPreferences 定位配置时可能创建
/// %APPDATA%\UEModManager 目录，这是它固有的行为，不会产生内容）。
///
/// 挂 collection 是因为本类会读 UiPreferences 的进程级内存单例，
/// 而 <see cref="UEModManager.Tests.Services.UiPreferencesFailureTests"/> 会把它的配置路径
/// 临时重定向到 temp 目录；并行跑会互相串味。
/// </summary>
[Collection(UiPreferencesStaticStateCollection.Name)]
public sealed class AppPathsTests
{
    [Fact]
    public void UiConfigFile_ResolvesWithoutRecursingIntoUiPreferences()
    {
        var path = AppPaths.UiConfigFile;

        Assert.Equal(Path.Combine(AppPaths.RoamingRoot, "ui_config.json"), path);
    }

    [Fact]
    public void UiPreferences_ReadingOverrides_DoesNotRecurse()
    {
        // 反方向再走一遍：读覆盖 → 定位配置文件 → AppPaths，必须能返回
        _ = UiPreferences.LoadRepositoryRoot();
        _ = AppPaths.RepositoryRoot;
    }

    [Fact]
    public void RoamingSideDirectories_StayUnderRoamingRoot()
    {
        // 这三项的磁盘位置是历史遗留，归口 AppPaths 时不得顺手搬家——搬了老用户数据就失联
        Assert.Equal(Path.Combine(AppPaths.RoamingRoot, "config"), AppPaths.SecretsDirectory);
        Assert.Equal(Path.Combine(AppPaths.RoamingRoot, "Backgrounds"), AppPaths.BackgroundsDirectory);
        Assert.Equal(Path.Combine(AppPaths.RoamingRoot, "local.db"), AppPaths.LocalDatabaseFile);
    }

    /// <summary>
    /// 设备标识必须在**本机**层，不能漫游。
    ///
    /// <para>
    /// 漫游目录在域环境里会在多台机器之间同步。设备标识一旦跟着同步过去，两台机器共用
    /// 一个"设备"，累计设备数和在线数会一起塌掉——而且塌得毫无痕迹：看板上只是数字偏小，
    /// 没有任何异常可查。这条判据没有别的地方能兜住，只能钉在这里。
    /// </para>
    /// </summary>
    [Fact]
    public void DeviceIdFile_StaysUnderLocalRoot_NotRoaming()
    {
        Assert.Equal(Path.Combine(AppPaths.LocalRoot, "device.id"), AppPaths.DeviceIdFile);
        Assert.DoesNotContain(AppPaths.RoamingRoot, AppPaths.DeviceIdFile);
    }
}
