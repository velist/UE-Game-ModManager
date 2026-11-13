using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

public class AppDataLayoutTests
{
    private const string Local = @"C:\Users\u\AppData\Local\UEModManager";
    private const string Roaming = @"C:\Users\u\AppData\Roaming\UEModManager";

    private static AppDataLayout Layout(IReadOnlyDictionary<DataRoot, string>? overrides = null)
        => new(Local, Roaming, overrides);

    // ─── 分层：本机 vs 漫游 ───

    [Fact]
    public void MachineBoundData_LivesUnderLocalRoot()
    {
        var layout = Layout();

        Assert.StartsWith(Local, layout.ConfigFile);
        Assert.StartsWith(Local, layout.DataDirectory);
        Assert.StartsWith(Local, layout.LogsDirectory);
        Assert.StartsWith(Local, layout.RepositoryRoot);
        Assert.StartsWith(Local, layout.OverwritesRoot);
        Assert.StartsWith(Local, layout.BackupsRoot);
    }

    [Fact]
    public void PortablePreferences_LiveUnderRoamingRoot()
    {
        var layout = Layout();

        Assert.StartsWith(Roaming, layout.UiConfigFile);
        Assert.StartsWith(Roaming, layout.AvatarsDirectory);
        Assert.StartsWith(Roaming, layout.BackgroundsDirectory);
        Assert.StartsWith(Roaming, layout.SecretsDirectory);
    }

    [Fact]
    public void SecretsDirectory_KeepsHistoricalFolderName()
    {
        // 属性名叫 Secrets，磁盘上必须仍是 config —— 改名会让老用户已加密的密钥文件失联
        Assert.Equal(Path.Combine(Roaming, "config"), Layout().SecretsDirectory);
    }

    [Fact]
    public void BackgroundsDirectory_SharesRootWithUiConfig()
    {
        var layout = Layout();

        // 背景图副本与记录它的 ui_config.json 必须同层，否则换机器后配置指向的图片留在原机
        Assert.Equal(Path.GetDirectoryName(layout.UiConfigFile), Path.GetDirectoryName(layout.BackgroundsDirectory));
    }

    [Fact]
    public void BothBackupKinds_ShareOneBackupRoot()
    {
        var layout = Layout();

        // 修掉历史上"部署事务备份"与"MOD 备份"两个互不相干的备份根
        Assert.StartsWith(layout.BackupsRoot, layout.DeploymentBackupsDirectory);
        Assert.StartsWith(layout.BackupsRoot, layout.ModBackupsDirectory);
        Assert.NotEqual(layout.DeploymentBackupsDirectory, layout.ModBackupsDirectory);
    }

    [Fact]
    public void GameIconsAndLaunchSessions_LiveUnderDataDirectory()
    {
        var layout = Layout();

        Assert.StartsWith(layout.DataDirectory, layout.GameIconsDirectory);
        Assert.StartsWith(layout.DataDirectory, layout.LaunchSessionsDirectory);
    }

    // ─── 用户覆盖 ───

    [Fact]
    public void Override_TakesPrecedenceOverDefault()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Repository] = @"E:\MyRepo" });

        Assert.Equal(@"E:\MyRepo", layout.RepositoryRoot);
        Assert.True(layout.IsOverridden(DataRoot.Repository));
    }

    [Fact]
    public void Override_DoesNotLeakToOtherRoots()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Repository] = @"E:\MyRepo" });

        Assert.StartsWith(Local, layout.OverwritesRoot);
        Assert.StartsWith(Local, layout.BackupsRoot);
        Assert.False(layout.IsOverridden(DataRoot.Overwrites));
        Assert.False(layout.IsOverridden(DataRoot.Backups));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverride_TreatedAsNotOverridden(string blank)
    {
        // 配置文件里留下一个空字符串，不应该把仓库指到当前工作目录
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Repository] = blank });

        Assert.StartsWith(Local, layout.RepositoryRoot);
        Assert.False(layout.IsOverridden(DataRoot.Repository));
    }

    [Fact]
    public void Override_IsTrimmed()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Repository] = @"  E:\MyRepo  " });

        Assert.Equal(@"E:\MyRepo", layout.RepositoryRoot);
    }

    [Fact]
    public void BackupsOverride_MovesBothSubdirectories()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Backups] = @"D:\Bak" });

        Assert.StartsWith(@"D:\Bak", layout.DeploymentBackupsDirectory);
        Assert.StartsWith(@"D:\Bak", layout.ModBackupsDirectory);
    }

    // ─── 部署事务备份跟着仓库走，不留在系统盘 ───

    [Fact]
    public void DeploymentBackups_StayAtFactoryLocation_WhenNothingIsCustomized()
    {
        var layout = Layout();

        Assert.Equal(layout.DefaultDeploymentBackupsDirectory, layout.DeploymentBackupsDirectory);
        Assert.Empty(layout.LegacyDeploymentBackupsDirectories);
    }

    [Fact]
    public void DeploymentBackups_FollowRepository_WhenRepositoryIsMovedOffFactoryLocation()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Repository] = @"D:\UEModManager\Repository" });

        // 每停用/替换一个 MOD 文件这里就多一份副本：仓库都搬去 D 盘了，这份不能还往 C 盘写。
        Assert.Equal(@"D:\UEModManager\Repository\.deploy-backups", layout.DeploymentBackupsDirectory);
        Assert.False(layout.DeploymentBackupsDirectory.StartsWith(Local, StringComparison.OrdinalIgnoreCase));
        // MOD 备份不跟着动：它的位置另由游戏所在盘决定，且老配置里存的是绝对路径。
        Assert.StartsWith(Local, layout.ModBackupsDirectory);
    }

    [Fact]
    public void DeploymentBackups_InsideRepository_UseAnInternalDotFolder()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Repository] = @"D:\Repo" });

        // 仓库的回收与体检按「以 . 开头」认内部目录；改了名字它就会被当成一个来历不明的包。
        Assert.StartsWith(".", Path.GetFileName(layout.DeploymentBackupsDirectory));
    }

    [Fact]
    public void DeploymentBackups_OldSystemDriveLocation_StaysVisibleAsLegacy()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Repository] = @"D:\Repo" });

        // 位置变了以后，老用户留在系统盘上的事务仍要能被崩溃恢复和定时清理看见。
        Assert.Equal([layout.DefaultDeploymentBackupsDirectory], layout.LegacyDeploymentBackupsDirectories);
    }

    [Fact]
    public void DeploymentBackups_ExplicitBackupsRoot_WinsOverRepository()
    {
        var layout = Layout(new Dictionary<DataRoot, string>
        {
            [DataRoot.Repository] = @"D:\Repo",
            [DataRoot.Backups] = @"E:\Bak",
        });

        Assert.Equal(@"E:\Bak\Deployments", layout.DeploymentBackupsDirectory);
    }

    [Fact]
    public void GetRoot_MatchesCorrespondingProperty()
    {
        var layout = Layout(new Dictionary<DataRoot, string> { [DataRoot.Overwrites] = @"F:\Ow" });

        Assert.Equal(layout.RepositoryRoot, layout.GetRoot(DataRoot.Repository));
        Assert.Equal(layout.OverwritesRoot, layout.GetRoot(DataRoot.Overwrites));
        Assert.Equal(layout.BackupsRoot, layout.GetRoot(DataRoot.Backups));
    }

    // ─── 参数校验 ───

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankRoot_Throws(string? bad)
    {
        // 这类错误只可能来自编程失误，静默回落只会把问题推到更难查的地方
        Assert.Throws<ArgumentException>(() => new AppDataLayout(bad!, Roaming));
        Assert.Throws<ArgumentException>(() => new AppDataLayout(Local, bad!));
    }

    [Fact]
    public void Layout_DoesNotTouchFileSystem()
    {
        // 用一个绝不存在的路径构造，所有属性都应能正常求值
        var layout = new AppDataLayout(@"Z:\nope\local", @"Z:\nope\roaming");

        Assert.NotEmpty(layout.ConfigFile);
        Assert.NotEmpty(layout.RepositoryRoot);
        Assert.NotEmpty(layout.AvatarsDirectory);
    }
}
