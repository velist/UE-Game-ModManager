using System.Text;
using UEModManager.Services;
using UEModManager.Services.Paths;

namespace UEModManager.Tests.Services;

/// <summary>
/// 搬迁执行器的文件操作测试。断电/中断相关的 bug 全部住在这几个操作里，
/// 而它们无法靠手工复现，故在临时目录上逐条驱动。
/// </summary>
public sealed class DataRelocationExecutorTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacy;
    private readonly string _target;
    private readonly DataRelocationExecutor _executor = new();

    public DataRelocationExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uemm_reloc_" + Guid.NewGuid().ToString("N")[..8]);
        _legacy = Path.Combine(_root, "legacy");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_legacy);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private RelocationStep Step(RelocationAction action, string? legacy = null, string? target = null)
        => new("测试项", RelocationKind.Relocate, legacy ?? _legacy, target ?? _target,
            action, RelocationSkipReason.NotSkipped, "test");

    private void WriteLegacy(string relativePath, string content)
    {
        var full = Path.Combine(_legacy, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ─── 复制 ───

    [Fact]
    public void CopyDirectory_CopiesNestedFiles()
    {
        WriteLegacy("a.json", """{"x":1}""");
        WriteLegacy(Path.Combine("sub", "b.json"), """{"y":2}""");

        DataRelocationExecutor.CopyDirectory(_legacy, _target);

        Assert.True(File.Exists(Path.Combine(_target, "a.json")));
        Assert.True(File.Exists(Path.Combine(_target, "sub", "b.json")));
    }

    [Fact]
    public void CopyDirectory_SkipsTombstone()
    {
        // 墓碑属于旧位置的标记，跟到新位置会让下次探测误判为"目标已完成迁移"
        WriteLegacy("a.json", "{}");
        WriteLegacy(DataRelocationExecutor.DirectoryTombstoneName, "已迁移");

        DataRelocationExecutor.CopyDirectory(_legacy, _target);

        Assert.True(File.Exists(Path.Combine(_target, "a.json")));
        Assert.False(File.Exists(Path.Combine(_target, DataRelocationExecutor.DirectoryTombstoneName)));
    }

    [Fact]
    public void CopyDirectory_ExcludedChild_NotCopied()
    {
        // Data\Backups 归"部署事务备份"那一项管，目标位置与 Data 完全不同，
        // 跟着 Data 走会被搬到错误位置。
        WriteLegacy("a.json", "{}");
        WriteLegacy(Path.Combine("Backups", "tx.json"), "{}");

        DataRelocationExecutor.CopyDirectory(_legacy, _target, new[] { "Backups" });

        Assert.True(File.Exists(Path.Combine(_target, "a.json")));
        Assert.False(Directory.Exists(Path.Combine(_target, "Backups")));
    }

    [Fact]
    public void CopyDirectory_ExcludesOnlyTopLevel()
    {
        // 排除的是具名的一项数据，不是所有同名子目录——
        // 深层的同名目录只是巧合重名，属于 Data 自己的内容。
        WriteLegacy(Path.Combine("sub", "Backups", "keep.json"), "{}");
        WriteLegacy(Path.Combine("Backups", "tx.json"), "{}");

        DataRelocationExecutor.CopyDirectory(_legacy, _target, new[] { "Backups" });

        Assert.True(File.Exists(Path.Combine(_target, "sub", "Backups", "keep.json")));
        Assert.False(Directory.Exists(Path.Combine(_target, "Backups")));
    }

    // ─── 校验 ───

    [Fact]
    public void VerifyDirectory_IgnoresExcludedChild()
    {
        // 排除项没被复制，若仍计入校验会因"文件数不一致"误判为复制失败，
        // 让一次本来成功的迁移回滚。
        WriteLegacy("a.json", """{"x":1}""");
        WriteLegacy(Path.Combine("Backups", "tx.json"), """{"y":2}""");

        DataRelocationExecutor.CopyDirectory(_legacy, _target, new[] { "Backups" });

        DataRelocationExecutor.VerifyDirectory(_legacy, _target, new[] { "Backups" }); // 不抛即通过
        Assert.Throws<IOException>(
            () => DataRelocationExecutor.VerifyDirectory(_legacy, _target));
    }

    [Fact]
    public void Execute_Copy_LeavesExcludedChildInPlace()
    {
        // 删源这一步最危险：排除项没有副本，删掉就是直接销毁用户数据
        // （部署事务备份没了 = 崩溃回滚失效）。
        WriteLegacy("a.json", """{"x":1}""");
        WriteLegacy(Path.Combine("Backups", "tx.json"), """{"y":2}""");

        _executor.Execute(Step(RelocationAction.Copy), isFile: false, new[] { "Backups" });

        Assert.True(File.Exists(Path.Combine(_target, "a.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, "a.json")));           // 已搬走
        Assert.True(File.Exists(Path.Combine(_legacy, "Backups", "tx.json"))); // 原样留下
        Assert.False(Directory.Exists(Path.Combine(_target, "Backups")));
    }

    [Fact]
    public void VerifyDirectory_IdenticalCopy_Passes()
    {
        WriteLegacy("a.json", """{"x":1}""");
        DataRelocationExecutor.CopyDirectory(_legacy, _target);

        DataRelocationExecutor.VerifyDirectory(_legacy, _target); // 不抛即通过
    }

    [Fact]
    public void VerifyDirectory_MissingFile_Throws()
    {
        WriteLegacy("a.json", "{}");
        WriteLegacy("b.json", "{}");
        DataRelocationExecutor.CopyDirectory(_legacy, _target);
        File.Delete(Path.Combine(_target, "b.json"));

        var ex = Assert.Throws<IOException>(
            () => DataRelocationExecutor.VerifyDirectory(_legacy, _target));
        Assert.Contains("文件数不一致", ex.Message);
    }

    [Fact]
    public void VerifyDirectory_TruncatedFile_Throws()
    {
        WriteLegacy("a.json", """{"value":"0123456789"}""");
        DataRelocationExecutor.CopyDirectory(_legacy, _target);
        File.WriteAllText(Path.Combine(_target, "a.json"), "{}");

        var ex = Assert.Throws<IOException>(
            () => DataRelocationExecutor.VerifyDirectory(_legacy, _target));
        Assert.Contains("字节数不一致", ex.Message);
    }

    [Fact]
    public void VerifyDirectory_CorruptJsonOfSameLength_Throws()
    {
        // 只比字节数抓不到这种情况：长度一样但内容已经不是合法 JSON
        WriteLegacy("a.json", """{"ab":12}""");
        DataRelocationExecutor.CopyDirectory(_legacy, _target);
        var copied = Path.Combine(_target, "a.json");
        File.WriteAllText(copied, """{"ab":12x""");

        Assert.Equal(
            new FileInfo(Path.Combine(_legacy, "a.json")).Length,
            new FileInfo(copied).Length);

        var ex = Assert.Throws<IOException>(
            () => DataRelocationExecutor.VerifyDirectory(_legacy, _target));
        Assert.Contains("无法解析", ex.Message);
    }

    [Fact]
    public void VerifyDirectory_NonJsonFilesAreNotParsed()
    {
        // 只有 JSON 走解析校验，二进制文件不该因内容"不合法"而被判失败
        WriteLegacy("preview.png", "\u0089PNG not really");
        DataRelocationExecutor.CopyDirectory(_legacy, _target);

        DataRelocationExecutor.VerifyDirectory(_legacy, _target);
    }

    [Fact]
    public void VerifyFile_SizeMismatch_Throws()
    {
        var src = Path.Combine(_legacy, "config.json");
        var dst = Path.Combine(_root, "config.json");
        File.WriteAllText(src, """{"a":1}""");
        File.WriteAllText(dst, "{}");

        Assert.Throws<IOException>(() => DataRelocationExecutor.VerifyFile(src, dst));
    }

    // ─── 墓碑 ───

    [Fact]
    public void WriteTombstone_Directory_LandsInsideLegacy()
    {
        WriteLegacy("a.json", "{}");

        _executor.WriteTombstone(Step(RelocationAction.Copy), isFile: false);

        var tombstone = Path.Combine(_legacy, DataRelocationExecutor.DirectoryTombstoneName);
        Assert.True(File.Exists(tombstone));
        Assert.Contains(_target, File.ReadAllText(tombstone));
    }

    [Fact]
    public void WriteTombstone_File_SitsNextToLegacyFile()
    {
        var legacyFile = Path.Combine(_legacy, "config.json");
        File.WriteAllText(legacyFile, "{}");
        var step = Step(RelocationAction.Copy, legacyFile, Path.Combine(_root, "config.json"));

        _executor.WriteTombstone(step, isFile: true);

        Assert.True(File.Exists(legacyFile + DataRelocationExecutor.FileTombstoneSuffix));
    }

    [Theory]
    [InlineData("migrated-to.txt")]
    [InlineData("MIGRATED-TO.TXT")]
    [InlineData("config.json.migrated-to.txt")]
    public void IsTombstone_RecognizesBothForms(string name)
        => Assert.True(DataRelocationExecutor.IsTombstone(Path.Combine(@"C:\x", name)));

    [Theory]
    [InlineData("a.json")]
    [InlineData("migrated-to.json")]
    public void IsTombstone_RejectsOrdinaryFiles(string name)
        => Assert.False(DataRelocationExecutor.IsTombstone(Path.Combine(@"C:\x", name)));

    // ─── 目录内容判定 ───

    [Fact]
    public void DirectoryHasContent_EmptyDirectory_IsFalse()
    {
        // 应用启动会主动创建一批空目录，把它们当成"有旧数据"会凭空产生无意义的搬移
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_legacy));
    }

    [Fact]
    public void DirectoryHasContent_OnlyTombstone_IsFalse()
    {
        WriteLegacy(DataRelocationExecutor.DirectoryTombstoneName, "已迁移");

        Assert.False(DataRelocationExecutor.DirectoryHasContent(_legacy));
    }

    [Fact]
    public void DirectoryHasContent_WithFile_IsTrue()
    {
        WriteLegacy("a.json", "{}");

        Assert.True(DataRelocationExecutor.DirectoryHasContent(_legacy));
    }

    [Fact]
    public void DirectoryHasContent_MissingDirectory_IsFalse()
        => Assert.False(DataRelocationExecutor.DirectoryHasContent(
            Path.Combine(_root, "nope")));

    // ─── 完整搬移与中断恢复 ───

    [Fact]
    public void Execute_Copy_MovesDataAndLeavesTombstone()
    {
        WriteLegacy("a.json", """{"x":1}""");
        WriteLegacy(Path.Combine("sub", "b.json"), """{"y":2}""");

        _executor.Execute(Step(RelocationAction.Copy), isFile: false);

        // 数据到位
        Assert.True(File.Exists(Path.Combine(_target, "a.json")));
        Assert.True(File.Exists(Path.Combine(_target, "sub", "b.json")));
        // 旧位置只剩墓碑
        Assert.True(File.Exists(Path.Combine(_legacy, DataRelocationExecutor.DirectoryTombstoneName)));
        Assert.False(File.Exists(Path.Combine(_legacy, "a.json")));
        Assert.False(Directory.Exists(Path.Combine(_legacy, "sub")));
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_legacy));
    }

    [Fact]
    public void Execute_PurgeTargetThenCopy_DiscardsPartialTarget()
    {
        // 模拟"上次断电停在复制中途"：目标里有半份数据、有进行中标记、没有墓碑
        WriteLegacy("a.json", """{"good":1}""");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "a.json"), """{"stale":true}""");
        File.WriteAllText(Path.Combine(_target, "orphan.json"), "{}");
        File.WriteAllText(
            DataRelocationExecutor.GetInProgressMarkerPath(_target, isFile: false), "in-progress");

        _executor.Execute(Step(RelocationAction.PurgeTargetThenCopy), isFile: false);

        Assert.Equal("""{"good":1}""", File.ReadAllText(Path.Combine(_target, "a.json")));
        // 残留的孤儿文件必须被清掉，否则会永久留在新位置
        Assert.False(File.Exists(Path.Combine(_target, "orphan.json")));
        // 墓碑写下之后标记必须被清掉：留着它等于永久授权下一次清空目标
        Assert.False(File.Exists(
            DataRelocationExecutor.GetInProgressMarkerPath(_target, isFile: false)));
    }

    [Fact]
    public void Execute_PurgeTargetThenCopy_WithoutMarker_RefusesAndKeepsBothSides()
    {
        // 目标有内容却没有进行中标记 —— 那不是上次中断的残留，而是新位置被正常使用后
        // 写下的真实数据（路径归口之后，老用户升级以来的全部新数据都在新位置）。
        // 清空它等于把用户升级后的劳动删光、再拿升级前的旧状态盖回去。
        WriteLegacy("a.json", """{"old":1}""");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "a.json"), """{"用户升级后的新数据":1}""");

        var ex = Assert.Throws<IOException>(
            () => _executor.Execute(Step(RelocationAction.PurgeTargetThenCopy), isFile: false));
        Assert.Contains("拒绝清空目标", ex.Message);

        Assert.Equal("""{"用户升级后的新数据":1}""", File.ReadAllText(Path.Combine(_target, "a.json")));
        Assert.Equal("""{"old":1}""", File.ReadAllText(Path.Combine(_legacy, "a.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, DataRelocationExecutor.DirectoryTombstoneName)));
    }

    [Fact]
    public void Execute_Copy_ClearsInProgressMarkerAfterTombstone()
    {
        WriteLegacy("a.json", """{"x":1}""");

        _executor.Execute(Step(RelocationAction.Copy), isFile: false);

        Assert.True(File.Exists(Path.Combine(_legacy, DataRelocationExecutor.DirectoryTombstoneName)));
        Assert.False(File.Exists(
            DataRelocationExecutor.GetInProgressMarkerPath(_target, isFile: false)));
    }

    [Fact]
    public void Execute_CopyFailure_LeavesMarkerSoNextRunMayPurge()
    {
        // 复制/校验失败时标记必须留着：它是"目标里这堆东西是我写的"的唯一证据，
        // 丢了的话下次启动会因为无从判断而拒绝清空，本来能自愈的中断变成永久卡死。
        WriteLegacy("a.json", """{"ab":12}""");
        DataRelocationExecutor.CopyDirectory(_legacy, _target);
        File.WriteAllText(Path.Combine(_target, "extra.json"), "{}");   // 制造文件数不一致

        Assert.Throws<IOException>(() => _executor.Execute(Step(RelocationAction.Copy), isFile: false));

        Assert.True(File.Exists(
            DataRelocationExecutor.GetInProgressMarkerPath(_target, isFile: false)));
        Assert.False(File.Exists(Path.Combine(_legacy, DataRelocationExecutor.DirectoryTombstoneName)));
    }

    [Fact]
    public void InProgressMarker_IsNotContentAndIsNotCopied()
    {
        // 标记既不能被算成"目标已有内容"，也不能跟着复制/校验走，否则会把自己卡死
        Directory.CreateDirectory(_target);
        File.WriteAllText(
            DataRelocationExecutor.GetInProgressMarkerPath(_target, isFile: false), "in-progress");
        Assert.False(DataRelocationExecutor.DirectoryHasContent(_target));

        WriteLegacy("a.json", "{}");
        _executor.Execute(Step(RelocationAction.Copy), isFile: false);

        // 目标里只该有 a.json：标记既没被算进校验（否则文件数不一致，搬迁当场回滚），
        // 也在写完墓碑后被清掉了
        Assert.Equal(new[] { "a.json" },
            Directory.GetFiles(_target).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Execute_ResumeCleanup_DeletesLegacyWithoutRecopying()
    {
        // 模拟"上次断电停在写墓碑之后、删源之前"：目标已是完整副本，不该重新复制
        WriteLegacy("a.json", """{"old":1}""");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "a.json"), """{"new":1}""");
        File.WriteAllText(Path.Combine(_legacy, DataRelocationExecutor.DirectoryTombstoneName), "已迁移");

        _executor.Execute(Step(RelocationAction.ResumeCleanup), isFile: false);

        // 目标内容未被旧数据覆盖
        Assert.Equal("""{"new":1}""", File.ReadAllText(Path.Combine(_target, "a.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, "a.json")));
    }

    [Fact]
    public void Execute_Copy_IsIdempotentWhenRepeated()
    {
        // 第一次搬完后再跑一次（模拟版本标记没写成功就重启）：不应报错，也不应把数据搬丢
        WriteLegacy("a.json", """{"x":1}""");
        _executor.Execute(Step(RelocationAction.Copy), isFile: false);

        _executor.Execute(Step(RelocationAction.ResumeCleanup), isFile: false);

        Assert.True(File.Exists(Path.Combine(_target, "a.json")));
        Assert.Equal("""{"x":1}""", File.ReadAllText(Path.Combine(_target, "a.json")));
    }

    [Fact]
    public void Execute_VerificationFailure_LeavesLegacyIntact()
    {
        // 校验不过时墓碑还没写下，旧数据必须原封不动——它此刻是唯一可信副本
        WriteLegacy("a.json", "{}");
        var step = Step(RelocationAction.Copy);

        // 目标路径指向一个已存在的同名文件，使 CreateDirectory 失败
        File.WriteAllText(_target, "occupied");

        Assert.ThrowsAny<Exception>(() => _executor.Execute(step, isFile: false));
        Assert.True(File.Exists(Path.Combine(_legacy, "a.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, DataRelocationExecutor.DirectoryTombstoneName)));
    }

    [Fact]
    public void Execute_File_MovesAndLeavesSuffixTombstone()
    {
        var legacyFile = Path.Combine(_legacy, "config.json");
        var targetFile = Path.Combine(_root, "moved", "config.json");
        File.WriteAllText(legacyFile, """{"gamePath":"D:\\Game"}""");

        _executor.Execute(Step(RelocationAction.Copy, legacyFile, targetFile), isFile: true);

        Assert.True(File.Exists(targetFile));
        Assert.False(File.Exists(legacyFile));
        Assert.True(File.Exists(legacyFile + DataRelocationExecutor.FileTombstoneSuffix));
    }

    [Fact]
    public void Execute_UnsupportedAction_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => _executor.Execute(Step(RelocationAction.RegisterInPlace), isFile: false));

    [Fact]
    public void Execute_Copy_PreservesFileBytesExactly()
    {
        var content = "中文内容 with \r\n mixed line endings\n";
        WriteLegacy("a.txt", content);
        var before = File.ReadAllBytes(Path.Combine(_legacy, "a.txt"));

        _executor.Execute(Step(RelocationAction.Copy), isFile: false);

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_target, "a.txt")));
        Assert.Equal(content, File.ReadAllText(Path.Combine(_target, "a.txt"), Encoding.UTF8));
    }
}
