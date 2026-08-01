using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// 仓库搬移被中断（断电、强杀、崩溃）之后，下次启动该怎么收拾。
///
/// <para>
/// <b>这张表就是"断电不丢数据"这句承诺本身。</b>判定只看一件事——旧位置有没有墓碑
/// （复制完成且校验通过的唯一证据）：有就往前推，没有就往回退。日记里的阶段值
/// <b>刻意不参与</b>推还是退的决定，它只负责记住"哪两个路径"——阶段是一次单独的偏好写入，
/// 它和墓碑之间必然有一个谁先谁后的窗口，拿它当判据就等于给自己造一个
/// "阶段说没搬完、其实已经搬完了"的时刻。
/// </para>
///
/// <para>
/// 用例按搬移的落地顺序逐个断点排列，每一条对应一个真实的"这一刻拔电源"。
/// </para>
/// </summary>
public class RepositoryRelocationRecoveryPlannerTests
{
    private const string Source = @"C:\Users\a\AppData\Local\UEModManager\Repository";
    private const string Target = @"D:\UEModManager\Repository";

    private static RepositoryRelocationRecoveryProbe Probe(
        RepositoryRelocationPhase phase = RepositoryRelocationPhase.Moving,
        string? source = Source,
        string? target = Target,
        string? currentRoot = Source,
        bool legacyTombstone = false,
        bool legacyHasContent = true,
        bool targetMarker = false,
        bool targetHasContent = false)
        => new(new RepositoryRelocationJournal(phase, source, target),
            currentRoot, legacyTombstone, legacyHasContent, targetMarker, targetHasContent);

    // ─── 没有在途搬移 ───

    [Fact]
    public void 没有日记时什么都不做()
    {
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            new RepositoryRelocationRecoveryProbe(
                RepositoryRelocationJournal.None, Source, false, true, false, false));

        Assert.Equal(RepositoryRelocationRecoveryAction.None, plan.Action);
    }

    [Theory]
    [InlineData(null, Target)]
    [InlineData(Source, null)]
    [InlineData("", Target)]
    [InlineData(Source, "   ")]
    public void 日记残缺时什么都不做(string? source, string? target)
    {
        // 缺路径的日记没法据以恢复。宁可什么都不做——两边的数据此刻都还在，
        // 而照着半份日记去删任何一侧都是不可逆的。
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(source: source, target: target));

        Assert.Equal(RepositoryRelocationRecoveryAction.None, plan.Action);
    }

    // ─── 断点 1：写完日记、还没往目标写任何东西 ───

    [Fact]
    public void 还没开始写目标时只划掉日记()
    {
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(targetMarker: false, targetHasContent: false));

        Assert.Equal(RepositoryRelocationRecoveryAction.ClearJournalOnly, plan.Action);
    }

    // ─── 断点 2：复制到一半 ───

    [Fact]
    public void 复制到一半断电时回退()
    {
        // 旧位置没有墓碑 ⇒ 新位置那堆是半份数据。旧位置自始至终一个字节都没被动过。
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(legacyTombstone: false, targetMarker: true, targetHasContent: true));

        Assert.Equal(RepositoryRelocationRecoveryAction.RollBack, plan.Action);
        Assert.Equal(Source, plan.SourceRoot);
        Assert.Equal(Target, plan.TargetRoot);
    }

    [Fact]
    public void 回退时只清带进行中标记的目标()
    {
        // 与 DataRelocationExecutor.PurgeTarget 同一条判据：没有标记却有内容，
        // 说明那个位置是被别人正常使用的（用户可能上次失败后自己把仓库指到了那儿
        // 并往里导过 MOD），此时清空等于把用户的数据删光。判不准就宁可留一堆垃圾。
        Assert.True(RepositoryRelocationRecoveryPlanner.MayPurgeTarget(
            Probe(targetMarker: true, targetHasContent: true)));

        Assert.False(RepositoryRelocationRecoveryPlanner.MayPurgeTarget(
            Probe(targetMarker: false, targetHasContent: true)));
    }

    [Fact]
    public void 目标有内容但没标记时仍判回退但不许清()
    {
        // 结论是回退（存放位置不动、旧位置为准），但清理那一步会被 MayPurgeTarget 挡住。
        // 两件事分开表达，是因为"该往哪走"和"能不能动手删"本来就是两个问题。
        var probe = Probe(legacyTombstone: false, targetMarker: false, targetHasContent: true);
        var plan = RepositoryRelocationRecoveryPlanner.Plan(probe);

        Assert.Equal(RepositoryRelocationRecoveryAction.RollBack, plan.Action);
        Assert.False(RepositoryRelocationRecoveryPlanner.MayPurgeTarget(probe));
    }

    // ─── 断点 3：墓碑已写下、存放位置还没改 ───

    [Fact]
    public void 墓碑已写下但存放位置还没改时往前推()
    {
        // 这是最关键的一格。复制与校验都过了，新位置是权威副本，
        // 而存放位置还指着旧位置——不推的话用户下次启动看到的是一个即将被清空的旧仓库。
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(legacyTombstone: true, currentRoot: Source, targetMarker: false));

        Assert.Equal(RepositoryRelocationRecoveryAction.RollForward, plan.Action);
        Assert.Equal(Target, plan.TargetRoot);
    }

    [Fact]
    public void 有墓碑时不看进行中标记也不看日记阶段()
    {
        // 墓碑是唯一判据。清标记那一步排在写墓碑之后，中间断电会留下"墓碑 + 标记"并存；
        // 阶段值同理，它比墓碑晚落盘。任何一个被当成判据都会把这一格判成回退，
        // 而回退会清掉那份已经校验通过的数据。
        foreach (var marker in new[] { true, false })
        foreach (var phase in new[] { RepositoryRelocationPhase.Moving, RepositoryRelocationPhase.Cleanup })
        {
            var plan = RepositoryRelocationRecoveryPlanner.Plan(
                Probe(phase: phase, legacyTombstone: true, currentRoot: Source, targetMarker: marker));

            Assert.Equal(RepositoryRelocationRecoveryAction.RollForward, plan.Action);
        }
    }

    // ─── 断点 4：存放位置已改、旧位置还没清完 ───

    [Fact]
    public void 存放位置已是新位置且旧位置还有东西时继续清理()
    {
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(phase: RepositoryRelocationPhase.Cleanup, currentRoot: Target,
                legacyTombstone: true, legacyHasContent: true));

        Assert.Equal(RepositoryRelocationRecoveryAction.ResumeCleanup, plan.Action);
        Assert.Equal(Source, plan.SourceRoot);
    }

    [Fact]
    public void 存放位置已是新位置且旧位置已空时只划掉日记()
    {
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(phase: RepositoryRelocationPhase.Cleanup, currentRoot: Target,
                legacyTombstone: true, legacyHasContent: false));

        Assert.Equal(RepositoryRelocationRecoveryAction.ClearJournalOnly, plan.Action);
    }

    [Fact]
    public void 存放位置已在新位置的判定排在墓碑判定之前()
    {
        // 两格的区别只在于存放位置改没改。若让"有墓碑 ⇒ 往前推"先命中，
        // 恢复会对一个已经写好的存放位置再写一次——无害但多余，
        // 而且会把"其实只剩清尾"这件事记成"刚刚才推过去"，日志从此说不清。
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(currentRoot: Target, legacyTombstone: true, legacyHasContent: true));

        Assert.Equal(RepositoryRelocationRecoveryAction.ResumeCleanup, plan.Action);
    }

    [Theory]
    [InlineData(@"D:\UEModManager\Repository\")]
    [InlineData(@"d:\uemodmanager\repository")]
    public void 存放位置比对不区分大小写与末尾分隔符(string currentRoot)
    {
        // 存放位置这一路上经过配置文件、目录选择器与 Path.Combine，
        // 末尾分隔符和大小写都不稳定。比错一次就是把"只剩清尾"判成"往前推"。
        var plan = RepositoryRelocationRecoveryPlanner.Plan(
            Probe(currentRoot: currentRoot, legacyTombstone: true, legacyHasContent: true));

        Assert.Equal(RepositoryRelocationRecoveryAction.ResumeCleanup, plan.Action);
    }

    // ─── 日记本身 ───

    [Fact]
    public void 空日记不可据以行动()
    {
        Assert.False(RepositoryRelocationJournal.None.IsActionable);
        Assert.Equal(RepositoryRelocationPhase.Idle, RepositoryRelocationJournal.None.Phase);
    }

    [Fact]
    public void 完整日记可据以行动()
    {
        Assert.True(new RepositoryRelocationJournal(
            RepositoryRelocationPhase.Moving, Source, Target).IsActionable);
    }
}
