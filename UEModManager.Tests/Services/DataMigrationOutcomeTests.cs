using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Tests.Services;

/// <summary>
/// <see cref="DataMigrationOutcome"/> 的派生属性测试。
///
/// <para>
/// 判定规则本身在 Core 有单测，这里只钉住"记录的三个计数确实按 Executed/Deferred/Failed
/// 的顺序喂给了分类器"——参数顺序写错的话编译照过，而结果会在开关翻开的那天
/// 变成"该提示的不提示"。
/// </para>
/// </summary>
public sealed class DataMigrationOutcomeTests
{
    private static DataMigrationOutcome Outcome(int executed, int skipped, int deferred, int failed)
        => new(failed == 0 && deferred == 0, executed, skipped, deferred, failed, "测试");

    [Fact]
    public void DeferredOnly_DoesNotNotify()
    {
        // 搬移开关关闭时的真实形态：原地登记执行了，搬移类全部推迟。
        // Completed 为 false，但一点问题都没有。
        var outcome = Outcome(executed: 2, skipped: 1, deferred: 3, failed: 0);

        Assert.False(outcome.Completed);
        Assert.Equal(DataMigrationStatus.Deferred, outcome.Status);
        Assert.False(outcome.ShouldNotifyUser);
        Assert.Null(outcome.UserMessage);
    }

    [Fact]
    public void FullyCompleted_DoesNotNotify()
    {
        var outcome = Outcome(executed: 6, skipped: 0, deferred: 0, failed: 0);

        Assert.True(outcome.Completed);
        Assert.Equal(DataMigrationStatus.Completed, outcome.Status);
        Assert.False(outcome.ShouldNotifyUser);
    }

    [Fact]
    public void PartialFailure_Notifies()
    {
        var outcome = Outcome(executed: 4, skipped: 0, deferred: 0, failed: 1);

        Assert.Equal(DataMigrationStatus.PartiallyFailed, outcome.Status);
        Assert.True(outcome.ShouldNotifyUser);
        Assert.Equal(DataMigrationStatusClassifier.PartiallyFailedMessage, outcome.UserMessage);
    }

    [Fact]
    public void TotalFailure_Notifies()
    {
        // RunAsync 捕获到异常时返回的正是这个形态
        var outcome = new DataMigrationOutcome(false, 0, 0, 0, 1, "迁移异常，已沿用旧位置");

        Assert.Equal(DataMigrationStatus.Failed, outcome.Status);
        Assert.True(outcome.ShouldNotifyUser);
        Assert.Equal(DataMigrationStatusClassifier.FailedMessage, outcome.UserMessage);
    }

    [Fact]
    public void SkippedCount_DoesNotAffectStatus()
    {
        // 跳过项（源不存在 / 用户自定义过位置）与"有没有出事"无关，
        // 混进判定会让全新安装看起来像出了问题。
        Assert.Equal(
            Outcome(executed: 0, skipped: 0, deferred: 0, failed: 0).Status,
            Outcome(executed: 0, skipped: 99, deferred: 0, failed: 0).Status);
    }
}
