using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Tests.Services;

/// <summary>
/// 数据搬迁<b>编排层</b>的测试，对应迁移方案 §七 的 D0–D5 形态矩阵中一切
/// 不依赖 GUI 的部分（"应用启动到主界面""界面显示是否正确"留给真机）。
///
/// <para>
/// 这段编排逻辑至今一行都没被执行过：生产开关
/// <c>DataLocationMigrator.ProductionRelocationExecutionEnabled</c> 仍是 false，
/// 而它翻开的那一刻就会真的复制数据并删除旧位置。这里的用例就是那次"第一次执行"
/// 的前置演练——每一条都在临时目录上把整个迁移器跑一遍真实的文件操作。
/// </para>
///
/// <para>
/// <b>绝不触碰开发机真实的 <c>%LOCALAPPDATA%</c> / <c>%APPDATA%</c>。</b>
/// 迁移器的路径与偏好全部由 <see cref="DataMigrationEnvironment"/> 注入，
/// 本类构造的环境只指向 <see cref="_root"/> 下的临时目录，偏好用内存替身。
/// 这正是把 <c>AppPaths</c> / <c>UiPreferences</c> 两个静态全局从迁移器里挖出去的原因
/// —— 本项目已经踩过一次：<c>OverwriteStore</c> 的测试原先靠"测试宿主进程目录"的
/// 隐式隔离，路径归口之后就开始写真实 <c>%LOCALAPPDATA%</c> 了。
/// </para>
/// </summary>
public sealed class DataLocationMigratorTests : IDisposable
{
    // 目录名刻意不带点：DataLocationMigrator.IsFileItem 用 Path.HasExtension 区分文件与目录，
    // 临时目录里混进一个点会让"数据索引"被当成文件处理。
    private readonly string _root;

    /// <summary>假的安装目录（旧位置）。</summary>
    private readonly string _install;

    /// <summary>假的 <c>%LOCALAPPDATA%\UEModManager</c>（新位置）。</summary>
    private readonly string _local;

    /// <summary>假的 <c>%APPDATA%\UEModManager</c>（仓库/生成物的旧默认位置在此）。</summary>
    private readonly string _roaming;

    private readonly FakePreferences _prefs = new();

    public DataLocationMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm_mig_" + Guid.NewGuid().ToString("N")[..8]);
        _install = Path.Combine(_root, "install");
        _local = Path.Combine(_root, "local");
        _roaming = Path.Combine(_root, "roaming");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ─── 环境构造 ───

    /// <summary>与 <c>DataLocationMigrator.CreateProductionEnvironment</c> 逐项同构，只是根换成临时目录。</summary>
    private DataMigrationPaths BuildPaths() => new(
        LegacyConfigFile: Path.Combine(_install, "config.json"),
        ConfigFile: Path.Combine(_local, "config.json"),
        LegacyDeploymentBackupsDirectory: Path.Combine(_install, "Data", "Backups"),
        DeploymentBackupsDirectory: Path.Combine(_local, "Backups", "Deployments"),
        LegacyDataDirectory: Path.Combine(_install, "Data"),
        DataDirectory: Path.Combine(_local, "Data"),
        LegacyModBackupsDirectory: Path.Combine(_install, "Backups"),
        ModBackupsDirectory: Path.Combine(_local, "Backups", "Mods"),
        LegacyRepositoryRoot: Path.Combine(_roaming, "Repository"),
        LegacyOverwritesRoot: Path.Combine(_roaming, "Overwrites"));

    private DataLocationMigrator CreateMigrator(
        bool relocationEnabled = true, DataRelocationExecutor? executor = null,
        IFreeSpaceProbe? freeSpace = null)
        => new(NullLogger<DataLocationMigrator>.Instance,
            new DataMigrationEnvironment(BuildPaths(), _prefs, relocationEnabled),
            executor, freeSpace);

    private DataMigrationOutcome Run(
        bool relocationEnabled = true, DataRelocationExecutor? executor = null,
        IFreeSpaceProbe? freeSpace = null)
        => CreateMigrator(relocationEnabled, executor, freeSpace)
            .RunAsync().GetAwaiter().GetResult();

    // ─── 形态构造 ───

    private string LegacyData(string relativePath) => Path.Combine(_install, "Data", relativePath);
    private string LegacyModBackup(string relativePath) => Path.Combine(_install, "Backups", relativePath);
    private string LegacyDeployBackup(string relativePath)
        => Path.Combine(_install, "Data", "Backups", relativePath);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Escape(string path) => path.Replace(@"\", @"\\");

    /// <summary>
    /// D1 典型老用户：exe 旁有 config.json、Data 下有三个游戏索引、Backups 下有 MOD 备份，
    /// %APPDATA% 下有仓库与生成物。config.json 里的 BackupPath / GameIcons 是绝对路径，
    /// 指向即将被搬走的两个目录。
    /// </summary>
    private void SeedTypicalLegacyUser()
    {
        Write(Path.Combine(_install, "config.json"),
            $$"""
            {
              "GamePath": "D:\\Games\\Wukong",
              "BackupPath": "{{Escape(Path.Combine(_install, "Backups", "悟空_备份"))}}",
              "GameIcons": {
                "黑神话": "{{Escape(Path.Combine(_install, "Data", "GameIcons", "wukong.png"))}}"
              },
              "UnknownFutureField": 42
            }
            """);

        Write(LegacyData("wukong_mods.json"), """[{"Name":"MOD-A"},{"Name":"MOD-B"}]""");
        Write(LegacyData("wukong_profiles.json"), """[{"Name":"默认方案"}]""");
        Write(LegacyData("wukong_categories.json"), """{"MOD-A":["武器"]}""");
        Write(LegacyData(Path.Combine("GameIcons", "wukong.png")), "\u0089PNG-fake-bytes");
        Write(LegacyData(Path.Combine("LaunchSessions", "s1.json")), """{"At":"2026-07-01"}""");

        Write(LegacyModBackup(Path.Combine("悟空_备份", "old.pak")), "备份内容 with \r\n 混合换行\n");

        Write(Path.Combine(_roaming, "Repository", "pkg-1", "body.bin"), "包实体");
        Write(Path.Combine(_roaming, "Overwrites", "ow-1", "gen.bin"), "生成物");
    }

    /// <summary>部署事务备份（<c>Data\Backups</c>）—— 数据索引的子目录，但目标位置完全不同。</summary>
    private void SeedDeploymentBackups()
    {
        Write(LegacyDeployBackup("tx-001.json"), """{"Id":"tx-001","Files":["a.pak"]}""");
        Write(LegacyDeployBackup(Path.Combine("tx-001", "a.pak")), "被覆盖前的原文件");
    }

    // ─── 快照与比对 ───

    /// <summary>目录内容快照：相对路径 → 字节。用于逐字节一致性与"第二次是空操作"的断言。</summary>
    private static SortedDictionary<string, byte[]> SnapshotBytes(string root)
    {
        var snapshot = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return snapshot;

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            snapshot[Path.GetRelativePath(root, file)] = File.ReadAllBytes(file);
        }
        return snapshot;
    }

    /// <summary>
    /// "数据索引"这一项自己的内容快照 —— 顶层的 <c>Backups</c> 是"部署事务备份"那一项的地盘，
    /// 靠排除清单与数据索引解耦，不参与数据索引的搬移，自然也不该算进它的预期内容。
    /// </summary>
    private SortedDictionary<string, byte[]> SnapshotDataIndexOnly(string dataRoot)
    {
        var snapshot = SnapshotBytes(dataRoot);
        foreach (var key in snapshot.Keys
                     .Where(k => k.StartsWith("Backups" + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            snapshot.Remove(key);
        }
        return snapshot;
    }

    /// <summary>整棵树的"文件 → 字节 + 最后写入时间"快照。比只比内容更狠：连"白写一遍同样内容"都会被抓到。</summary>
    private static SortedDictionary<string, (long Length, DateTime WrittenUtc, byte[] Bytes)> SnapshotTree(string root)
    {
        var snapshot = new SortedDictionary<string, (long, DateTime, byte[])>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return snapshot;

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            snapshot[Path.GetRelativePath(root, file)] =
                (info.Length, info.LastWriteTimeUtc, File.ReadAllBytes(file));
        }
        return snapshot;
    }

    /// <summary>
    /// 同 <see cref="SnapshotTree"/>，但滤掉搬迁器自己的元数据（墓碑 / 已跳过标记 / 进行中标记）。
    /// 用于断言"旧位置的<b>数据</b>一个字节都没动"——留一张说明是允许的，动数据不是。
    /// </summary>
    private static SortedDictionary<string, (long Length, DateTime WrittenUtc, byte[] Bytes)>
        SnapshotDataOnly(string root)
    {
        var snapshot = SnapshotTree(root);
        foreach (var key in snapshot.Keys
                     .Where(k => DataRelocationExecutor.IsMigratorMetadata(k)).ToList())
        {
            snapshot.Remove(key);
        }
        return snapshot;
    }

    private static void AssertSameBytes(
        SortedDictionary<string, byte[]> expected, SortedDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (relative, bytes) in expected)
        {
            Assert.Equal(bytes, actual[relative]);
        }
    }

    private static void AssertTreeUnchanged(
        SortedDictionary<string, (long Length, DateTime WrittenUtc, byte[] Bytes)> before,
        SortedDictionary<string, (long Length, DateTime WrittenUtc, byte[] Bytes)> after)
    {
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (relative, expected) in before)
        {
            var actual = after[relative];
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected.Bytes, actual.Bytes);
            Assert.Equal(expected.WrittenUtc, actual.WrittenUtc);
        }
    }

    private bool DirectoryTombstoneExists(string legacyDirectory)
        => File.Exists(Path.Combine(legacyDirectory, DataRelocationExecutor.DirectoryTombstoneName));

    private bool SupersededMarkerExists(string legacyDirectory)
        => File.Exists(Path.Combine(
            legacyDirectory, DataRelocationExecutor.DirectorySupersededMarkerName));

    private IReadOnlyList<string> AllTombstones()
        => Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .Where(DataRelocationExecutor.IsTombstone).OrderBy(x => x).ToList()
            : Array.Empty<string>();

    /// <summary>某个目录下的数据文件相对路径（滤掉搬迁器元数据），用于"两处内容是否一致"的断言。</summary>
    private static IReadOnlyList<string> DataFileNames(string root)
        => Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !DataRelocationExecutor.IsMigratorMetadata(f))
                .Select(f => Path.GetRelativePath(root, f))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    // ─── 空间预检里"哪个目标"的判别 ───
    //
    // 临时根目录名形如 uemm_mig_<十六进制>，不含字母 d/m 组成的这些词，
    // 因此按路径片段区分目标是稳的。

    private const long Abundant = 1024L * 1024 * 1024 * 1024;   // 1 TiB，随便什么都装得下

    private static bool IsModBackupTarget(string path)
        => path.EndsWith(Path.Combine("Backups", "Mods"), StringComparison.OrdinalIgnoreCase);

    private static bool IsDataIndexTarget(string path)
        => path.EndsWith(Path.Combine("local", "Data"), StringComparison.OrdinalIgnoreCase);

    // ─── 生产默认值守卫 ───

    /// <summary>
    /// <b>这条用例的作用是拦住"顺手把开关翻开"。</b>翻开它意味着正式版会真的复制数据、
    /// 删除旧位置，那是一次需要 D0–D5 真机验收背书的决定，不该跟着某次重构溜进来。
    /// 真要翻开时，请连同本用例一起改，并在提交信息里写清真机验收结论。
    /// </summary>
    [Fact]
    public void 生产搬移开关必须保持关闭()
    {
        var field = typeof(DataLocationMigrator).GetField(
            "ProductionRelocationExecutionEnabled",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.False((bool)field!.GetRawConstantValue()!);
    }

    /// <summary>开关的默认值也必须是"不搬"：漏传参数时应该退到最保守的行为。</summary>
    [Fact]
    public void 环境的搬移开关默认关闭()
        => Assert.False(new DataMigrationEnvironment(BuildPaths(), _prefs).RelocationExecutionEnabled);

    /// <summary>
    /// 开关关闭时（= 当前生产行为）：搬移类动作一律推迟，磁盘一个字节都不动。
    /// 原地登记不受开关影响——它只写配置值，要么与当前行为等价，要么写的是还没有读取方的新键。
    /// </summary>
    [Fact]
    public void 开关关闭时不动任何文件且不打版本标记()
    {
        SeedTypicalLegacyUser();
        SeedDeploymentBackups();
        var before = SnapshotTree(_root);

        var outcome = Run(relocationEnabled: false);

        Assert.Equal(4, outcome.Deferred);              // 主配置 / 部署事务备份 / 数据索引 / MOD 备份
        Assert.Equal(2, outcome.Executed);              // 两项原地登记
        Assert.Equal(0, outcome.Failed);
        Assert.False(outcome.Completed);                // 有推迟项就不算完成
        Assert.Equal(0, _prefs.DataLayoutVersion);      // 因而绝不能打版本标记
        Assert.Empty(AllTombstones());
        AssertTreeUnchanged(before, SnapshotTree(_root));
    }

    // ─── D0 全新安装 ───

    [Fact]
    public void D0_全新安装不产生搬移与墓碑()
    {
        var outcome = Run();

        Assert.Equal(0, outcome.Executed);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(0, outcome.Deferred);
        Assert.True(outcome.Completed);
        Assert.Empty(AllTombstones());

        // 无旧数据时不该凭空建出新目录，也不该往偏好里写任何自定义位置
        Assert.False(Directory.Exists(_local));
        Assert.Null(_prefs.RepositoryRoot);
        Assert.Null(_prefs.OverwritesRoot);

        // 版本标记仍要打：否则每次启动都要重新探测一遍磁盘
        Assert.Equal(DataRelocationPlanner.CurrentLayoutVersion, _prefs.DataLayoutVersion);
    }

    [Fact]
    public void D0_连跑两次第二次是彻底的空操作()
    {
        Run();
        var after1 = SnapshotTree(_root);

        var outcome2 = Run();

        Assert.Equal(0, outcome2.Executed);
        Assert.Equal(1, _prefs.DataLayoutVersionSaveCount); // 第二次不再重复写标记
        AssertTreeUnchanged(after1, SnapshotTree(_root));
    }

    // ─── D1 典型老用户 ───

    [Fact]
    public void D1_老用户数据搬到新位置且逐字节一致()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));
        var modBackupsBefore = SnapshotBytes(Path.Combine(_install, "Backups"));

        var outcome = Run();

        Assert.Equal(0, outcome.Failed);
        Assert.Equal(0, outcome.Deferred);
        Assert.True(outcome.Completed);
        // 主配置 + 数据索引 + MOD 备份 + 包仓库登记 + 生成物登记；部署事务备份此形态下不存在
        Assert.Equal(5, outcome.Executed);
        Assert.Equal(1, outcome.Skipped);

        // 条数与内容都要对得上——只比条数抓不到"复制了但内容变了"
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        AssertSameBytes(modBackupsBefore, SnapshotBytes(Path.Combine(_local, "Backups", "Mods")));
        Assert.Equal(5, dataBefore.Count);

        // 旧位置留墓碑，数据已清空
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Backups")));
        Assert.True(File.Exists(Path.Combine(_install, "config.json")
            + DataRelocationExecutor.FileTombstoneSuffix));
        Assert.False(File.Exists(Path.Combine(_install, "config.json")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_install, "Data")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_install, "Backups")));

        // 仓库与生成物原地登记：一个字节都不搬，只把当前位置写进偏好
        Assert.Equal(Path.Combine(_roaming, "Repository"), _prefs.RepositoryRoot);
        Assert.Equal(Path.Combine(_roaming, "Overwrites"), _prefs.OverwritesRoot);
        Assert.True(File.Exists(Path.Combine(_roaming, "Repository", "pkg-1", "body.bin")));
    }

    [Fact]
    public void D1_配置里的绝对路径跟着搬到新位置()
    {
        SeedTypicalLegacyUser();

        Run();

        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_local, "config.json"))).RootElement;
        Assert.Equal(
            Path.Combine(_local, "Backups", "Mods", "悟空_备份"),
            config.GetProperty("BackupPath").GetString());
        Assert.Equal(
            Path.Combine(_local, "Data", "GameIcons", "wukong.png"),
            config.GetProperty("GameIcons").GetProperty("黑神话").GetString());

        // 与本次搬迁无关的字段一个都不能动，包括当前模型不认识的
        Assert.Equal(@"D:\Games\Wukong", config.GetProperty("GamePath").GetString());
        Assert.Equal(42, config.GetProperty("UnknownFutureField").GetInt32());
    }

    [Fact]
    public void D1_连跑两次第二次是彻底的空操作()
    {
        SeedTypicalLegacyUser();
        Run();
        var after1 = SnapshotTree(_root);

        var outcome2 = Run();

        Assert.Equal(0, outcome2.Executed);
        Assert.Equal(0, outcome2.Failed);
        Assert.Equal(6, outcome2.Skipped);
        Assert.Equal(1, _prefs.DataLayoutVersionSaveCount);
        // 连 config.json 的最后写入时间都不该被扰动：无改动就不写盘
        AssertTreeUnchanged(after1, SnapshotTree(_root));
    }

    // ─── D2 自定义仓库位置（关键项） ───

    [Fact]
    public void D2_用户自定义的仓库位置一个字都不能动()
    {
        SeedTypicalLegacyUser();
        var custom = Path.Combine(_root, "custom-repo");
        Write(Path.Combine(custom, "pkg-9", "body.bin"), "用户放在别的盘的包");
        _prefs.RepositoryRoot = custom;
        _prefs.ResetCounters();
        var customBefore = SnapshotTree(custom);

        var outcome = Run();

        Assert.Equal(0, outcome.Failed);
        Assert.Equal(custom, _prefs.RepositoryRoot);
        Assert.Equal(0, _prefs.RepositoryRootSaveCount);   // 连"写回同一个值"都不许发生
        AssertTreeUnchanged(customBefore, SnapshotTree(custom));

        // 旧的默认仓库位置同样保持原样：用户已自定义，这里的残留不归迁移器处置
        Assert.True(File.Exists(Path.Combine(_roaming, "Repository", "pkg-1", "body.bin")));
        Assert.False(DirectoryTombstoneExists(Path.Combine(_roaming, "Repository")));

        // 其余项照常搬迁，不因为仓库被跳过而受影响
        Assert.True(File.Exists(Path.Combine(_local, "Data", "wukong_mods.json")));
    }

    [Fact]
    public void D2_连跑两次仍不碰自定义仓库()
    {
        SeedTypicalLegacyUser();
        var custom = Path.Combine(_root, "custom-repo");
        Write(Path.Combine(custom, "pkg-9", "body.bin"), "用户放在别的盘的包");
        _prefs.RepositoryRoot = custom;

        Run();
        var after1 = SnapshotTree(_root);
        _prefs.ResetCounters();

        var outcome2 = Run();

        Assert.Equal(0, outcome2.Executed);
        Assert.Equal(0, _prefs.RepositoryRootSaveCount);
        Assert.Equal(custom, _prefs.RepositoryRoot);
        AssertTreeUnchanged(after1, SnapshotTree(_root));
    }

    // ─── 偏好写失败（UiPreferences 改成 log + throw 之后的看门测试） ───

    /// <summary>
    /// 偏好落盘失败不得阻断启动。
    ///
    /// <para>
    /// <c>UiPreferences</c> 的写入口已统一为 log + throw（用户在设置界面改的东西写不进去
    /// 必须看得见），生产实现 <c>UiPreferencesDataMigrationAdapter</c> 是直通转发，
    /// 于是这个异常会顺着 <see cref="IDataMigrationPreferences"/> 进到迁移器里。
    /// 迁移器的铁律是任何失败都不得阻断启动——原地登记抛出来只该计一次 failed，
    /// 数据完整留在旧位置，下次启动重来。
    /// </para>
    /// </summary>
    [Fact]
    public void 原地登记时偏好写失败只计失败不阻断()
    {
        SeedTypicalLegacyUser();
        _prefs.ThrowOnSaveRepositoryRoot = true;

        var outcome = Run();

        Assert.Equal(1, outcome.Failed);                 // 仓库那一项
        Assert.False(outcome.Completed);                 // 有失败就不打版本标记
        Assert.Equal(0, _prefs.DataLayoutVersion);
        Assert.Null(_prefs.RepositoryRoot);

        // 其余项照常搬完，且仓库数据一个字节都没动
        Assert.True(File.Exists(Path.Combine(_local, "Data", "wukong_mods.json")));
        Assert.True(File.Exists(Path.Combine(_roaming, "Repository", "pkg-1", "body.bin")));
    }

    /// <summary>
    /// 版本标记这一句在 <c>DataLocationMigrator.Run</c> 里没有局部 try/catch，
    /// 抛出去会让所有步骤都成功之后半途中断，用户收到"迁移异常"的误报。
    /// 这正是 <c>UiPreferences.SaveDataLayoutVersion</c> 保持静默（WriteQuietly）的原因；
    /// 这里补一道外层保险：万一将来它也改成抛，启动仍然不会断。
    /// </summary>
    [Fact]
    public void 版本标记写失败时迁移器不抛异常()
    {
        SeedTypicalLegacyUser();
        _prefs.ThrowOnSaveDataLayoutVersion = true;

        var outcome = Run();

        Assert.False(outcome.Completed);
        Assert.True(File.Exists(Path.Combine(_local, "Data", "wukong_mods.json")));
    }

    // ─── D5 中断恢复（关键项） ───

    [Fact]
    public void D5_复制中途中断_重启后清空目标重来且不留残留()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        var interrupted = Run(executor: new FaultInjectingExecutor("数据索引", FaultStage.DuringCopy));

        Assert.Equal(1, interrupted.Failed);
        Assert.False(interrupted.Completed);
        Assert.Equal(0, _prefs.DataLayoutVersion);                                  // 没搬完不许打标记
        Assert.False(DirectoryTombstoneExists(Path.Combine(_install, "Data")));      // 没有墓碑
        Assert.True(File.Exists(LegacyData("wukong_mods.json")));                    // 旧数据仍是唯一可信副本
        Assert.True(File.Exists(Path.Combine(_local, "Data", "上次中断的孤儿.json"))); // 目标里是半份数据

        var healed = Run();

        Assert.Equal(0, healed.Failed);
        Assert.True(healed.Completed);
        // 关键：必须先清空目标再重新复制，绝不能在残留上继续
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        Assert.False(File.Exists(Path.Combine(_local, "Data", "上次中断的孤儿.json")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
    }

    [Fact]
    public void D5_校验后写墓碑前中断_重启后重新复制而不是把残留当成完成()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        var interrupted = Run(executor: new FaultInjectingExecutor(
            "数据索引", FaultStage.AfterVerifyBeforeTombstone));

        Assert.Equal(1, interrupted.Failed);
        // 目标是完整副本，但没有墓碑——这个状态外观上与"复制到一半"无法区分，
        // 规划器只认墓碑，因此下一轮必然走 PurgeTargetThenCopy。
        Assert.True(File.Exists(Path.Combine(_local, "Data", "wukong_mods.json")));
        Assert.False(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.True(File.Exists(LegacyData("wukong_mods.json")));

        var healed = Run();

        Assert.Equal(0, healed.Failed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_install, "Data")));
    }

    [Fact]
    public void D5_删源中途中断_重启后续做删源而不重新复制()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        var interrupted = Run(executor: new FaultInjectingExecutor(
            "数据索引", FaultStage.DuringDeleteLegacy));

        Assert.Equal(1, interrupted.Failed);
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));   // 墓碑已写下
        Assert.True(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_install, "Data"))); // 源没删干净

        // 在旧位置塞一个只有它才有的文件：若第二轮错误地重新复制，它会被带到新位置去
        Write(LegacyData("不该被重新复制.json"), """{"stale":true}""");

        var healed = Run();

        Assert.Equal(0, healed.Failed);
        Assert.True(healed.Completed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        Assert.False(File.Exists(Path.Combine(_local, "Data", "不该被重新复制.json")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_install, "Data")));
    }

    /// <summary>
    /// 方案 §七 第 9 项要求"反复中断 3 次以上，每次在不同阶段"。这里把三个阶段连成一串：
    /// 复制中 → 校验后写墓碑前 → 删源中 → 正常跑完，全程不重启进程之外的任何状态。
    /// </summary>
    [Fact]
    public void D5_三个阶段依次中断后仍能自愈且不产生重复或半份数据()
    {
        SeedTypicalLegacyUser();
        SeedDeploymentBackups();
        var dataBefore = SnapshotDataIndexOnly(Path.Combine(_install, "Data"));
        var deployBefore = SnapshotBytes(Path.Combine(_install, "Data", "Backups"));

        foreach (var stage in new[]
                 {
                     FaultStage.DuringCopy,
                     FaultStage.AfterVerifyBeforeTombstone,
                     FaultStage.DuringDeleteLegacy,
                 })
        {
            var outcome = Run(executor: new FaultInjectingExecutor("数据索引", stage));
            Assert.Equal(1, outcome.Failed);
            Assert.Equal(0, _prefs.DataLayoutVersion);
        }

        var healed = Run();

        Assert.Equal(0, healed.Failed);
        Assert.True(healed.Completed);
        Assert.Equal(DataRelocationPlanner.CurrentLayoutVersion, _prefs.DataLayoutVersion);

        // 数据索引：内容逐字节一致，且不多不少
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        // 部署事务备份走的是另一条路，三次中断都不该波及它
        AssertSameBytes(deployBefore, SnapshotBytes(Path.Combine(_local, "Backups", "Deployments")));
        Assert.False(Directory.Exists(Path.Combine(_local, "Data", "Backups")));

        // 再跑一次仍是空操作
        var after = SnapshotTree(_root);
        Assert.Equal(0, Run().Executed);
        AssertTreeUnchanged(after, SnapshotTree(_root));
    }

    // ─── 部署事务备份与数据索引的解耦（编排层） ───

    [Fact]
    public void 部署事务备份与数据索引各自到达正确位置()
    {
        SeedTypicalLegacyUser();
        SeedDeploymentBackups();
        var deployBefore = SnapshotBytes(Path.Combine(_install, "Data", "Backups"));

        var outcome = Run();

        Assert.Equal(0, outcome.Failed);
        Assert.Equal(6, outcome.Executed);

        // 部署事务备份落在 Backups\Deployments，而不是跟着 Data 走到 Data\Backups
        AssertSameBytes(deployBefore, SnapshotBytes(Path.Combine(_local, "Backups", "Deployments")));
        Assert.False(Directory.Exists(Path.Combine(_local, "Data", "Backups")));

        // 数据索引本身不受影响
        Assert.True(File.Exists(Path.Combine(_local, "Data", "wukong_mods.json")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data", "Backups")));
    }

    [Fact]
    public void 部署事务备份失败不波及数据索引且其数据完整留在旧位置()
    {
        SeedTypicalLegacyUser();
        SeedDeploymentBackups();
        var deployBefore = SnapshotBytes(Path.Combine(_install, "Data", "Backups"));

        var outcome = Run(executor: new FaultInjectingExecutor("部署事务备份", FaultStage.BeforeCopy));

        Assert.Equal(1, outcome.Failed);
        // 数据索引照常到位
        Assert.True(File.Exists(Path.Combine(_local, "Data", "wukong_mods.json")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        // 部署事务备份的数据一个字节都没少——它没有副本，被顺手删掉就是崩溃回滚彻底失效
        AssertSameBytes(deployBefore, SnapshotBytes(Path.Combine(_install, "Data", "Backups")));
        // 目标侧只可能剩一个进行中标记（复制前立的），没有任何数据落地
        Assert.False(DataRelocationExecutor.DirectoryHasContent(
            Path.Combine(_local, "Backups", "Deployments")));
    }

    [Fact]
    public void 数据索引失败不波及部署事务备份()
    {
        SeedTypicalLegacyUser();
        SeedDeploymentBackups();
        var dataBefore = SnapshotDataIndexOnly(Path.Combine(_install, "Data"));
        var deployBefore = SnapshotBytes(Path.Combine(_install, "Data", "Backups"));

        var outcome = Run(executor: new FaultInjectingExecutor("数据索引", FaultStage.BeforeCopy));

        Assert.Equal(1, outcome.Failed);
        AssertSameBytes(deployBefore, SnapshotBytes(Path.Combine(_local, "Backups", "Deployments")));
        // 数据索引的内容原封不动留在旧位置（部署事务备份已被搬走，只剩它的墓碑）
        AssertSameBytes(dataBefore, SnapshotDataIndexOnly(Path.Combine(_install, "Data")));
        Assert.False(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_local, "Data")));
    }

    // ─── 搬迁失败时配置不被改写 ───

    [Fact]
    public void MOD备份搬迁失败时配置里的备份路径一个字都不改()
    {
        SeedTypicalLegacyUser();
        var originalBackupPath = Path.Combine(_install, "Backups", "悟空_备份");

        var outcome = Run(executor: new FaultInjectingExecutor("MOD 备份", FaultStage.BeforeCopy));

        Assert.Equal(1, outcome.Failed);

        // 主配置那一项自己搬成功了，配置已在新位置；但 BackupPath 必须仍指向旧位置——
        // 数据确实还在那儿，改了配置反而会让备份写进一个空目录。
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_local, "config.json"))).RootElement;
        Assert.Equal(originalBackupPath, config.GetProperty("BackupPath").GetString());
        // 数据索引搬成功了，GameIcons 就该改
        Assert.Equal(
            Path.Combine(_local, "Data", "GameIcons", "wukong.png"),
            config.GetProperty("GameIcons").GetProperty("黑神话").GetString());
        Assert.True(File.Exists(Path.Combine(originalBackupPath, "old.pak")));
    }

    [Fact]
    public void 数据索引搬迁失败时游戏图标路径一个字都不改()
    {
        SeedTypicalLegacyUser();
        var originalIcon = Path.Combine(_install, "Data", "GameIcons", "wukong.png");

        var outcome = Run(executor: new FaultInjectingExecutor("数据索引", FaultStage.BeforeCopy));

        Assert.Equal(1, outcome.Failed);

        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_local, "config.json"))).RootElement;
        Assert.Equal(originalIcon, config.GetProperty("GameIcons").GetProperty("黑神话").GetString());
        Assert.Equal(
            Path.Combine(_local, "Backups", "Mods", "悟空_备份"),
            config.GetProperty("BackupPath").GetString());
    }

    [Fact]
    public void 全部搬迁失败时配置文件完全不被触碰()
    {
        SeedTypicalLegacyUser();
        var configPath = Path.Combine(_install, "config.json");
        var before = new FileInfo(configPath);
        var (bytesBefore, writtenBefore) = (File.ReadAllBytes(configPath), before.LastWriteTimeUtc);

        var outcome = Run(executor: new FaultInjectingExecutor(itemName: null, FaultStage.BeforeCopy));

        Assert.Equal(3, outcome.Failed);   // 主配置 / 数据索引 / MOD 备份
        Assert.Equal(bytesBefore, File.ReadAllBytes(configPath));
        Assert.Equal(writtenBefore, new FileInfo(configPath).LastWriteTimeUtc);
        Assert.Empty(AllTombstones());
        Assert.Equal(0, _prefs.DataLayoutVersion);
    }

    // ─── 新位置已被应用正常使用（开关翻开当天的默认形态） ───

    /// <summary>
    /// <b>翻开开关那一刻每台老用户机器的真实形态。</b>
    ///
    /// <para>
    /// 路径归口（c6523fc / 7c58a43）之后，全部读写方都走 <c>AppPaths</c> 的新位置，
    /// 而搬移开关一直关着——于是老用户升级到 2.0.5 后，旧数据静静躺在安装目录，
    /// <b>新数据一直在往 <c>%LOCALAPPDATA%</c> 里写</b>。开关翻开的那次启动，
    /// 两处都有内容且都没有墓碑。
    /// </para>
    ///
    /// <para>
    /// 策略：<b>新位置赢，旧位置原样保留不删</b>。新位置那份才是用户界面上看得见、
    /// 升级以来一直在改的真实状态；反过来让旧位置赢，就是迁移方案 §三 否决"保留旧位置只读"
    /// 时点名的那类 bug——"用户的方案/分类会凭空回退"，而且还带删除。
    /// 而旧位置一个字节都不删，是这条启发式判断的安全网：万一判错，用户的历史数据仍在原处。
    /// </para>
    ///
    /// <para>
    /// 上一版在这里是<b>拒绝并计为失败</b>（靠 <c>PurgeTarget</c> 的进行中标记拦阻）。
    /// 那一版不会毁数据，但这两项会每次启动都失败，等于给全体老用户挂一条永久的
    /// "迁移未完成"。判据下沉到规划器之后，这条路径成了正常完成。
    /// </para>
    /// </summary>
    [Fact]
    public void 新旧两处都有数据时认新位置为准_两边一个字节都不动()
    {
        SeedTypicalLegacyUser();

        // 用户升级后这段时间的劳动：新增了一个 MOD 清单、改了配置
        Write(Path.Combine(_local, "Data", "wukong_mods.json"),
            """[{"Name":"MOD-A"},{"Name":"MOD-B"},{"Name":"升级后新装的 MOD-C"}]""");
        Write(Path.Combine(_local, "Data", "starfield_mods.json"), """[{"Name":"升级后新加的游戏"}]""");
        Write(Path.Combine(_local, "config.json"), """{"GamePath":"E:\\Games\\Wukong"}""");
        var localDataBefore = SnapshotTree(Path.Combine(_local, "Data"));
        var localConfigBytes = File.ReadAllBytes(Path.Combine(_local, "config.json"));
        var localConfigWritten = new FileInfo(Path.Combine(_local, "config.json")).LastWriteTimeUtc;
        var legacyDataBefore = SnapshotDataOnly(Path.Combine(_install, "Data"));
        var legacyConfigBytes = File.ReadAllBytes(Path.Combine(_install, "config.json"));

        var outcome = Run();

        // 主配置 + 数据索引跳过（算执行）、MOD 备份正常搬、两项原地登记；部署事务备份此形态下不存在
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(5, outcome.Executed);
        Assert.True(outcome.Completed);
        Assert.Equal(DataRelocationPlanner.CurrentLayoutVersion, _prefs.DataLayoutVersion);

        // 冲突的两项：新位置一个字节没变（连修改时间都没动）
        AssertTreeUnchanged(localDataBefore, SnapshotTree(Path.Combine(_local, "Data")));
        Assert.Equal(localConfigBytes, File.ReadAllBytes(Path.Combine(_local, "config.json")));
        Assert.Equal(localConfigWritten,
            new FileInfo(Path.Combine(_local, "config.json")).LastWriteTimeUtc);

        // 旧位置一个字节没删——这是本策略"零数据丢失"的支点
        AssertTreeUnchanged(legacyDataBefore, SnapshotDataOnly(Path.Combine(_install, "Data")));
        Assert.Equal(legacyConfigBytes, File.ReadAllBytes(Path.Combine(_install, "config.json")));

        // 留下的是"已跳过"标记，不是搬移墓碑——两者语义相反，混用会让下一轮去删旧数据
        Assert.True(SupersededMarkerExists(Path.Combine(_install, "Data")));
        Assert.True(File.Exists(Path.Combine(_install, "config.json")
            + DataRelocationExecutor.FileSupersededMarkerSuffix));
        Assert.False(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.False(File.Exists(Path.Combine(_install, "config.json")
            + DataRelocationExecutor.FileTombstoneSuffix));

        // 不冲突的 MOD 备份照常搬迁：判定的粒度是单项，不是整体停摆
        Assert.True(File.Exists(Path.Combine(_local, "Backups", "Mods", "悟空_备份", "old.pak")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Backups")));
    }

    /// <summary>
    /// 四态语义：跳过属于<b>正常完成</b>，绝不能触发"迁移未完成"提示。
    /// 这正是上一版最大的代价——两项每次启动都失败，用户看到一条永久挂着的告警。
    /// </summary>
    [Fact]
    public void 新位置赢属于正常完成_不给用户报迁移未完成()
    {
        SeedTypicalLegacyUser();
        Write(Path.Combine(_local, "Data", "wukong_mods.json"), """[{"Name":"新位置的真实数据"}]""");
        Write(Path.Combine(_local, "config.json"), """{"GamePath":"E:\\Games\\Wukong"}""");

        var outcome = Run();

        Assert.Equal(DataMigrationStatus.Completed, outcome.Status);
        Assert.False(outcome.ShouldNotifyUser);
        Assert.Null(outcome.UserMessage);
    }

    /// <summary>
    /// 幂等：第二次启动看到跳过标记直接跳过，两棵树的字节与修改时间全不许变。
    ///
    /// <para>
    /// 这条用例真正拦的是一个很容易犯的错：跳过标记若被误当成搬移墓碑，
    /// 规划器会判成"搬完了只差删源"（<c>ResumeCleanup</c>），第二轮直接把旧位置那份
    /// 从没被复制过的数据删光。两种标记用两个文件名，就是为了让这件事无从发生。
    /// </para>
    /// </summary>
    [Fact]
    public void 新位置赢之后连跑两次第二次是彻底的空操作()
    {
        SeedTypicalLegacyUser();
        Write(Path.Combine(_local, "Data", "wukong_mods.json"), """[{"Name":"新位置的真实数据"}]""");
        Write(Path.Combine(_local, "config.json"), """{"GamePath":"E:\\Games\\Wukong"}""");

        Run();
        var after1 = SnapshotTree(_root);

        var outcome2 = Run();

        Assert.Equal(0, outcome2.Executed);
        Assert.Equal(0, outcome2.Failed);
        Assert.Equal(6, outcome2.Skipped);
        Assert.Equal(1, _prefs.DataLayoutVersionSaveCount);
        // 旧位置的数据必须仍在——被误判成 ResumeCleanup 的话这一行会先炸
        Assert.True(File.Exists(LegacyData("wukong_mods.json")));
        AssertTreeUnchanged(after1, SnapshotTree(_root));
    }

    /// <summary>
    /// <b>"墓碑存在但数据其实没搬过去"这一类的核心用例。</b>
    ///
    /// <para>
    /// 形态：新位置只有 <c>Data</c> 有内容（用户升级后加过游戏），<c>config.json</c> 还没有。
    /// 于是主配置走正常搬移（旧的那份被复制到新位置），数据索引走"新位置赢"。
    /// 被搬过去的那份 config.json 里，<c>GameIcons</c> 指向 <c>{安装目录}\Data\GameIcons\wukong.png</c>。
    /// </para>
    ///
    /// <para>
    /// 若沿用"有标记就改写"的判据，这个值会被平移到 <c>{新位置}\Data\GameIcons\wukong.png</c>
    /// ——而那个文件<b>根本不存在</b>（数据索引压根没搬），用户的自定义游戏图标全部失效，
    /// 且是"界面上图标空了、日志里什么都没有"的静默故障。
    /// 正确做法是一个字都不改：png 还在安装目录里，旧的绝对路径依然有效。
    /// </para>
    ///
    /// <para>
    /// 对照组是同一次运行里的 <c>BackupPath</c>：MOD 备份走的是真正的搬移，
    /// 留下的是搬移墓碑，它必须被改写——否则备份会继续写进已被清空的安装目录。
    /// 一次运行里两个字段一改一不改，判据的边界就钉死了。
    /// </para>
    /// </summary>
    [Fact]
    public void 数据索引被跳过时游戏图标路径一个字都不改()
    {
        SeedTypicalLegacyUser();
        // 新位置只有 Data 有内容，config.json 尚未生成 —— 主配置搬、数据索引跳
        Write(Path.Combine(_local, "Data", "starfield_mods.json"), """[{"Name":"升级后新加的游戏"}]""");
        var originalIcon = LegacyData(Path.Combine("GameIcons", "wukong.png"));

        var outcome = Run();

        Assert.Equal(0, outcome.Failed);
        Assert.True(outcome.Completed);

        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_local, "config.json"))).RootElement;

        // 图标：值原样保留，且它指向的文件确实还在旧位置（跳过从不删源）
        Assert.Equal(originalIcon, config.GetProperty("GameIcons").GetProperty("黑神话").GetString());
        Assert.True(File.Exists(originalIcon));
        // 反证：新位置下同名文件根本不存在，改写过去就是让图标彻底失效
        Assert.False(File.Exists(Path.Combine(_local, "Data", "GameIcons", "wukong.png")));

        // 对照组：MOD 备份真的搬走了，它的绝对路径就必须跟着改
        Assert.Equal(
            Path.Combine(_local, "Backups", "Mods", "悟空_备份"),
            config.GetProperty("BackupPath").GetString());
        Assert.True(File.Exists(Path.Combine(_local, "Backups", "Mods", "悟空_备份", "old.pak")));
    }

    /// <summary>
    /// 新位置只有空目录时仍按正常搬移处理。空目录不算内容是既有语义：
    /// 应用启动时会主动建出一批空目录，把它们当成"新位置已有数据"会让所有项都被跳过，
    /// 搬迁从此永远不会发生。
    /// </summary>
    [Fact]
    public void 新位置只有空目录时仍走正常搬移()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));
        Directory.CreateDirectory(Path.Combine(_local, "Data"));
        Directory.CreateDirectory(Path.Combine(_local, "Backups", "Mods"));

        var outcome = Run();

        Assert.Equal(0, outcome.Failed);
        Assert.Equal(5, outcome.Executed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));

        // 留下的是搬移墓碑而不是跳过标记，旧位置也确实被清空了
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.False(SupersededMarkerExists(Path.Combine(_install, "Data")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_install, "Data")));
    }

    /// <summary>
    /// 上一条的反面：残留确实是搬迁器自己留下的（有进行中标记）时，照旧清空重来。
    /// 判据必须精确到"是不是我写的"，宽一格就丢用户数据，严一格就治不好断电。
    /// </summary>
    [Fact]
    public void 残留带着进行中标记时仍然清空重来()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        // 模拟上次复制到一半断电：目标有半份数据 + 进行中标记
        Write(Path.Combine(_local, "Data", "wukong_mods.json"), """[{"Name":"半份"}]""");
        Write(Path.Combine(_local, "Data", "孤儿.json"), "{}");
        Write(DataRelocationExecutor.GetInProgressMarkerPath(
            Path.Combine(_local, "Data"), isFile: false), "in-progress");

        var outcome = Run();

        Assert.Equal(0, outcome.Failed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        Assert.False(File.Exists(Path.Combine(_local, "Data", "孤儿.json")));
        // 走的是搬移而不是"新位置赢"：标记在，残留就是我自己的
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
        Assert.False(SupersededMarkerExists(Path.Combine(_install, "Data")));
        // 标记在写完墓碑后必须被清掉，否则它会永久授权下一次清空
        Assert.False(File.Exists(DataRelocationExecutor.GetInProgressMarkerPath(
            Path.Combine(_local, "Data"), isFile: false)));
    }

    /// <summary>进行中标记不能被当成"目标已有内容"，也不能混进复制与校验，否则会把自己卡死。</summary>
    [Fact]
    public void 只剩进行中标记的目标按空目标处理()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));
        Write(DataRelocationExecutor.GetInProgressMarkerPath(
            Path.Combine(_local, "Data"), isFile: false), "in-progress");

        var outcome = Run();

        Assert.Equal(0, outcome.Failed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
    }

    // ─── D3 装在不可写目录 ───

    /// <summary>
    /// <b>制造"不可写"的手法：在目录该在的位置放一个同名文件。</b>
    /// 之后任何 <c>Directory.CreateDirectory</c> 都抛 <c>IOException</c>——不动权限、
    /// 不需要管理员、跑完不留残留、跨机器稳定复现（与
    /// <c>SaveFailureReportingTests</c> 用的是同一招）。真正改 ACL 的那种只读目录留给真机。
    ///
    /// <para>
    /// 这一条打的是 D3 里最要命的形态：<b>目标</b>整个不可写（<c>%LOCALAPPDATA%</c> 被组策略
    /// 锁死、被同步盘占用）。验收点三条：应用不崩、数据一个字节不丢、失败对用户可见。
    /// </para>
    /// </summary>
    [Fact]
    public void D3_目标位置不可写时全部搬移失败_旧数据一个字节不少()
    {
        SeedTypicalLegacyUser();
        SeedDeploymentBackups();
        File.WriteAllText(_local, "我是文件，不是目录");
        var legacyBefore = SnapshotTree(_install);

        // 显式给足空间：否则这条用例会跟着开发机的剩余空间浮动，
        // 失败原因也分不清是"写不进去"还是"装不下"。
        var outcome = Run(freeSpace: FakeFreeSpace.Abundant);

        // 四项搬移全失败，两项原地登记照常（它们只写配置值，不碰目标目录）
        Assert.Equal(4, outcome.Failed);
        Assert.Equal(2, outcome.Executed);
        Assert.False(outcome.Completed);
        Assert.Equal(0, _prefs.DataLayoutVersion);      // 不打标记 = 修好之后下次启动重试

        // 失败对用户可见——这正是 355589a 那轮修掉的"UI 静默失败通道"的同类
        Assert.Equal(DataMigrationStatus.PartiallyFailed, outcome.Status);
        Assert.True(outcome.ShouldNotifyUser);
        Assert.False(string.IsNullOrWhiteSpace(outcome.UserMessage));

        // 数据完整留在旧位置，连墓碑都没留下
        AssertTreeUnchanged(legacyBefore, SnapshotTree(_install));
        Assert.Empty(AllTombstones());
    }

    /// <summary>
    /// D3 的另一半：<b>源</b>不可写。安装目录只读时复制读得出来、校验也过得了，
    /// 卡在"往旧位置写墓碑"这一步——把墓碑该在的位置换成同名<b>目录</b>即可稳定复现。
    ///
    /// <para>
    /// 这个形态的危险之处在于：目标已经有一份完整副本，旧位置却没有任何记号。
    /// 外观上它与"复制到一半断电"完全一样，只能靠目标侧的进行中标记区分。
    /// 判错的后果是第二次启动把那份完整副本当成真实数据认掉（<c>AdoptTargetKeepLegacy</c>），
    /// 或者反过来在残留上继续追加。这里连跑两次，钉死"既不重复也不丢"。
    /// </para>
    /// </summary>
    [Fact]
    public void D3_安装目录写不下墓碑时_数据不丢且第二次启动不产生重复()
    {
        SeedTypicalLegacyUser();
        Directory.CreateDirectory(
            DataRelocationExecutor.GetTombstonePath(Path.Combine(_install, "Data"), isFile: false));
        var legacyDataBefore = SnapshotDataOnly(Path.Combine(_install, "Data"));

        var first = Run(freeSpace: FakeFreeSpace.Abundant);

        Assert.Equal(1, first.Failed);
        Assert.True(first.ShouldNotifyUser);
        Assert.Equal(0, _prefs.DataLayoutVersion);
        // 墓碑没写下 → 删源一步都没做 → 旧位置仍是唯一可信副本
        AssertTreeUnchanged(legacyDataBefore, SnapshotDataOnly(Path.Combine(_install, "Data")));

        var second = Run(freeSpace: FakeFreeSpace.Abundant);

        // 安装目录还是写不进去，所以还是失败——但绝不能因此毁掉或复制出第二份数据
        Assert.Equal(1, second.Failed);
        AssertTreeUnchanged(legacyDataBefore, SnapshotDataOnly(Path.Combine(_install, "Data")));
        Assert.Equal(
            DataFileNames(Path.Combine(_install, "Data")),
            DataFileNames(Path.Combine(_local, "Data")));
    }

    /// <summary>
    /// D3 里最常见的真实形态：旧文件被别人占着删不掉（杀软扫描、同步盘、游戏进程）。
    /// 用 <c>FileShare.Read</c> 持有一个句柄即可——允许 <c>File.Copy</c> 读走内容，
    /// 但挡住 <c>File.Delete</c>，进程一退就干净。
    ///
    /// <para>
    /// 与 <c>D5_删源中途中断</c> 测的是同一条恢复路径，区别在于这里的
    /// <c>IOException</c> 是操作系统真的抛出来的，而不是注入的——它验证的是
    /// "真实失败也确实落在那条路径上"，而不只是"注入失败被正确处理"。
    /// </para>
    /// </summary>
    [Fact]
    public void D3_旧文件被占用删不掉时_墓碑仍在且下次启动续做删源()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        using (new FileStream(LegacyData("wukong_mods.json"),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blocked = Run(freeSpace: FakeFreeSpace.Abundant);

            Assert.Equal(1, blocked.Failed);
            Assert.True(blocked.ShouldNotifyUser);
            Assert.Equal(0, _prefs.DataLayoutVersion);
            // 复制与校验都过了，墓碑已写下——数据两处都完整，一个字节都没丢
            Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
            AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
            Assert.True(File.Exists(LegacyData("wukong_mods.json")));
        }

        var healed = Run(freeSpace: FakeFreeSpace.Abundant);

        // 占用解除后续做删源，而不是把已经搬好的那份重新复制一遍
        Assert.Equal(0, healed.Failed);
        Assert.True(healed.Completed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(Path.Combine(_install, "Data")));
    }

    // ─── D4 目标盘空间不足 ───

    /// <summary>
    /// <b>D4 的核心验收：空间不足时不产生半份数据。</b>
    ///
    /// <para>
    /// 没有预检的话，复制会一路写到撞满盘才抛：目标里躺着一堆残留 + 一张进行中标记，
    /// 而磁盘已经满得连墓碑都写不下；下次启动清空重来、再撞一次。数据始终不丢，
    /// 但用户的目标盘被反复填满又清空，每次启动白折腾几分钟。预检把这件事变成
    /// "目标位置一个字节都没写过"。
    /// </para>
    /// </summary>
    [Fact]
    public void D4_目标盘空间不足时该项不搬移且不产生半份数据()
    {
        SeedTypicalLegacyUser();
        SeedDeploymentBackups();
        var legacyBefore = SnapshotTree(_install);

        var outcome = Run(freeSpace: FakeFreeSpace.Full);

        Assert.Equal(4, outcome.Failed);            // 四项搬移全被拦下
        Assert.Equal(2, outcome.Executed);          // 两项原地登记不受影响：它们从不复制
        Assert.False(outcome.Completed);
        Assert.Equal(0, _prefs.DataLayoutVersion);  // 不打标记 = 腾出空间后下次启动自动重试

        // 计为失败而不是推迟，正是为了让提示对用户可见（推迟是不提示的正常状态）
        Assert.True(outcome.ShouldNotifyUser);

        // 关键：目标位置一个字节都没写过——没有半份数据，也没有进行中标记
        Assert.False(Directory.Exists(_local));
        Assert.Empty(AllTombstones());
        AssertTreeUnchanged(legacyBefore, SnapshotTree(_install));
    }

    [Fact]
    public void D4_只有装不下的那一项被拦下_其余照常搬迁()
    {
        SeedTypicalLegacyUser();
        var modBackupsBefore = SnapshotDataOnly(Path.Combine(_install, "Backups"));
        var originalBackupPath = Path.Combine(_install, "Backups", "悟空_备份");

        // 只有 MOD 备份的目标盘满。判定的粒度是单项，不是整体停摆。
        var outcome = Run(freeSpace: new FakeFreeSpace(
            path => IsModBackupTarget(path) ? 0L : Abundant));

        Assert.Equal(1, outcome.Failed);
        Assert.Equal(4, outcome.Executed);          // 主配置 + 数据索引 + 两项原地登记

        // 被拦下的那项：旧数据一个字节没动，目标位置压根没被创建
        AssertTreeUnchanged(modBackupsBefore, SnapshotDataOnly(Path.Combine(_install, "Backups")));
        Assert.False(Directory.Exists(Path.Combine(_local, "Backups", "Mods")));
        Assert.False(DirectoryTombstoneExists(Path.Combine(_install, "Backups")));

        // 没搬成就绝不能改配置里的绝对路径——改了会让备份写进一个空目录
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_local, "config.json"))).RootElement;
        Assert.Equal(originalBackupPath, config.GetProperty("BackupPath").GetString());

        // 其余项照常到位
        Assert.True(File.Exists(Path.Combine(_local, "Data", "wukong_mods.json")));
    }

    /// <summary>
    /// 判据必须<b>留余量</b>：剩余空间正好等于源体积时复制仍会失败（簇对齐、目录项、
    /// NTFS 元数据都要地方）。余量的取值与理由在 Core 的 <c>DiskSpacePrecheck</c> 里，
    /// 这一条只负责证明它确实被接到了搬迁链路上，而不是算完就扔。
    /// </summary>
    [Fact]
    public void D4_可用空间正好等于源体积时仍然拦下()
    {
        SeedTypicalLegacyUser();
        var exact = DataRelocationExecutor.TryEstimateBytes(
            Path.Combine(_install, "Backups"), isFile: false)!.Value;

        var outcome = Run(freeSpace: new FakeFreeSpace(
            path => IsModBackupTarget(path) ? exact : Abundant));

        Assert.Equal(1, outcome.Failed);
        Assert.False(Directory.Exists(Path.Combine(_local, "Backups", "Mods")));
    }

    [Fact]
    public void D4_腾出空间后下次启动自动搬成且不留残留()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        var blocked = Run(freeSpace: FakeFreeSpace.Full);
        Assert.Equal(3, blocked.Failed);            // 主配置 / 数据索引 / MOD 备份

        var healed = Run(freeSpace: FakeFreeSpace.Abundant);

        Assert.Equal(0, healed.Failed);
        Assert.True(healed.Completed);
        Assert.Equal(DataRelocationPlanner.CurrentLayoutVersion, _prefs.DataLayoutVersion);
        // 上一轮什么都没写，所以这一轮就是一次干干净净的全新搬移
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        Assert.True(DirectoryTombstoneExists(Path.Combine(_install, "Data")));
    }

    /// <summary>
    /// <b>目标里的残留占的空间要算作"马上会被释放"。</b>
    ///
    /// <para>
    /// 不算的话有一个能稳定复现的死锁：上次复制到九成时断电，目标盘剩余空间刚好卡在阈值下方，
    /// 于是每次启动都判"不足"——而那份残留只要执行下去（<c>PurgeTargetThenCopy</c> 的第一步
    /// 就是清空目标）就会被清掉。用户看到的会是"盘上明明躺着我自己的垃圾，软件却永远搬不了，
    /// 还说我空间不够"。
    /// </para>
    /// </summary>
    [Fact]
    public void D4_目标残留占用的空间算作马上会被释放()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        // 上次复制到一半断电：目标里躺着一份 4 KiB 的残留 + 进行中标记
        const int leftover = 4096;
        Write(Path.Combine(_local, "Data", "上次中断的孤儿.json"), new string('x', leftover));
        Write(DataRelocationExecutor.GetInProgressMarkerPath(
            Path.Combine(_local, "Data"), isFile: false), "in-progress");

        var required = DataRelocationExecutor.TryEstimateBytes(
            Path.Combine(_install, "Data"), isFile: false)!.Value;
        // 单看盘上的空间差 4 KiB 就够，加上马上要被清掉的残留刚好够
        var tight = DiskSpacePrecheck.RequiredWithHeadroom(required) - leftover;

        var outcome = Run(freeSpace: new FakeFreeSpace(
            path => IsDataIndexTarget(path) ? tight : Abundant));

        Assert.Equal(0, outcome.Failed);
        Assert.True(outcome.Completed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
        Assert.False(File.Exists(Path.Combine(_local, "Data", "上次中断的孤儿.json")));
    }

    /// <summary>
    /// 查不出目标卷可用空间（UNC / 映射盘 / 未就绪卷）时<b>放行</b>。
    /// 预检的职责是拦住注定失败的复制，不是给搬迁加一道新的准入闸门——
    /// "查不到就拒绝"会让这些用户永远搬不了，而他们的盘八成是够的。
    /// </summary>
    [Fact]
    public void D4_查不出目标卷可用空间时照常搬迁()
    {
        SeedTypicalLegacyUser();
        var dataBefore = SnapshotBytes(Path.Combine(_install, "Data"));

        var outcome = Run(freeSpace: new FakeFreeSpace(_ => (long?)null));

        Assert.Equal(0, outcome.Failed);
        Assert.True(outcome.Completed);
        AssertSameBytes(dataBefore, SnapshotBytes(Path.Combine(_local, "Data")));
    }

    /// <summary>
    /// 不往目标写数据的动作一律不查盘：跳过（新位置赢）只在旧位置写一张几 KB 的说明，
    /// 原地登记只写一个配置值。<b>几十 GB 的仓库与生成物正是靠这一条彻底不参与空间预检</b>
    /// ——方案 §三② 当初要给它们留降级出口，是因为那时它们还在搬移类里。
    /// </summary>
    [Fact]
    public void D4_不复制的项从不参与空间预检()
    {
        SeedTypicalLegacyUser();
        // 新位置已有应用写入的真实数据 → 主配置与数据索引都走"新位置赢"，不复制
        Write(Path.Combine(_local, "Data", "wukong_mods.json"), """[{"Name":"新位置的真实数据"}]""");
        Write(Path.Combine(_local, "config.json"), """{"GamePath":"E:\\Games\\Wukong"}""");
        var probe = FakeFreeSpace.Full;

        var outcome = Run(freeSpace: probe);

        // 满盘也只拦得住真正要复制的 MOD 备份那一项
        Assert.Equal(1, outcome.Failed);
        Assert.True(SupersededMarkerExists(Path.Combine(_install, "Data")));
        Assert.Equal(Path.Combine(_roaming, "Repository"), _prefs.RepositoryRoot);
        Assert.Equal(Path.Combine(_roaming, "Overwrites"), _prefs.OverwritesRoot);

        // 原地登记项压根不该出现在查询里
        Assert.DoesNotContain(probe.QueriedPaths,
            p => p.Contains("Repository", StringComparison.OrdinalIgnoreCase)
                 || p.Contains("Overwrites", StringComparison.OrdinalIgnoreCase));
    }

    // ─── 替身 ───

    private enum FaultStage
    {
        /// <summary>整项失败，目标一个字节都没写过。</summary>
        BeforeCopy,

        /// <summary>复制到一半断电：目标里有半份数据 + 上次残留的孤儿文件，且没有墓碑。</summary>
        DuringCopy,

        /// <summary>复制与校验都完成、墓碑还没写下时断电。外观上与"复制到一半"无法区分。</summary>
        AfterVerifyBeforeTombstone,

        /// <summary>墓碑已写下、删源删到一半时断电。</summary>
        DuringDeleteLegacy,
    }

    /// <summary>
    /// 在四步搬移的<b>指定阶段</b>注入失败。
    ///
    /// <para>
    /// 只覆盖单个步骤，不覆盖 <c>Execute</c>——步骤的先后顺序正是被测的不变量。
    /// 断电落在哪一步决定了下次启动该走 PurgeTargetThenCopy 还是 ResumeCleanup，
    /// 而这三种中断没法靠真实的强杀进程稳定复现，必须精确注入。
    /// </para>
    /// </summary>
    /// <param name="itemName">只对这一项注入失败；<c>null</c> 表示所有搬移项都失败。</param>
    private sealed class FaultInjectingExecutor(string? itemName, FaultStage stage) : DataRelocationExecutor
    {
        private bool Targets(RelocationStep step) => itemName is null || step.Name == itemName;

        public override void CopyAndVerify(RelocationStep step, bool isFile,
            IReadOnlyCollection<string>? excludedChildDirectories = null,
            RelocationCopyContext? copyContext = null)
        {
            if (!Targets(step) || stage is not (FaultStage.BeforeCopy or FaultStage.DuringCopy))
            {
                base.CopyAndVerify(step, isFile, excludedChildDirectories, copyContext);
                return;
            }

            if (stage == FaultStage.BeforeCopy) throw new IOException("模拟：该项整体失败");

            WriteHalfOfTarget(step, isFile);
            throw new IOException("模拟：复制中途断电");
        }

        public override void WriteTombstone(RelocationStep step, bool isFile)
        {
            if (Targets(step) && stage == FaultStage.AfterVerifyBeforeTombstone)
            {
                throw new IOException("模拟：校验通过、写墓碑前断电");
            }

            base.WriteTombstone(step, isFile);
        }

        public override void DeleteLegacy(RelocationStep step, bool isFile,
            IReadOnlyCollection<string>? excludedChildDirectories = null)
        {
            if (!Targets(step) || stage != FaultStage.DuringDeleteLegacy)
            {
                base.DeleteLegacy(step, isFile, excludedChildDirectories);
                return;
            }

            // 删掉一个就断电，剩下的留给下次启动续做
            var victim = Directory.GetFiles(step.LegacyPath)
                .FirstOrDefault(f => !IsTombstone(f));
            if (victim != null) File.Delete(victim);
            throw new IOException("模拟：删源中途断电");
        }

        /// <summary>只复制顶层第一个文件，并额外留下一个孤儿文件冒充上一版本的残留。</summary>
        private static void WriteHalfOfTarget(RelocationStep step, bool isFile)
        {
            if (isFile)
            {
                var dir = Path.GetDirectoryName(step.TargetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(step.TargetPath, "{");   // 截断的半份文件
                return;
            }

            Directory.CreateDirectory(step.TargetPath);
            var first = Directory.GetFiles(step.LegacyPath).FirstOrDefault(f => !IsTombstone(f));
            if (first != null)
            {
                File.Copy(first, Path.Combine(step.TargetPath, Path.GetFileName(first)), overwrite: true);
            }
            File.WriteAllText(Path.Combine(step.TargetPath, "上次中断的孤儿.json"), "{}");
        }
    }

    /// <summary>
    /// <see cref="IFreeSpaceProbe"/> 的替身：把"目标卷还剩多少空间"变成一个返回值。
    ///
    /// <para>
    /// 这是 D4 能被自动化的全部原因。真实地造一个满盘要么挂 VHD、要么找一个小容量卷，
    /// 跨机器不可复现、需要额外权限、跑完还留残留；而空间判定本身早已下沉成 Core 的纯函数，
    /// 这一侧只剩一次 <c>DriveInfo</c> 查询——把它换掉，D4 就退化成普通的替身注入。
    /// </para>
    ///
    /// <para>
    /// 顺带记下被查过的路径：用来断言"原地登记项与跳过项压根不查盘"。
    /// </para>
    /// </summary>
    private sealed class FakeFreeSpace(Func<string, long?> bytes) : IFreeSpaceProbe
    {
        /// <summary>随便什么都装得下。用于把"空间"这个变量从一条用例里彻底摘掉。</summary>
        public static FakeFreeSpace Abundant => new(_ => DataLocationMigratorTests.Abundant);

        /// <summary>一个字节都没有。</summary>
        public static FakeFreeSpace Full => new(_ => 0L);

        public List<string> QueriedPaths { get; } = new();

        public long? TryGetAvailableFreeBytes(string path)
        {
            QueriedPaths.Add(path);
            return bytes(path);
        }
    }

    /// <summary>
    /// <see cref="IDataMigrationPreferences"/> 的内存替身。
    ///
    /// <para>
    /// 生产实现转调静态的 <c>UiPreferences</c>——进程级内存单例 + 真实的
    /// <c>%APPDATA%\UEModManager\ui_config.json</c>，用例之间会互相串味，
    /// 还会改掉开发者本机的配置。替身另外记下写入次数，
    /// 用来断言"第二次启动是彻底的空操作"和"自定义仓库位置连写回同一个值都不许"。
    /// </para>
    /// </summary>
    private sealed class FakePreferences : IDataMigrationPreferences
    {
        public int DataLayoutVersion { get; private set; }
        public string? RepositoryRoot { get; set; }
        public string? OverwritesRoot { get; set; }

        public int DataLayoutVersionSaveCount { get; private set; }
        public int RepositoryRootSaveCount { get; private set; }
        public int OverwritesRootSaveCount { get; private set; }

        /// <summary>
        /// 模拟 <c>UiPreferences</c> 落盘失败（目标不可写：磁盘满 / 权限 / 杀软锁定）。
        /// 生产实现是直通转发，所以这个异常真的会传到迁移器里。
        /// </summary>
        public bool ThrowOnSaveRepositoryRoot { get; set; }

        public bool ThrowOnSaveDataLayoutVersion { get; set; }

        public void ResetCounters()
        {
            DataLayoutVersionSaveCount = 0;
            RepositoryRootSaveCount = 0;
            OverwritesRootSaveCount = 0;
        }

        public int LoadDataLayoutVersion() => DataLayoutVersion;

        public void SaveDataLayoutVersion(int version)
        {
            if (ThrowOnSaveDataLayoutVersion) throw new IOException("模拟：版本标记写不进去");
            DataLayoutVersion = version;
            DataLayoutVersionSaveCount++;
        }

        public string? LoadRepositoryRoot() => RepositoryRoot;

        public void SaveRepositoryRoot(string? path)
        {
            if (ThrowOnSaveRepositoryRoot) throw new IOException("模拟：仓库根写不进去");
            RepositoryRoot = path;
            RepositoryRootSaveCount++;
        }

        public string? LoadOverwritesRoot() => OverwritesRoot;

        public void SaveOverwritesRoot(string? path)
        {
            OverwritesRoot = path;
            OverwritesRootSaveCount++;
        }
    }
}
