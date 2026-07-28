using System;

namespace UEModManager.Services.Paths;

/// <summary>
/// 一次数据搬迁的整体状态。
///
/// <para>
/// 之所以要把"三个计数"收敛成一个枚举：判断"该不该提示用户"时，唯一危险的写法是
/// <c>if (!completed) 提示</c>。<see cref="Deferred"/> 也会让 completed 为 false，
/// 而它恰恰是<b>一切正常</b>的状态——搬移开关没打开时，每台老用户机器每次启动都会
/// 产生一批推迟项。按 completed 提示的话，全体用户会天天看到一条"整理未完成"的告警，
/// 说的却是一件根本还没开始做的事。
/// </para>
/// </summary>
public enum DataMigrationStatus
{
    /// <summary>
    /// 彻底完成：没有失败项，也没有推迟项。可能一步都没做（全新安装），也算完成。
    /// </summary>
    Completed,

    /// <summary>
    /// 有推迟项但无失败项。<b>不是异常</b>：搬移开关关闭时的正常状态，
    /// 数据完整留在旧位置，应用照常读写。不得提示用户。
    /// </summary>
    Deferred,

    /// <summary>
    /// 部分失败：有项目搬成了，也有项目失败了。数据此刻分处新旧两地——
    /// 失败项仍在旧位置且完整可用，但下次启动会重试，用户应当知情。
    /// </summary>
    PartiallyFailed,

    /// <summary>
    /// 完全失败：一项都没搬成。应用整体沿用旧位置继续运行，功能不受影响。
    /// </summary>
    Failed,
}

/// <summary>
/// 把搬迁计数判成 <see cref="DataMigrationStatus"/>，并给出对应的用户可见文案。
/// 纯函数，不碰 IO——UI 侧只需要"要不要提示、提示什么"，不该自己去数三个整数。
/// </summary>
public static class DataMigrationStatusClassifier
{
    /// <summary>完全失败时的提示语。</summary>
    public const string FailedMessage =
        "数据目录整理未完成，软件仍在使用旧位置，详情见日志。";

    /// <summary>部分失败时的提示语。</summary>
    public const string PartiallyFailedMessage =
        "数据目录整理部分未完成，部分数据仍在旧位置，功能不受影响，详情见日志。";

    /// <summary>
    /// 判定状态。
    ///
    /// <para>
    /// 优先级是<b>失败 &gt; 推迟 &gt; 完成</b>：一次运行里同时出现失败与推迟时，
    /// 值得报出去的是失败那一半；推迟项本来就打算留到下次，说它只会稀释信号。
    /// </para>
    /// </summary>
    /// <param name="executed">成功执行的项数。</param>
    /// <param name="deferred">因开关关闭而推迟的项数。</param>
    /// <param name="failed">失败的项数。</param>
    public static DataMigrationStatus Classify(int executed, int deferred, int failed)
    {
        if (executed < 0) throw new ArgumentOutOfRangeException(nameof(executed));
        if (deferred < 0) throw new ArgumentOutOfRangeException(nameof(deferred));
        if (failed < 0) throw new ArgumentOutOfRangeException(nameof(failed));

        if (failed > 0)
        {
            // 分开报的意义在排障：全失败通常是一个共因（目标盘不可写、权限、空间），
            // 部分失败则是单项问题（某个文件被占用），两者的下一步动作完全不同。
            return executed > 0 ? DataMigrationStatus.PartiallyFailed : DataMigrationStatus.Failed;
        }

        return deferred > 0 ? DataMigrationStatus.Deferred : DataMigrationStatus.Completed;
    }

    /// <summary>
    /// 是否该给用户一条非模态提示。
    /// 只有失败类才提示：完成没什么可说的，推迟是设计中的正常状态。
    /// </summary>
    public static bool ShouldNotifyUser(DataMigrationStatus status)
        => status is DataMigrationStatus.PartiallyFailed or DataMigrationStatus.Failed;

    /// <summary>
    /// 用户可见文案；不需要提示时返回 <c>null</c>。
    ///
    /// <para>
    /// 两条文案都以"功能不受影响 / 仍在使用旧位置"开路，是刻意的：搬迁失败不阻断启动，
    /// 用户此刻什么都没丢，唯一需要他做的是在真出问题时把日志交出来。写成惊悚的口径
    /// 只会换来一批"我的数据是不是没了"的求助。
    /// </para>
    /// </summary>
    public static string? BuildUserMessage(DataMigrationStatus status) => status switch
    {
        DataMigrationStatus.Failed => FailedMessage,
        DataMigrationStatus.PartiallyFailed => PartiallyFailedMessage,
        _ => null,
    };
}
