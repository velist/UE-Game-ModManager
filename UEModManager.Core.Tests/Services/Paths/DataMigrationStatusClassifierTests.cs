using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

public class DataMigrationStatusClassifierTests
{
    // ─── 四种状态的判定 ───

    [Fact]
    public void NoWorkAtAll_IsCompleted()
    {
        // 全新安装：一步都不用做，同样算彻底完成
        Assert.Equal(DataMigrationStatus.Completed,
            DataMigrationStatusClassifier.Classify(executed: 0, deferred: 0, failed: 0));
    }

    [Fact]
    public void AllExecuted_IsCompleted()
    {
        Assert.Equal(DataMigrationStatus.Completed,
            DataMigrationStatusClassifier.Classify(executed: 5, deferred: 0, failed: 0));
    }

    [Fact]
    public void DeferredOnly_IsDeferred()
    {
        // 搬移开关关闭时每台老用户机器的常态
        Assert.Equal(DataMigrationStatus.Deferred,
            DataMigrationStatusClassifier.Classify(executed: 0, deferred: 4, failed: 0));
    }

    [Fact]
    public void RegisterInPlaceExecutedPlusDeferred_IsDeferred()
    {
        // 开关关着时的真实组合：原地登记执行了，搬移类全部推迟
        Assert.Equal(DataMigrationStatus.Deferred,
            DataMigrationStatusClassifier.Classify(executed: 2, deferred: 4, failed: 0));
    }

    [Fact]
    public void SomeExecutedSomeFailed_IsPartiallyFailed()
    {
        Assert.Equal(DataMigrationStatus.PartiallyFailed,
            DataMigrationStatusClassifier.Classify(executed: 3, deferred: 0, failed: 1));
    }

    [Fact]
    public void NothingExecutedAndFailed_IsFailed()
    {
        Assert.Equal(DataMigrationStatus.Failed,
            DataMigrationStatusClassifier.Classify(executed: 0, deferred: 0, failed: 2));
    }

    [Fact]
    public void FailureBeatsDeferral()
    {
        // 失败与推迟同时出现时，值得报出去的是失败那一半
        Assert.Equal(DataMigrationStatus.Failed,
            DataMigrationStatusClassifier.Classify(executed: 0, deferred: 3, failed: 1));
        Assert.Equal(DataMigrationStatus.PartiallyFailed,
            DataMigrationStatusClassifier.Classify(executed: 1, deferred: 3, failed: 1));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void NegativeCounts_Throw(int executed, int deferred, int failed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DataMigrationStatusClassifier.Classify(executed, deferred, failed));
    }

    // ─── 该不该提示用户 ───

    [Fact]
    public void CompletedAndDeferred_DoNotNotify()
    {
        // 这一条是本类存在的全部理由：推迟不是异常，照 "completed == false" 提示的话，
        // 搬移开关关着的当下，全体老用户每次启动都会收到一条"整理未完成"。
        Assert.False(DataMigrationStatusClassifier.ShouldNotifyUser(DataMigrationStatus.Completed));
        Assert.False(DataMigrationStatusClassifier.ShouldNotifyUser(DataMigrationStatus.Deferred));
    }

    [Fact]
    public void FailureStates_Notify()
    {
        Assert.True(DataMigrationStatusClassifier.ShouldNotifyUser(DataMigrationStatus.PartiallyFailed));
        Assert.True(DataMigrationStatusClassifier.ShouldNotifyUser(DataMigrationStatus.Failed));
    }

    // ─── 文案 ───

    [Fact]
    public void NonFailureStates_HaveNoMessage()
    {
        Assert.Null(DataMigrationStatusClassifier.BuildUserMessage(DataMigrationStatus.Completed));
        Assert.Null(DataMigrationStatusClassifier.BuildUserMessage(DataMigrationStatus.Deferred));
    }

    [Fact]
    public void FailureStates_HaveDistinctMessages()
    {
        var failed = DataMigrationStatusClassifier.BuildUserMessage(DataMigrationStatus.Failed);
        var partial = DataMigrationStatusClassifier.BuildUserMessage(DataMigrationStatus.PartiallyFailed);

        Assert.False(string.IsNullOrWhiteSpace(failed));
        Assert.False(string.IsNullOrWhiteSpace(partial));
        Assert.NotEqual(failed, partial);
    }

    [Fact]
    public void Messages_TellUserNothingIsLost()
    {
        // 失败不阻断启动、数据也没丢，文案必须说清"仍在旧位置"，
        // 否则换来的是一批"我的数据是不是没了"的求助。
        Assert.Contains("旧位置", DataMigrationStatusClassifier.FailedMessage);
        Assert.Contains("旧位置", DataMigrationStatusClassifier.PartiallyFailedMessage);
        Assert.Contains("日志", DataMigrationStatusClassifier.FailedMessage);
        Assert.Contains("日志", DataMigrationStatusClassifier.PartiallyFailedMessage);
    }

    [Fact]
    public void EveryStatus_IsClassifiable()
    {
        // 新增状态时这条会先红：漏掉 ShouldNotifyUser / BuildUserMessage 的分支
        // 会让新状态默默按"不提示"处理。
        foreach (DataMigrationStatus status in Enum.GetValues<DataMigrationStatus>())
        {
            var notify = DataMigrationStatusClassifier.ShouldNotifyUser(status);
            var message = DataMigrationStatusClassifier.BuildUserMessage(status);
            Assert.Equal(notify, message is not null);
        }
    }
}
