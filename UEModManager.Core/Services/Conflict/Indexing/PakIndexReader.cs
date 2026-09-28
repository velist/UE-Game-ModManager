using System;
using System.Collections.Generic;
using System.IO;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Services.Conflict.Indexing;

/// <summary>
/// UE .pak 索引读取器。
///
/// 格式细节（已在仓库里的 15 个真实 pak 上验证，见
/// <c>docs/architecture/overview.md</c> 的容器格式一节）：
/// 从文件末尾向前 300 字节内找魔数得到页脚起点 <c>M</c>，页脚里依次是
/// EncryptionKeyGuid(16) + bEncryptedIndex(1) + Magic(4) + Version(4)，
/// 之后才是 IndexOffset / IndexSize。
///
/// 黑神话·悟空的 pak 在 Version 之后多 2 字节，IndexOffset 因此要再往后挪 2 字节。
/// 判定方式是"先按标准布局读、校验越界就换变体"，而不是读某一版特有字段 ——
/// 变体布局的样本里那 2 字节是版本号 10 的副本，按字段名匹配会随版本漂移，
/// 而"Offset 落点是否在页脚之前"是格式本身的不变量。
/// </summary>
public sealed class PakIndexReader : IContainerIndexReader
{
    /// <summary>支持的 pak 版本。v8/v9 是内联条目布局，v10/v11 是编码条目布局。</summary>
    private const int MinSupportedVersion = 8;
    private const int MaxSupportedVersion = 11;

    /// <summary>页脚里 Magic 之前固定的字节数：EncryptionKeyGuid(16) + bEncryptedIndex(1)。</summary>
    private const int FooterPrefixSize = 17;

    /// <summary>变体布局在 Version 之后多出的字节数。</summary>
    private const int VariantExtraBytes = 2;

    public ContainerIndexResult ReadIndex(Stream stream, string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sourceFile);

        try
        {
            var footer = ContainerFormat.FindPakFooter(stream);
            if (footer < 0)
                return Fail("找不到 pak 页脚魔数", -1);

            var reader = new ContainerBinaryReader(stream);

            // bEncryptedIndex 紧贴 Magic 之前
            if (footer < FooterPrefixSize)
                return Fail("pak 页脚不完整", -1);

            reader.Position = footer - 1;
            var encryptedIndex = reader.ReadByte();
            if (encryptedIndex != 0)
                return new ContainerIndexResult(IndexStatus.Encrypted, "容器索引已加密", [], -1);

            reader.Position = footer + 4;
            var version = reader.ReadInt32();
            if (version < MinSupportedVersion || version > MaxSupportedVersion)
                return new ContainerIndexResult(
                    IndexStatus.Unsupported, $"不支持的 pak 版本 {version}", [], version);

            var layout = ResolveIndexLayout(reader, footer, version);
            if (layout is null)
                return new ContainerIndexResult(
                    IndexStatus.Unsupported, "pak 索引偏移越界，布局无法识别", [], version);

            var (indexOffset, indexSize) = layout.Value;

            reader.Position = indexOffset;
            var mountPoint = reader.ReadFString();

            if (version >= 10)
                return ReadModernIndex(reader, version, mountPoint, indexOffset, indexSize, sourceFile);

            return ReadLegacyIndex(reader, version, mountPoint, sourceFile);
        }
        catch (Exception ex)
        {
            // 一个 MOD 的容器格式异常不该让整个分析失败。
            return Fail(ex.Message, -1);
        }
    }

    /// <summary>
    /// 试出索引偏移。标准布局读不通就按变体布局再读一次，都不成立返回 null。
    /// </summary>
    private static (long Offset, long Size)? ResolveIndexLayout(
        ContainerBinaryReader reader, long footer, int version)
    {
        foreach (var extra in new[] { 0, VariantExtraBytes })
        {
            var start = footer + 8 + extra;
            if (start + 16 > reader.Length) continue;

            reader.Position = start;
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();

            // 索引必须真的在文件里、且不能越过页脚：越界说明布局猜错了。
            if (offset > 0 && size >= 0 && offset + size <= footer - FooterPrefixSize
                && offset < reader.Length)
                return (offset, size);
        }

        _ = version;
        return null;
    }

    /// <summary>v10 / v11：主索引里只有编码条目表 + 目录索引偏移，路径要另去目录索引读。</summary>
    private ContainerIndexResult ReadModernIndex(
        ContainerBinaryReader reader, int version, string mountPoint,
        long indexOffset, long indexSize, string sourceFile)
    {
        // MountPoint 之后：NumEntries / PathHashSeed / bHasPathHashIndex 等头部字段。
        // 本模块只关心"目录索引在哪"，但仍然要按格式把前面的字段读过去。
        reader.ReadInt32();                 // NumEntries
        reader.ReadUInt64();                // PathHashSeed

        var hasPathHashIndex = reader.ReadInt32();
        if (hasPathHashIndex == 1)
        {
            reader.ReadInt64();             // PathHashIndexOffset
            reader.ReadInt64();             // PathHashIndexSize
            reader.Skip(20);                // PathHashIndexHash
        }

        var hasFullDirectoryIndex = reader.ReadInt32();
        long directoryIndexOffset = 0;
        if (hasFullDirectoryIndex == 1)
        {
            directoryIndexOffset = reader.ReadInt64();
            reader.ReadInt64();             // FullDirectoryIndexSize
            reader.Skip(20);                // FullDirectoryIndexHash
        }

        var encodedEntrySize = reader.ReadInt32();
        if (encodedEntrySize < 0)
            return Fail("编码条目表长度为负", version);

        var encodedEntries = reader.ReadBytes(encodedEntrySize);
        var encodedEntriesStart = reader.Position - encodedEntrySize;

        // 非编码条目（实测为 0）：路径挂在后面的 FullDirectoryIndex 上，
        // 这里只把 FPakEntry 数组跳过去。
        var inlineFileCount = reader.ReadInt32();
        if (inlineFileCount < 0)
            return Fail("内联条目数为负", version);
        for (var i = 0; i < inlineFileCount; i++)
        {
            var entry = ReadPakEntry(reader);
            reader.Position += PackedEntryHeaderSize(entry);
        }

        _ = indexOffset;
        _ = indexSize;

        if (hasFullDirectoryIndex != 1)
            return new ContainerIndexResult(
                IndexStatus.Partial, "pak 没有目录索引，读不出资产路径", [], version);

        if (directoryIndexOffset <= 0 || directoryIndexOffset >= reader.Length)
            return Fail("目录索引偏移越界", version);

        reader.Position = directoryIndexOffset;
        var entries = ReadDirectoryIndex(
            reader, mountPoint, encodedEntries, encodedEntriesStart, sourceFile);

        return new ContainerIndexResult(IndexStatus.Complete, null, entries, version);
    }

    /// <summary>
    /// 读 FullDirectoryIndex：按目录分组，每个文件给出一个 EntryLocation，
    /// 指向编码条目表里的字节偏移。
    /// </summary>
    private static List<AssetEntry> ReadDirectoryIndex(
        ContainerBinaryReader reader, string mountPoint,
        byte[] encodedEntries, long encodedEntriesStart, string sourceFile)
    {
        var entries = new List<AssetEntry>();
        var directoryCount = reader.ReadInt32();
        if (directoryCount < 0)
            throw new InvalidDataException($"目录数为负: {directoryCount}");

        for (var d = 0; d < directoryCount; d++)
        {
            var directoryName = reader.ReadFString();
            var fileCount = reader.ReadInt32();
            if (fileCount < 0)
                throw new InvalidDataException($"目录内文件数为负: {fileCount}");

            for (var f = 0; f < fileCount; f++)
            {
                var fileName = reader.ReadFString();
                var entryLocation = reader.ReadInt32();

                // int.MinValue 表示条目已删除；其他负值是"非编码条目"下标，
                // 本轮样本没有，跳过但不算失败。
                if (entryLocation == int.MinValue || entryLocation < 0) continue;

                var displayPath = AssetKeyNormalizer.DisplayPath(mountPoint, directoryName, fileName);
                if (displayPath.Length == 0) continue;

                if (!TryReadEncodedEntry(encodedEntries, encodedEntriesStart, entryLocation, out var packed))
                    continue;

                entries.Add(ToAssetEntry(displayPath, packed, sourceFile));
            }
        }

        return entries;
    }

    /// <summary>编码条目：从 EntryLocation 给出的字节偏移解析出大小与压缩信息。</summary>
    private static bool TryReadEncodedEntry(
        byte[] buffer, long bufferStart, int entryLocation, out PackedPakEntry entry)
    {
        entry = default;
        if (entryLocation < 0 || entryLocation >= buffer.Length) return false;

        var flags = BitConverter.ToUInt32(buffer, entryLocation);
        var offset = entryLocation + 4;

        var offsetIs32Bit = (flags & (1u << 31)) != 0;
        var uncompressedIs32Bit = (flags & (1u << 30)) != 0;
        var sizeIs32Bit = (flags & (1u << 29)) != 0;
        var compressionMethodIndex = (int)((flags >> 23) & 0x3F);
        var encrypted = (flags & (1u << 22)) != 0;
        // 压缩块数不可省：条目数据前面那份 FPakEntry 头副本的长度随它变化，
        // 算成 0 会让指纹从错误的字节位置开始读（压缩条目上必然如此）。
        var compressionBlockCount = (int)((flags >> 6) & 0xFFFF);

        if (!TryReadVariable(buffer, ref offset, offsetIs32Bit, out var dataOffset)) return false;
        if (!TryReadVariable(buffer, ref offset, uncompressedIs32Bit, out var uncompressedSize)) return false;

        long storedSize;
        if (compressionMethodIndex != 0)
        {
            if (!TryReadVariable(buffer, ref offset, sizeIs32Bit, out storedSize)) return false;
        }
        else
        {
            // 未压缩时不存在 Size 字段，存储大小等于未压缩大小。
            storedSize = uncompressedSize;
        }

        _ = bufferStart;
        entry = new PackedPakEntry(
            dataOffset, uncompressedSize, storedSize, compressionMethodIndex, encrypted, compressionBlockCount);
        return true;
    }

    private static bool TryReadVariable(byte[] buffer, ref int offset, bool is32Bit, out long value)
    {
        value = 0;
        var width = is32Bit ? 4 : 8;
        if (offset + width > buffer.Length) return false;

        value = is32Bit
            ? BitConverter.ToUInt32(buffer, offset)
            : BitConverter.ToInt64(buffer, offset);
        offset += width;
        return true;
    }

    /// <summary>v8 / v9：主索引里直接是 "文件名 + FPakEntry" 的内联数组。</summary>
    private ContainerIndexResult ReadLegacyIndex(
        ContainerBinaryReader reader, int version, string mountPoint, string sourceFile)
    {
        var entryCount = reader.ReadInt32();
        if (entryCount < 0)
            return Fail("条目数为负", version);

        var entries = new List<AssetEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var fileName = reader.ReadFString();
            var packed = ReadPakEntry(reader);

            var displayPath = AssetKeyNormalizer.NormalizePath(mountPoint + fileName);
            if (displayPath.Length == 0) continue;

            entries.Add(ToAssetEntry(displayPath, packed, sourceFile));
        }

        return new ContainerIndexResult(IndexStatus.Complete, null, entries, version);
    }

    /// <summary>读取一个内联 FPakEntry 的固定部分。</summary>
    private static PackedPakEntry ReadPakEntry(ContainerBinaryReader reader)
    {
        var offset = reader.ReadInt64();
        var size = reader.ReadInt64();
        var uncompressedSize = reader.ReadInt64();
        var compressionMethodIndex = reader.ReadInt32();
        reader.Skip(20);                    // Hash
        var blockCount = 0;
        if (compressionMethodIndex != 0)
        {
            blockCount = reader.ReadInt32();
            if (blockCount < 0)
                throw new InvalidDataException($"压缩块数为负: {blockCount}");
            reader.Skip(16L * blockCount);
        }
        reader.ReadByte();                  // bEncrypted
        reader.ReadUInt32();                // CompressionBlockSize
        return new PackedPakEntry(offset, uncompressedSize, size, compressionMethodIndex, false, blockCount);
    }

    /// <summary>
    /// 条目数据前面的那份 FPakEntry 头副本有多长。
    ///
    /// 文件里的实际数据在 <c>Offset + 头长度</c> 处，算指纹时必须先跳过去 ——
    /// 少跳或多跳都会让指纹变成"数据区随机某一段的哈希"，
    /// 两个真正相同的文件会算出不同指纹，冲突检测于是报出假的冲突组。
    /// </summary>
    public static long PackedEntryHeaderSize(PackedPakEntry entry)
    {
        var size = 8L + 8 + 8 + 4 + 20;                 // Offset/Size/UncompressedSize/Method/Hash
        if (entry.CompressionMethodIndex != 0) size += 4 + 16L * entry.BlockCount;
        size += 1 + 4;                                  // bEncrypted / CompressionBlockSize
        return size;
    }

    private static AssetEntry ToAssetEntry(string displayPath, PackedPakEntry packed, string sourceFile)
        => new(
            AssetKey: AssetKeyNormalizer.NormalizeAssetKey(displayPath),
            DisplayPath: displayPath,
            Fingerprint: null,
            FingerprintKind: FingerprintKind.None,
            Size: packed.UncompressedSize,
            DataOffset: packed.DataOffset,
            StoredSize: packed.StoredSize,
            CompressionMethod: packed.CompressionMethodIndex,
            SourceFile: sourceFile);

    private static ContainerIndexResult Fail(string detail, int version)
        => new(IndexStatus.Failed, detail, [], version);

    /// <summary>索引里读出来的条目信息（还没有指纹）。</summary>
    public readonly record struct PackedPakEntry(
        long DataOffset, long UncompressedSize, long StoredSize,
        int CompressionMethodIndex, bool Encrypted, int BlockCount);
}
