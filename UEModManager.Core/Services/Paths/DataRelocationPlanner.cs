using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace UEModManager.Services.Paths;

/// <summary>一项数据的处置方式。</summary>
public enum RelocationKind
{
    /// <summary>
    /// 搬移。适用于体积恒定在 MB 级的数据（JSON 索引、配置、日志）。
    /// </summary>
    Relocate,

    /// <summary>
    /// 原地登记 —— 不搬移，只把当前绝对路径写进配置让应用继续从原处读。
    /// 适用于可能有几十 GB 的仓库类数据：它们已经不在安装目录里，卸载不会丢，
    /// 为一个理论上的位置改进去搬几十 GB，收益不抵中断/耗时/空间的风险。
    /// </summary>
    RegisterInPlace,
}

/// <summary>规划出的动作。</summary>
public enum RelocationAction
{
    /// <summary>无需动作。</summary>
    None,

    /// <summary>全新搬移：复制 → 校验 → 写墓碑 → 删源。</summary>
    Copy,

    /// <summary>
    /// 目标已有内容、没有墓碑，<b>但目标侧有"搬迁进行中"标记</b> —— 那堆东西是上一次
    /// 被打断的搬迁自己写下的，可能是半份数据。必须先清空目标再重新复制，
    /// 绝不能在残留上继续。
    ///
    /// <para>
    /// <b>与 <see cref="AdoptTargetKeepLegacy"/> 的分界线只有一条：进行中标记在不在。</b>
    /// 有标记 = 目标里的是"我上次写了一半的东西"，清掉是唯一正确做法；
    /// 无标记 = 目标里的是"应用正常使用写下的真实数据"，清掉就是毁数据。
    /// 两者在磁盘上的外观完全一样（都是"有内容 + 无墓碑"），除了这个标记再无别的区分手段，
    /// 所以判据必须精确到它，宽一格丢用户数据，严一格治不好断电。
    /// </para>
    /// </summary>
    PurgeTargetThenCopy,

    /// <summary>
    /// 目标已有实质内容、没有墓碑、<b>也没有"搬迁进行中"标记</b> —— 说明那是应用正常使用
    /// 新位置写下的真实数据，而不是上次中断的残留。此时<b>认新位置为准</b>：
    /// 不复制、不删源，只在旧位置留一个"已跳过"标记（<c>superseded-by.txt</c>），
    /// 把它标记为"已处置，今后跳过"。
    ///
    /// <para>
    /// <b>为什么新位置赢。</b>路径归口之后全部读写方都走新位置，而搬移开关一直关着，
    /// 于是每台老用户机器上"旧数据躺在安装目录、新数据一直往 <c>%LOCALAPPDATA%</c> 里写"
    /// 是<b>常态而非边角料</b>。新位置那份才是用户界面上看得见、这段时间一直在改的真实状态；
    /// 旧位置是升级前的历史，已经被忽略很久了。反过来让旧位置赢，就是迁移方案 §三 否决
    /// "保留旧位置只读"时点名的那类 bug ——"用户的方案/分类会凭空回退"，而且还带删除。
    /// </para>
    ///
    /// <para>
    /// <b>为什么旧位置一个字节都不删。</b>这是本策略"零数据丢失"的唯一支点：新位置赢是个
    /// 启发式判断（"有内容"不等于"更好"），万一判错，用户的旧数据仍原封不动躺在安装目录里，
    /// 可以人工找回。代价只是安装目录留一点残留——比删掉真数据便宜太多了。
    /// </para>
    /// </summary>
    AdoptTargetKeepLegacy,

    /// <summary>
    /// 已有墓碑 —— 复制与校验都完成过，只是删源没做完。续做删除即可，不重新复制。
    /// </summary>
    ResumeCleanup,

    /// <summary>把当前位置登记进配置。</summary>
    RegisterInPlace,
}

/// <summary>不动作的原因（仅当 Action 为 None 时有意义）。</summary>
public enum RelocationSkipReason
{
    /// <summary>不适用（有实际动作）。</summary>
    NotSkipped,

    /// <summary>本布局版本已迁移过。</summary>
    AlreadyMigrated,

    /// <summary>源不存在，没有东西要处理。</summary>
    NothingToRelocate,

    /// <summary>用户已显式指定过位置，一步都不能动。</summary>
    UserOverride,

    /// <summary>源与目标是同一个路径。</summary>
    SourceIsTarget,

    /// <summary>
    /// 此前已判定为"新位置赢"（旧位置留有已跳过标记），旧位置的历史副本原样保留，不再处置。
    /// 与 <see cref="NothingToRelocate"/> 分开，是因为两者对旧位置的期望完全相反：
    /// 前者旧位置<b>确实还有数据</b>且必须留着，后者是旧位置本就空了。
    /// 混用会让日志读起来像"这里没东西"，而实际上安装目录里躺着一整份历史数据。
    /// </summary>
    SupersededByTarget,
}

/// <summary>
/// 对一项数据的探测结果。由 IO 适配器填充，规划器只读不查盘。
/// </summary>
/// <param name="Name">用于日志与墓碑内容的可读名称。</param>
/// <param name="Kind">处置方式。</param>
/// <param name="LegacyPath">旧位置。</param>
/// <param name="TargetPath">新位置（<see cref="RelocationKind.RegisterInPlace"/> 时无意义）。</param>
/// <param name="LegacyExists">旧位置是否存在。</param>
/// <param name="TargetExists">新位置是否已存在内容。</param>
/// <param name="TombstoneExists">旧位置是否已有<b>搬移</b>墓碑（数据真的被搬走了）。</param>
/// <param name="SupersededMarkerExists">
/// 旧位置是否已有<b>已跳过</b>标记（此前判定为"新位置赢"，数据<b>没有</b>被搬走）。
/// 与 <paramref name="TombstoneExists"/> 是两个物理上不同的文件，绝不能合成一个布尔：
/// 合了之后第二次启动会把"跳过"误读成"搬完了只差删源"，转头去删旧位置——
/// 那正是本策略承诺永不发生的事。
/// </param>
/// <param name="TargetMigrationInProgress">
/// 目标侧是否有"搬迁进行中"标记。它是区分
/// <see cref="RelocationAction.PurgeTargetThenCopy"/>（目标里是我上次写了一半的残留）与
/// <see cref="RelocationAction.AdoptTargetKeepLegacy"/>（目标里是应用的真实数据）的唯一判据。
/// </param>
/// <param name="IsUserOverridden">用户是否已显式指定过该位置。</param>
public sealed record RelocationProbe(
    string Name,
    RelocationKind Kind,
    string LegacyPath,
    string TargetPath,
    bool LegacyExists,
    bool TargetExists,
    bool TombstoneExists,
    bool SupersededMarkerExists = false,
    bool TargetMigrationInProgress = false,
    bool IsUserOverridden = false);

/// <summary>规划出的一步。</summary>
public sealed record RelocationStep(
    string Name,
    RelocationKind Kind,
    string LegacyPath,
    string TargetPath,
    RelocationAction Action,
    RelocationSkipReason SkipReason,
    string Reason);

/// <summary>整体计划。</summary>
public sealed class RelocationPlan
{
    internal RelocationPlan(int fromVersion, int toVersion, IReadOnlyList<RelocationStep> steps)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Steps = steps;
    }

    /// <summary>配置中记录的已迁移布局版本。</summary>
    public int FromVersion { get; }

    /// <summary>本次要达到的布局版本。</summary>
    public int ToVersion { get; }

    /// <summary>全部步骤，包含无需动作的项（便于日志里看清每一项的判定）。</summary>
    public IReadOnlyList<RelocationStep> Steps { get; }

    /// <summary>需要实际执行的步骤。</summary>
    public IEnumerable<RelocationStep> ActionableSteps
        => Steps.Where(s => s.Action != RelocationAction.None);

    /// <summary>是否有任何实际动作。</summary>
    public bool HasWork => ActionableSteps.Any();

    /// <summary>
    /// 本次是否应该在结束后写入版本标记。
    /// 版本落后就要写 —— 即使一步都不用做（全新安装就是这种情况），
    /// 否则每次启动都要重新探测一遍磁盘。
    /// </summary>
    public bool ShouldStampVersion => FromVersion < ToVersion;
}

/// <summary>
/// 数据搬迁规划器。纯函数：给定"已迁移到哪个布局版本"与每项数据的探测结果，
/// 决定每一项该做什么。
///
/// <para>
/// 幂等性的关键在于<b>以旧位置的标记为准，而不是以目标位置是否存在为准</b>。
/// 目标位置存在只能说明"复制开始过"，不能说明"复制完整且校验通过"；
/// 只有墓碑才代表后者。把这两者混为一谈，断电恢复时就会在半份数据上继续追加。
/// </para>
///
/// <para>
/// 旧位置的标记有<b>两种，语义相反，绝不可混用</b>：
/// <list type="bullet">
/// <item><b>搬移墓碑</b>（<c>migrated-to.txt</c>）—— 数据真的搬到新位置去了，旧位置已清空
/// 或只差删源；</item>
/// <item><b>已跳过标记</b>（<c>superseded-by.txt</c>）—— 新位置更新，本项<b>没有</b>搬，
/// 旧位置那份是有意留下的历史副本，<b>永远不许删</b>。</item>
/// </list>
/// 见 <see cref="RelocationAction.AdoptTargetKeepLegacy"/>。
/// </para>
///
/// <para>
/// 全局版本闸门（<see cref="CurrentLayoutVersion"/>）保证正常路径下只跑一次；
/// 逐项状态机只在"上一次跑到一半被打断"时才会用到。用版本号而不是布尔标记，
/// 是为了将来再次调整目录结构时能做增量迁移。
/// </para>
/// </summary>
public static class DataRelocationPlanner
{
    /// <summary>
    /// 当前布局版本。目录结构再次调整时 +1，并在此注明每一版的差异。
    /// v1：数据从安装目录迁至 %LOCALAPPDATA%，仓库类数据原地登记。
    /// </summary>
    public const int CurrentLayoutVersion = 1;

    /// <summary>规划。</summary>
    /// <param name="migratedVersion">配置中记录的已迁移布局版本，全新安装为 0。</param>
    /// <param name="probes">每项数据的探测结果。</param>
    public static RelocationPlan Plan(int migratedVersion, IEnumerable<RelocationProbe> probes)
    {
        if (probes is null) throw new ArgumentNullException(nameof(probes));

        var steps = new List<RelocationStep>();
        var alreadyMigrated = migratedVersion >= CurrentLayoutVersion;

        foreach (var probe in probes)
        {
            steps.Add(alreadyMigrated
                ? Skip(probe, RelocationSkipReason.AlreadyMigrated,
                    $"布局版本已是 v{migratedVersion}，无需再迁移")
                : PlanOne(probe));
        }

        return new RelocationPlan(migratedVersion, CurrentLayoutVersion,
            new ReadOnlyCollection<RelocationStep>(steps));
    }

    private static RelocationStep PlanOne(RelocationProbe probe)
        => probe.Kind == RelocationKind.RegisterInPlace
            ? PlanRegisterInPlace(probe)
            : PlanRelocate(probe);

    private static RelocationStep PlanRegisterInPlace(RelocationProbe probe)
    {
        // 用户显式指定过位置 —— 一步都不能动。判据是"配置里有非空值"，
        // 而不是"当前路径与默认值不同"：后者在默认值变化时会把用户的选择误判为默认。
        if (probe.IsUserOverridden)
        {
            return Skip(probe, RelocationSkipReason.UserOverride,
                "用户已自定义该位置，保持不变");
        }

        if (!probe.LegacyExists)
        {
            return Skip(probe, RelocationSkipReason.NothingToRelocate,
                "旧位置不存在，将使用新的默认位置");
        }

        return new RelocationStep(probe.Name, probe.Kind, probe.LegacyPath, probe.LegacyPath,
            RelocationAction.RegisterInPlace, RelocationSkipReason.NotSkipped,
            "旧位置有数据且不在安装目录内，原地登记，不搬移");
    }

    private static RelocationStep PlanRelocate(RelocationProbe probe)
    {
        if (PathsEqual(probe.LegacyPath, probe.TargetPath))
        {
            return Skip(probe, RelocationSkipReason.SourceIsTarget,
                "源与目标是同一路径");
        }

        if (probe.SupersededMarkerExists)
        {
            // 上一轮判定为"新位置赢"，数据并没有被搬走，旧位置那份是有意留下的历史副本。
            // 这一条必须排在墓碑判断之前：两个标记同时存在时（例如先搬成功、后来又被人工
            // 把旧数据放回去、再跑出一次跳过），保守的一边是"什么都不做"。
            // 若让墓碑分支先命中，动作会是 ResumeCleanup —— 直接删掉旧位置的数据。
            return Skip(probe, RelocationSkipReason.SupersededByTarget,
                "新位置已有更新的数据，本项此前已判定为跳过，旧位置的历史副本原样保留");
        }

        if (probe.TombstoneExists)
        {
            // 墓碑代表"复制完成且校验通过"。此时源还在，只能是删源没做完。
            return probe.LegacyExists
                ? new RelocationStep(probe.Name, probe.Kind, probe.LegacyPath, probe.TargetPath,
                    RelocationAction.ResumeCleanup, RelocationSkipReason.NotSkipped,
                    "已有墓碑，复制与校验已完成，续做删源")
                : Skip(probe, RelocationSkipReason.NothingToRelocate,
                    "已有墓碑且源已清理，迁移已完成");
        }

        if (!probe.LegacyExists)
        {
            return Skip(probe, RelocationSkipReason.NothingToRelocate,
                "旧位置不存在，无需搬移");
        }

        if (!probe.TargetExists)
        {
            return new RelocationStep(probe.Name, probe.Kind, probe.LegacyPath, probe.TargetPath,
                RelocationAction.Copy, RelocationSkipReason.NotSkipped,
                "旧位置有数据，执行搬移");
        }

        // 走到这里：新旧两处都有内容，且没有任何墓碑/跳过标记。
        // 是"上次中断的残留"还是"应用写下的真实数据"，全靠目标侧的进行中标记来分——
        // 两者在磁盘上的外观一模一样，没有第二个可用的信号。
        return probe.TargetMigrationInProgress
            ? new RelocationStep(probe.Name, probe.Kind, probe.LegacyPath, probe.TargetPath,
                RelocationAction.PurgeTargetThenCopy, RelocationSkipReason.NotSkipped,
                "目标有内容且带搬迁进行中标记，判定为上次中断的残留，清空后重新复制")
            : new RelocationStep(probe.Name, probe.Kind, probe.LegacyPath, probe.TargetPath,
                RelocationAction.AdoptTargetKeepLegacy, RelocationSkipReason.NotSkipped,
                "新位置已有应用写入的数据，认新位置为准；不复制、不删源，旧位置留跳过标记");
    }

    private static RelocationStep Skip(RelocationProbe probe, RelocationSkipReason reason, string message)
        => new(probe.Name, probe.Kind, probe.LegacyPath, probe.TargetPath,
            RelocationAction.None, reason, message);

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(
            a.TrimEnd('\\', '/'),
            b.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
