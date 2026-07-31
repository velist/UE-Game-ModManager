using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Tests.Services;

/// <summary>
/// 仓库搬移的 IO 编排层。
///
/// <para>
/// <b>这一层的每一条用例都是在证明"用户的 MOD 没丢"。</b>被替换掉的功能只有一行
/// <c>SetRepositoryRoot</c>——只改指针不搬数据，用户改完位置界面上 MOD 全没了。
/// 所以断言的重点不是"搬成了没有"，而是"任何一条失败路径上，旧位置的数据是不是还完好、
/// 存放位置是不是原封不动"。
/// </para>
///
/// <para>
/// <b>绝不触碰开发机真实的 <c>%LOCALAPPDATA%</c> / <c>%APPDATA%</c>。</b>
/// 偏好由 <see cref="FakePreferences"/> 全量替换（内存），仓库根由
/// <c>ObjectStore</c> 的"指定路径构造"指到临时目录，磁盘可用空间由
/// <see cref="FakeFreeSpace"/> 给。本项目已经因为这件事踩过三次坑，其中一次在开发者
/// 真实目录里留了 594 个孤儿文件。
/// </para>
/// </summary>
public sealed class RepositoryRelocationServiceTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;

    private readonly string _root;
    private readonly string _source;
    private readonly string _target;
    private readonly FakePreferences _prefs = new();
    private readonly FakeFreeSpace _freeSpace = new(500 * Gib);

    public RepositoryRelocationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm_move_" + Guid.NewGuid().ToString("N")[..8]);
        _source = Path.Combine(_root, "source");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ─── 脚手架 ───

    private ObjectStore CreateStore(string? root = null)
        => new(NullLogger<ObjectStore>.Instance, root ?? _source);

    private RepositoryRelocationService CreateService(ObjectStore store)
        => new(NullLogger<RepositoryRelocationService>.Instance, () => store, _prefs, _freeSpace);

    /// <summary>造一个像真仓库的目录：几个包，每个包有 manifest 和 files。</summary>
    private void SeedRepository(string root, int packages = 3, int filesPerPackage = 2)
    {
        for (var p = 0; p < packages; p++)
        {
            var pkg = Path.Combine(root, $"pkg{p}");
            Directory.CreateDirectory(Path.Combine(pkg, "files"));
            File.WriteAllText(Path.Combine(pkg, "manifest.json"), $"{{\"key\":\"pkg{p}\"}}");
            for (var f = 0; f < filesPerPackage; f++)
            {
                File.WriteAllText(Path.Combine(pkg, "files", $"mod{f}.pak"),
                    new string((char)('a' + p), 512 + f));
            }
        }
    }

    /// <summary>目录下每个文件的相对路径 → 内容，用于逐字节比对。</summary>
    private static Dictionary<string, string> SnapshotContents(string root)
    {
        if (!Directory.Exists(root)) return new Dictionary<string, string>();

        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !DataRelocationExecutor.IsMigratorMetadata(f))
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════
    //  搬成了
    // ═════════════════════════════════════

    [Fact]
    public async Task 搬移后新位置与旧位置逐字节一致()
    {
        SeedRepository(_source);
        var before = SnapshotContents(_source);
        var store = CreateStore();

        var service = CreateService(store);
        var outcome = await service.ExecuteAsync(service.Plan(_target));

        Assert.Equal(RepositoryRelocationStatus.Moved, outcome.Status);

        var after = SnapshotContents(_target);
        Assert.Equal(before.Count, after.Count);
        Assert.NotEmpty(after);
        foreach (var (relative, content) in before)
        {
            Assert.True(after.TryGetValue(relative, out var moved), $"新位置缺了 {relative}");
            Assert.Equal(content, moved);
        }
    }

    [Fact]
    public async Task 搬成之后存放位置才切换()
    {
        SeedRepository(_source);
        var store = CreateStore();
        var service = CreateService(store);

        await service.ExecuteAsync(service.Plan(_target));

        // 三处都要跟着走，缺任何一处用户下次启动都会看到一个空仓库
        Assert.Equal(_target, store.RepositoryRoot);
        Assert.Equal(_target, _prefs.RepositoryRoot);
    }

    [Fact]
    public async Task 搬完旧位置只剩墓碑()
    {
        SeedRepository(_source);
        var store = CreateStore();
        var service = CreateService(store);

        await service.ExecuteAsync(service.Plan(_target));

        // 墓碑刻意保留：整个删掉的话下次启动会因"旧位置不存在"判定为未迁移过，
        // 结论虽然相同，但排障时看不出这里发生过什么
        Assert.True(File.Exists(
            DataRelocationExecutor.GetTombstonePath(_source, isFile: false)));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_source));
    }

    [Fact]
    public async Task 搬完之后日记被划掉()
    {
        SeedRepository(_source);
        var store = CreateStore();
        var service = CreateService(store);

        await service.ExecuteAsync(service.Plan(_target));

        Assert.Equal(RepositoryRelocationPhase.Idle, _prefs.Journal.Phase);
    }

    [Fact]
    public async Task 搬移期间能看到进度且走到百分之百()
    {
        SeedRepository(_source, packages: 4, filesPerPackage: 3);
        var store = CreateStore();
        var service = CreateService(store);
        var reports = new List<RepositoryRelocationProgress>();

        await service.ExecuteAsync(service.Plan(_target),
            new Progress<RepositoryRelocationProgress>(p => { lock (reports) reports.Add(p); }));

        // Progress<T> 在无同步上下文的测试里是异步投递的，等一小会儿让回调排完
        await Task.Delay(200);

        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.Contains(reports, r => r.Stage == RepositoryRelocationStage.Copying);
            Assert.Contains(reports, r => r.Stage == RepositoryRelocationStage.Done);
        }
    }

    // ═════════════════════════════════════
    //  空仓库快速路径
    // ═════════════════════════════════════

    [Fact]
    public async Task 空仓库只改指针不产生任何搬移痕迹()
    {
        Directory.CreateDirectory(_source);
        var store = CreateStore();
        var service = CreateService(store);

        var plan = service.Plan(_target);
        Assert.Equal(RepositoryRelocationAction.PointerOnly, plan.Action);

        var outcome = await service.ExecuteAsync(plan);

        Assert.Equal(RepositoryRelocationStatus.PointerOnly, outcome.Status);
        Assert.Equal(_target, store.RepositoryRoot);
        // 没搬就不该有墓碑，也不该在偏好里留下一条永远不会被消费的日记
        Assert.False(File.Exists(
            DataRelocationExecutor.GetTombstonePath(_source, isFile: false)));
        Assert.Equal(RepositoryRelocationPhase.Idle, _prefs.Journal.Phase);
    }

    [Fact]
    public async Task 目标就是当前位置时什么都不做()
    {
        SeedRepository(_source);
        var store = CreateStore();
        var service = CreateService(store);

        var outcome = await service.ExecuteAsync(service.Plan(_source));

        Assert.Equal(RepositoryRelocationStatus.NothingToDo, outcome.Status);
        Assert.Null(_prefs.RepositoryRoot);
        Assert.True(DataRelocationExecutor.DirectoryHasContent(_source));
    }

    // ═════════════════════════════════════
    //  空间不足：提前拦下，一个字节都不写
    // ═════════════════════════════════════

    [Fact]
    public void 空间不足时提前拦下且目标一个字节都没写()
    {
        SeedRepository(_source);
        _freeSpace.AvailableBytes = 1024;   // 比源体积小得多
        var service = CreateService(CreateStore());

        var plan = service.Plan(_target);

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.InsufficientSpace, plan.Blocker);
        Assert.False(Directory.Exists(_target));
        Assert.True(RepositoryRelocationPlanner.ShortfallBytes(plan) > 0);
    }

    [Fact]
    public async Task 被拦下的计划就算硬塞给执行也什么都不动()
    {
        // 界面本该在按钮层面挡住，但现场可能在用户看完计划之后变了（盘被别的程序塞满）
        SeedRepository(_source);
        _freeSpace.AvailableBytes = 1024;
        var store = CreateStore();
        var service = CreateService(store);

        var outcome = await service.ExecuteAsync(service.Plan(_target));

        Assert.Equal(RepositoryRelocationStatus.Failed, outcome.Status);
        Assert.Equal(_source, store.RepositoryRoot);
        Assert.Null(_prefs.RepositoryRoot);
        Assert.True(DataRelocationExecutor.DirectoryHasContent(_source));
    }

    // ═════════════════════════════════════
    //  取消：状态必须完好
    // ═════════════════════════════════════

    [Fact]
    public async Task 取消之后旧位置完好且存放位置没动()
    {
        SeedRepository(_source, packages: 6, filesPerPackage: 4);
        var before = SnapshotContents(_source);
        var store = CreateStore();
        var service = CreateService(store);

        using var cts = new CancellationTokenSource();
        cts.Cancel();   // 复制的第一个取消检查点就会命中

        var outcome = await service.ExecuteAsync(service.Plan(_target), null, cts.Token);

        Assert.Equal(RepositoryRelocationStatus.Cancelled, outcome.Status);

        // 三条断言合起来才是"取消后状态完好"：数据一个字节没少、位置没改、目标没残留
        Assert.Equal(before, SnapshotContents(_source));
        Assert.Equal(_source, store.RepositoryRoot);
        Assert.Null(_prefs.RepositoryRoot);
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_target));
    }

    [Fact]
    public async Task 取消之后没有半份数据留在目标盘上()
    {
        // 取消的回退会清掉目标——但只清带着进行中标记的那种，
        // 这里的目标全是我们自己刚写的，所以必须被清干净
        SeedRepository(_source, packages: 8, filesPerPackage: 5);
        var store = CreateStore();
        var service = CreateService(store);

        using var cts = new CancellationTokenSource();
        var plan = service.Plan(_target);

        // 让复制真的跑起来几个文件再取消，逼出"半份数据"的现场
        var progress = new Progress<RepositoryRelocationProgress>(p =>
        {
            if (p.CopiedFiles >= 3) cts.Cancel();
        });

        var outcome = await service.ExecuteAsync(plan, progress, cts.Token);

        Assert.Equal(RepositoryRelocationStatus.Cancelled, outcome.Status);
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_target));
        Assert.True(DataRelocationExecutor.DirectoryHasContent(_source));
    }

    [Fact]
    public async Task 取消之后日记被划掉()
    {
        SeedRepository(_source);
        var service = CreateService(CreateStore());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await service.ExecuteAsync(service.Plan(_target), null, cts.Token);

        // 日记留着的话，下次启动会对着一个已经收拾干净的现场再判一次，
        // 日志里那条"发现在途搬移"会一直误导排障的人
        Assert.Equal(RepositoryRelocationPhase.Idle, _prefs.Journal.Phase);
    }

    // ═════════════════════════════════════
    //  失败：指针绝不能已经指向新位置
    // ═════════════════════════════════════

    [Fact]
    public async Task 日记写不下去时压根不开始搬()
    {
        SeedRepository(_source);
        var before = SnapshotContents(_source);
        _prefs.FailJournalWrites = true;
        var store = CreateStore();
        var service = CreateService(store);

        var outcome = await service.ExecuteAsync(service.Plan(_target));

        // 日记是断电之后唯一能认出"那两个路径"的东西，写不下去就说明这次搬移不该开始
        Assert.Equal(RepositoryRelocationStatus.Failed, outcome.Status);
        Assert.Equal(before, SnapshotContents(_source));
        Assert.Equal(_source, store.RepositoryRoot);
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public async Task 搬移中途失败时存放位置不动且旧数据完好()
    {
        SeedRepository(_source);
        var before = SnapshotContents(_source);
        var store = CreateStore();
        var service = CreateService(store);
        var plan = service.Plan(_target);

        // 造一个走不通的目标：先在落点建一个同名文件，CreateDirectory 会当场失败
        File.WriteAllText(_target, "占位");

        var outcome = await service.ExecuteAsync(plan);

        Assert.Equal(RepositoryRelocationStatus.Failed, outcome.Status);
        Assert.Equal(before, SnapshotContents(_source));
        Assert.Equal(_source, store.RepositoryRoot);
        Assert.Null(_prefs.RepositoryRoot);
    }

    [Fact]
    public void 目标已有别的内容时拒绝搬移()
    {
        // 回退要清空目标，而清空只有在目标里全是我们自己写的东西时才安全
        SeedRepository(_source);
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "用户自己的文件.txt"), "别删我");

        var service = CreateService(CreateStore());
        var plan = service.Plan(_target);

        Assert.Equal(RepositoryRelocationAction.Blocked, plan.Action);
        Assert.Equal(RepositoryRelocationBlocker.TargetNotEmpty, plan.Blocker);
        Assert.True(File.Exists(Path.Combine(_target, "用户自己的文件.txt")));
    }

    // ═════════════════════════════════════
    //  并发：搬移期间不许写仓库
    // ═════════════════════════════════════

    [Fact]
    public void 上闸期间导入会被明确拒绝而不是静默丢数据()
    {
        var store = CreateStore();
        using var gate = store.BeginRelocation();

        var ex = Assert.Throws<InvalidOperationException>(
            () => store.ThrowIfRelocating("导入 MOD"));

        // 用户看到的必须是一句能看懂的话，不是一个包被静默删掉
        Assert.Contains("正在搬移", ex.Message);
        Assert.Contains("导入 MOD", ex.Message);
    }

    [Fact]
    public void 开闸之后写入恢复正常()
    {
        var store = CreateStore();
        using (store.BeginRelocation())
        {
            Assert.True(store.IsRelocating);
        }

        Assert.False(store.IsRelocating);
        store.ThrowIfRelocating("导入 MOD");   // 不抛即通过
    }

    [Fact]
    public void 不允许两次搬移同时开始()
    {
        // 两个并发的搬移会各自往同一个目标复制，谁的墓碑先落地都是灾难
        var store = CreateStore();
        using var first = store.BeginRelocation();

        Assert.Throws<InvalidOperationException>(() => store.BeginRelocation());
    }

    [Fact]
    public async Task 搬移完成后闸门一定开回来()
    {
        SeedRepository(_source);
        var store = CreateStore();
        var service = CreateService(store);

        await service.ExecuteAsync(service.Plan(_target));

        Assert.False(store.IsRelocating);
    }

    [Fact]
    public async Task 搬移失败后闸门也一定开回来()
    {
        SeedRepository(_source);
        File.WriteAllText(_target, "占位");
        var store = CreateStore();
        var service = CreateService(store);

        await service.ExecuteAsync(service.Plan(_target));

        // 闸门漏开的表现是"从此再也导不进 MOD"，而且重启才能恢复
        Assert.False(store.IsRelocating);
    }

    // ═════════════════════════════════════
    //  断电自愈
    // ═════════════════════════════════════

    [Fact]
    public void 复制到一半断电_下次启动清掉残留且位置不动()
    {
        // 现场：日记停在搬移中、旧位置完好、目标有半份数据和进行中标记、没有墓碑
        SeedRepository(_source);
        var before = SnapshotContents(_source);

        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "半份.pak"), "xxx");
        File.WriteAllText(
            DataRelocationExecutor.GetInProgressMarkerPath(_target, isFile: false), "in progress");
        _prefs.Journal = new RepositoryRelocationJournal(
            RepositoryRelocationPhase.Moving, _source, _target);

        var plan = CreateService(CreateStore()).RecoverInterrupted();

        Assert.Equal(RepositoryRelocationRecoveryAction.RollBack, plan.Action);
        Assert.Equal(before, SnapshotContents(_source));
        Assert.Null(_prefs.RepositoryRoot);
        Assert.False(Directory.Exists(_target));
        Assert.Equal(RepositoryRelocationPhase.Idle, _prefs.Journal.Phase);
    }

    [Fact]
    public void 墓碑已写下时断电_下次启动把位置推过去并清空旧位置()
    {
        // 这是最关键的一格：数据已经在新位置并校验通过，而存放位置还指着旧位置。
        // 判错方向就会清掉那份唯一完整的副本。
        SeedRepository(_source);
        SeedRepository(_target);
        File.WriteAllText(
            DataRelocationExecutor.GetTombstonePath(_source, isFile: false), "已搬走");
        _prefs.Journal = new RepositoryRelocationJournal(
            RepositoryRelocationPhase.Moving, _source, _target);

        var plan = CreateService(CreateStore()).RecoverInterrupted();

        Assert.Equal(RepositoryRelocationRecoveryAction.RollForward, plan.Action);
        Assert.Equal(_target, _prefs.RepositoryRoot);
        Assert.True(DataRelocationExecutor.DirectoryHasContent(_target));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_source));
        Assert.Equal(RepositoryRelocationPhase.Idle, _prefs.Journal.Phase);
    }

    [Fact]
    public void 清理旧位置途中断电_下次启动继续清完()
    {
        SeedRepository(_source);
        SeedRepository(_target);
        File.WriteAllText(
            DataRelocationExecutor.GetTombstonePath(_source, isFile: false), "已搬走");
        _prefs.RepositoryRoot = _target;   // 存放位置已经改过去了
        _prefs.Journal = new RepositoryRelocationJournal(
            RepositoryRelocationPhase.Cleanup, _source, _target);

        var plan = CreateService(CreateStore(_target)).RecoverInterrupted();

        Assert.Equal(RepositoryRelocationRecoveryAction.ResumeCleanup, plan.Action);
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_source));
        Assert.True(DataRelocationExecutor.DirectoryHasContent(_target));
        Assert.Equal(_target, _prefs.RepositoryRoot);
    }

    [Fact]
    public void 目标有残留但没有进行中标记时不敢清()
    {
        // 没有标记却有内容，说明那个位置是被别人正常使用的——
        // 用户可能上次搬移失败后自己把仓库指到了那儿并往里导过 MOD。
        // 清空等于把用户的数据删光，判不准就宁可留一堆垃圾。
        SeedRepository(_source);
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "别人的数据.pak"), "重要");
        _prefs.Journal = new RepositoryRelocationJournal(
            RepositoryRelocationPhase.Moving, _source, _target);

        var plan = CreateService(CreateStore()).RecoverInterrupted();

        Assert.Equal(RepositoryRelocationRecoveryAction.RollBack, plan.Action);
        Assert.True(File.Exists(Path.Combine(_target, "别人的数据.pak")));
    }

    [Fact]
    public void 没有日记时恢复什么都不做()
    {
        SeedRepository(_source);
        var before = SnapshotContents(_source);

        var plan = CreateService(CreateStore()).RecoverInterrupted();

        Assert.Equal(RepositoryRelocationRecoveryAction.None, plan.Action);
        Assert.Equal(before, SnapshotContents(_source));
        Assert.Null(_prefs.RepositoryRoot);
    }

    [Fact]
    public void 恢复不解析ObjectStore()
    {
        // ObjectStore 构造时读一次存放位置就记进字段。恢复期间把它构造出来，
        // 紧接着推过去的新位置这次会话就不生效了——用户看到的是一个空仓库。
        SeedRepository(_source);
        SeedRepository(_target);
        File.WriteAllText(
            DataRelocationExecutor.GetTombstonePath(_source, isFile: false), "已搬走");
        _prefs.Journal = new RepositoryRelocationJournal(
            RepositoryRelocationPhase.Moving, _source, _target);

        var resolved = 0;
        var service = new RepositoryRelocationService(
            NullLogger<RepositoryRelocationService>.Instance,
            () => { resolved++; return CreateStore(); },
            _prefs, _freeSpace);

        service.RecoverInterrupted();

        Assert.Equal(0, resolved);
    }

    [Fact]
    public void 恢复过程出错也不阻断启动()
    {
        // 一次恢复判定把用户挡在主界面之外是完全不成比例的代价
        _prefs.Journal = new RepositoryRelocationJournal(
            RepositoryRelocationPhase.Moving, _source, _target);
        _prefs.FailJournalWrites = true;

        var plan = CreateService(CreateStore()).RecoverInterrupted();

        Assert.NotNull(plan);   // 不抛即通过
    }

    // ─── 替身 ───

    /// <summary>偏好的内存替身。真实的 <c>UiPreferences</c> 会写开发机的 ui_config.json。</summary>
    private sealed class FakePreferences : IRepositoryRelocationPreferences
    {
        public string? RepositoryRoot { get; set; }

        public RepositoryRelocationJournal Journal { get; set; } = RepositoryRelocationJournal.None;

        /// <summary>模拟"配置文件写不进去"。</summary>
        public bool FailJournalWrites { get; set; }

        public string? LoadRepositoryRoot() => RepositoryRoot;

        public RepositoryRelocationJournal LoadJournal() => Journal;

        public void SaveJournal(RepositoryRelocationJournal journal)
        {
            if (FailJournalWrites) throw new IOException("模拟：配置文件写不进去");
            Journal = journal;
        }

        public void Commit(string targetRoot, string sourceRoot)
        {
            if (FailJournalWrites) throw new IOException("模拟：配置文件写不进去");

            // 与生产实现一样是一次原子写：两个值要么都生效、要么都不生效
            RepositoryRoot = targetRoot;
            Journal = new RepositoryRelocationJournal(
                RepositoryRelocationPhase.Cleanup, sourceRoot, targetRoot);
        }

        public void ClearJournal() => Journal = RepositoryRelocationJournal.None;
    }

    /// <summary>可用空间的替身。真造一个满盘要挂 VHD，跨机器不可复现还留残留。</summary>
    private sealed class FakeFreeSpace(long available) : IFreeSpaceProbe
    {
        public long AvailableBytes { get; set; } = available;

        public long? TryGetAvailableFreeBytes(string path) => AvailableBytes;
    }
}
