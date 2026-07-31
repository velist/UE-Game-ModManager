using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Services.Paths;

namespace UEModManager.Services
{
    /// <summary>一次搬移的收场。</summary>
    public enum RepositoryRelocationStatus
    {
        /// <summary>目标就是当前位置，什么都没做。</summary>
        NothingToDo,

        /// <summary>仓库是空的，只改了存放位置。</summary>
        PointerOnly,

        /// <summary>数据已搬完，存放位置已切换。</summary>
        Moved,

        /// <summary>用户中途停下。数据完整留在旧位置，存放位置没动。</summary>
        Cancelled,

        /// <summary>失败。数据完整留在旧位置，存放位置没动。</summary>
        Failed,
    }

    /// <summary>一次搬移的结果。</summary>
    /// <param name="Status">收场。</param>
    /// <param name="Plan">当初的计划。</param>
    /// <param name="FailureDetail">失败原因（给用户看的一句话）；非失败时为 <c>null</c>。</param>
    public sealed record RepositoryRelocationOutcome(
        RepositoryRelocationStatus Status,
        RepositoryRelocationPlan Plan,
        string? FailureDetail = null)
    {
        /// <summary>存放位置是否真的换到新位置了。</summary>
        public bool Switched => Status is RepositoryRelocationStatus.PointerOnly
            or RepositoryRelocationStatus.Moved;
    }

    /// <summary>
    /// 把"换个地方存 MOD"做成一件事：<b>先搬数据，搬成了才改存放位置</b>。
    ///
    /// <para><b>为什么必须是一条路径</b></para>
    /// 此前设置界面上改仓库位置只有一行 <c>SetRepositoryRoot</c>——只改指针、不搬数据。
    /// 用户改完，已导入的包实体还躺在旧位置、新仓库是空的，界面上 MOD 全没了，
    /// 而他既不知道发生了什么，也不会想到要自己去拷目录。那个一行就能换掉位置的方法
    /// 已经<b>整个删掉</b>了（<c>ObjectStore.SetRepositoryRoot</c>）：只要它还在，
    /// 早晚会有第二处照着它写。现在改仓库位置只有本类这一条路，有守卫测试钉住。
    ///
    /// <para><b>复用的是 <c>Execute</c> 那一整套顺序，不是零件</b></para>
    /// <see cref="DataRelocationExecutor"/> 存在的全部价值就是
    /// "复制 → 校验 → 写墓碑 → 删源"这个顺序，以及夹在其中的一对进行中标记的写与清。
    /// 本类刻意<b>不</b>把 <c>CopyAndVerify</c> / <c>WriteTombstone</c> / <c>DeleteLegacy</c>
    /// 拆出来自己编排——那样就有了第二份必须永远和第一份保持一致的顺序表，
    /// 而这种表迟早会漏掉墓碑或漏掉标记，漏掉的那天正好是断电的那天。
    /// 进度与取消以一个可选的 <see cref="RelocationCopyContext"/> 穿过既有的那一处顺序；
    /// 阶段播报通过覆盖那几个 <c>virtual</c> 步骤方法完成——那正是它们被设计成
    /// <c>virtual</c> 而 <c>Execute</c> 不是的原因（可以换"某一步做了什么"，不能换顺序）。
    ///
    /// <para><b>失败与断电用的是同一张判定表</b></para>
    /// 搬移中途抛异常时，本类<b>不</b>自己推断该回退还是该推进，而是重新观测一次现场、
    /// 交给 <see cref="RepositoryRelocationRecoveryPlanner"/> 判——那正是下次启动会用的同一张表。
    /// 这一点很要紧：<c>Execute</c> 从外面看是原子的，但它内部的失败可能发生在写墓碑之前
    /// （数据还在旧位置，该回退）也可能发生在删源途中（数据已经在新位置并校验过，该推进）。
    /// 让"这次失败了"和"上次断电了"走同一段代码，两种情形就不可能给出不一致的结论。
    /// </summary>
    public sealed class RepositoryRelocationService
    {
        /// <summary>搬移项在日志里的名字。</summary>
        private const string ItemName = "MOD 存放位置";

        private readonly ILogger<RepositoryRelocationService> _logger;

        /// <summary>
        /// <b>惰性</b>取 <see cref="ObjectStore"/>，不在构造时求值。
        ///
        /// <para>
        /// 这不是风格问题，是时序问题。<see cref="RecoverInterrupted"/> 跑在启动早期，
        /// 而 <c>ObjectStore</c> 是 DI 单例、构造时读一次存放位置就记进字段，此后本次会话
        /// 不再回头看配置。若本服务在构造函数里注入 <c>ObjectStore</c>，那么"解析本服务"
        /// 就等于"构造 ObjectStore"——恢复紧接着把存放位置改到新位置，而那个已经构造好的
        /// 单例仍指着旧位置，用户这一整次会话看到的都是一个空仓库。
        /// 换成工厂之后，恢复路径一次都不会碰它（它只读偏好），
        /// 真正需要它的只有用户触发的搬移，那时主界面早就起来了。
        /// </para>
        /// </summary>
        private readonly Func<ObjectStore> _objectStoreAccessor;

        private readonly IRepositoryRelocationPreferences _preferences;
        private readonly IFreeSpaceProbe _freeSpace;

        public RepositoryRelocationService(
            ILogger<RepositoryRelocationService> logger, Func<ObjectStore> objectStoreAccessor)
            : this(logger, objectStoreAccessor, null, null)
        {
        }

        /// <summary>
        /// 测试用构造：把偏好与空间探针换成可控实现。标 <c>internal</c>，
        /// DI 只看得到上面那个公开构造，因此不存在"某处顺手 new 一个会写真实偏好的服务"。
        /// </summary>
        internal RepositoryRelocationService(
            ILogger<RepositoryRelocationService> logger,
            Func<ObjectStore> objectStoreAccessor,
            IRepositoryRelocationPreferences? preferences,
            IFreeSpaceProbe? freeSpace)
        {
            _logger = logger;
            _objectStoreAccessor = objectStoreAccessor
                ?? throw new ArgumentNullException(nameof(objectStoreAccessor));
            _preferences = preferences ?? UiPreferencesRelocationAdapter.Instance;
            _freeSpace = freeSpace ?? DriveFreeSpaceProbe.Instance;
        }

        private ObjectStore Store => _objectStoreAccessor();

        // ═══════════════════════════════════════════
        //  规划
        // ═══════════════════════════════════════════

        /// <summary>
        /// 算一次计划。<b>只读，不动任何东西</b>——界面拿它来显示"要搬多少、够不够地方"，
        /// 用户看完才决定点不点。
        /// </summary>
        /// <param name="resolvedTargetPath">
        /// 已经过 <see cref="RepositoryLocationValidator.ResolveRepositoryPath"/> 换算的落点。
        /// </param>
        /// <param name="targetUsable">上游位置校验的结论不是 Blocked。</param>
        public RepositoryRelocationPlan Plan(string? resolvedTargetPath, bool targetUsable = true)
        {
            var source = Store.RepositoryRoot;
            var estimate = DataRelocationExecutor.TryEstimatePayload(source, isFile: false);

            var plan = RepositoryRelocationPlanner.Plan(new RepositoryRelocationProbe(
                SourceRoot: source,
                TargetRoot: resolvedTargetPath,
                TargetUsable: targetUsable,
                SourceHasContent: DataRelocationExecutor.DirectoryHasContent(source),
                PayloadBytes: estimate.Bytes,
                PayloadPackageCount: estimate.TopLevelDirectoryCount,
                TargetAvailableBytes: _freeSpace.TryGetAvailableFreeBytes(resolvedTargetPath ?? source),
                TargetHasContent: DataRelocationExecutor.DirectoryHasContent(resolvedTargetPath ?? string.Empty)));

            _logger.LogInformation("[RepoMove] 计划：{Plan}", plan);
            return plan;
        }

        // ═══════════════════════════════════════════
        //  执行
        // ═══════════════════════════════════════════

        /// <summary>
        /// 按计划搬。<b>绝不抛异常</b>——失败与取消都以
        /// <see cref="RepositoryRelocationOutcome"/> 的形式返回，界面据此显示对应的收场文案。
        /// 一次"换存放位置"把主界面带崩是完全不成比例的代价。
        /// </summary>
        /// <param name="progress">
        /// 进度回调。<b>在后台线程上被调用</b>，界面侧必须自己丢回 Dispatcher
        /// （WPF 的 <c>Progress&lt;T&gt;</c> 会自动做这件事，因为它捕获了创建时的同步上下文）。
        /// </param>
        public async Task<RepositoryRelocationOutcome> ExecuteAsync(
            RepositoryRelocationPlan plan,
            IProgress<RepositoryRelocationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (plan is null) throw new ArgumentNullException(nameof(plan));

            switch (plan.Action)
            {
                case RepositoryRelocationAction.None:
                    return new RepositoryRelocationOutcome(
                        RepositoryRelocationStatus.NothingToDo, plan);

                case RepositoryRelocationAction.Blocked:
                    // 界面本该在按钮层面就挡住，走到这里说明现场在用户看完计划之后变了
                    // （盘被别的程序塞满了）。当作失败处理，什么都不动。
                    _logger.LogWarning("[RepoMove] 计划被拦下，什么都不做：{Plan}", plan);
                    return new RepositoryRelocationOutcome(
                        RepositoryRelocationStatus.Failed, plan,
                        RepositoryRelocationMessages.BlockedBody(plan));

                case RepositoryRelocationAction.PointerOnly:
                    return PointerOnly(plan);
            }

            return await MoveAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 空仓库的快速路径：没东西可搬，直接改存放位置。
        ///
        /// <para>
        /// <b>不写日记</b>：日记的用途是断电之后认出"哪两个路径"，而这里根本没有第二个位置
        /// ——一次偏好写入要么成功要么失败，没有中间态可恢复。给它写日记只会在
        /// <c>ui_config.json</c> 里留下一条永远不会被消费的记录。
        /// </para>
        /// </summary>
        private RepositoryRelocationOutcome PointerOnly(RepositoryRelocationPlan plan)
        {
            try
            {
                Directory.CreateDirectory(plan.TargetRoot);

                // 走 _preferences 而不是任何"顺手把配置也写了"的捷径：
                // 偏好这一层是可注入的，测试才不会真的改掉开发机的 ui_config.json
                // ——那一项一旦被改，开发者下次启动看到的就是一个空仓库。
                // 顺序仍然是先落盘、再改内存：反过来的话写盘失败时本次会话已经指向新目录，
                // 用户看到错误提示，但界面上 MOD 会全部"消失"，重启后又回到旧目录。
                _preferences.Commit(plan.TargetRoot, plan.SourceRoot);
                _preferences.ClearJournal();
                Store.AdoptRelocatedRoot(plan.TargetRoot);

                _logger.LogInformation("[RepoMove] 仓库为空，只改存放位置：{From} → {To}",
                    plan.SourceRoot, plan.TargetRoot);

                return new RepositoryRelocationOutcome(
                    RepositoryRelocationStatus.PointerOnly, plan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RepoMove] 改存放位置失败，仍指向旧位置 {Path}", plan.SourceRoot);
                return new RepositoryRelocationOutcome(
                    RepositoryRelocationStatus.Failed, plan, ex.Message);
            }
        }

        private async Task<RepositoryRelocationOutcome> MoveAsync(
            RepositoryRelocationPlan plan,
            IProgress<RepositoryRelocationProgress>? progress,
            CancellationToken cancellationToken)
        {
            IDisposable gate;
            try
            {
                // 上闸要在写日记之前：闸没上就先写日记，两个并发的搬移会各自留下一条日记，
                // 后写的那条把前一条覆盖掉，前一次搬移从此无人认领。
                gate = Store.BeginRelocation();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "[RepoMove] 已有搬移在进行中，本次拒绝");
                return new RepositoryRelocationOutcome(
                    RepositoryRelocationStatus.Failed, plan, ex.Message);
            }

            using (gate)
            {
                try
                {
                    // 日记先落盘，之后才允许往目标写第一个字节。反过来的话，
                    // "复制刚开始就断电"留下的残留没有任何记录认得出来。
                    _preferences.SaveJournal(new RepositoryRelocationJournal(
                        RepositoryRelocationPhase.Moving, plan.SourceRoot, plan.TargetRoot));
                }
                catch (Exception ex)
                {
                    // 日记写不下去就说明这次搬移不该开始：真断电了没人收拾得了。
                    _logger.LogError(ex, "[RepoMove] 搬移日记写不下去，本次不搬");
                    return new RepositoryRelocationOutcome(
                        RepositoryRelocationStatus.Failed, plan, ex.Message);
                }

                progress?.Report(new RepositoryRelocationProgress(
                    RepositoryRelocationStage.Preparing, 0, plan.PayloadBytes, 0, 0));

                try
                {
                    await Task.Run(() => RunFourSteps(plan, progress, cancellationToken),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 取消也走这里。该回退还是该推进不由本方法判断——重新观测一次现场，
                    // 交给下次启动会用的同一张表，两种情形就不可能给出不一致的结论。
                    var cancelled = ex is OperationCanceledException;
                    _logger.LogWarning(ex, "[RepoMove] 搬移{What}，按恢复判定收场",
                        cancelled ? "被用户停下" : "失败");

                    var recovered = RecoverInline(plan);
                    if (recovered.Switched)
                    {
                        // 罕见但真实：失败发生在墓碑之后（删源途中）。数据已经在新位置并校验过，
                        // 此时"回退"才是毁数据。恢复判定把它推完了，这次算搬成。
                        _logger.LogInformation("[RepoMove] 失败点在落定之后，已推进完成");
                        return recovered;
                    }

                    return new RepositoryRelocationOutcome(
                        cancelled ? RepositoryRelocationStatus.Cancelled : RepositoryRelocationStatus.Failed,
                        plan, cancelled ? null : ex.Message);
                }

                // 四步全过了：数据在新位置、墓碑已写、旧位置已清空。
                // 落定（改存放位置 + 推进日记）是一次原子写，理由见
                // UiPreferences.CommitRepositoryRelocation。
                progress?.Report(new RepositoryRelocationProgress(
                    RepositoryRelocationStage.Committing,
                    plan.PayloadBytes, plan.PayloadBytes, 0, 0));

                try
                {
                    _preferences.Commit(plan.TargetRoot, plan.SourceRoot);
                    Store.AdoptRelocatedRoot(plan.TargetRoot);
                }
                catch (Exception ex)
                {
                    // 数据已经在新位置且校验通过，只是存放位置这一下没写进去。
                    // 不谎报成功——本次会话仍指向旧位置（那里现在只剩墓碑，界面上 MOD 会是空的），
                    // 但下次启动的恢复判定会看到"旧位置有墓碑"并把它推完，数据一个都不会丢。
                    _logger.LogError(ex,
                        "[RepoMove] 数据已搬到 {To} 并校验通过，但存放位置没写进去；"
                        + "下次启动会自动补上", plan.TargetRoot);
                    return new RepositoryRelocationOutcome(
                        RepositoryRelocationStatus.Failed, plan,
                        ex.Message + "（数据已经安全搬到新位置，重启程序后会自动切换过去）");
                }

                _preferences.ClearJournal();

                progress?.Report(new RepositoryRelocationProgress(
                    RepositoryRelocationStage.Done,
                    plan.PayloadBytes, plan.PayloadBytes, 0, 0));

                _logger.LogInformation("[RepoMove] 搬移完成：{From} → {To}",
                    plan.SourceRoot, plan.TargetRoot);
                return new RepositoryRelocationOutcome(RepositoryRelocationStatus.Moved, plan);
            }
        }

        /// <summary>
        /// 跑那四步。<b>整个方法体在线程池线程上</b>——几十 GB 的复制绝不能占着 UI 线程，
        /// 而 <see cref="DataRelocationExecutor"/> 是同步 API（它做的是纯文件操作，
        /// 改成异步只会把同一份工作换个线程做，还得把顺序不变量重写一遍）。
        /// </summary>
        private void RunFourSteps(
            RepositoryRelocationPlan plan,
            IProgress<RepositoryRelocationProgress>? progress,
            CancellationToken cancellationToken)
        {
            long copied = 0;
            var files = 0;
            var total = plan.PayloadSizeKnown ? plan.PayloadBytes : 0;

            var context = new RelocationCopyContext(cancellationToken, bytes =>
            {
                // 回调在复制线程上、逐文件触发。Interlocked 不是为了并发（复制是单线程的），
                // 而是为了让这两个数在被 Report 读走时不会撕裂。
                var soFar = Interlocked.Add(ref copied, bytes);
                var count = Interlocked.Increment(ref files);

                // 复制完最后一个字节之后就报"正在核对"：校验是紧接着的下一步，
                // 而进度条停在 85% 一动不动的那几秒，用户读出来是"卡住了"。
                var stage = total > 0 && soFar >= total
                    ? RepositoryRelocationStage.Verifying
                    : RepositoryRelocationStage.Copying;

                progress?.Report(new RepositoryRelocationProgress(stage, soFar, total, count, 0));
            });

            var step = new RelocationStep(
                ItemName, RelocationKind.Relocate, plan.SourceRoot, plan.TargetRoot,
                RelocationAction.Copy, RelocationSkipReason.NotSkipped, "用户要求换一个位置存 MOD");

            var executor = new StageReportingExecutor(_logger, progress, total);

            // 一次调用，六个动作，顺序在执行器里：
            // 立进行中标记 → 复制 → 校验 → 写墓碑 → 清标记 → 删源。
            executor.Execute(step, isFile: false, excludedChildDirectories: null, copyContext: context);
        }

        /// <summary>
        /// 播报"写墓碑"与"删源"两步的开始。
        ///
        /// <para>
        /// 覆盖 <c>virtual</c> 的步骤方法正是它们被设计成 <c>virtual</c> 的用途
        /// ——换"某一步做了什么"（这里是"顺带报一次进度"），而不是换步骤的先后顺序。
        /// 顺序仍然只在 <see cref="DataRelocationExecutor.Execute"/> 那一处。
        /// </para>
        /// </summary>
        private sealed class StageReportingExecutor : DataRelocationExecutor
        {
            private readonly IProgress<RepositoryRelocationProgress>? _progress;
            private readonly long _total;

            internal StageReportingExecutor(
                ILogger? logger, IProgress<RepositoryRelocationProgress>? progress, long total)
                : base(logger)
            {
                _progress = progress;
                _total = total;
            }

            public override void WriteTombstone(RelocationStep step, bool isFile)
            {
                Report(RepositoryRelocationStage.Committing);
                base.WriteTombstone(step, isFile);
            }

            public override void DeleteLegacy(RelocationStep step, bool isFile,
                IReadOnlyCollection<string>? excludedChildDirectories = null)
            {
                Report(RepositoryRelocationStage.CleaningUp);
                base.DeleteLegacy(step, isFile, excludedChildDirectories);
            }

            private void Report(RepositoryRelocationStage stage)
                => _progress?.Report(new RepositoryRelocationProgress(stage, _total, _total, 0, 0));
        }

        // ═══════════════════════════════════════════
        //  断电恢复
        // ═══════════════════════════════════════════

        /// <summary>
        /// 启动时收拾上次被中断的搬移。<b>绝不抛异常</b>——它跑在启动早期，
        /// 一次恢复判定把用户挡在主界面之外是完全不成比例的代价；而且什么都不做是安全的
        /// （两边的数据此刻至少有一份是完整的），下次启动还会再判一次。
        ///
        /// <para>
        /// <b>时序：必须排在数据搬迁器之后、任何会拖出 <see cref="ObjectStore"/> 的解析之前。</b>
        /// 前者是因为搬迁器的原地登记会写存放位置；后者是因为 <c>ObjectStore</c> 构造时
        /// 读一次存放位置就记进字段，晚一步的话恢复推过去的新位置这次会话根本不生效
        /// ——用户看到的是一个空仓库。与首次运行引导同一组约束，有守卫测试钉住。
        /// </para>
        ///
        /// <para>
        /// 本方法<b>刻意不解析 <see cref="ObjectStore"/></b>，只写偏好，理由同上：
        /// 解析它就等于把它构造出来，正好把"第一次读配置"的时机提前到恢复内部。
        /// </para>
        /// </summary>
        /// <returns>做了什么，供启动日志记录。</returns>
        public RepositoryRelocationRecoveryPlan RecoverInterrupted()
        {
            try
            {
                var probe = ObserveRecovery(_preferences.LoadJournal(), CurrentConfiguredRoot());
                var plan = RepositoryRelocationRecoveryPlanner.Plan(probe);

                if (plan.Action != RepositoryRelocationRecoveryAction.None)
                {
                    _logger.LogWarning("[RepoMove] 发现上次未完成的搬移：{Plan}", plan);
                }

                Apply(plan, probe);
                return plan;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RepoMove] 恢复上次搬移时出错，本次什么都不做");
                return new RepositoryRelocationRecoveryPlan(
                    RepositoryRelocationRecoveryAction.None, null, null, "恢复过程出错");
            }
        }

        /// <summary>
        /// 搬移中途失败之后立刻收拾一次，用的是与 <see cref="RecoverInterrupted"/>
        /// 完全相同的判定与执行。差别只在于此刻 <see cref="ObjectStore"/> 已经在手上，
        /// 存放位置真的被改了要同步给它，不能等下次启动。
        /// </summary>
        private RepositoryRelocationOutcome RecoverInline(RepositoryRelocationPlan plan)
        {
            try
            {
                var probe = ObserveRecovery(
                    new RepositoryRelocationJournal(
                        RepositoryRelocationPhase.Moving, plan.SourceRoot, plan.TargetRoot),
                    Store.RepositoryRoot);

                var recovery = RepositoryRelocationRecoveryPlanner.Plan(probe);
                _logger.LogInformation("[RepoMove] 失败后的收场判定：{Plan}", recovery);

                Apply(recovery, probe);

                if (recovery.Action == RepositoryRelocationRecoveryAction.RollForward)
                {
                    Store.AdoptRelocatedRoot(plan.TargetRoot);
                    return new RepositoryRelocationOutcome(RepositoryRelocationStatus.Moved, plan);
                }
            }
            catch (Exception ex)
            {
                // 收拾失败只是留下一些垃圾，数据本身仍然完整（回退这一侧从不碰旧位置）。
                _logger.LogError(ex, "[RepoMove] 失败后的收场没做干净，残留留待下次启动清理");
            }

            return new RepositoryRelocationOutcome(RepositoryRelocationStatus.Failed, plan);
        }

        /// <summary>观测现场。IO 全在这里，判定全在 Core。</summary>
        private static RepositoryRelocationRecoveryProbe ObserveRecovery(
            RepositoryRelocationJournal journal, string? currentRoot)
        {
            if (!journal.IsActionable)
            {
                return new RepositoryRelocationRecoveryProbe(
                    journal, currentRoot, false, false, false, false);
            }

            var source = journal.SourceRoot!;
            var target = journal.TargetRoot!;

            return new RepositoryRelocationRecoveryProbe(
                Journal: journal,
                CurrentRoot: currentRoot,
                LegacyTombstoneExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetTombstonePath(source, isFile: false)),
                LegacyHasContent: DataRelocationExecutor.DirectoryHasContent(source),
                TargetInProgressMarkerExists: DataRelocationExecutor.FileExists(
                    DataRelocationExecutor.GetInProgressMarkerPath(target, isFile: false)),
                TargetHasContent: DataRelocationExecutor.DirectoryHasContent(target));
        }

        private void Apply(
            RepositoryRelocationRecoveryPlan plan, RepositoryRelocationRecoveryProbe probe)
        {
            var executor = new DataRelocationExecutor(_logger);
            var step = plan.SourceRoot is null || plan.TargetRoot is null
                ? null
                : new RelocationStep(ItemName, RelocationKind.Relocate,
                    plan.SourceRoot, plan.TargetRoot,
                    RelocationAction.Copy, RelocationSkipReason.NotSkipped, plan.Reason);

            switch (plan.Action)
            {
                case RepositoryRelocationRecoveryAction.None:
                    return;

                case RepositoryRelocationRecoveryAction.RollBack:
                    // 只清带着进行中标记的目标。没有标记却有内容说明那是被别人正常使用的位置，
                    // 清空等于把用户的数据删光——判不准就宁可留一堆垃圾。
                    if (step != null && RepositoryRelocationRecoveryPlanner.MayPurgeTarget(probe))
                    {
                        executor.PurgeTarget(step, isFile: false);
                        _logger.LogInformation("[RepoMove] 已清掉上次中断留在 {Path} 的半份数据",
                            plan.TargetRoot);
                    }
                    else if (step != null)
                    {
                        _logger.LogWarning(
                            "[RepoMove] {Path} 有内容但没有进行中标记，不敢清（可能是别的数据），留给用户自己处理",
                            plan.TargetRoot);
                    }
                    break;

                case RepositoryRelocationRecoveryAction.RollForward:
                    if (step == null) return;
                    // 顺序与正常路径一致：先落定（改存放位置 + 推进日记，一次原子写），
                    // 再清旧位置。反过来的话，清到一半断电会留下一个"存放位置还指着半空旧仓库"的状态。
                    _preferences.Commit(plan.TargetRoot!, plan.SourceRoot!);
                    executor.DeleteLegacy(step, isFile: false);
                    _logger.LogInformation("[RepoMove] 已把存放位置推进到 {Path} 并清空旧位置",
                        plan.TargetRoot);
                    break;

                case RepositoryRelocationRecoveryAction.ResumeCleanup:
                    if (step == null) return;
                    executor.DeleteLegacy(step, isFile: false);
                    _logger.LogInformation("[RepoMove] 已继续清空旧位置 {Path}", plan.SourceRoot);
                    break;
            }

            _preferences.ClearJournal();
        }

        /// <summary>
        /// 配置里此刻生效的仓库根。<b>不读 <see cref="ObjectStore"/></b>——
        /// 恢复跑在它被构造之前，理由见 <see cref="RecoverInterrupted"/>。
        /// </summary>
        private string CurrentConfiguredRoot()
            => _preferences.LoadRepositoryRoot() ?? AppPaths.RepositoryRoot;
    }

    /// <summary>
    /// 搬移要读写的那一小部分偏好。抽出接口的理由与 <c>IRepositorySetupPreferences</c>
    /// 完全一致：把 <see cref="UiPreferences"/> 这个静态全局挡在服务外面，
    /// 否则任何一条测试用例都会真的改掉开发者本机的仓库位置——而这一项一旦被改，
    /// <c>ObjectStore</c> 会跟着换目录，开发者界面上的 MOD 直接消失。
    /// </summary>
    internal interface IRepositoryRelocationPreferences
    {
        string? LoadRepositoryRoot();

        RepositoryRelocationJournal LoadJournal();

        /// <summary>写日记。<b>失败上抛</b>：写不下去就说明这次搬移不该开始。</summary>
        void SaveJournal(RepositoryRelocationJournal journal);

        /// <summary>落定：改存放位置 + 推进日记，<b>必须是一次原子写</b>。</summary>
        void Commit(string targetRoot, string sourceRoot);

        /// <summary>划掉日记。静默，失败只留痕。</summary>
        void ClearJournal();
    }

    /// <summary>
    /// <see cref="IRepositoryRelocationPreferences"/> 的生产实现，转调静态的
    /// <see cref="UiPreferences"/>。只是一层直通转发，没有自己的状态，故用单例
    /// （与 <c>UiPreferencesDataMigrationAdapter</c> 同一形态）。
    /// </summary>
    internal sealed class UiPreferencesRelocationAdapter : IRepositoryRelocationPreferences
    {
        internal static readonly UiPreferencesRelocationAdapter Instance = new();

        private UiPreferencesRelocationAdapter() { }

        public string? LoadRepositoryRoot() => UiPreferences.LoadRepositoryRoot();

        public RepositoryRelocationJournal LoadJournal()
            => UiPreferences.LoadRepositoryRelocationJournal();

        public void SaveJournal(RepositoryRelocationJournal journal)
            => UiPreferences.SaveRepositoryRelocationJournal(journal);

        public void Commit(string targetRoot, string sourceRoot)
            => UiPreferences.CommitRepositoryRelocation(targetRoot, sourceRoot);

        public void ClearJournal() => UiPreferences.ClearRepositoryRelocationJournal();
    }
}
