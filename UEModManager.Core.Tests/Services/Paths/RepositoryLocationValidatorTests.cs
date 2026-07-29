using System.Linq;
using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// 引导里"用户选的这个位置能不能用"的判定。
///
/// <para>
/// 引导是我们<b>主动</b>把用户推到目录选择器前面的，所以他选出的每一种坏位置都算我们制造的。
/// 下面每一条用例都对应一种真实后果：写不进去 = 启动崩溃或设置静默失效；
/// 可移动盘 / 网络位置 = 某天 MOD 全部读不到；安装目录 = 卸载时被清空；
/// 非空目录 = 用户自己的文件夹被当成仓库根。
/// </para>
/// </summary>
public class RepositoryLocationValidatorTests
{
    /// <summary>一个完全健康的候选位置：本机固定盘、空目录、可写、空间充足。</summary>
    private static RepositoryLocationProbe Healthy(string path = @"D:\Mods") => new(
        SelectedPath: path,
        DirectoryHasContent: false,
        IsWritable: true,
        WriteFailureMessage: null,
        VolumeKind: RepositoryVolumeKind.Fixed,
        AvailableBytes: 500L * 1024 * 1024 * 1024,
        IsInsideInstallDirectory: false);

    private static RepositoryLocationIssueCode[] Codes(RepositoryLocationVerdict verdict)
        => verdict.Issues.Select(i => i.Code).ToArray();

    [Fact]
    public void 健康位置直接可用()
    {
        var verdict = RepositoryLocationValidator.Validate(Healthy());

        Assert.Equal(RepositoryLocationSeverity.Ok, verdict.Severity);
        Assert.True(verdict.CanUse);
        Assert.False(verdict.NeedsConfirmation);
        Assert.Empty(verdict.Issues);
        Assert.Equal(@"D:\Mods", verdict.ResolvedPath);
    }

    // ─── 拦下来的（Blocked）───

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 没选路径时拦下(string? path)
    {
        var verdict = RepositoryLocationValidator.Validate(Healthy() with { SelectedPath = path });

        Assert.Equal(RepositoryLocationSeverity.Blocked, verdict.Severity);
        Assert.False(verdict.CanUse);
        Assert.Equal(new[] { RepositoryLocationIssueCode.Empty }, Codes(verdict));
    }

    [Theory]
    [InlineData(@"Mods")]
    [InlineData(@"..\Mods")]
    [InlineData(@"\Mods")]
    [InlineData(@"D:Mods")]
    public void 相对路径拦下(string path)
    {
        // 相对路径按"相对当前工作目录"解析，而 WPF 进程的工作目录不受控
        // （快捷方式、拖拽启动、开机自启各不相同），仓库位置会跟着漂
        var verdict = RepositoryLocationValidator.Validate(Healthy() with { SelectedPath = path });

        Assert.Equal(RepositoryLocationSeverity.Blocked, verdict.Severity);
        Assert.Equal(new[] { RepositoryLocationIssueCode.NotAbsolute }, Codes(verdict));
    }

    [Fact]
    public void 含非法字符的路径拦下()
    {
        var verdict = RepositoryLocationValidator.Validate(
            Healthy() with { SelectedPath = "D:\\Mods\u0000bad" });

        Assert.Equal(RepositoryLocationSeverity.Blocked, verdict.Severity);
        Assert.Equal(new[] { RepositoryLocationIssueCode.Malformed }, Codes(verdict));
    }

    [Fact]
    public void 不可写的位置拦下并带上原因()
    {
        // 这是引导必须提前拦住的头号情形：SaveRepositoryRoot 的写失败会上抛，
        // 而引导跑在启动早期，不拦就是一次启动崩溃。
        var verdict = RepositoryLocationValidator.Validate(Healthy() with
        {
            IsWritable = false,
            WriteFailureMessage = "对路径的访问被拒绝",
        });

        Assert.Equal(RepositoryLocationSeverity.Blocked, verdict.Severity);
        Assert.False(verdict.CanUse);
        Assert.Equal(new[] { RepositoryLocationIssueCode.NotWritable }, Codes(verdict));
        Assert.Contains("对路径的访问被拒绝", verdict.Issues[0].Message);
    }

    [Fact]
    public void 不可写且原因缺失时也给得出一句完整的话()
    {
        // 空白原因拼进去会得到一个"这个位置写不进去（）"的半截句子
        var verdict = RepositoryLocationValidator.Validate(Healthy() with
        {
            IsWritable = false,
            WriteFailureMessage = null,
        });

        Assert.DoesNotContain("（）", verdict.Issues[0].Message);
    }

    [Fact]
    public void 不可写优先于一切警告()
    {
        // 一个不可写的 U 盘上报四条警告没有意义，用户只需要知道"换一个"
        var verdict = RepositoryLocationValidator.Validate(Healthy() with
        {
            IsWritable = false,
            VolumeKind = RepositoryVolumeKind.Removable,
            AvailableBytes = 0,
            IsInsideInstallDirectory = true,
        });

        Assert.Equal(new[] { RepositoryLocationIssueCode.NotWritable }, Codes(verdict));
    }

    // ─── 放行但要确认的（Warning）───

    [Fact]
    public void 可移动盘给警告但不禁止()
    {
        var verdict = RepositoryLocationValidator.Validate(
            Healthy() with { VolumeKind = RepositoryVolumeKind.Removable });

        Assert.Equal(RepositoryLocationSeverity.Warning, verdict.Severity);
        Assert.True(verdict.CanUse);
        Assert.True(verdict.NeedsConfirmation);
        Assert.Contains(RepositoryLocationIssueCode.RemovableVolume, Codes(verdict));
    }

    [Fact]
    public void 网络位置给警告但不禁止()
    {
        var verdict = RepositoryLocationValidator.Validate(
            Healthy() with { VolumeKind = RepositoryVolumeKind.Network });

        Assert.Equal(RepositoryLocationSeverity.Warning, verdict.Severity);
        Assert.Contains(RepositoryLocationIssueCode.NetworkVolume, Codes(verdict));
    }

    [Fact]
    public void 安装目录内给警告()
    {
        // 卸载与覆盖安装会清空安装目录。把用户数据放进去正是数据搬迁那一整轮要根治的病根，
        // 引导不该反手再造一个。
        var verdict = RepositoryLocationValidator.Validate(
            Healthy() with { IsInsideInstallDirectory = true });

        Assert.Equal(RepositoryLocationSeverity.Warning, verdict.Severity);
        Assert.Contains(RepositoryLocationIssueCode.InsideInstallDirectory, Codes(verdict));
    }

    [Fact]
    public void 空间偏少给警告()
    {
        var verdict = RepositoryLocationValidator.Validate(Healthy() with
        {
            AvailableBytes = RepositoryLocationValidator.RecommendedFreeBytes - 1,
        });

        Assert.Equal(RepositoryLocationSeverity.Warning, verdict.Severity);
        Assert.Contains(RepositoryLocationIssueCode.LowFreeSpace, Codes(verdict));
    }

    [Fact]
    public void 空间刚好达标不给警告()
    {
        var verdict = RepositoryLocationValidator.Validate(Healthy() with
        {
            AvailableBytes = RepositoryLocationValidator.RecommendedFreeBytes,
        });

        Assert.Equal(RepositoryLocationSeverity.Ok, verdict.Severity);
    }

    [Fact]
    public void 查不到可用空间时不报空间警告()
    {
        // 判据本身不成立。拿"查不到"当"不够"，会给映射盘/UNC 这一整类用户凭空加一条假警告，
        // 与 DiskSpacePrecheck 对 Unknown 的处理是同一条原则。
        var verdict = RepositoryLocationValidator.Validate(Healthy() with { AvailableBytes = null });

        Assert.DoesNotContain(RepositoryLocationIssueCode.LowFreeSpace, Codes(verdict));
        Assert.Equal(RepositoryLocationSeverity.Ok, verdict.Severity);
    }

    [Fact]
    public void 多个问题同时列出()
    {
        // U 盘 + 快满了：两条都要说，用户才知道这个选择有两笔账
        var verdict = RepositoryLocationValidator.Validate(Healthy() with
        {
            VolumeKind = RepositoryVolumeKind.Removable,
            AvailableBytes = 1024,
            DirectoryHasContent = true,
        });

        Assert.Equal(RepositoryLocationSeverity.Warning, verdict.Severity);
        Assert.Contains(RepositoryLocationIssueCode.RemovableVolume, Codes(verdict));
        Assert.Contains(RepositoryLocationIssueCode.LowFreeSpace, Codes(verdict));
        Assert.Contains(RepositoryLocationIssueCode.DirectoryNotEmpty, Codes(verdict));
    }

    // ─── 非空目录：提示，不是警告 ───

    [Fact]
    public void 目录非空只提示落点变化不升级成警告()
    {
        // 落点已经退到专用子目录，风险本身被消掉了，剩下的只是"要让用户看见落点变了"。
        // 升级成 Warning 会让绝大多数选择（谁都会选一个已有文件的盘/文件夹）都弹一次二次确认。
        var verdict = RepositoryLocationValidator.Validate(
            Healthy() with { DirectoryHasContent = true });

        Assert.Equal(RepositoryLocationSeverity.Ok, verdict.Severity);
        Assert.False(verdict.NeedsConfirmation);
        Assert.Equal(new[] { RepositoryLocationIssueCode.DirectoryNotEmpty }, Codes(verdict));
    }

    // ─── 落点计算 ───

    [Fact]
    public void 空目录直接当仓库根()
    {
        Assert.Equal(@"D:\Mods",
            RepositoryLocationValidator.ResolveRepositoryPath(@"D:\Mods", directoryHasContent: false));
    }

    [Fact]
    public void 非空目录退到专用子目录()
    {
        // 这一条挡的正是 RepositoryReclaimPlanner 第 5 条判据在防的场景：
        // 用户把仓库根指到 D:\Games\Mods 这种既有目录，里面的每个子目录都"没有 manifest、
        // 不在任何索引里"，全靠"目录形态不像仓库产物"才没被回收器误删。
        // 引导不制造这个场景 —— 退一层子目录，用户原有的文件与仓库彻底分开。
        Assert.Equal(@"D:\Games\Mods\UEModManager\Repository",
            RepositoryLocationValidator.ResolveRepositoryPath(@"D:\Games\Mods", directoryHasContent: true));
    }

    [Fact]
    public void 选盘符根目录也会退到专用子目录()
    {
        // 盘根几乎不可能是空的，这正是想要的结果：绝不把 D:\ 本身当成仓库根
        Assert.Equal(@"D:\UEModManager\Repository",
            RepositoryLocationValidator.ResolveRepositoryPath(@"D:\", directoryHasContent: true));
    }

    [Fact]
    public void 落点会剪掉首尾空白()
    {
        Assert.Equal(@"D:\Mods",
            RepositoryLocationValidator.ResolveRepositoryPath("  D:\\Mods  ", directoryHasContent: false));
    }

    [Fact]
    public void 空路径算不出落点()
    {
        Assert.Throws<ArgumentException>(
            () => RepositoryLocationValidator.ResolveRepositoryPath("  ", directoryHasContent: false));
    }

    [Fact]
    public void 判定给出的落点与单独计算一致()
    {
        // 界面上显示的落点与最终写进配置的必须是同一个值，
        // 否则用户看到 A、数据落到 B —— 这类不一致事后完全无从解释
        var verdict = RepositoryLocationValidator.Validate(
            Healthy(@"D:\Games") with { DirectoryHasContent = true });

        Assert.Equal(
            RepositoryLocationValidator.ResolveRepositoryPath(@"D:\Games", true),
            verdict.ResolvedPath);
    }
}
