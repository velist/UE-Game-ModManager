using System;
using System.Globalization;
using System.IO;

namespace UEModManager.Services.Paths;

/// <summary>"把 MOD 换个地方存"这一次到底要做什么。</summary>
public enum RepositoryRelocationAction
{
    /// <summary>目标就是当前位置，一件事都不用做。</summary>
    None,

    /// <summary>
    /// 仓库里还没有任何东西：只改指针，不搬数据。
    ///
    /// <para>
    /// 这条快速路径存在的理由是<b>界面</b>而不是性能：新用户的仓库是空的，
    /// 给他弹一个"正在搬 0 个文件"的进度条、再让他点一次"完成"，是纯粹的噪音。
    /// 判据是"仓库里有没有东西"，不是"目录存不存在"——仓库目录会被
    /// <c>ObjectStore.EnsureInitialized</c> 顺手建出来，拿存在性当判据会让所有新用户
    /// 都走进搬移流程。
    /// </para>
    /// </summary>
    PointerOnly,

    /// <summary>先把数据搬过去，<b>搬成了才改指针</b>。</summary>
    MoveThenPoint,

    /// <summary>这个位置不能用，一个字节都不动，指针也不动。</summary>
    Blocked,
}

/// <summary>拦下这次搬移的原因。<see cref="RepositoryRelocationAction.Blocked"/> 时才有意义。</summary>
public enum RepositoryRelocationBlocker
{
    /// <summary>没被拦。</summary>
    None,

    /// <summary>目标盘装不下。</summary>
    InsufficientSpace,

    /// <summary>目标在当前仓库<b>里面</b>（<c>D:\Mods</c> → <c>D:\Mods\New</c>）。</summary>
    TargetInsideSource,

    /// <summary>当前仓库在目标<b>里面</b>（<c>D:\Mods\Repo</c> → <c>D:\Mods</c>）。</summary>
    SourceInsideTarget,

    /// <summary>
    /// 目标落点里已经有别的东西。
    ///
    /// <para>
    /// 这一条不是洁癖，是取消与断电两条回退路径的前提：回退要清空目标，
    /// 而"清空"只有在目标里全是我们自己写的东西时才是安全的
    /// （与 <c>DataRelocationExecutor.PurgeTarget</c> 同一条判据）。目标里混着用户原有的文件，
    /// 一次取消就会把它们一起删掉。合并两个仓库是另一件事，不该由"换个地方存"顺手做掉。
    /// </para>
    /// </summary>
    TargetNotEmpty,

    /// <summary>目标位置本身就不能用（不可写、路径不成立等），由上游的位置校验给出。</summary>
    TargetUnusable,
}

/// <summary>
/// 一次搬移前对现场的观测。全部 IO（仓库里有多少东西、目标盘剩多少）由主项目做完再填进来。
/// </summary>
/// <param name="SourceRoot">当前生效的仓库根。</param>
/// <param name="TargetRoot">已经过 <see cref="RepositoryLocationValidator.ResolveRepositoryPath"/> 换算的落点。</param>
/// <param name="TargetUsable">目标位置本身可用（上游校验结论不是 Blocked）。</param>
/// <param name="SourceHasContent">当前仓库里有东西。</param>
/// <param name="PayloadBytes">
/// 要搬的净字节数；<c>null</c> 表示估不出来（权限/占用）。
/// 与 <see cref="DiskSpacePrecheck.Evaluate"/> 的口径一致：估不出来时空间预检放行，
/// 理由见那里——预检不该比它保护的复制更容易失败。
/// </param>
/// <param name="PayloadPackageCount">要搬的包个数，只用于给用户一句"要搬多少个 MOD"。</param>
/// <param name="TargetAvailableBytes">目标卷可用字节数；查不到为 <c>null</c>。</param>
/// <param name="TargetHasContent">
/// 目标落点里已经有东西（搬迁器自己的元数据——墓碑/进行中标记——不算）。
/// 为 true 时一律拦下，理由见 <see cref="RepositoryRelocationBlocker.TargetNotEmpty"/>。
/// </param>
public readonly record struct RepositoryRelocationProbe(
    string? SourceRoot,
    string? TargetRoot,
    bool TargetUsable,
    bool SourceHasContent,
    long? PayloadBytes,
    int PayloadPackageCount,
    long? TargetAvailableBytes,
    bool TargetHasContent = false);

/// <summary>一次搬移的计划。</summary>
/// <param name="Action">这次要做什么。</param>
/// <param name="Blocker">被拦的原因。</param>
/// <param name="SourceRoot">从哪搬。</param>
/// <param name="TargetRoot">搬到哪。</param>
/// <param name="PayloadBytes">要搬多少字节；估不出来时为 0（此时 <see cref="PayloadSizeKnown"/> 为 false）。</param>
/// <param name="PayloadSizeKnown">体积估出来了。</param>
/// <param name="PayloadPackageCount">要搬多少个包。</param>
/// <param name="SpaceCheck">空间预检的完整输入与结论，供日志自证。</param>
/// <param name="Reason">判定理由，进日志用——"为什么这次没搬"必须能查。</param>
public sealed record RepositoryRelocationPlan(
    RepositoryRelocationAction Action,
    RepositoryRelocationBlocker Blocker,
    string SourceRoot,
    string TargetRoot,
    long PayloadBytes,
    bool PayloadSizeKnown,
    int PayloadPackageCount,
    DiskSpaceCheck SpaceCheck,
    string Reason)
{
    /// <summary>要真的搬文件（需要进度与取消）。</summary>
    public bool NeedsMove => Action == RepositoryRelocationAction.MoveThenPoint;

    /// <summary>可以往下走（含"什么都不用做"）。</summary>
    public bool CanProceed => Action != RepositoryRelocationAction.Blocked;

    public override string ToString()
        => $"{Action}"
            + (Blocker == RepositoryRelocationBlocker.None ? string.Empty : $"/{Blocker}")
            + $"：{SourceRoot} → {TargetRoot}"
            + (NeedsMove
                ? $"（{PayloadPackageCount} 个包，"
                    + $"{(PayloadSizeKnown ? DiskSpacePrecheck.Humanize(PayloadBytes) : "体积未知")}）"
                : string.Empty)
            + $"｜{Reason}";
}

/// <summary>
/// "换个地方存 MOD"这一次该做什么的判定（纯函数，不碰 IO）。
///
/// <para><b>要解决的问题</b></para>
/// 改仓库位置此前只有一行 <c>SetRepositoryRoot</c>——<b>只改指针，不搬数据</b>。
/// 用户改完，已导入的包实体还躺在旧位置，新仓库是空的，界面上 MOD 全没了。
/// 他不会知道发生了什么，也不会想到要自己去拷目录。所以"改位置"从此必须连着"搬数据"，
/// 而这里负责在动手之前把该拦的都拦下来。
///
/// <para><b>为什么同盘搬移也要求足够空间</b></para>
/// 同一个卷内换目录，<c>Directory.Move</c> 只是一次改名，既快又不需要额外空间。
/// <b>本类刻意不给它开这条路</b>：改名没有"复制完成"这个可校验的中间态，
/// 一旦中途失败（跨挂载点、句柄占用、权限），留下的是一棵搬了一半、既没有墓碑
/// 也没法校验的目录树——正是"半吊子状态"本身。而且它会给崩溃恢复多出一整套
/// 独立的判据。搬移走"复制 → 校验 → 写墓碑 → 删源"一条路，代价是同盘搬移也要
/// 临时占用一份等量空间，换来的是断电不丢数据只有一套逻辑要维护。
///
/// <para><b>嵌套关系必须拦</b></para>
/// 目标在源里面（<c>D:\Mods</c> → <c>D:\Mods\New</c>）会让递归复制自己吃自己；
/// 源在目标里面（<c>D:\Mods\Repo</c> → <c>D:\Mods</c>）会让"删源"这一步删到刚复制过去的东西。
/// 两种都不是用户会故意做的事，但目录选择器点两下就能选出来，而后果都是丢数据。
/// </summary>
public static class RepositoryRelocationPlanner
{
    /// <summary>判定。</summary>
    public static RepositoryRelocationPlan Plan(RepositoryRelocationProbe probe)
    {
        var source = probe.SourceRoot?.Trim() ?? string.Empty;
        var target = probe.TargetRoot?.Trim() ?? string.Empty;

        if (target.Length == 0)
        {
            return Blocked(RepositoryRelocationBlocker.TargetUnusable, source, target,
                "没有给出目标位置");
        }

        if (!probe.TargetUsable)
        {
            return Blocked(RepositoryRelocationBlocker.TargetUnusable, source, target,
                "目标位置本身不可用（上游位置校验判定为 Blocked）");
        }

        // 相同位置最先判：它既不该被算成"没东西可搬"，也不该走进任何嵌套判断
        // （一个路径当然"包含"它自己）。
        if (VolumePaths.IsSameOrInside(source, target) && VolumePaths.IsSameOrInside(target, source))
        {
            return new RepositoryRelocationPlan(
                RepositoryRelocationAction.None, RepositoryRelocationBlocker.None,
                source, target, 0, PayloadSizeKnown: true, 0,
                DiskSpacePrecheck.Evaluate(0, probe.TargetAvailableBytes),
                "目标就是当前位置，什么都不用做");
        }

        if (VolumePaths.IsSameOrInside(source, target))
        {
            return Blocked(RepositoryRelocationBlocker.TargetInsideSource, source, target,
                "目标在当前仓库里面，递归复制会自己吃自己");
        }

        if (VolumePaths.IsSameOrInside(target, source))
        {
            return Blocked(RepositoryRelocationBlocker.SourceInsideTarget, source, target,
                "当前仓库在目标里面，删源那一步会删掉刚复制过去的数据");
        }

        // 仓库是空的：只改指针。放在空间预检之前——没东西要搬就没有"装不装得下"这回事，
        // 而一个空仓库因为"目标盘只剩 30 MB"被拦下来纯属荒唐。
        //
        // 也放在"目标非空"之前：什么都不搬就没有回退，也就没有"清空目标"这个动作，
        // 那条判据在这里根本不成立。老用户把仓库指到一个装着上一版仓库的目录上、
        // 而当前仓库恰好是空的，正是这条顺序在照顾的情形。
        if (!probe.SourceHasContent)
        {
            return new RepositoryRelocationPlan(
                RepositoryRelocationAction.PointerOnly, RepositoryRelocationBlocker.None,
                source, target, 0, PayloadSizeKnown: true, 0,
                DiskSpacePrecheck.Evaluate(0, probe.TargetAvailableBytes),
                "当前仓库里没有东西，只改存放位置");
        }

        if (probe.TargetHasContent)
        {
            return Blocked(RepositoryRelocationBlocker.TargetNotEmpty, source, target,
                "目标落点里已经有别的东西，回退时无法安全清空，拒绝搬移");
        }

        var check = DiskSpacePrecheck.Evaluate(
            probe.PayloadBytes ?? 0, probe.TargetAvailableBytes);

        if (probe.PayloadBytes is not null && check.IsBlocking)
        {
            return new RepositoryRelocationPlan(
                RepositoryRelocationAction.Blocked, RepositoryRelocationBlocker.InsufficientSpace,
                source, target, probe.PayloadBytes.Value, PayloadSizeKnown: true,
                Math.Max(0, probe.PayloadPackageCount), check,
                "目标盘装不下，提前拦下，目标位置一个字节都不写");
        }

        return new RepositoryRelocationPlan(
            RepositoryRelocationAction.MoveThenPoint, RepositoryRelocationBlocker.None,
            source, target, probe.PayloadBytes ?? 0, probe.PayloadBytes is not null,
            Math.Max(0, probe.PayloadPackageCount), check,
            probe.PayloadBytes is null
                ? "体积估不出来，照常搬移（预检不该比它保护的复制更容易失败）"
                : "空间够，开始搬移");
    }

    private static RepositoryRelocationPlan Blocked(
        RepositoryRelocationBlocker blocker, string source, string target, string reason)
        => new(RepositoryRelocationAction.Blocked, blocker, source, target,
            0, PayloadSizeKnown: false, 0,
            new DiskSpaceCheck(DiskSpaceDecision.Unknown, 0, 0, null), reason);

    /// <summary>
    /// 还差多少字节才够。<see cref="RepositoryRelocationBlocker.InsufficientSpace"/> 时才有意义，
    /// 其余情况返回 0。
    ///
    /// <para>
    /// 单独一个方法而不是塞进 <see cref="DiskSpaceCheck"/>：那个类型服务的是日志
    /// （"需要 X，可用 Z"，让排障的人自己减），而给用户看的必须是<b>已经减好的那个数</b>
    /// ——"还差 3.2 GiB"他知道该清多少，"需要 12 GiB、可用 8.8 GiB"他得自己算。
    /// </para>
    /// </summary>
    public static long ShortfallBytes(RepositoryRelocationPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (plan.Blocker != RepositoryRelocationBlocker.InsufficientSpace) return 0;

        var available = plan.SpaceCheck.AvailableBytes ?? 0;
        var required = plan.SpaceCheck.RequiredWithHeadroomBytes;
        return required > available ? required - available : 0;
    }
}

/// <summary>搬移进行到哪一步。顺序即 <c>DataRelocationExecutor</c> 四步搬移的顺序。</summary>
public enum RepositoryRelocationStage
{
    /// <summary>还在数要搬多少。</summary>
    Preparing,

    /// <summary>正在复制。整个流程里唯一耗时可能到几十分钟的一步。</summary>
    Copying,

    /// <summary>正在核对复制结果（文件数、总字节数）。</summary>
    Verifying,

    /// <summary>正在落定：写墓碑、改存放位置。<b>过了这一步就不能取消了。</b></summary>
    Committing,

    /// <summary>正在清理旧位置。</summary>
    CleaningUp,

    /// <summary>搬完了。</summary>
    Done,
}

/// <summary>
/// 一次进度快照。
///
/// <para>
/// 做成不可变的记录而不是让 UI 直接读服务字段：复制跑在后台线程上，
/// 进度要跨线程丢给 <c>Dispatcher</c>，可变对象在半路被改写会让界面显示出
/// 一个从来没真实存在过的状态（复制了 8 GB / 总共 0 字节）。
/// </para>
/// </summary>
/// <param name="Stage">当前步骤。</param>
/// <param name="CopiedBytes">已复制字节数。</param>
/// <param name="TotalBytes">总字节数；估不出来时为 0。</param>
/// <param name="CopiedFiles">已复制文件数。</param>
/// <param name="TotalFiles">总文件数；数不出来时为 0。</param>
public readonly record struct RepositoryRelocationProgress(
    RepositoryRelocationStage Stage,
    long CopiedBytes,
    long TotalBytes,
    int CopiedFiles,
    int TotalFiles)
{
    /// <summary>
    /// 复制这一步在整条进度条里占的比例。
    ///
    /// <para>
    /// 剩下的 15% 留给校验、落定与清理。<b>不是因为它们真的占 15% 的时间</b>——
    /// 校验是一遍目录枚举、清理是一批删除，相对复制（O(字节)）几乎不花钱。
    /// 留出来是因为一条走到 100% 之后还要停留一段时间的进度条，用户读出来的是"卡死了"；
    /// 而反过来把它们压成 0，界面会在 100% 处凝固几秒再关掉，同样像卡住。
    /// </para>
    /// </summary>
    public const double CopyShare = 0.85;

    /// <summary>整体百分比（0–100）。</summary>
    public double Percent => Stage switch
    {
        RepositoryRelocationStage.Preparing => 0,
        RepositoryRelocationStage.Copying => CopyFraction * CopyShare * 100,
        RepositoryRelocationStage.Verifying => (CopyShare + 0.08) * 100,
        RepositoryRelocationStage.Committing => (CopyShare + 0.11) * 100,
        RepositoryRelocationStage.CleaningUp => (CopyShare + 0.13) * 100,
        _ => 100,
    };

    /// <summary>
    /// 复制完成的比例（0–1）。<b>优先按字节算</b>：包实体的大小差几个数量级
    /// （一个 3 KB 的 ini 和一个 8 GB 的 pak 各算"一个文件"），
    /// 按文件数算出来的进度条会在大文件上停死半天再突然跳一大截。
    /// 字节总数估不出来时才退回按文件数，两者都没有就停在 0（界面此时应显示为未知进度）。
    /// </summary>
    public double CopyFraction
    {
        get
        {
            if (TotalBytes > 0) return Math.Clamp(CopiedBytes / (double)TotalBytes, 0, 1);
            if (TotalFiles > 0) return Math.Clamp(CopiedFiles / (double)TotalFiles, 0, 1);
            return 0;
        }
    }

    /// <summary>
    /// 总量根本估不出来（既没有字节数也没有文件数）。
    /// 界面据此把进度条切成来回滚动的未知模式——显示一个永远停在 0% 的确定进度条，
    /// 用户读出来的就是"卡住了"。
    /// </summary>
    public bool IsIndeterminate
        => Stage == RepositoryRelocationStage.Copying && TotalBytes <= 0 && TotalFiles <= 0;

    /// <summary>
    /// 给用户看的一行状态。刻意不含文件名与路径：一行飞速滚动的路径对普通玩家没有信息量，
    /// 却会让窗口宽度随机跳动，还容易把 MOD 名字截成一半。
    /// </summary>
    public string StatusText => Stage switch
    {
        RepositoryRelocationStage.Preparing => "正在看看有多少要搬…",
        RepositoryRelocationStage.Copying => TotalBytes > 0
            ? $"正在搬… {DiskSpacePrecheck.Humanize(CopiedBytes)} / {DiskSpacePrecheck.Humanize(TotalBytes)}"
            : "正在搬…",
        RepositoryRelocationStage.Verifying => "正在核对，确认一个文件都没少…",
        RepositoryRelocationStage.Committing => "快好了，正在切换到新位置…",
        RepositoryRelocationStage.CleaningUp => "正在清空原来的位置…",
        _ => "搬完了",
    };
}

/// <summary>
/// 搬移相关的用户可见文案。
///
/// <para>
/// 与 <c>DeploymentDegradationNotice</c> 同一条规矩：文案连同它依赖的数字一起放在 Core，
/// 于是"还差多少 GB"这种最容易写反的句子能被单测钉住。写给<b>普通玩家</b>——
/// 不出现"卷""指针""墓碑"，盘符只用"D 盘"这一种说法，技术细节留在日志里。
/// </para>
/// </summary>
public static class RepositoryRelocationMessages
{
    /// <summary>把 <c>D:\Mods</c> 说成"D 盘"；说不清就退回原路径，绝不编一个"未知盘"。</summary>
    public static string DescribeWhere(string path)
        => VolumePaths.TryDescribeVolume(path) ?? path;

    /// <summary>确认阶段的标题。</summary>
    public static string ConfirmTitle(RepositoryRelocationPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        return $"把 MOD 挪到{DescribeWhere(plan.TargetRoot)}";
    }

    /// <summary>
    /// 确认阶段的正文：要搬多少、搬完有什么好处、要多久。
    /// 空仓库（<see cref="RepositoryRelocationAction.PointerOnly"/>）不会走到这里
    /// ——那条路径连窗口都不弹。
    /// </summary>
    public static string ConfirmBody(RepositoryRelocationPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var what = plan.PayloadPackageCount > 0
            ? $"{plan.PayloadPackageCount} 个 MOD"
            : "已导入的 MOD";
        var size = plan.PayloadSizeKnown
            ? $"，一共 {DiskSpacePrecheck.Humanize(plan.PayloadBytes)}"
            : string.Empty;

        return $"程序会把你{what}{size}整个搬到{DescribeWhere(plan.TargetRoot)}，"
            + "搬完自动切换过去，你不用手动拷任何文件。"
            + Environment.NewLine + Environment.NewLine
            + "搬的过程中先复制、核对无误之后才删掉原来的那份，"
            + "所以中途停下或者断电都不会丢东西。"
            + Environment.NewLine + Environment.NewLine
            + "东西多的话可能要几分钟到十几分钟，期间请不要装 MOD。";
    }

    /// <summary>被拦下时给用户的话。每一条都必须告诉他<b>下一步做什么</b>。</summary>
    public static string BlockedBody(RepositoryRelocationPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        switch (plan.Blocker)
        {
            case RepositoryRelocationBlocker.InsufficientSpace:
            {
                var shortfall = RepositoryRelocationPlanner.ShortfallBytes(plan);
                return $"{DescribeWhere(plan.TargetRoot)}的空间不够，还差 "
                    + $"{DiskSpacePrecheck.Humanize(shortfall)}。"
                    + Environment.NewLine + Environment.NewLine
                    + $"你的 MOD 一共 {DiskSpacePrecheck.Humanize(plan.PayloadBytes)}，"
                    + "搬的时候要先复制过去再删掉原来那份，所以目标盘得先腾出这么多地方。"
                    + Environment.NewLine + Environment.NewLine
                    + "腾一点空间之后再来一次，或者换一个更宽裕的盘。"
                    + "现在什么都没动，你的 MOD 还在原来的位置。";
            }

            case RepositoryRelocationBlocker.TargetInsideSource:
                return "新位置在现在这个文件夹里面，这样搬会把文件夹搬进它自己。"
                    + Environment.NewLine + Environment.NewLine
                    + "请挑一个在它之外的文件夹，或者干脆换一个盘。";

            case RepositoryRelocationBlocker.SourceInsideTarget:
                return "新位置把现在这个文件夹包在里面了，这样搬完清理旧位置时会把刚搬好的文件删掉。"
                    + Environment.NewLine + Environment.NewLine
                    + "请挑一个和它没有上下级关系的文件夹，或者干脆换一个盘。";

            case RepositoryRelocationBlocker.TargetNotEmpty:
                return "新位置那个文件夹里已经有别的东西了。"
                    + Environment.NewLine + Environment.NewLine
                    + "为了不动到你原有的文件，程序不会往里搬。请挑一个空文件夹，"
                    + "或者新建一个文件夹再选它。现在什么都没动，你的 MOD 还在原来的位置。";

            default:
                return "这个位置用不了，请换一个。现在什么都没动，你的 MOD 还在原来的位置。";
        }
    }

    /// <summary>
    /// 搬完之后的话。
    ///
    /// <para>
    /// <b>必须提醒重新部署</b>，这是整条流程里最容易被忽略、后果又最难自查的一件事：
    /// 已经装进游戏目录的文件要么是副本、要么是指向<b>旧仓库</b>的硬链接。
    /// 硬链接指向的是磁盘上的数据本体而不是路径，删掉旧仓库里那一份只是少了一个名字，
    /// 数据仍被游戏目录里的链接引用着——所以<b>游戏照样能玩，MOD 不会坏</b>。
    /// 代价是那份数据被钉在旧盘上（空间没真的省下来），而新仓库里的那一份成了第三份拷贝。
    /// 重新部署一次就能把游戏目录里的链接重新指到新仓库，旧盘上的占用随之释放。
    /// </para>
    /// </summary>
    public static string SuccessBody(RepositoryRelocationPlan plan, bool anyDeployed)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var body = $"MOD 已经搬到{DescribeWhere(plan.TargetRoot)}了"
            + (plan.PayloadSizeKnown
                ? $"，一共 {DiskSpacePrecheck.Humanize(plan.PayloadBytes)}。"
                : "。")
            + Environment.NewLine + Environment.NewLine
            + "原来的位置已经清空，以后导入的 MOD 都会存到新位置。";

        if (anyDeployed)
        {
            body += Environment.NewLine + Environment.NewLine
                + "游戏现在可以照常玩，已经装好的 MOD 不会失效。不过它们还连着原来那个盘，"
                + "那部分空间要等你把 MOD 重新装一次（在列表里关掉再打开）才会真正腾出来。";
        }

        return body;
    }

    /// <summary>
    /// 用户中途点了"停下来"之后的话。
    /// 重点只有一句：<b>你的东西一个都没少</b>。取消发生在"删源"之前，
    /// 旧位置始终是完整且唯一被指向的那一份。
    /// </summary>
    public static string CancelledBody(RepositoryRelocationPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        return "已经停下来了。你的 MOD 一个都没少，还在原来的位置，存放位置也没有改。"
            + Environment.NewLine + Environment.NewLine
            + $"刚才复制到{DescribeWhere(plan.TargetRoot)}的那部分已经清掉了，不会占地方。"
            + Environment.NewLine + Environment.NewLine
            + "想换位置的话随时可以再来一次。";
    }

    /// <summary>搬移失败之后的话。同样以"东西没丢"开头——那是用户此刻唯一想知道的事。</summary>
    public static string FailureBody(RepositoryRelocationPlan plan, string detail)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var reason = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"（{detail.Trim()}）";
        return $"没搬成{reason}。"
            + Environment.NewLine + Environment.NewLine
            + "你的 MOD 一个都没少，还在原来的位置，存放位置也没有改，可以照常继续玩。"
            + Environment.NewLine + Environment.NewLine
            + "常见原因是目标盘被占用或者没有写入权限。换一个文件夹再试一次，或者先这样用着。";
    }
}
