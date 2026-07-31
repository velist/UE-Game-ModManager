using System;

namespace UEModManager.Services.Paths;

/// <summary>搬移日记里记的阶段。</summary>
public enum RepositoryRelocationPhase
{
    /// <summary>没有搬移在进行（也没有中断的残留）。</summary>
    Idle = 0,

    /// <summary>
    /// 已经开始往新位置写，但还没落定。
    /// 落定的唯一证据是<b>旧位置的墓碑</b>，不是这个阶段值——阶段只用来记住"哪两个路径"。
    /// </summary>
    Moving = 1,

    /// <summary>
    /// 已落定（墓碑已写、存放位置已改成新的），只剩清空旧位置。
    /// 此后任何中断都只是"旧盘上多留了一份垃圾"，不再有丢数据的可能。
    /// </summary>
    Cleanup = 2,
}

/// <summary>
/// 一次搬移的日记。
///
/// <para><b>为什么光有墓碑不够</b></para>
/// <c>DataRelocationExecutor</c> 那套搬移里，墓碑 + 进行中标记足以自愈，
/// 因为<b>旧位置与新位置都是写死的</b>（<c>{安装目录}\Data</c> → <c>%LOCALAPPDATA%\Data</c>），
/// 下次启动照着常量重新探测一遍就知道该做什么。仓库搬移不是这样：新位置由用户临时挑，
/// 只存在于这一次操作里。启动时只凭"当前仓库根"这一个值，两个方向都问不出答案——
/// <list type="bullet">
/// <item>指针还指着旧位置、旧位置有墓碑 ⇒ 需要知道<b>该改到哪</b>（只写在墓碑正文里，
/// 而正文是给人看的、可被用户编辑、措辞随时会被润色，拿它当机器判据必然出事）；</item>
/// <item>指针已经指着新位置、旧位置还剩一堆没删完的东西 ⇒ 需要知道<b>旧位置在哪</b>，
/// 而那个路径此刻已经不在任何配置项里了。</item>
/// </list>
/// 所以把这一对路径连同阶段一起记在偏好里。它和仓库位置本身在同一个文件里，
/// 于是"改指针"和"记进度"能在<b>同一次原子写</b>里完成，不存在两者对不上的中间态。
/// </summary>
/// <param name="Phase">阶段。</param>
/// <param name="SourceRoot">从哪搬（搬移前的仓库根）。</param>
/// <param name="TargetRoot">搬到哪。</param>
public sealed record RepositoryRelocationJournal(
    RepositoryRelocationPhase Phase,
    string? SourceRoot,
    string? TargetRoot)
{
    /// <summary>没有任何在途搬移。</summary>
    public static readonly RepositoryRelocationJournal None =
        new(RepositoryRelocationPhase.Idle, null, null);

    /// <summary>日记内容是否完整可用（阶段非 Idle 且两个路径都在）。</summary>
    public bool IsActionable
        => Phase != RepositoryRelocationPhase.Idle
            && !string.IsNullOrWhiteSpace(SourceRoot)
            && !string.IsNullOrWhiteSpace(TargetRoot);

    public override string ToString()
        => Phase == RepositoryRelocationPhase.Idle
            ? "Idle"
            : $"{Phase}: {SourceRoot} → {TargetRoot}";
}

/// <summary>下次启动时该替用户收拾什么。</summary>
public enum RepositoryRelocationRecoveryAction
{
    /// <summary>什么都不用做。</summary>
    None,

    /// <summary>
    /// 回退：上次<b>没有</b>搬完（旧位置没有墓碑）。清掉新位置的半份残留，
    /// 存放位置继续指着旧位置。旧位置的数据自始至终一个字节都没被动过。
    /// </summary>
    RollBack,

    /// <summary>
    /// 推进：上次<b>已经搬完并校验通过</b>（旧位置有墓碑），只是还没来得及改存放位置。
    /// 把存放位置改成新位置，再清空旧位置。
    /// </summary>
    RollForward,

    /// <summary>
    /// 只剩清尾：存放位置已经是新的了，旧位置还剩没删完的东西。删完即可。
    /// </summary>
    ResumeCleanup,

    /// <summary>
    /// 日记有内容但已经对不上现场（路径不存在、没有任何残留），只把日记划掉。
    /// 单独一种动作而不是并进 <see cref="None"/>：日记不清掉，每次启动都会再判一遍，
    /// 而日志里那条"发现在途搬移"会一直误导排障的人。
    /// </summary>
    ClearJournalOnly,
}

/// <summary>
/// 启动时对现场的观测。IO 由主项目做完再填进来。
/// </summary>
/// <param name="Journal">读出来的日记。</param>
/// <param name="CurrentRoot">此刻配置里生效的仓库根。</param>
/// <param name="LegacyTombstoneExists">旧位置有<b>搬移墓碑</b>（<c>migrated-to.txt</c>）。</param>
/// <param name="LegacyHasContent">旧位置除墓碑之外还有真数据。</param>
/// <param name="TargetInProgressMarkerExists">新位置有<b>搬迁进行中标记</b>。</param>
/// <param name="TargetHasContent">新位置有内容（含半份残留）。</param>
public readonly record struct RepositoryRelocationRecoveryProbe(
    RepositoryRelocationJournal Journal,
    string? CurrentRoot,
    bool LegacyTombstoneExists,
    bool LegacyHasContent,
    bool TargetInProgressMarkerExists,
    bool TargetHasContent);

/// <summary>一次恢复判定。</summary>
/// <param name="Action">要做什么。</param>
/// <param name="SourceRoot">旧位置。</param>
/// <param name="TargetRoot">新位置。</param>
/// <param name="Reason">理由，进日志——"上次断电之后到底发生了什么"必须能查。</param>
public sealed record RepositoryRelocationRecoveryPlan(
    RepositoryRelocationRecoveryAction Action,
    string? SourceRoot,
    string? TargetRoot,
    string Reason)
{
    public override string ToString()
        => Action == RepositoryRelocationRecoveryAction.None
            ? $"None｜{Reason}"
            : $"{Action}: {SourceRoot} → {TargetRoot}｜{Reason}";
}

/// <summary>
/// 搬移被中断（断电、强杀、崩溃）之后，下次启动该怎么收拾（纯函数，不碰 IO）。
///
/// <para><b>判据只有一条：旧位置有没有墓碑</b></para>
/// 与 <c>DataRelocationExecutor</c> 完全同源。墓碑代表"复制完成且校验通过"，
/// 是"数据确实已经在新位置"的唯一证据。有墓碑就往前推，没墓碑就往回退。
/// <b>刻意不看日记里的阶段值来决定推还是退</b>：阶段是一次单独的偏好写入，
/// 它和墓碑之间必然存在一个谁先谁后的窗口，拿它当判据就等于给自己造一个
/// "阶段说没搬完、其实已经搬完了"的时刻。阶段只负责记住那一对路径。
///
/// <para><b>搬移执行时的落地顺序，就是这张表的输入</b></para>
/// <list type="number">
/// <item>写日记 <see cref="RepositoryRelocationPhase.Moving"/>（指针不动）</item>
/// <item>新位置立进行中标记 → 复制 → 校验</item>
/// <item>旧位置写墓碑 ← <b>落定点</b></item>
/// <item>清掉进行中标记</item>
/// <item><b>一次原子写</b>：指针改成新位置 + 日记改成 <see cref="RepositoryRelocationPhase.Cleanup"/></item>
/// <item>清空旧位置（保留墓碑）</item>
/// <item>划掉日记</item>
/// </list>
/// 在 1–2 之间断电 ⇒ 无墓碑 ⇒ <see cref="RepositoryRelocationRecoveryAction.RollBack"/>；
/// 3–5 之间 ⇒ 有墓碑但指针还是旧的 ⇒ <see cref="RepositoryRelocationRecoveryAction.RollForward"/>；
/// 6 中间 ⇒ 指针已是新的 ⇒ <see cref="RepositoryRelocationRecoveryAction.ResumeCleanup"/>。
/// 每一格都对得上，没有"两边都不完整"的格子——这正是那个顺序不可换的原因。
/// </summary>
public static class RepositoryRelocationRecoveryPlanner
{
    /// <summary>判定。</summary>
    public static RepositoryRelocationRecoveryPlan Plan(RepositoryRelocationRecoveryProbe probe)
    {
        var journal = probe.Journal ?? RepositoryRelocationJournal.None;

        if (!journal.IsActionable)
        {
            return new RepositoryRelocationRecoveryPlan(
                RepositoryRelocationRecoveryAction.None, journal.SourceRoot, journal.TargetRoot,
                journal.Phase == RepositoryRelocationPhase.Idle
                    ? "没有在途搬移"
                    : "日记残缺（缺路径），无法据此恢复，按没有在途搬移处理");
        }

        var source = journal.SourceRoot!;
        var target = journal.TargetRoot!;
        var pointerAtTarget = VolumePaths.IsSameOrInside(target, probe.CurrentRoot)
            && VolumePaths.IsSameOrInside(probe.CurrentRoot, target);

        // 指针已经在新位置：落定早就发生过了，剩下的只可能是没删完的旧位置。
        // 这一格必须最先判——它与下面"有墓碑 ⇒ 推进"的区别只在于指针改没改成,
        // 而推进那一步会去写指针，对已经写好的指针再写一次是无害但多余的。
        if (pointerAtTarget)
        {
            return probe.LegacyHasContent
                ? new RepositoryRelocationRecoveryPlan(
                    RepositoryRelocationRecoveryAction.ResumeCleanup, source, target,
                    "存放位置已经是新位置，旧位置还有没删完的东西，继续清理")
                : new RepositoryRelocationRecoveryPlan(
                    RepositoryRelocationRecoveryAction.ClearJournalOnly, source, target,
                    "存放位置已经是新位置且旧位置已空，上次其实已经做完了，只划掉日记");
        }

        // 有墓碑 ⇒ 新位置那份已经复制完并校验过，往前推。
        if (probe.LegacyTombstoneExists)
        {
            return new RepositoryRelocationRecoveryPlan(
                RepositoryRelocationRecoveryAction.RollForward, source, target,
                "旧位置已有墓碑（复制与校验都完成过），把存放位置改到新位置并清理旧位置");
        }

        // 没墓碑 ⇒ 上次没搬完。新位置那堆是半份残留，清掉；旧位置一个字节都没动过。
        if (probe.TargetInProgressMarkerExists || probe.TargetHasContent)
        {
            return new RepositoryRelocationRecoveryPlan(
                RepositoryRelocationRecoveryAction.RollBack, source, target,
                probe.TargetInProgressMarkerExists
                    ? "旧位置没有墓碑、新位置带着进行中标记，是上次中断的半份数据，清掉重来"
                    : "旧位置没有墓碑、新位置有内容但没有进行中标记，"
                        + "按上次中断处理但只清我们能确认的部分");
        }

        return new RepositoryRelocationRecoveryPlan(
            RepositoryRelocationRecoveryAction.ClearJournalOnly, source, target,
            "旧位置没有墓碑、新位置也没有残留，上次搬移还没写出任何东西，只划掉日记");
    }

    /// <summary>
    /// <see cref="RepositoryRelocationRecoveryAction.RollBack"/> 时，允不允许真的去删新位置。
    ///
    /// <para>
    /// <b>只清带着进行中标记的那种。</b>与 <c>DataRelocationExecutor.PurgeTarget</c> 同一条判据、
    /// 同一个理由：没有标记却有内容，说明那个位置是被别人正常使用的
    /// （用户可能上次搬移失败后自己在设置里把仓库指到了那儿，并且已经往里导过 MOD），
    /// 此时清空等于把用户的数据删光。判不准就宁可留一堆垃圾。
    /// </para>
    /// </summary>
    public static bool MayPurgeTarget(RepositoryRelocationRecoveryProbe probe)
        => probe.TargetInProgressMarkerExists;
}
