using UEModManager.Models;
using UEModManager.Services.Repository;

namespace UEModManager.Core.Tests.Services.Repository;

public class PackageDeletionPlannerTests
{
    private static PackageReferenceReport Report(int total, int enabled)
    {
        var profileIds = Enumerable.Range(0, total).Select(_ => Guid.NewGuid()).ToList();
        var enabledIds = profileIds.Take(enabled).ToList();
        return new PackageReferenceReport(
            PackageKey: "MyMod",
            ProfileReferenceCount: total,
            EnabledReferenceCount: enabled,
            ReferencingProfileIds: profileIds,
            EnabledInProfileIds: enabledIds);
    }

    [Fact]
    public void Plan_NoReferences_ReturnsSafeToDelete()
    {
        var plan = PackageDeletionPlanner.Plan(Report(total: 0, enabled: 0));

        Assert.Equal(PackageDeletionDecision.SafeToDelete, plan.Decision);
        Assert.False(plan.RequiresUserConfirmation);
        Assert.False(plan.RequiresRollback);
        Assert.Equal("MyMod", plan.PackageKey);
    }

    [Fact]
    public void Plan_ReferencedAllDisabled_ReturnsReferencedButDisabled()
    {
        var plan = PackageDeletionPlanner.Plan(Report(total: 3, enabled: 0));

        Assert.Equal(PackageDeletionDecision.ReferencedButDisabled, plan.Decision);
        Assert.True(plan.RequiresUserConfirmation);
        Assert.False(plan.RequiresRollback); // 都已禁用 → 文件不在游戏目录 → 无需回滚
        Assert.Contains("3", plan.Explanation);
    }

    [Fact]
    public void Plan_ReferencedSomeEnabled_ReturnsActivelyDeployed()
    {
        var plan = PackageDeletionPlanner.Plan(Report(total: 3, enabled: 1));

        Assert.Equal(PackageDeletionDecision.ActivelyDeployed, plan.Decision);
        Assert.True(plan.RequiresUserConfirmation);
        Assert.True(plan.RequiresRollback); // 至少一个 Profile 启用 → 必须回滚
    }

    [Fact]
    public void Plan_ReferencedAllEnabled_ReturnsActivelyDeployed()
    {
        var plan = PackageDeletionPlanner.Plan(Report(total: 5, enabled: 5));

        Assert.Equal(PackageDeletionDecision.ActivelyDeployed, plan.Decision);
        Assert.True(plan.RequiresRollback);
        Assert.Contains("5", plan.Explanation);
    }

    [Fact]
    public void Plan_NullReport_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            PackageDeletionPlanner.Plan(null!));

    [Fact]
    public void Plan_PreservesReferenceReport()
    {
        var report = Report(total: 2, enabled: 1);
        var plan = PackageDeletionPlanner.Plan(report);

        Assert.Same(report, plan.Reference);
    }

    [Fact]
    public void Plan_SafeToDelete_HasNoConfirmationOrRollback()
    {
        // 严格保证：SafeToDelete 是唯一不需要用户确认的状态
        var plan = PackageDeletionPlanner.Plan(Report(total: 0, enabled: 0));

        Assert.False(plan.RequiresUserConfirmation);
        Assert.False(plan.RequiresRollback);
    }

    [Fact]
    public void Plan_ActivelyDeployed_AlwaysRequiresRollback()
    {
        // 严格保证：只要 IsEnabledAnywhere=true，RequiresRollback 必为 true
        for (int enabled = 1; enabled <= 5; enabled++)
        {
            var plan = PackageDeletionPlanner.Plan(Report(total: 5, enabled: enabled));
            Assert.True(plan.RequiresRollback,
                $"enabled={enabled} 时 RequiresRollback 应为 true");
        }
    }
}
