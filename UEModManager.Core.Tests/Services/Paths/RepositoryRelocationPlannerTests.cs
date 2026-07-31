using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// "换个地方存 MOD"的判定。
///
/// <para>
/// 这一层要挡住的是一件很具体的事故：改仓库位置此前只有一行
/// <c>SetRepositoryRoot</c>——只改指针、不搬数据。用户改完，已导入的包实体还躺在旧位置，
/// 新仓库是空的，界面上 MOD 全没了。他既不知道发生了什么，也不会想到要自己去拷目录。
/// 所以从此"改位置"必须连着"搬数据"，而这里负责在动手之前把该拦的全拦下来。
/// </para>
///
/// <para>
/// 判定做成纯函数的收益在这里格外明显：空间够不够、目标是不是嵌在源里面、空仓库该不该
/// 走快速路径，每一条错了都是丢数据，而每一条都能用两个数字和两个字符串钉死。
/// </para>
/// </summary>
public class RepositoryRelocationPlannerTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    private static RepositoryRelocationProbe Probe(
        string source = @"C:\Users\a\AppData\Local\UEModManager\Repository",
        string target = @"D:\UEModManager\Repository",
        bool targetUsable = true,
        bool sourceHasContent = true,
        long? payloadBytes = 8 * Gib,
        int packageCount = 42,
        long? targetAvailableBytes = 200 * Gib,
        bool targetHasContent = false)
        => new(source, target, targetUsable, sourceHasContent,
            payloadBytes, packageCount, targetAvailableBytes, targetHasContent);

    // ─── 正常路径 ───

    [Fact]
    public void 空间够时给出搬移计划()
    {
        var plan = RepositoryRelocationPlanner.Plan(Probe());

        Assert.Equal(RepositoryRelocationAction.MoveThenPoint, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.None, plan.Blocker);
        Assert.True(plan.NeedsMove);
        Assert.True(plan.CanProceed);
        Assert.Equal(8 * Gib, plan.PayloadBytes);
        Assert.True(plan.PayloadSizeKnown);
        Assert.Equal(42, plan.PayloadPackageCount);
    }

    // ─── 空仓库快速路径 ───

    [Fact]
    public void 仓库为空时只改指针不搬数据()
    {
        // 新用户的仓库是空的。给他弹一个"正在搬 0 个文件"的进度条、
        // 再让他点一次"完成"，是纯粹的噪音。
        var plan = RepositoryRelocationPlanner.Plan(Probe(sourceHasContent: false));

        Assert.Equal(RepositoryRelocationAction.PointerOnly, plan.Action);
        Assert.False(plan.NeedsMove);
        Assert.True(plan.CanProceed);
        Assert.Equal(0, plan.PayloadBytes);
    }

    [Fact]
    public void 仓库为空时不因目标盘空间小而被拦()
    {
        // 没东西要搬就没有"装不装得下"这回事。一个空仓库因为目标盘只剩 30 MB
        // 被拦下来纯属荒唐，而这正是"空仓库判据排在空间预检之前"要保证的。
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(sourceHasContent: false, payloadBytes: 0, targetAvailableBytes: 30 * Mib));

        Assert.Equal(RepositoryRelocationAction.PointerOnly, plan.Action);
    }

    [Fact]
    public void 仓库为空时不因目标已有内容而被拦()
    {
        // 什么都不搬就没有回退，也就没有"清空目标"这个动作，那条判据在这里不成立。
        // 老用户把仓库指到一个装着上一版仓库的目录、而当前仓库恰好是空的，正是这种情形。
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(sourceHasContent: false, targetHasContent: true));

        Assert.Equal(RepositoryRelocationAction.PointerOnly, plan.Action);
    }

    // ─── 目标就是当前位置 ───

    [Theory]
    [InlineData(@"D:\Mods\Repo", @"D:\Mods\Repo")]
    [InlineData(@"D:\Mods\Repo", @"D:\Mods\Repo\")]
    [InlineData(@"D:\Mods\Repo", @"d:\mods\repo")]
    public void 目标就是当前位置时什么都不做(string source, string target)
    {
        var plan = RepositoryRelocationPlanner.Plan(Probe(source: source, target: target));

        Assert.Equal(RepositoryRelocationAction.None, plan.Action);
        Assert.True(plan.CanProceed);
        Assert.False(plan.NeedsMove);
    }

    [Fact]
    public void 相同位置的判定排在嵌套判定之前()
    {
        // 一个路径当然"包含"它自己。相同位置若落进嵌套那两格，
        // 用户在设置里点了保存却没改路径就会收到一句"新位置在现在这个文件夹里面"。
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(source: @"D:\Mods", target: @"D:\Mods"));

        Assert.Equal(RepositoryRelocationAction.None, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.None, plan.Blocker);
    }

    // ─── 嵌套：两个方向都会丢数据 ───

    [Fact]
    public void 目标在源里面时拦下()
    {
        // 递归复制会自己吃自己
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(source: @"D:\Mods", target: @"D:\Mods\New"));

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.TargetInsideSource, plan.Blocker);
        Assert.False(plan.CanProceed);
    }

    [Fact]
    public void 源在目标里面时拦下()
    {
        // "删源"那一步会删掉刚复制过去的数据
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(source: @"D:\Mods\Repo", target: @"D:\Mods"));

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.SourceInsideTarget, plan.Blocker);
    }

    [Fact]
    public void 同名前缀不算嵌套()
    {
        // D:\Mods 不该把 D:\ModsBackup 当成自己的子目录，否则一次完全合法的搬移被误拒
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(source: @"D:\Mods", target: @"D:\ModsBackup"));

        Assert.Equal(RepositoryRelocationAction.MoveThenPoint, plan.Action);
    }

    // ─── 目标非空 ───

    [Fact]
    public void 目标已有别的东西时拦下()
    {
        // 回退（取消/断电）要清空目标，而清空只有在目标里全是我们自己写的东西时才安全。
        // 与 DataRelocationExecutor.PurgeTarget 同一条判据。
        var plan = RepositoryRelocationPlanner.Plan(Probe(targetHasContent: true));

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.TargetNotEmpty, plan.Blocker);
    }

    // ─── 空间预检 ───

    [Fact]
    public void 目标盘装不下时提前拦下()
    {
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(payloadBytes: 8 * Gib, targetAvailableBytes: 5 * Gib));

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.InsufficientSpace, plan.Blocker);
        Assert.False(plan.CanProceed);
    }

    [Fact]
    public void 空间不足时算得出还差多少()
    {
        // 用户要的是"还差 X"这个已经减好的数字，不是"需要 X、可用 Z"让他自己算
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(payloadBytes: 10 * Gib, targetAvailableBytes: 4 * Gib));

        var shortfall = RepositoryRelocationPlanner.ShortfallBytes(plan);

        // 需要 = 10 GiB + max(10 GiB/10, 64 MiB) = 11 GiB；可用 4 GiB ⇒ 差 7 GiB
        Assert.Equal(7 * Gib, shortfall);
    }

    [Fact]
    public void 没被空间拦下时还差多少恒为零()
    {
        Assert.Equal(0, RepositoryRelocationPlanner.ShortfallBytes(
            RepositoryRelocationPlanner.Plan(Probe())));
        Assert.Equal(0, RepositoryRelocationPlanner.ShortfallBytes(
            RepositoryRelocationPlanner.Plan(Probe(targetHasContent: true))));
    }

    [Fact]
    public void 同盘搬移也要求足够空间()
    {
        // 刻意不给 Directory.Move 开快速通道：改名没有"复制完成"这个可校验的中间态，
        // 中途失败留下的正是"半吊子状态"本身，还要给崩溃恢复多出一整套独立判据。
        // 代价就是同盘搬移也要临时占一份等量空间，这条测试把它钉住。
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(source: @"D:\Old\Repo", target: @"D:\New\Repo",
                payloadBytes: 8 * Gib, targetAvailableBytes: 2 * Gib));

        Assert.Equal(RepositoryRelocationBlocker.InsufficientSpace, plan.Blocker);
    }

    [Fact]
    public void 体积估不出来时照常搬移()
    {
        // 预检不该比它保护的复制更容易失败，否则它自己会变成一个新的"永远搬不了"的原因
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(payloadBytes: null, targetAvailableBytes: 1 * Mib));

        Assert.Equal(RepositoryRelocationAction.MoveThenPoint, plan.Action);
        Assert.False(plan.PayloadSizeKnown);
    }

    [Fact]
    public void 目标卷可用空间查不出来时照常搬移()
    {
        // 网络位置 / 映射盘 / 卷未就绪。查不到就退回预检存在之前的行为，
        // 而不是给一整类用户加一道永久性的准入闸门。
        var plan = RepositoryRelocationPlanner.Plan(Probe(targetAvailableBytes: null));

        Assert.Equal(RepositoryRelocationAction.MoveThenPoint, plan.Action);
        Assert.Equal(DiskSpaceDecision.Unknown, plan.SpaceCheck.Decision);
    }

    // ─── 目标本身不可用 ───

    [Fact]
    public void 上游判定目标不可用时拦下()
    {
        var plan = RepositoryRelocationPlanner.Plan(Probe(targetUsable: false));

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.TargetUnusable, plan.Blocker);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 没有目标位置时拦下(string? target)
    {
        var plan = RepositoryRelocationPlanner.Plan(Probe(target: target!));

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.TargetUnusable, plan.Blocker);
    }

    // ─── 文案：写给普通玩家，且数字不能说反 ───

    [Fact]
    public void 空间不足的文案里带着还差多少()
    {
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(payloadBytes: 10 * Gib, targetAvailableBytes: 4 * Gib));

        var body = RepositoryRelocationMessages.BlockedBody(plan);

        Assert.Contains("还差", body);
        Assert.Contains("7 GiB", body);
        // 最要紧的一句：现在什么都没动
        Assert.Contains("还在原来的位置", body);
    }

    [Fact]
    public void 确认文案说清楚不用手动拷文件()
    {
        var body = RepositoryRelocationMessages.ConfirmBody(
            RepositoryRelocationPlanner.Plan(Probe()));

        Assert.Contains("42 个 MOD", body);
        Assert.Contains("8 GiB", body);
        Assert.Contains("不用手动", body);
        // 断电不丢数据这件事要说给用户听，那正是他犹豫要不要点的原因
        Assert.Contains("断电", body);
    }

    [Fact]
    public void 取消文案的第一件事是东西没少()
    {
        var body = RepositoryRelocationMessages.CancelledBody(
            RepositoryRelocationPlanner.Plan(Probe()));

        Assert.Contains("一个都没少", body);
        Assert.Contains("存放位置也没有改", body);
    }

    [Fact]
    public void 失败文案的第一件事也是东西没少()
    {
        var body = RepositoryRelocationMessages.FailureBody(
            RepositoryRelocationPlanner.Plan(Probe()), "拒绝访问");

        Assert.Contains("一个都没少", body);
        Assert.Contains("拒绝访问", body);
    }

    [Fact]
    public void 已部署过时成功文案提醒重新装一次()
    {
        // 已装进游戏目录的硬链接指向的是旧盘上的数据本体，删掉旧仓库里那个名字之后
        // 游戏照样能玩（数据仍被引用着），但那份空间要重新部署才腾得出来。
        // 这是整条流程里最容易被忽略、后果又最难自查的一件事。
        var plan = RepositoryRelocationPlanner.Plan(Probe());

        var withDeployed = RepositoryRelocationMessages.SuccessBody(plan, anyDeployed: true);
        var withoutDeployed = RepositoryRelocationMessages.SuccessBody(plan, anyDeployed: false);

        Assert.Contains("照常玩", withDeployed);
        Assert.Contains("重新装一次", withDeployed);
        Assert.DoesNotContain("重新装一次", withoutDeployed);
    }

    [Fact]
    public void 文案里不出现技术词()
    {
        // 面向普通玩家：不出现"卷""指针""墓碑""硬链接"这类只有我们自己看得懂的词
        var plan = RepositoryRelocationPlanner.Plan(
            Probe(payloadBytes: 10 * Gib, targetAvailableBytes: 4 * Gib));

        var texts = new[]
        {
            RepositoryRelocationMessages.ConfirmTitle(plan),
            RepositoryRelocationMessages.BlockedBody(plan),
            RepositoryRelocationMessages.CancelledBody(plan),
            RepositoryRelocationMessages.SuccessBody(plan, anyDeployed: true),
            RepositoryRelocationMessages.FailureBody(plan, "x"),
        };

        foreach (var text in texts)
        {
            foreach (var jargon in new[] { "卷", "指针", "墓碑", "硬链接", "校验和" })
            {
                Assert.DoesNotContain(jargon, text);
            }
        }
    }

    [Fact]
    public void 盘符说成人话()
    {
        Assert.Equal("D 盘", RepositoryRelocationMessages.DescribeWhere(@"D:\UEModManager\Repository"));
        // 说不清时退回原路径，绝不编一个"未知盘"塞进用户看的句子里
        Assert.Equal("relative\\path", RepositoryRelocationMessages.DescribeWhere(@"relative\path"));
    }

    // ─── 进度：百分比与文案 ───

    [Fact]
    public void 复制进度按字节算而不是按文件数()
    {
        // 包实体大小差几个数量级（3 KB 的 ini 和 8 GB 的 pak 各算"一个文件"），
        // 按文件数算的进度条会在大文件上停死半天再突然跳一大截
        var progress = new RepositoryRelocationProgress(
            RepositoryRelocationStage.Copying,
            CopiedBytes: 5 * Gib, TotalBytes: 10 * Gib,
            CopiedFiles: 1, TotalFiles: 100);

        Assert.Equal(0.5, progress.CopyFraction, 3);
    }

    [Fact]
    public void 字节数不可知时退回按文件数()
    {
        var progress = new RepositoryRelocationProgress(
            RepositoryRelocationStage.Copying, 0, 0, CopiedFiles: 25, TotalFiles: 100);

        Assert.Equal(0.25, progress.CopyFraction, 3);
        Assert.False(progress.IsIndeterminate);
    }

    [Fact]
    public void 总量完全不可知时进度条走未知模式()
    {
        // 一个永远停在 0% 的确定进度条，用户读出来的就是"卡住了"
        var progress = new RepositoryRelocationProgress(
            RepositoryRelocationStage.Copying, 0, 0, 0, 0);

        Assert.True(progress.IsIndeterminate);
    }

    [Fact]
    public void 复制走完时整体进度不到百分之百()
    {
        // 校验、落定、清理都还没做。一条走到 100% 之后还停留一段时间的进度条
        // 用户读出来是"卡死了"。
        var copying = new RepositoryRelocationProgress(
            RepositoryRelocationStage.Copying, 10 * Gib, 10 * Gib, 100, 100);

        Assert.Equal(85, copying.Percent, 3);
        Assert.True(copying.Percent < 100);
    }

    [Fact]
    public void 各阶段的整体进度单调不减()
    {
        var stages = new[]
        {
            RepositoryRelocationStage.Preparing,
            RepositoryRelocationStage.Copying,
            RepositoryRelocationStage.Verifying,
            RepositoryRelocationStage.Committing,
            RepositoryRelocationStage.CleaningUp,
            RepositoryRelocationStage.Done,
        };

        // 复制阶段取"已经复制完"的那一刻，才能和后面几步接得上
        double previous = -1;
        foreach (var stage in stages)
        {
            var percent = new RepositoryRelocationProgress(stage, 10, 10, 1, 1).Percent;
            Assert.True(percent >= previous,
                $"{stage} 的进度 {percent} 比上一步的 {previous} 还小——进度条会倒退");
            previous = percent;
        }

        Assert.Equal(100, previous, 3);
    }

    [Fact]
    public void 进度文案里不出现文件路径()
    {
        // 一行飞速滚动的路径对普通玩家没有信息量，却会让窗口宽度随机跳动
        var progress = new RepositoryRelocationProgress(
            RepositoryRelocationStage.Copying, 2 * Gib, 8 * Gib, 10, 40);

        Assert.Contains("2 GiB", progress.StatusText);
        Assert.Contains("8 GiB", progress.StatusText);
        Assert.DoesNotContain("\\", progress.StatusText);
    }

    [Fact]
    public void 落定阶段的文案不说取消()
    {
        // 过了落定点就不能取消了，文案不该给用户一个"还能停"的暗示
        var committing = new RepositoryRelocationProgress(
            RepositoryRelocationStage.Committing, 8 * Gib, 8 * Gib, 40, 40);

        Assert.DoesNotContain("取消", committing.StatusText);
        Assert.DoesNotContain("停", committing.StatusText);
    }
}
