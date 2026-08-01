using System.Windows;
using System.Windows.Media;
using UEModManager.Services.Paths;
using UEModManager.Views;

namespace UEModManager.Tests.Views;

/// <summary>
/// 首次运行引导界面的行展示模型。
///
/// <para>
/// 无头环境点不了 WPF 交互，但"多大空间显示成什么颜色""可移动盘有没有被打上标签"
/// 这些映射是纯函数，可以直接钉住。这也正是把它们从控件构造里拆出来的收益，
/// 与 <see cref="ManagementCenterRowTests"/> 同一形态。
/// </para>
///
/// <para>
/// 画刷解析器一律传 <c>_ =&gt; null</c>：这里要锁的是"选了哪个令牌"，
/// 而不是令牌当前解析成什么颜色——后者属于主题，不该由本测试固定。
/// </para>
/// </summary>
public class RepositorySetupRowTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly Func<string, Brush?> NoBrush = _ => null;

    private static RepositoryDriveOption Option(
        RepositoryVolumeKind kind = RepositoryVolumeKind.Fixed,
        long? available = 500 * GiB, long? total = 1000 * GiB, bool isCurrentDefault = false,
        bool hostsCurrentGame = false)
        => new(@"D:\", "D:", kind, available, total, isCurrentDefault, hostsCurrentGame);

    // ─── 角标 ───

    [Fact]
    public void 可移动盘的角标压过推荐()
    {
        // 一个 4 TB 的移动硬盘在按空间排的列表里会很显眼，而它拔掉之后 MOD 全部读不到。
        // "推荐"两个字会让普通玩家直接采纳，所以风险标签必须优先。
        Assert.Equal("可移动", RepositoryDriveRow.BadgeTextFor(
            RepositoryVolumeKind.Removable, isRecommended: true, isCurrentDefault: false));
        Assert.Equal("StatusOrangeBrush", RepositoryDriveRow.BadgeBrushKeyFor(
            RepositoryVolumeKind.Removable, isRecommended: true));
    }

    [Fact]
    public void 网络位置同样打风险标签()
    {
        Assert.Equal("网络位置", RepositoryDriveRow.BadgeTextFor(
            RepositoryVolumeKind.Network, isRecommended: true, isCurrentDefault: true));
        Assert.Equal("StatusOrangeBrush", RepositoryDriveRow.BadgeBrushKeyFor(
            RepositoryVolumeKind.Network, isRecommended: false));
    }

    [Theory]
    [InlineData(true, false, "推荐", "StatusGreenBrush")]
    [InlineData(false, true, "当前位置", "Text500Brush")]
    [InlineData(false, false, "", "Text500Brush")]
    public void 固定盘按推荐与现状打标(
        bool isRecommended, bool isCurrentDefault, string expectedText, string expectedKey)
    {
        Assert.Equal(expectedText, RepositoryDriveRow.BadgeTextFor(
            RepositoryVolumeKind.Fixed, isRecommended, isCurrentDefault));
        Assert.Equal(expectedKey, RepositoryDriveRow.BadgeBrushKeyFor(
            RepositoryVolumeKind.Fixed, isRecommended));
    }

    [Fact]
    public void 没有角标时整块隐藏()
    {
        // 留一个空的角标背景块，界面上会多出一个没有内容的灰色小方块
        var row = RepositoryDriveRow.Create(Option(), isRecommended: false, NoBrush);

        Assert.Equal(Visibility.Collapsed, row.BadgeVisibility);
    }

    [Fact]
    public void 有角标时显示()
    {
        var row = RepositoryDriveRow.Create(Option(), isRecommended: true, NoBrush);

        Assert.Equal(Visibility.Visible, row.BadgeVisibility);
        Assert.Equal("推荐", row.BadgeText);
    }

    // ─── 容量 ───

    [Fact]
    public void 空间不足时比例条转红()
    {
        // 判据取剩余绝对值而不是百分比：一个 90% 已用的 4 TB 盘仍然绰绰有余，
        // 而一个 50% 已用的 16 GB 盘装不下一个整合包。
        Assert.Equal("StatusRedBrush", RepositoryDriveRow.UsageBrushKeyFor(
            RepositoryLocationValidator.RecommendedFreeBytes - 1));
        Assert.Equal("PrimaryBrush", RepositoryDriveRow.UsageBrushKeyFor(
            RepositoryLocationValidator.RecommendedFreeBytes));
    }

    [Fact]
    public void 空间未知时不转红()
    {
        // 证明不了它装不下。与 DiskSpacePrecheck 对 Unknown 的处理是同一条原则。
        Assert.Equal("PrimaryBrush", RepositoryDriveRow.UsageBrushKeyFor(null));
    }

    [Fact]
    public void 容量文字带上可用与总量()
    {
        var text = RepositoryDriveRow.CapacityTextFor(100 * GiB, 500 * GiB);

        Assert.Contains("100 GiB", text);
        Assert.Contains("500 GiB", text);
    }

    [Theory]
    [InlineData(null, 100L)]
    [InlineData(100L, null)]
    [InlineData(100L, 0L)]
    public void 容量查不到时不编数字(long? available, long? total)
    {
        Assert.Equal("容量未知", RepositoryDriveRow.CapacityTextFor(available, total));
        Assert.Equal(0, RepositoryDriveRow.UsedPercentFor(available, total));
    }

    [Fact]
    public void 已用比例正常换算()
    {
        Assert.Equal(80, RepositoryDriveRow.UsedPercentFor(20 * GiB, 100 * GiB));
    }

    [Fact]
    public void 已用比例被夹在合法区间内()
    {
        // AvailableFreeSpace 会把磁盘配额算进去，可能比 TotalSize 还大（域环境），
        // 不夹的话 ProgressBar 会拿到负值
        Assert.Equal(0, RepositoryDriveRow.UsedPercentFor(200 * GiB, 100 * GiB));
    }

    [Fact]
    public void 行模型带上盘符供选中后取用()
    {
        var row = RepositoryDriveRow.Create(Option(), isRecommended: true, NoBrush);

        Assert.Equal(@"D:\", row.RootPath);
        Assert.True(row.IsRecommended);
    }

    // ─── 与游戏同一个盘 ───

    [Fact]
    public void 拿不到游戏路径时整行不显示()
    {
        // 首次运行引导跑在启动早期，config.json 还不存在（它不存在正是引导会弹的前提之一），
        // 所以"拿不到游戏路径"是常态而不是异常。此时这条提示整体消失，
        // 界面上绝不能出现"未知"或任何错误字样。
        var row = RepositoryDriveRow.Create(Option(hostsCurrentGame: false), isRecommended: false, NoBrush);

        Assert.Equal(Visibility.Collapsed, row.GameVolumeVisibility);
        Assert.Equal(string.Empty, row.GameVolumeText);
    }

    [Fact]
    public void 游戏在这个盘时说清楚选它能省什么()
    {
        var row = RepositoryDriveRow.Create(Option(hostsCurrentGame: true), isRecommended: false, NoBrush);

        Assert.Equal(Visibility.Visible, row.GameVolumeVisibility);
        Assert.Contains("游戏", row.GameVolumeText);
        Assert.Contains("空间", row.GameVolumeText);
        Assert.Equal("StatusGreenBrush", row.GameVolumeBrushKey);
    }

    [Fact]
    public void 同盘提示不和风险角标抢位置()
    {
        // 一块装着游戏的移动硬盘：拔掉就全没了，和放这里能省空间，两件事都成立。
        // 角标那一格已经按"风险 > 推荐 > 现状"排好优先级，所以同盘提示单独占一行，
        // 两条信息同时在，由用户自己权衡——不该由我们替他二选一。
        var row = RepositoryDriveRow.Create(
            Option(RepositoryVolumeKind.Removable, hostsCurrentGame: true),
            isRecommended: false, NoBrush);

        Assert.Equal("可移动", row.BadgeText);
        Assert.Equal("StatusOrangeBrush", row.BadgeBrushKey);
        Assert.Equal(Visibility.Visible, row.GameVolumeVisibility);
    }

    [Fact]
    public void 同盘不改变推荐资格()
    {
        // 让移动硬盘借"同盘"绕过"可移动盘永不推荐"，正是那条规则要防的事
        var drives = new[]
        {
            new RepositoryDriveOption(@"E:\", "E:", RepositoryVolumeKind.Removable,
                4000 * GiB, 4000 * GiB, false, HostsCurrentGame: true),
            new RepositoryDriveOption(@"C:\", "C:", RepositoryVolumeKind.Fixed,
                40 * GiB, 500 * GiB, true, HostsCurrentGame: false),
        };

        Assert.Equal(@"C:\", RepositoryDriveAdvisor.Recommend(drives)?.RootPath);
    }

    // ─── 提示行 ───
    [Fact]
    public void 落点变化只是提示不是警告()
    {
        // 风险已经被"退到专用子目录"消掉了，剩下的只是让用户看见落点变了。
        // 用警示色会让绝大多数选择都显得像出了问题。
        Assert.True(RepositoryIssueRow.IsAdvisory(RepositoryLocationIssueCode.DirectoryNotEmpty));
        Assert.Equal("Text500Brush", RepositoryIssueRow.BrushKeyFor(
            RepositoryLocationIssueCode.DirectoryNotEmpty, RepositoryLocationSeverity.Ok));
    }

    [Theory]
    [InlineData(RepositoryLocationIssueCode.RemovableVolume)]
    [InlineData(RepositoryLocationIssueCode.NetworkVolume)]
    [InlineData(RepositoryLocationIssueCode.InsideInstallDirectory)]
    [InlineData(RepositoryLocationIssueCode.LowFreeSpace)]
    public void 每一条真实风险都走警示色(RepositoryLocationIssueCode code)
    {
        Assert.False(RepositoryIssueRow.IsAdvisory(code));
        Assert.Equal("StatusOrangeBrush",
            RepositoryIssueRow.BrushKeyFor(code, RepositoryLocationSeverity.Warning));
    }

    [Fact]
    public void 被拦下的位置用红色()
    {
        Assert.Equal("StatusRedBrush", RepositoryIssueRow.BrushKeyFor(
            RepositoryLocationIssueCode.NotWritable, RepositoryLocationSeverity.Blocked));
    }

    [Fact]
    public void 提示行原样带上可展示的文案()
    {
        // Core 给的 Message 是面向普通玩家写的完整句子，界面不许再加工
        var issue = new RepositoryLocationIssue(
            RepositoryLocationIssueCode.RemovableVolume, "这是一个可移动磁盘。");

        var row = RepositoryIssueRow.Create(issue, RepositoryLocationSeverity.Warning, NoBrush);

        Assert.Equal("这是一个可移动磁盘。", row.Message);
        Assert.False(string.IsNullOrEmpty(row.Glyph));
    }
}
