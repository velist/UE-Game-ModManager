using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

public class DataRelocationPlannerTests
{
    private const string Legacy = @"C:\Program Files\UEModManager\Data";
    private const string Target = @"C:\Users\u\AppData\Local\UEModManager\Data";

    private static RelocationProbe Probe(
        RelocationKind kind = RelocationKind.Relocate,
        bool legacyExists = true,
        bool targetExists = false,
        bool tombstoneExists = false,
        bool isUserOverridden = false,
        string legacyPath = Legacy,
        string targetPath = Target,
        bool supersededMarkerExists = false,
        bool targetMigrationInProgress = false)
        => new("Data", kind, legacyPath, targetPath,
            legacyExists, targetExists, tombstoneExists,
            supersededMarkerExists, targetMigrationInProgress, isUserOverridden);

    private static RelocationStep PlanOne(RelocationProbe probe, int migratedVersion = 0)
        => Assert.Single(DataRelocationPlanner.Plan(migratedVersion, new[] { probe }).Steps);

    // ─── 全局版本闸门：正常路径下只跑一次 ───

    [Fact]
    public void AlreadyAtCurrentVersion_SkipsEverything()
    {
        var step = PlanOne(Probe(), migratedVersion: DataRelocationPlanner.CurrentLayoutVersion);

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.AlreadyMigrated, step.SkipReason);
    }

    [Fact]
    public void FutureVersion_SkipsEverything()
    {
        // 用户降级安装：配置里的版本比当前代码还新，不能倒着迁
        var step = PlanOne(Probe(), migratedVersion: DataRelocationPlanner.CurrentLayoutVersion + 1);

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.AlreadyMigrated, step.SkipReason);
    }

    [Fact]
    public void AlreadyMigrated_StillStampsNothing()
    {
        var plan = DataRelocationPlanner.Plan(
            DataRelocationPlanner.CurrentLayoutVersion, new[] { Probe() });

        Assert.False(plan.HasWork);
        Assert.False(plan.ShouldStampVersion);
    }

    [Fact]
    public void FreshInstall_NoWorkButStampsVersion()
    {
        // 全新安装：没有任何旧数据，但仍要写版本标记，否则每次启动都重新探测磁盘
        var plan = DataRelocationPlanner.Plan(0, new[] { Probe(legacyExists: false) });

        Assert.False(plan.HasWork);
        Assert.True(plan.ShouldStampVersion);
    }

    // ─── 墓碑状态机：搬移类 ───

    [Fact]
    public void LegacyOnly_PlansCopy()
    {
        var step = PlanOne(Probe(legacyExists: true, targetExists: false, tombstoneExists: false));

        Assert.Equal(RelocationAction.Copy, step.Action);
    }

    [Fact]
    public void TargetExistsWithInProgressMarker_PurgesTargetFirst()
    {
        // 断电停在"复制中途" —— 进行中标记证明目标里的残留是搬迁器自己写的，
        // 可能是半份数据，绝不能在上面继续
        var step = PlanOne(Probe(legacyExists: true, targetExists: true, tombstoneExists: false,
            targetMigrationInProgress: true));

        Assert.Equal(RelocationAction.PurgeTargetThenCopy, step.Action);
    }

    [Fact]
    public void TombstoneWithLegacyStillPresent_ResumesCleanupOnly()
    {
        // 断电停在"写墓碑之后、删源之前" —— 复制与校验都过了，不该重新复制
        var step = PlanOne(Probe(legacyExists: true, targetExists: true, tombstoneExists: true));

        Assert.Equal(RelocationAction.ResumeCleanup, step.Action);
    }

    [Fact]
    public void TombstoneAndLegacyGone_IsComplete()
    {
        var step = PlanOne(Probe(legacyExists: false, tombstoneExists: true));

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.NothingToRelocate, step.SkipReason);
    }

    [Fact]
    public void NoLegacy_NothingToDo()
    {
        var step = PlanOne(Probe(legacyExists: false, targetExists: true));

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.NothingToRelocate, step.SkipReason);
    }

    [Fact]
    public void TombstoneDecidesOverTargetExistence()
    {
        // 幂等性的核心：目标存在只说明"复制开始过"，只有墓碑代表"复制完整且校验通过"。
        // 同样是 targetExists=true，墓碑的有无必须导出完全不同的动作。
        var withTombstone = PlanOne(Probe(targetExists: true, tombstoneExists: true));
        var withoutTombstone = PlanOne(Probe(targetExists: true, tombstoneExists: false,
            targetMigrationInProgress: true));

        Assert.Equal(RelocationAction.ResumeCleanup, withTombstone.Action);
        Assert.Equal(RelocationAction.PurgeTargetThenCopy, withoutTombstone.Action);
    }

    // ─── 新旧两处都有数据：新位置赢，旧位置原样保留 ───

    [Fact]
    public void TargetHasContentWithoutMarkers_AdoptsTargetAndKeepsLegacy()
    {
        // 开关翻开当天每台老用户机器的形态：两处都有内容、都没有墓碑，
        // 而目标侧没有进行中标记 —— 那是应用一直在读写的真实数据，不是搬迁残留。
        var step = PlanOne(Probe(legacyExists: true, targetExists: true, tombstoneExists: false));

        Assert.Equal(RelocationAction.AdoptTargetKeepLegacy, step.Action);
        Assert.Equal(RelocationSkipReason.NotSkipped, step.SkipReason);
    }

    [Fact]
    public void InProgressMarkerIsTheOnlyLineBetweenPurgeAndAdopt()
    {
        // 两种形态在磁盘上长得一模一样（有内容 + 无墓碑），唯一的分界线就是这个标记。
        // 判宽一格删用户数据，判严一格治不好断电，因此必须钉死成一条用例。
        var mine = PlanOne(Probe(targetExists: true, targetMigrationInProgress: true));
        var theirs = PlanOne(Probe(targetExists: true, targetMigrationInProgress: false));

        Assert.Equal(RelocationAction.PurgeTargetThenCopy, mine.Action);
        Assert.Equal(RelocationAction.AdoptTargetKeepLegacy, theirs.Action);
    }

    [Fact]
    public void EmptyTarget_StillPlansPlainCopy()
    {
        // "目标为空"由 IO 侧的 DirectoryHasContent 判定（空目录不算内容）。
        // 应用启动时会主动建出一批空目录，把它们当成"新位置已有数据"会让所有项都被跳过。
        var step = PlanOne(Probe(legacyExists: true, targetExists: false,
            targetMigrationInProgress: true));

        Assert.Equal(RelocationAction.Copy, step.Action);
    }

    [Fact]
    public void SupersededMarker_SkipsAndNeverResumesCleanup()
    {
        // 第二次启动：旧位置有跳过标记、数据也还在。绝不能因为"有标记 + 源还在"
        // 就走 ResumeCleanup —— 那会删掉一份从来没被复制过的数据。
        var step = PlanOne(Probe(legacyExists: true, targetExists: true,
            supersededMarkerExists: true));

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.SupersededByTarget, step.SkipReason);
    }

    [Fact]
    public void SupersededMarker_WinsOverTombstone()
    {
        // 两种标记同时存在（人工把旧数据放回去、又跑出一次跳过）时，保守的一边是什么都不做。
        // 墓碑分支若先命中，动作会是 ResumeCleanup，直接删掉旧位置。
        var step = PlanOne(Probe(legacyExists: true, targetExists: true,
            tombstoneExists: true, supersededMarkerExists: true));

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.SupersededByTarget, step.SkipReason);
    }

    [Fact]
    public void SourceEqualsTarget_IsSkipped()
    {
        var step = PlanOne(Probe(legacyPath: Target, targetPath: Target));

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.SourceIsTarget, step.SkipReason);
    }

    [Theory]
    [InlineData(@"C:\Data\", @"C:\Data")]
    [InlineData(@"C:\DATA", @"c:\data")]
    [InlineData(@"C:\Data/", @"C:\Data")]
    public void SourceEqualsTarget_IgnoresTrailingSeparatorAndCase(string a, string b)
    {
        var step = PlanOne(Probe(legacyPath: a, targetPath: b));

        Assert.Equal(RelocationSkipReason.SourceIsTarget, step.SkipReason);
    }

    // ─── 原地登记类：大目录不搬 ───

    [Fact]
    public void RegisterInPlace_WithLegacyData_RegistersLegacyPath()
    {
        var step = PlanOne(Probe(RelocationKind.RegisterInPlace, legacyExists: true));

        Assert.Equal(RelocationAction.RegisterInPlace, step.Action);
        // 登记的是旧位置本身，不是新默认位置
        Assert.Equal(Legacy, step.TargetPath);
    }

    [Fact]
    public void RegisterInPlace_UserOverridden_IsUntouched()
    {
        // 用户显式指定过仓库位置 —— 一步都不能动
        var step = PlanOne(Probe(RelocationKind.RegisterInPlace,
            legacyExists: true, isUserOverridden: true));

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.UserOverride, step.SkipReason);
    }

    [Fact]
    public void RegisterInPlace_UserOverrideWins_EvenWhenTombstonePresent()
    {
        var step = PlanOne(Probe(RelocationKind.RegisterInPlace,
            legacyExists: true, targetExists: true, tombstoneExists: true, isUserOverridden: true));

        Assert.Equal(RelocationSkipReason.UserOverride, step.SkipReason);
    }

    [Fact]
    public void RegisterInPlace_NoLegacyData_UsesNewDefault()
    {
        var step = PlanOne(Probe(RelocationKind.RegisterInPlace, legacyExists: false));

        Assert.Equal(RelocationAction.None, step.Action);
        Assert.Equal(RelocationSkipReason.NothingToRelocate, step.SkipReason);
    }

    [Fact]
    public void RegisterInPlace_NeverCopies()
    {
        // 大目录一律不搬 —— 任何输入组合都不该产出复制类动作，也不该产出"新位置赢"
        // （原地登记项的新位置就是旧位置本身，谈不上谁赢）
        foreach (var legacyExists in new[] { true, false })
        foreach (var targetExists in new[] { true, false })
        foreach (var tombstone in new[] { true, false })
        foreach (var inProgress in new[] { true, false })
        {
            var step = PlanOne(Probe(RelocationKind.RegisterInPlace,
                legacyExists, targetExists, tombstone, targetMigrationInProgress: inProgress));

            Assert.NotEqual(RelocationAction.Copy, step.Action);
            Assert.NotEqual(RelocationAction.PurgeTargetThenCopy, step.Action);
            Assert.NotEqual(RelocationAction.ResumeCleanup, step.Action);
            Assert.NotEqual(RelocationAction.AdoptTargetKeepLegacy, step.Action);
        }
    }

    // ─── 计划聚合 ───

    [Fact]
    public void Plan_KeepsSkippedStepsForDiagnostics()
    {
        var plan = DataRelocationPlanner.Plan(0, new[]
        {
            Probe(legacyExists: true),                       // Copy
            Probe(legacyExists: false),                      // None
            Probe(RelocationKind.RegisterInPlace, isUserOverridden: true), // None
        });

        // 全部步骤都保留，便于日志里看清每一项的判定；但只有一项需要执行
        Assert.Equal(3, plan.Steps.Count);
        Assert.Single(plan.ActionableSteps);
        Assert.True(plan.HasWork);
    }

    [Fact]
    public void Plan_RecordsVersionTransition()
    {
        var plan = DataRelocationPlanner.Plan(0, Array.Empty<RelocationProbe>());

        Assert.Equal(0, plan.FromVersion);
        Assert.Equal(DataRelocationPlanner.CurrentLayoutVersion, plan.ToVersion);
    }

    [Fact]
    public void Plan_NullProbes_Throws()
        => Assert.Throws<ArgumentNullException>(() => DataRelocationPlanner.Plan(0, null!));

    [Fact]
    public void Plan_EveryStepCarriesAReason()
    {
        var plan = DataRelocationPlanner.Plan(0, new[]
        {
            Probe(legacyExists: true),
            Probe(legacyExists: false),
            Probe(RelocationKind.RegisterInPlace, isUserOverridden: true),
        });

        Assert.All(plan.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
    }

    // ─── 幂等性：反复重放同一状态不产生副作用差异 ───

    [Fact]
    public void Replan_AfterStamping_IsStable()
    {
        // 模拟"迁移完成 → 写版本标记 → 下次启动"：即使磁盘状态没变，也不该再动
        var probe = Probe(legacyExists: true, targetExists: true, tombstoneExists: true);

        var first = DataRelocationPlanner.Plan(0, new[] { probe });
        var second = DataRelocationPlanner.Plan(DataRelocationPlanner.CurrentLayoutVersion, new[] { probe });

        Assert.True(first.HasWork);
        Assert.False(second.HasWork);
    }
}
