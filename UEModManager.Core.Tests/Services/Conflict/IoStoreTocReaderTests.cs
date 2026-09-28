using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Services.Conflict.Indexing;
using UEModManager.Services.Conflict.Model;
using static UEModManager.Core.Tests.Services.Conflict.UtocFixtureBuilder;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// <see cref="IoStoreTocReader"/> 的格式回归。
///
/// 夹具是"按文档定义写出来"的 utoc 字节，读取器按同一定义读回；
/// 目录树用 FirstChild/NextSibling 串、名字全部下沉到字符串表，
/// 两边只要有一处不一致，路径就会拼错。
/// </summary>
public class IoStoreTocReaderTests
{
    private const string MountPoint = "../../../SB/Content/Art/Character/PC/";

    private static readonly FixtureChunk[] Sample =
    [
        new("CH_P_EVE_16/Textures/", "TEX_Shoes_0775_N.uasset", Hash(0x11), 0xA1),
        new("CH_P_EVE_16/Textures/", "TEX_Shoes_0775_D.uasset", Hash(0x22), 0xA1),
        new("CH_P_EVE_16/Textures/", "TEX_Shoes_0775_ARM.uasset", Hash(0x33), 0xA1),
        new("CH_P_EVE_09/", "CH_P_EVE_09.uasset", Hash(0x44), 0xA2),
        new("CH_P_EVE_09/Blueprints/", "CH_P_EVE_09_AnimBP.uasset", Hash(0x55), 0xA2),
    ];

    /// <summary>
    /// 包 ID 键是包 ID 那 8 个字节的**原始**十六进制。
    /// UE 是 memcpy 写入（小端），所以 A1 在文件里是 A1 00 .. 00。
    /// 这里照实算出来，避免把"显示顺序"当成格式的一部分。
    /// </summary>
    private static string PackageIdKey(ulong packageId)
        => Convert.ToHexString(BitConverter.GetBytes(packageId)).ToLowerInvariant();

    /// <summary>第 <paramref name="seed"/> 个字节打底的 32 字节 ChunkHash。</summary>
    private static byte[] Hash(byte seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    private static ContainerIndexResult Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new IoStoreTocReader().ReadIndex(stream, "sample.utoc");
    }

    [Fact]
    public void 读回全部路径与内容指纹()
    {
        var result = Read(Build(MountPoint, Sample));

        Assert.Equal(IndexStatus.Complete, result.Status);
        Assert.Equal(2, result.FormatVersion);
        Assert.Equal(5, result.Entries.Count);

        var texture = result.Entries.Single(e => e.AssetKey.EndsWith("tex_shoes_0775_n"));
        // 路径要剥掉 ../、转小写、合并重复斜杠；指纹直接来自 ChunkMetas。
        Assert.Equal("sb/content/art/character/pc/ch_p_eve_16/textures/tex_shoes_0775_n.uasset",
            texture.DisplayPath);
        Assert.Equal("sb/content/art/character/pc/ch_p_eve_16/textures/tex_shoes_0775_n",
            texture.AssetKey);
        Assert.Equal(Convert.ToHexString(Hash(0x11)), texture.Fingerprint);
        Assert.Equal(FingerprintKind.ContentHash, texture.FingerprintKind);
        // utoc 不读 .ucas，所以没有数据区信息。
        Assert.Equal(-1, texture.DataOffset);
        Assert.Equal(-1, texture.StoredSize);
    }

    [Fact]
    public void 同一资产的不同扩展名归一化后共用一个键()
    {
        var result = Read(Build(MountPoint,
        [
            new("CH_P_EVE_09/", "CH_P_EVE_09.uasset", Hash(0x44), 0xA2),
            new("CH_P_EVE_09/", "CH_P_EVE_09.uexp", Hash(0x45), 0xA2),
            new("CH_P_EVE_09/", "CH_P_EVE_09.ubulk", Hash(0x46), 0xA2),
        ]));

        Assert.Equal(3, result.Entries.Count);
        Assert.Single(result.Entries.Select(e => e.AssetKey).Distinct());
    }

    [Fact]
    public void 根目录下的文件不出现双斜杠()
    {
        var result = Read(Build(MountPoint, [new("", "Root.uasset", Hash(0x77), 0xA3)]));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("sb/content/art/character/pc/root.uasset", entry.DisplayPath);
        Assert.DoesNotContain("//", entry.DisplayPath, StringComparison.Ordinal);
    }

    [Fact]
    public void 加密标志返回Encrypted()
    {
        var result = Read(Build(MountPoint, Sample, encrypted: true));

        Assert.Equal(IndexStatus.Encrypted, result.Status);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void 没有目录索引时降级为Partial并按包ID出条目()
    {
        var result = Read(Build(MountPoint, Sample, withDirectoryIndex: false));

        Assert.Equal(IndexStatus.Partial, result.Status);
        Assert.NotNull(result.Detail);
        // 路径没有了，但条目还在：5 个条目 → 2 个不同包 ID。
        Assert.Equal(
            [PackageIdKey(0xA1), PackageIdKey(0xA2)],
            result.Entries.Select(e => e.AssetKey).Distinct().OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(result.Entries, e => Assert.StartsWith("包 ID ", e.DisplayPath));
        // 指纹仍然可用，否则这两个包会被保守地当成冲突。
        Assert.All(result.Entries, e => Assert.Equal(FingerprintKind.ContentHash, e.FingerprintKind));
    }

    [Fact]
    public void 不支持的版本返回Unsupported()
    {
        var result = Read(Build(MountPoint, Sample, version: 1));

        Assert.Equal(IndexStatus.Unsupported, result.Status);
        Assert.Empty(result.Entries);
        Assert.Equal(1, result.FormatVersion);
    }

    [Fact]
    public void 截断文件降级为Failed且不抛异常()
    {
        var bytes = Build(MountPoint, Sample);
        var truncated = bytes.Take(bytes.Length / 2).ToArray();

        var result = Read(truncated);

        Assert.Equal(IndexStatus.Failed, result.Status);
        Assert.Empty(result.Entries);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void 不是utoc的文件返回Unsupported()
    {
        var result = Read([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18]);

        Assert.Equal(IndexStatus.Unsupported, result.Status);
        Assert.Empty(result.Entries);
    }

    /// <summary>
    /// 真实样本 smoke：仓库历史里有一个剑星 MOD 的 utoc。
    /// 文件不在时直接通过（不把仓库内容当成测试前提）。
    /// </summary>
    [Fact]
    public void 真实utoc样本读出14个文件条目()
    {
        var bytes = TryGetSampleUtoc();
        if (bytes is null) return;

        var result = Read(bytes);

        Assert.Equal(IndexStatus.Complete, result.Status);
        // 目录索引只引用 14 个 TOC 条目（UserData 1..14）：第 0 个是容器级 chunk，
        // 不是文件。TocEntryCount 是 15，但能给出路径的是 14 个。
        Assert.Equal(14, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.AssetKey.EndsWith("tex_shoes_0775_d"));
        Assert.Contains(result.Entries, e => e.AssetKey.EndsWith("mat_bottom2_0775_add"));
        Assert.All(result.Entries, e => Assert.Equal(FingerprintKind.ContentHash, e.FingerprintKind));
        Assert.All(result.Entries, e => Assert.StartsWith("sb/content/art/character/pc/", e.DisplayPath));
    }

    private static byte[]? TryGetSampleUtoc()
    {
        var path = Path.Combine(Path.GetTempPath(), "sample.utoc");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
