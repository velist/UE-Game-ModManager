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
    public void 版本2返回Unsupported()
    {
        // v1/v2 的 FPakEntry 带时间戳、没有加密与块大小字段，没有样本，不读。
        var result = Read(BuildLegacy(MountPoint, Sample, version: 2));

        Assert.Equal(IndexStatus.Unsupported, result.Status);
        Assert.Contains("2", result.Detail);
    }

    [Theory]
    [InlineData(3)]   // 页脚里 Magic 之前什么都没有
    [InlineData(4)]   // 多了 bEncryptedIndex
    [InlineData(7)]   // 再多 EncryptionKeyGuid
    [InlineData(8)]   // Magic 之后开始带压缩方法名
    [InlineData(9)]
    public void 旧版内联布局读回全部路径与大小(int version)
    {
        var result = Read(BuildLegacy(MountPoint, Sample, version));

        Assert.Equal(IndexStatus.Complete, result.Status);
        Assert.Equal(version, result.FormatVersion);
        Assert.Equal(3, result.Entries.Count);

        var texture = Assert.Single(result.Entries, e => e.DisplayPath.EndsWith(".uasset")
                                                         && e.AssetKey.EndsWith("t_wukong_born_cloth_ring_d"));
        Assert.Equal("b1/content/00mainhz/characters/wukong/textures/equip/born/t_wukong_born_cloth_ring_d.uasset",
            texture.DisplayPath);
        Assert.Equal(4, texture.Size);
        Assert.Contains(result.Entries, e => e.AssetKey.EndsWith("sk_wukong_body_dashengtao") && e.Size == 3);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    public void 旧版索引加密时返回Encrypted(int version)
    {
        var result = Read(BuildLegacy(MountPoint, Sample, version, encryptedIndex: 1));

        Assert.Equal(IndexStatus.Encrypted, result.Status);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void 版本3没有加密标志_索引末字节非零不算加密()
    {
        // v3 的页脚从 Magic 开始，Magic 前一个字节是索引的最后一个字节
        // （最后一个条目的 CompressionBlockSize 高位）。把它当成 bEncryptedIndex 读，
        // 这个 pak 就会被误报成"索引已加密"。
        var result = Read(BuildLegacy(MountPoint, Sample, version: 3, compressionBlockSize: 0xFF00_0000));

        Assert.Equal(IndexStatus.Complete, result.Status);
        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public void 空壳pak索引偏移为零时读作Complete且没有条目()
    {
        // IoStore MOD（retoc 等工具产出）的 .pak 只是个空壳：内容全在同名 .utoc/.ucas 里，
        // 没有数据区，索引从文件头开始。它必须读作 Complete —— 判成"布局无法识别"
        // 会把整个 MOD 拖进"无法分析"，连读得出来的 .utoc 也被掩盖。
        var result = Read(Build("../../../", []));

        Assert.Equal(IndexStatus.Complete, result.Status);
        Assert.Equal(11, result.FormatVersion);
        Assert.Empty(result.Entries);
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
}