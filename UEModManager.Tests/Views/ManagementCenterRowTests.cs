using System.Windows;
using System.Windows.Media;
using UEModManager.Models;
using UEModManager.Views;

namespace UEModManager.Tests.Views;

/// <summary>
/// 管理中心三个列表的行展示模型测试。
///
/// 这些映射（状态 → 文字 / 颜色令牌 / 按钮可见性）原本埋在 187 行手工建控件的代码里，
/// 只能靠肉眼比对。搬到行模型后可以直接断言，改主题令牌名或状态枚举时能立刻发现。
///
/// 画刷解析器一律传 <c>_ =&gt; null</c>：这里要锁的是"选了哪个令牌"，
/// 而不是令牌当前解析成什么颜色——后者属于主题，不该由本测试固定。
/// </summary>
public class ManagementCenterRowTests
{
    private static readonly Func<string, Brush?> NoBrush = _ => null;

    // ─── MOD 库 ───

    [Theory]
    [InlineData(true, "方案在用", "StatusGreenBrush")]
    [InlineData(false, "未使用", "StatusOrangeBrush")]
    public void RepoPackageRow_MapsReferencedStateToTextAndToken(
        bool isReferenced, string expectedText, string expectedKey)
    {
        Assert.Equal(expectedText, RepoPackageRow.StatusTextFor(isReferenced));
        Assert.Equal(expectedKey, RepoPackageRow.StatusBrushKeyFor(isReferenced));
    }

    [Fact]
    public void RepoPackageRow_Create_CarriesDisplayNameAndResolvesBrush()
    {
        var package = new Package { PackageKey = "k", DisplayName = "我的 MOD" };
        var probe = new List<string>();

        var row = RepoPackageRow.Create(package, isReferenced: true, key =>
        {
            probe.Add(key);
            return Brushes.Red;
        });

        Assert.Equal("我的 MOD", row.DisplayName);
        Assert.Equal("方案在用", row.StatusText);
        Assert.Same(Brushes.Red, row.StatusBrush);
        Assert.Equal(new[] { "StatusGreenBrush" }, probe);
    }

    // ─── 部署记录 ───

    [Theory]
    [InlineData(DeploymentStatus.Committed, "StatusGreenBrush")]
    [InlineData(DeploymentStatus.Failed, "StatusRedBrush")]
    [InlineData(DeploymentStatus.RolledBack, "StatusOrangeBrush")]
    [InlineData(DeploymentStatus.InProgress, "Text500Brush")]
    [InlineData(DeploymentStatus.Pending, "Text500Brush")]
    [InlineData(DeploymentStatus.PartiallyRolledBack, "Text500Brush")]
    [InlineData(DeploymentStatus.Dismissed, "Text500Brush")]
    public void DeployHistoryRow_MapsStatusToToken(DeploymentStatus status, string expectedKey)
        => Assert.Equal(expectedKey, DeployHistoryRow.StatusBrushKeyFor(status));

    [Fact]
    public void DeployHistoryRow_FormatsTimeAndSummary()
    {
        var tx = new DeploymentTransaction
        {
            CreatedAt = new DateTime(2026, 7, 27, 14, 5, 0),
            TotalOperations = 42,
            BackendType = DeploymentBackendType.Copy,
            Status = DeploymentStatus.Committed
        };

        var row = DeployHistoryRow.Create(tx, NoBrush);

        Assert.Equal("2026-07-27 14:05", row.TimeText);
        Assert.StartsWith("42 个操作 · ", row.SummaryText);
    }

    [Fact]
    public void DeployHistoryRow_CommittedTransaction_ShowsRollbackButton()
    {
        var tx = new DeploymentTransaction { Status = DeploymentStatus.Committed };

        // 前提校验：CanRollback 是行模型的输入，若其语义变了本断言应当先失败
        Assert.True(tx.CanRollback);
        Assert.Equal(Visibility.Visible, DeployHistoryRow.Create(tx, NoBrush).RollbackVisibility);
    }

    [Fact]
    public void DeployHistoryRow_NonRollbackable_CollapsesButton()
    {
        // 折叠而不是不生成：DataTemplate 是固定结构，Collapsed 不占位，
        // 与改造前"不 Add 这个按钮"的布局结果一致
        var tx = new DeploymentTransaction { Status = DeploymentStatus.RolledBack };

        Assert.False(tx.CanRollback);
        Assert.Equal(Visibility.Collapsed, DeployHistoryRow.Create(tx, NoBrush).RollbackVisibility);
    }

    [Fact]
    public void DeployHistoryRow_KeepsTransactionForButtonTag()
    {
        var tx = new DeploymentTransaction { Status = DeploymentStatus.Committed };

        // 回滚按钮的 Click 处理器靠 Tag 取回事务对象，必须是同一个实例
        Assert.Same(tx, DeployHistoryRow.Create(tx, NoBrush).Transaction);
    }

    // ─── 生成文件 ───

    [Theory]
    [InlineData(GeneratedArtifactStatus.Stale, "可清理", "StatusOrangeBrush")]
    [InlineData(GeneratedArtifactStatus.Active, "使用中", "StatusGreenBrush")]
    public void GenFileRow_MapsStatusToTextAndToken(
        GeneratedArtifactStatus status, string expectedText, string expectedKey)
    {
        Assert.Equal(expectedText, GenFileRow.StatusTextFor(status));
        Assert.Equal(expectedKey, GenFileRow.StatusBrushKeyFor(status));
    }

    [Fact]
    public void GenFileRow_Create_CarriesIdForButtonTags()
    {
        var artifact = new GeneratedArtifact
        {
            DisplayName = "backup.pak",
            Type = GeneratedArtifactType.DeploymentSnapshot,
            Status = GeneratedArtifactStatus.Active
        };

        var row = GenFileRow.Create(artifact, NoBrush);

        // 两个按钮的 Click 处理器都用 Tag 里的 Guid 定位生成物
        Assert.Equal(artifact.Id, row.ArtifactId);
        Assert.Equal("backup.pak", row.DisplayName);
        Assert.Contains("DeploymentSnapshot", row.SummaryText);
    }

    [Fact]
    public void GenFileRow_SummaryUsesMiddleDotSeparator()
    {
        var artifact = new GeneratedArtifact { DisplayName = "x", Type = GeneratedArtifactType.Other };

        // 分隔符是 U+00B7，改造前源码里写成 · 转义，容易在重构中被换成普通句点
        Assert.Contains(" · ", GenFileRow.SummaryTextFor(artifact));
    }
}
