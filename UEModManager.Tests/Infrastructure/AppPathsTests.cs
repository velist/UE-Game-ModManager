using UEModManager.Infrastructure;
using UEModManager.Services;

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
/// </summary>
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
}
