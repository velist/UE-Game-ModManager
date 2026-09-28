using System.IO;
using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

/// <summary>
/// 导入单元切分。
///
/// 现场症状：导入 0916.zip（7 个 MOD 文件夹、14 个 .pak、1177 个插件载荷文件）后，
/// 仓库里只多出一个名叫 0916 的巨型包，里面混着 UE4SS 运行时与互不相干的 MOD 文件。
/// 界面里因此只有一个条目，冲突检测也随之失效——所有文件同属一个 PackageKey，
/// ConflictDetector 去重后只剩一个声称者，"两个包抢同一路径"永远不成立。
/// </summary>
public sealed class ArchiveImportUnitResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Core.Tests", "ImportUnits", Guid.NewGuid().ToString("N"));

    public ArchiveImportUnitResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Partition_EachTopLevelFolder_BecomesItsOwnUnit()
    {
        // 复刻 0916.zip 的形状：多个 MOD 文件夹并列。
        Touch(Path.Combine("超级披挂", "b1", "Content", "Paks", "~mods", "超级披挂.pak"));
        Touch(Path.Combine("黑神话哪吒", "1.哪吒皮肤mod", "nezha_yao_110_p.pak"));
        Touch(Path.Combine("黑神话哪吒", "1.哪吒皮肤mod", "nezha_yao_1145_p.pak"));
        Touch(Path.Combine("超级识破", "b1", "Binaries", "Win64", "ue4ss", "Mods", "BPML.lua"));

        // 显式给出顺序：Directory.EnumerateFiles 的返回顺序由文件系统决定，
        // 断言"首个出现即单元顺序"必须用确定的入参，否则测试会随机失败。
        var units = ArchiveImportUnitResolver.Partition(
        [
            Path.Combine(_root, "超级披挂", "b1", "Content", "Paks", "~mods", "超级披挂.pak"),
            Path.Combine(_root, "黑神话哪吒", "1.哪吒皮肤mod", "nezha_yao_110_p.pak"),
            Path.Combine(_root, "黑神话哪吒", "1.哪吒皮肤mod", "nezha_yao_1145_p.pak"),
            Path.Combine(_root, "超级识破", "b1", "Binaries", "Win64", "ue4ss", "Mods", "BPML.lua"),
        ], _root);

        Assert.Equal(3, units.Count);
        Assert.Equal(["超级披挂", "黑神话哪吒", "超级识破"], units.Select(u => u.Name));
        Assert.Equal(2, units.Single(u => u.Name == "黑神话哪吒").Files.Count);
    }

    [Fact]
    public void Partition_FilesLooseAtArchiveRoot_AllGoToRootUnit()
    {
        Touch("SomeMod.pak");
        Touch("readme.txt");

        var unit = Assert.Single(ArchiveImportUnitResolver.Partition(AllFiles(), _root));

        Assert.Equal(ArchiveImportUnitResolver.RootUnitName, unit.Name);
        Assert.Equal(2, unit.Files.Count);
    }

    [Fact]
    public void Partition_NestedSubfolders_StayInsideTheirTopLevelUnit()
    {
        // 单元内部再深也不该被拆开：一个 MOD 的自有层级结构必须整体保留。
        Touch(Path.Combine("ModA", "a", "b", "c", "deep.pak"));
        Touch(Path.Combine("ModA", "top.pak"));

        var unit = Assert.Single(ArchiveImportUnitResolver.Partition(AllFiles(), _root));

        Assert.Equal("ModA", unit.Name);
        Assert.Equal(2, unit.Files.Count);
    }

    [Fact]
    public void ResolveUnitName_FirstSegmentIsAFile_FallsBackToRootUnit()
    {
        // 压缩包里 "SomeMod.pak/..." 这种形状不存在，但要保证"第一段不是目录"时
        // 不会被误当成单元名。
        Touch("SomeMod.pak");

        var name = ArchiveImportUnitResolver.ResolveUnitName(
            Path.Combine(_root, "SomeMod.pak"), _root);

        Assert.Equal(ArchiveImportUnitResolver.RootUnitName, name);
    }

    [Fact]
    public void ResolveUnitName_OutsideRoot_FallsBackToRootUnit()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".pak");

        var name = ArchiveImportUnitResolver.ResolveUnitName(outside, _root);

        Assert.Equal(ArchiveImportUnitResolver.RootUnitName, name);
    }

    [Fact]
    public void ResolveUnitName_EmptyFilePath_Throws()
        => Assert.Throws<ArgumentException>(() => ArchiveImportUnitResolver.ResolveUnitName("  ", _root));

    [Fact]
    public void Partition_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ArchiveImportUnitResolver.Partition(null!, _root));
        Assert.Throws<ArgumentNullException>(() => ArchiveImportUnitResolver.Partition(AllFiles(), null!));
    }

    // ─── 与冲突检测的联动 ───

    [Fact]
    public void Partition_TwoModsSharingOneFilePath_ProduceTwoOwners()
    {
        // 这是"冲突检测没生效"的根因：两个 MOD 各自带一份 ue4ss 的 mods.json。
        // 并包时它们同属一个 PackageKey，DetectConflicts 去重后成为 0 个冲突；
        // 按单元切开后是两个包，冲突才立得住。
        Touch(Path.Combine("超级筋斗云", "b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json"));
        Touch(Path.Combine("超级识破", "b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json"));

        var units = ArchiveImportUnitResolver.Partition(AllFiles(), _root);

        Assert.Equal(2, units.Count);
        Assert.All(units, u => Assert.Single(u.Files));
        Assert.NotEqual(units[0].Name, units[1].Name);
    }

    private IEnumerable<string> AllFiles()
        => Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories);

    // ─── "整个输入其实只是一个 MOD"的识别（T2 回归）───

    [Fact]
    public void LooksLikeSingleModLayout_TopLevelIsB1_ReturnsTrue()
    {
        // 用户单独把解压后的「超级识破」文件夹拖进来，文件夹内部直接是 b1\Binaries\Win64\...
        // 顶层第一段是项目名 b1 而不是 MOD 名，退回单单元才能保住 MOD 的真名。
        Touch(Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "BPML.lua"));

        var units = ArchiveImportUnitResolver.Partition(AllFiles(), _root);

        Assert.Equal("b1", Assert.Single(units).Name);
        Assert.True(ArchiveImportUnitResolver.LooksLikeSingleModLayout(units));
    }

    [Fact]
    public void LooksLikeSingleModLayout_TopLevelIsBinaries_ReturnsTrue()
    {
        // 同一形状的另一半：脚本把锚点又剥了一层，压缩包顶层直接是 Binaries\Win64\...
        Touch(Path.Combine("Binaries", "Win64", "dwmapi.dll"));

        Assert.True(ArchiveImportUnitResolver.LooksLikeSingleModLayout(
            ArchiveImportUnitResolver.Partition(AllFiles(), _root)));
    }

    [Fact]
    public void LooksLikeSingleModLayout_TopLevelAreModNames_ReturnsFalse()
    {
        // 0916.zip 的形状：每个顶层目录都是 MOD 名，不能退回单单元，
        // 否则"一个文件夹 = 一个 MOD"的结构会再次丢掉、冲突检测随之失效。
        Touch(Path.Combine("超级筋斗云", "b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json"));
        Touch(Path.Combine("超级识破", "b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json"));

        var units = ArchiveImportUnitResolver.Partition(AllFiles(), _root);

        Assert.Equal(2, units.Count);
        Assert.False(ArchiveImportUnitResolver.LooksLikeSingleModLayout(units));
    }

    [Fact]
    public void LooksLikeSingleModLayout_RootUnitOnly_ReturnsFalse()
    {
        // 散落在根下的文件（没有顶层目录）不该被判成"自带游戏目录层级"。
        Touch("SomeMod.pak");

        Assert.False(ArchiveImportUnitResolver.LooksLikeSingleModLayout(
            ArchiveImportUnitResolver.Partition(AllFiles(), _root)));
    }

    [Fact]
    public void LooksLikeSingleModLayout_NullUnits_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => ArchiveImportUnitResolver.LooksLikeSingleModLayout(null!));

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
