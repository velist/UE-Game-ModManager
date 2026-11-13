using UEModManager.Models;
using UEModManager.Services.Deployment;

namespace UEModManager.Core.Tests.Services.Deployment;

public class DeploymentBackupRetentionPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private const long Mb = 1024 * 1024;

    private static readonly DeploymentBackupRetentionPolicy Policy = new(
        MaxCount: 3,
        MaxAge: TimeSpan.FromDays(3),
        MaxTotalBytes: 100 * Mb,
        OrphanGrace: TimeSpan.FromDays(1));

    private static DeploymentBackupEntry Entry(
        string name, double hoursAgo, long sizeMb = 1,
        DeploymentStatus? status = DeploymentStatus.Committed, bool durable = true)
        => new(name, status, Now.AddHours(-hoursAgo), sizeMb * Mb, durable);

    private static IReadOnlyList<string> Plan(params DeploymentBackupEntry[] entries)
        => DeploymentBackupRetentionPlanner.PlanDeletions(entries, Now, Policy);

    [Fact]
    public void RecentSmallBackups_AreAllKept()
    {
        Assert.Empty(Plan(Entry("a", 1), Entry("b", 2), Entry("c", 3)));
    }

    [Fact]
    public void OverCount_DeletesOldestFirst()
    {
        var deletions = Plan(Entry("new", 1), Entry("mid", 2), Entry("old", 3), Entry("oldest", 4));

        Assert.Equal(["oldest"], deletions);
    }

    [Fact]
    public void OlderThanMaxAge_IsDeleted_EvenWhenItIsTheOnlyOne()
    {
        // 「定时清理」的核心：不再部署也要清。此前只在下一次部署提交后按次数清，这一条永远不会被删。
        Assert.Equal(["stale"], Plan(Entry("stale", hoursAgo: 24 * 4)));
    }

    [Fact]
    public void OverBudget_DeletesOlderOnes_ButNeverTheNewest()
    {
        var deletions = Plan(
            Entry("newest-huge", 1, sizeMb: 500),   // 自己就超预算，但它是「撤销上一次部署」要用的
            Entry("older-small", 2, sizeMb: 1));

        Assert.Equal(["older-small"], deletions);
    }

    [Fact]
    public void Budget_IsCumulativeNewestFirst()
    {
        var deletions = Plan(
            Entry("a", 1, sizeMb: 60),
            Entry("b", 2, sizeMb: 30),
            Entry("c", 3, sizeMb: 30));   // 60 + 30 + 30 > 100

        Assert.Equal(["c"], deletions);
    }

    [Theory]
    [InlineData(DeploymentStatus.Pending)]
    [InlineData(DeploymentStatus.InProgress)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.PartiallyRolledBack)]
    [InlineData(DeploymentStatus.LogPersistenceFailed)]
    public void UnfinishedTransactions_AreNeverDeleted(DeploymentStatus status)
    {
        // 这些还可能要回滚或人工核查——崩溃恢复靠的就是它们，再旧再大也不能清。
        Assert.Empty(Plan(Entry("needs-recovery", hoursAgo: 24 * 365, sizeMb: 10_000, status: status)));
    }

    [Theory]
    [InlineData(DeploymentStatus.Committed)]
    [InlineData(DeploymentStatus.RolledBack)]
    [InlineData(DeploymentStatus.Dismissed)]
    public void FinishedTransactions_AreEligible(DeploymentStatus status)
    {
        Assert.Equal(["done"], Plan(Entry("done", hoursAgo: 24 * 10, status: status)));
    }

    [Fact]
    public void UnfinishedTransactions_DoNotConsumeCountOrBudget()
    {
        var deletions = Plan(
            Entry("crashed", 0.5, sizeMb: 10_000, status: DeploymentStatus.InProgress),
            Entry("a", 1), Entry("b", 2), Entry("c", 3));

        Assert.Empty(deletions);
    }

    [Fact]
    public void LegacyTransactionsWithoutState_OnlyFollowCountRule()
    {
        // 2.1.0 之前的事务：升级用户第一次部署时要靠它们认领旧文件，
        // 被替换掉的游戏原始文件也只在这些备份里。不能因为「旧」或「大」提前清掉。
        var deletions = Plan(
            Entry("legacy-old-huge", hoursAgo: 24 * 60, sizeMb: 5_000, durable: false));

        Assert.Empty(deletions);
    }

    [Fact]
    public void LegacyTransactions_StillRespectCount()
    {
        var deletions = Plan(
            Entry("l1", 24 * 10, durable: false), Entry("l2", 24 * 11, durable: false),
            Entry("l3", 24 * 12, durable: false), Entry("l4", 24 * 13, durable: false));

        Assert.Equal(["l4"], deletions);
    }

    [Fact]
    public void OrphanWithoutLog_IsDeletedOnlyAfterGrace()
    {
        var deletions = Plan(
            Entry("just-created", hoursAgo: 0.1, status: null),   // 可能是正在创建中的事务目录
            Entry("abandoned", hoursAgo: 48, status: null));

        Assert.Equal(["abandoned"], deletions);
    }

    [Fact]
    public void DefaultPolicy_IsBounded()
    {
        var policy = DeploymentBackupRetentionPolicy.Default;

        // 守住量级：谁把默认值改回「不限时间 / 不限大小」，系统盘被占满的问题就回来了。
        Assert.InRange(policy.MaxAge, TimeSpan.FromHours(1), TimeSpan.FromDays(7));
        Assert.InRange(policy.MaxTotalBytes, 1, 5L * 1024 * Mb);
        Assert.InRange(policy.MaxCount, 1, 10);
    }
}
