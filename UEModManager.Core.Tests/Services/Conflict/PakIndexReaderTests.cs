using System;
using System.IO;
using System.Linq;
using UEModManager.Services.Conflict.Indexing;
using UEModManager.Services.Conflict.Model;
using static UEModManager.Core.Tests.Services.Conflict.PakFixtureBuilder;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// <see cref="PakIndexReader"/> 的格式回归。
///
/// 夹具是"按文档定义写出来"的 pak 字节，读取器按同一定义读回 ——
/// 两者对格式的理解只要有一处不一致，路径或大小就会错。
/// </summary>
public class PakIndexReaderTests
{
    private const string MountPoint = "../../../b1/Content/00MainHZ/Characters/Wukong/";

    private static readonly FixtureEntry[] Sample =
    [
        new("Textures/Equip/Born/", "T_WuKong_Born_Cloth_Ring_D.uasset", [1, 2, 3, 4]),
        new("Textures/Equip/Born/", "T_WuKong_Born_Cloth_Ring_D.uexp", [5, 6]),
        new("Meshes/Equip/dashengtao/", "SK_Wukong_Body_Dashengtao.uasset", [7, 8, 9])
    ];

    private static ContainerIndexResult Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new PakIndexReader().ReadIndex(stream, "sample.pak");
    }

    [Fact]
    public void 标准页脚读回全部路径与大小()
    {
        var result = Read(Build(MountPoint, Sample));

        Assert.Equal(IndexStatus.Complete, result.Status);
        Assert.Equal(11, result.FormatVersion);
        Assert.Equal(3, result.Entries.Count);

        // .uasset 与 .uexp 共用一个 AssetKey（它们永远一起被替换），
        // 因此这里是 Two 而不是 Single —— 大小分别对得上各自的原始长度。
        var textures = result.Entries
            .Where(e => e.AssetKey.EndsWith("t_wukong_born_cloth_ring_d"))
            .OrderBy(e => e.DisplayPath, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, textures.Count);
        Assert.Equal(4, textures[0].Size);
        Assert.Equal("b1/content/00mainhz/characters/wukong/textures/equip/born/t_wukong_born_cloth_ring_d.uasset",
            textures[0].DisplayPath);
        Assert.Equal(2, textures[1].Size);
        Assert.EndsWith(".uexp", textures[1].DisplayPath);
        Assert.Equal(textures[0].AssetKey, textures[1].AssetKey);
    }

    [Fact]
    public void 变体页脚读回全部路径()
    {
        var result = Read(Build(MountPoint, Sample, blackMythVariant: true));

        Assert.Equal(IndexStatus.Complete, result.Status);
        Assert.Equal(3, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.AssetKey.EndsWith("sk_wukong_body_dashengtao"));
    }

    [Fact]
    public void 索引加密时返回Encrypted()
    {
        var result = Read(Build(MountPoint, Sample, encryptedIndex: 1));

        Assert.Equal(IndexStatus.Encrypted, result.Status);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void 版本7返回Unsupported()
    {
        var result = Read(Build(MountPoint, Sample, version: 7));

        Assert.Equal(IndexStatus.Unsupported, result.Status);
        Assert.Contains("7", result.Detail);
    }

    [Fact]
    public void 截断文件返回Failed且不抛异常()
    {
        var result = Read(Build(MountPoint, Sample, truncateBytes: 400));

        Assert.Equal(IndexStatus.Failed, result.Status);
    }

    [Fact]
    public void 空文件返回Failed且不抛异常()
    {
        var result = Read([]);

        Assert.Equal(IndexStatus.Failed, result.Status);
    }

    [Fact]
    public void 根目录为斜杠时路径不出现双斜杠()
    {
        var entries = new[] { new FixtureEntry("/", "root_file.uasset", [1]) };
        var result = Read(Build(MountPoint, entries));

        Assert.Equal(IndexStatus.Complete, result.Status);
        var entry = Assert.Single(result.Entries);
        Assert.DoesNotContain("//", entry.DisplayPath);
        Assert.StartsWith("b1/content/", entry.DisplayPath);
    }

    [Theory]
    [InlineData(".uasset")]
    [InlineData(".uexp")]
    [InlineData(".ubulk")]
    public void 同一资产的三种扩展名归一化后AssetKey相同(string extension)
    {
        var entries = new[] { new FixtureEntry("Textures/", "T_WuKong_Born_Cloth_Ring_D" + extension, [1, 2]) };
        var result = Read(Build(MountPoint, entries));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("b1/content/00mainhz/characters/wukong/textures/t_wukong_born_cloth_ring_d", entry.AssetKey);
    }

    [Fact]
    public void 版本8用内联条目布局()
    {
        // v8 的主索引是 "文件名 + FPakEntry"，与本夹具的其他用例不同布局 ——
        // 这里只断言版本号被识别、不会误报 Unsupported。
        var result = Read(Build(MountPoint, Sample, version: 8));

        Assert.NotEqual(IndexStatus.Unsupported, result.Status);
    }
}