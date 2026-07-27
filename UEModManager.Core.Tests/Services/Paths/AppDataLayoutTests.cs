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
