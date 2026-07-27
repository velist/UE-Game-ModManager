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
    /// 目标已有内容但没有墓碑 —— 上一次在"复制完成"与"校验通过"之间被打断，
    /// 目标里可能是半份数据。必须先清空目标再重新复制，绝不能在残留上继续。
    /// </summary>
    PurgeTargetThenCopy,

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
/// <param name="TombstoneExists">旧位置是否已有墓碑。</param>
/// <param name="IsUserOverridden">用户是否已显式指定过该位置。</param>
public sealed record RelocationProbe(
    string Name,
    RelocationKind Kind,
    string LegacyPath,
    string TargetPath,
    bool LegacyExists,
    bool TargetExists,
    bool TombstoneExists,
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
/// 幂等性的关键在于<b>以墓碑为准，而不是以目标位置是否存在为准</b>。
/// 目标位置存在只能说明"复制开始过"，不能说明"复制完整且校验通过"；
/// 只有墓碑才代表后者。把这两者混为一谈，断电恢复时就会在半份数据上继续追加。
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

        // 没有墓碑却有目标内容 —— 上次被打断在复制途中，目标里可能是半份数据。
        return probe.TargetExists
            ? new RelocationStep(probe.Name, probe.Kind, probe.LegacyPath, probe.TargetPath,
                RelocationAction.PurgeTargetThenCopy, RelocationSkipReason.NotSkipped,
                "目标已有内容但无墓碑，判定为上次中断的残留，清空后重新复制")
            : new RelocationStep(probe.Name, probe.Kind, probe.LegacyPath, probe.TargetPath,
                RelocationAction.Copy, RelocationSkipReason.NotSkipped,
                "旧位置有数据，执行搬移");
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
