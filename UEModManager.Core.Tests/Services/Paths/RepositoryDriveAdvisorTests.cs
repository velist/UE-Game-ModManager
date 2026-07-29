using System.Linq;
using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// 引导界面上盘位列表的排序与推荐。
///
/// <para>
/// 这一层的价值全在"别把用户往坑里推"：一个 2 TB 的移动硬盘在按可用空间排的列表里会稳稳
/// 占据第一行，而它拔掉之后用户的 MOD 全部读不到。排序规则与推荐资格必须分开钉死。
/// </para>
/// </summary>
public class RepositoryDriveAdvisorTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static RepositoryDriveOption Drive(
        string root, RepositoryVolumeKind kind, long? available, long? total = null)
        => new(root, root.TrimEnd('\\'), kind, available, total ?? (available * 2), false);

    [Fact]
    public void 固定盘排在可移动与网络之前()
    {
        var ranked = RepositoryDriveAdvisor.Rank(new[]
        {
            Drive(@"E:\", RepositoryVolumeKind.Removable, 2000 * GiB),
            Drive(@"Z:\", RepositoryVolumeKind.Network, 5000 * GiB),
            Drive(@"C:\", RepositoryVolumeKind.Fixed, 40 * GiB),
        });

        Assert.Equal(new[] { @"C:\", @"E:\", @"Z:\" }, ranked.Select(d => d.RootPath));
    }

    [Fact]
    public void 同类之间按可用空间降序()
    {
        var ranked = RepositoryDriveAdvisor.Rank(new[]
        {
            Drive(@"C:\", RepositoryVolumeKind.Fixed, 40 * GiB),
            Drive(@"D:\", RepositoryVolumeKind.Fixed, 900 * GiB),
            Drive(@"F:\", RepositoryVolumeKind.Fixed, 300 * GiB),
        });

        Assert.Equal(new[] { @"D:\", @"F:\", @"C:\" }, ranked.Select(d => d.RootPath));
    }

    [Fact]
    public void 查不到空间的排在同类最后()
    {
        // 界面上它们只能显示"容量未知"，放前面会挤掉真正能帮用户判断的那几行
        var ranked = RepositoryDriveAdvisor.Rank(new[]
        {
            Drive(@"D:\", RepositoryVolumeKind.Fixed, null, null),
            Drive(@"C:\", RepositoryVolumeKind.Fixed, 10 * GiB),
        });

        Assert.Equal(new[] { @"C:\", @"D:\" }, ranked.Select(d => d.RootPath));
    }

    [Fact]
    public void 排序稳定_同类同空间按盘符()
    {
        var ranked = RepositoryDriveAdvisor.Rank(new[]
        {
            Drive(@"F:\", RepositoryVolumeKind.Fixed, 100 * GiB),
            Drive(@"D:\", RepositoryVolumeKind.Fixed, 100 * GiB),
        });

        Assert.Equal(new[] { @"D:\", @"F:\" }, ranked.Select(d => d.RootPath));
    }

    [Fact]
    public void 空列表不炸()
    {
        Assert.Empty(RepositoryDriveAdvisor.Rank(Array.Empty<RepositoryDriveOption>()));
        Assert.Null(RepositoryDriveAdvisor.Recommend(Array.Empty<RepositoryDriveOption>()));
    }

    // ─── 推荐资格 ───

    [Fact]
    public void 推荐可用空间最大的固定盘()
    {
        var recommended = RepositoryDriveAdvisor.Recommend(new[]
        {
            Drive(@"C:\", RepositoryVolumeKind.Fixed, 40 * GiB),
            Drive(@"D:\", RepositoryVolumeKind.Fixed, 900 * GiB),
        });

        Assert.Equal(@"D:\", recommended?.RootPath);
    }

    [Fact]
    public void 再大的移动硬盘也不推荐()
    {
        // 这条是整个推荐逻辑存在的理由：拔掉之后仓库整个消失，
        // 而"推荐"两个字会让普通玩家直接采纳
        var recommended = RepositoryDriveAdvisor.Recommend(new[]
        {
            Drive(@"E:\", RepositoryVolumeKind.Removable, 4000 * GiB),
            Drive(@"C:\", RepositoryVolumeKind.Fixed, 40 * GiB),
        });

        Assert.Equal(@"C:\", recommended?.RootPath);
    }

    [Fact]
    public void 网络位置不推荐()
    {
        var recommended = RepositoryDriveAdvisor.Recommend(new[]
        {
            Drive(@"Z:\", RepositoryVolumeKind.Network, 4000 * GiB),
        });

        Assert.Null(recommended);
    }

    [Fact]
    public void 没有盘装得下时不推荐()
    {
        // 推荐一个装不下的位置比不推荐更糟：用户会以为程序已经替他确认过了
        var recommended = RepositoryDriveAdvisor.Recommend(new[]
        {
            Drive(@"C:\", RepositoryVolumeKind.Fixed,
                RepositoryLocationValidator.RecommendedFreeBytes - 1),
        });

        Assert.Null(recommended);
    }

    [Fact]
    public void 空间查不到的固定盘不推荐()
    {
        // 证明不了它装得下
        var recommended = RepositoryDriveAdvisor.Recommend(new[]
        {
            Drive(@"D:\", RepositoryVolumeKind.Fixed, null, null),
        });

        Assert.Null(recommended);
    }

    [Fact]
    public void 空间刚好达标就够资格()
    {
        var recommended = RepositoryDriveAdvisor.Recommend(new[]
        {
            Drive(@"D:\", RepositoryVolumeKind.Fixed,
                RepositoryLocationValidator.RecommendedFreeBytes),
        });

        Assert.Equal(@"D:\", recommended?.RootPath);
    }

    [Fact]
    public void 推荐项来自传入的同一批实例()
    {
        // 界面靠引用相等把"推荐"角标打到对应的那一行上，返回副本会让角标整个消失
        var drives = new[] { Drive(@"D:\", RepositoryVolumeKind.Fixed, 900 * GiB) };

        Assert.Same(drives[0], RepositoryDriveAdvisor.Recommend(drives));
        Assert.Same(drives[0], RepositoryDriveAdvisor.Rank(drives)[0]);
    }
}
