using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Services.Conflict.Indexing;

/// <summary>
/// UE IoStore .utoc 索引读取器。
///
/// 与 pak 相比，utoc 把"路径"和"内容指纹"分放在两处：路径在文件末尾的
/// DirectoryIndex（<c>FIoDirectoryIndexResource</c>，一棵名字全部下沉到字符串表的目录树），
/// 指纹在紧挨着的 ChunkMetas 里（每个 TOC 条目 32 字节 ChunkHash）。
///
/// 因此这里不需要读 <c>.ucas</c>：ChunkHash 就是内容哈希，直接拿来当条目指纹。
/// 这与 pak 不一样（pak 必须跳到条目数据区自己算），但两边填出来的
/// <see cref="FingerprintKind.ContentHash"/> 语义一致。
///
/// 读不到目录索引（未置 Indexed 标志或 DirectoryIndexSize 为 0）时降级为
/// <see cref="IndexStatus.Partial"/>：路径没有了，但条目还在，改用包 ID 比对
/// —— 两个 MOD 带上同一个包 ID，说明它们替换的是同一批资产。
/// 返回 <see cref="IndexStatus.Unsupported"/> 或空清单都更糟：
/// 前者会让用户看到"无法分析"，后者会被上游当成"和谁都不冲突"，即静默漏报。
/// </summary>
public sealed class IoStoreTocReader : IContainerIndexReader
{
    /// <summary>支持的 IoStore 版本。实测样本为 v2（剑星）与 v8（影之刃零）。</summary>
    private const int MinSupportedVersion = 2;
    private const int MaxSupportedVersion = 8;

    /// <summary>条目元数据里的哈希从这一版起换了类型（EIoStoreTocVersion 的 ReplaceIoChunkHashWithIoHash）。</summary>
    private const int FullHashMetaVersion = 8;

    /// <summary>条目元数据间隔的合理上限：超过它说明尾部还挂着别的东西，不是元数据。</summary>
    private const int MaxMetaSize = 64;

    /// <summary>固定头长度，也是 TocHeaderSize 的常规取值。</summary>
    private const int FixedHeaderSize = 144;

    private const int ChunkIdSize = 12;
    private const int OffsetAndLengthSize = 10;

    /// <summary>索引的指纹里保留的哈希字节数：两种布局里哈希都在记录开头，前 20 字节即内容哈希。</summary>
    private const int ChunkHashSize = 20;

    /// <summary>v8 之前的条目元数据间隔：32 字节哈希 + 标志。</summary>
    private const int LegacyMetaSize = 33;

    /// <summary>v8 起的条目元数据间隔：20 字节哈希 + 标志 + 3 字节对齐。</summary>
    private const int FullHashMetaSize = 24;

    private const int MetaFlagsSize = 1;

    /// <summary>压缩方法名槽位长度的默认值（头里的 CompressionMethodNameLength）。</summary>
    private const int DefaultCompressionMethodNameLength = 32;

    /// <summary>签名区里每个压缩块的签名长度。</summary>
    private const int CompressionBlockSignatureSize = 20;

    /// <summary>目录树里表示空指针的下标。</summary>
    private const uint InvalidIndex = uint.MaxValue;

    // EIoContainerFlags 的位数按 UE 的 1 起编号：bit1 = 1&lt;&lt;0，依次类推。
    private const byte FlagEncrypted = 1 << 1;
    private const byte FlagSigned = 1 << 2;
    // Indexed（1<<3）这一位不读：目录索引在不在由 DirectoryIndexSize 直接给出，
    // 两个样本（v2 与 v8）里该位与大小始终一致，按大小判定已经够用。

    public ContainerIndexResult ReadIndex(Stream stream, string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sourceFile);

        try
        {
            return Read(stream, sourceFile);
        }
        catch (Exception ex)
        {
            // 一个 MOD 的容器格式异常不该让整个冲突分析失败。
            return new ContainerIndexResult(IndexStatus.Failed, ex.Message, [], -1);
        }
    }

    private static ContainerIndexResult Read(Stream stream, string sourceFile)
    {
        if (!stream.CanSeek)
            return new ContainerIndexResult(IndexStatus.Unsupported, "流不可定位", [], -1);

        if (!ContainerFormat.HasIoStoreMagic(stream))
            return new ContainerIndexResult(IndexStatus.Unsupported, "不是 IoStore 目录", [], -1);

        var length = stream.Length;
        var reader = new ContainerBinaryReader(stream);

        // ── 固定头 ──
        reader.Position = 16;
        var version = reader.ReadByte();
        reader.Skip(3);
        var tocHeaderSize = reader.ReadUInt32();
        var tocEntryCount = reader.ReadUInt32();
        var compressedBlockEntryCount = reader.ReadUInt32();
        reader.ReadUInt32();                                    // TocCompressedBlockEntrySize（固定 12）
        var compressionMethodNameCount = reader.ReadUInt32();
        var compressionMethodNameLength = reader.ReadUInt32();
        reader.ReadUInt32();                                    // CompressionBlockSize
        var directoryIndexSize = reader.ReadUInt32();
        reader.ReadUInt32();                                    // PartitionCount
        reader.ReadUInt64();                                    // ContainerId
        reader.Skip(16);                                        // EncryptionKeyGuid
        var containerFlags = reader.ReadByte();
        reader.Skip(3);
        var perfectHashSeedsCount = reader.ReadUInt32();
        reader.ReadUInt64();                                    // PartitionSize
        var chunksWithoutPerfectHashCount = reader.ReadUInt32();
        reader.Skip(FixedHeaderSize - 100);                     // 到固定头结束

        if (version < MinSupportedVersion || version > MaxSupportedVersion)
            return new ContainerIndexResult(
                IndexStatus.Unsupported, "不支持的 IoStore 版本 " + version, [], version);

        if ((containerFlags & FlagEncrypted) != 0)
            return new ContainerIndexResult(IndexStatus.Encrypted, "容器索引已加密", [], version);

        if (tocHeaderSize < FixedHeaderSize || tocHeaderSize > length)
            throw new InvalidDataException("TocHeaderSize 越界: " + tocHeaderSize);

        reader.Position = tocHeaderSize;

        // ── 条目区：ChunkIds 与 OffsetAndLengths 一一对应 ──
        var chunkIds = reader.ReadBytes(
            (int)CheckedBlock(tocEntryCount, ChunkIdSize, reader, "ChunkIds"));
        reader.Skip(CheckedBlock(tocEntryCount, OffsetAndLengthSize, reader, "OffsetAndLengths"));
        reader.Skip(CheckedBlock(perfectHashSeedsCount, 4, reader, "PerfectHashSeeds"));
        reader.Skip(CheckedBlock(chunksWithoutPerfectHashCount, 4, reader, "ChunksWithoutPerfectHash"));

        // 压缩块表与压缩方法名本模块不用，但要按格式跳过去才能到达目录索引。
        reader.Skip(CheckedBlock(compressedBlockEntryCount, 12, reader, "CompressionBlocks"));
        var nameLength = compressionMethodNameLength == 0
            ? DefaultCompressionMethodNameLength
            : (int)compressionMethodNameLength;
        reader.Skip(CheckedBlock(compressionMethodNameCount, nameLength, reader, "CompressionMethodNames"));

        if ((containerFlags & FlagSigned) != 0)
        {
            var hashSize = reader.ReadInt32();
            if (hashSize < 0) throw new InvalidDataException("签名长度字段为负: " + hashSize);
            reader.Skip(hashSize * 2L);
            reader.Skip(CheckedBlock(
                compressedBlockEntryCount, CompressionBlockSignatureSize, reader, "BlockSignatures"));
        }

        // ── 目录索引与条目哈希 ──
        var directoryIndex = reader.ReadBytes(
            (int)CheckedBlock(directoryIndexSize, 1, reader, "DirectoryIndex"));

        // 元数据间隔由文件自己说出来：目录索引之后剩下的字节按条目数均分。
        // 除不尽（尾部还挂着别的东西）才按版本回退。
        var trailing = reader.Length - reader.Position;
        var metaSize = ResolveMetaSize(version, trailing, tocEntryCount);
        var metaCount = Math.Min((int)Math.Max(trailing / metaSize, 0), (int)tocEntryCount);
        var chunkMetas = reader.ReadBytes(metaCount * metaSize);

        var chunkHashes = ReadChunkHashes(chunkMetas, (int)tocEntryCount, metaSize);

        if (directoryIndex.Length == 0)
        {
            return new ContainerIndexResult(
                IndexStatus.Partial,
                "没有目录索引，只能按包 ID 比对",
                ReadPackageIdEntries(chunkIds, chunkHashes, sourceFile),
                version);
        }

        return new ContainerIndexResult(
            IndexStatus.Complete, null,
            ReadDirectoryIndex(directoryIndex, chunkHashes, sourceFile),
            version);
    }

    /// <summary>
    /// 每条条目元数据的间隔（相邻两条哈希之间的距离）。
    ///
    /// 实测两份 IoStore v8 样本（影之刃零的两个 MOD）都是 24 字节 = 20 字节哈希 + 标志 + 对齐，
    /// 更早版本的样本与夹具是 33 字节 = 32 字节哈希 + 标志。两种布局里哈希都在记录开头，
    /// 因此指纹取前 20 字节都对；但间隔读错会让每条哈希都错位 —— 同一批资产被算成互不相同，
    /// 冲突检测直接漏报，所以间隔优先按文件尾部的实际剩余字节数推断。
    /// </summary>
    private static int ResolveMetaSize(int version, long trailing, uint entryCount)
    {
        if (entryCount > 0 && trailing > 0 && trailing % entryCount == 0)
        {
            var stride = trailing / entryCount;
            if (stride >= ChunkHashSize + MetaFlagsSize && stride <= MaxMetaSize)
                return (int)stride;
        }

        return version >= FullHashMetaVersion ? FullHashMetaSize : LegacyMetaSize;
    }

    /// <summary>
    /// 每个 TOC 条目的 ChunkHash（十六进制），按条目下标给出；元数据不足的条目为 null。
    /// </summary>
    private static string?[] ReadChunkHashes(byte[] chunkMetas, int entryCount, int metaSize)
    {
        var hashes = new string?[entryCount];
        var available = metaSize <= 0 ? 0 : chunkMetas.Length / metaSize;
        for (var i = 0; i < entryCount && i < available; i++)
            hashes[i] = Convert.ToHexString(chunkMetas.AsSpan(i * metaSize, ChunkHashSize));
        return hashes;
    }

    /// <summary>
    /// 没有目录索引时的退路：一个条目一行，键取 ChunkId 前 8 字节（包 ID）的十六进制。
    ///
    /// 同一包的不同 chunk 因此会算出同一个键 —— 这正是想要的粒度：
    /// 路径都没有了，能比较的只剩"这两个 MOD 是否都带这个包"。
    /// 包内多个 chunk 的合并交给调用方按 AssetKey 去重。
    /// </summary>
    private static IReadOnlyList<AssetEntry> ReadPackageIdEntries(
        byte[] chunkIds, string?[] chunkHashes, string sourceFile)
    {
        var entries = new List<AssetEntry>(chunkIds.Length / ChunkIdSize);
        for (var i = 0; i * ChunkIdSize + ChunkIdSize <= chunkIds.Length; i++)
        {
            var packageId = Convert.ToHexString(chunkIds.AsSpan(i * ChunkIdSize, 8));
            var hash = HashAt(chunkHashes, (uint)i);
            entries.Add(new AssetEntry(
                AssetKey: packageId.ToLowerInvariant(),
                DisplayPath: "包 ID " + packageId,
                Fingerprint: hash,
                FingerprintKind: hash is null ? FingerprintKind.None : FingerprintKind.ContentHash,
                Size: -1,
                DataOffset: -1,
                StoredSize: -1,
                CompressionMethod: -1,
                SourceFile: sourceFile));
        }

        return entries;
    }

    /// <summary>
    /// 从目录索引还原每个文件的完整路径。
    ///
    /// 名字全部下沉到末尾的字符串表：目录项与文件项里的 Name 都是字符串表下标，
    /// 所以目录名和文件名要用同一个表去查。树用 FirstChild / NextSibling 串兄弟，
    /// FirstFile / NextFile 串同目录下的文件；任何一处出现 <see cref="InvalidIndex"/>
    /// 即表示那里是空的。
    /// </summary>
    private static IReadOnlyList<AssetEntry> ReadDirectoryIndex(
        byte[] directoryIndex, string?[] chunkHashes, string sourceFile)
    {
        var reader = new ContainerBinaryReader(new MemoryStream(directoryIndex, writable: false));
        var mountPoint = reader.ReadFString();

        var directoryCount = reader.ReadInt32();
        if (directoryCount < 0) throw new InvalidDataException("目录数为负: " + directoryCount);
        var directories = new (uint Name, uint FirstChild, uint NextSibling, uint FirstFile)[directoryCount];
        for (var i = 0; i < directoryCount; i++)
            directories[i] = (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());

        var fileCount = reader.ReadInt32();
        if (fileCount < 0) throw new InvalidDataException("文件数为负: " + fileCount);
        var files = new (uint Name, uint NextFile, uint UserData)[fileCount];
        for (var i = 0; i < fileCount; i++)
            files[i] = (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());

        var stringCount = reader.ReadInt32();
        if (stringCount < 0) throw new InvalidDataException("字符串数为负: " + stringCount);
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++) strings[i] = reader.ReadFString();

        var entries = new List<AssetEntry>();
        var pending = new Stack<(uint Index, string ParentPath)>();
        pending.Push((0u, string.Empty));

        while (pending.Count > 0)
        {
            var (index, parentPath) = pending.Pop();
            if (index == InvalidIndex || index >= directories.Length) continue;

            var directory = directories[index];
            var path = parentPath + StringAt(strings, directory.Name) + "/";

            // 文件用链表串起来；给个上限，避免索引被写坏时在这里转不出去。
            var fileIndex = directory.FirstFile;
            for (var guard = 0; fileIndex != InvalidIndex && guard <= files.Length; guard++)
            {
                if (fileIndex >= files.Length) break;
                var file = files[fileIndex];
                var fileName = StringAt(strings, file.Name);
                if (fileName.Length > 0)
                {
                    var displayPath = AssetKeyNormalizer.NormalizePath(mountPoint + path + fileName);
                    if (displayPath.Length > 0)
                    {
                        var hash = HashAt(chunkHashes, file.UserData);
                        entries.Add(new AssetEntry(
                            AssetKey: AssetKeyNormalizer.NormalizeAssetKey(displayPath),
                            DisplayPath: displayPath,
                            Fingerprint: hash,
                            FingerprintKind: hash is null ? FingerprintKind.None : FingerprintKind.ContentHash,
                            Size: -1,
                            DataOffset: -1,
                            StoredSize: -1,
                            CompressionMethod: -1,
                            SourceFile: sourceFile));
                    }
                }

                fileIndex = file.NextFile;
            }

            var childIndex = directory.FirstChild;
            for (var guard = 0; childIndex != InvalidIndex && guard <= directories.Length; guard++)
            {
                if (childIndex >= directories.Length) break;
                pending.Push((childIndex, path));
                childIndex = directories[childIndex].NextSibling;
            }
        }

        return entries.OrderBy(e => e.AssetKey, StringComparer.Ordinal).ToList();
    }

    private static string StringAt(string[] strings, uint index)
        => index == InvalidIndex || index >= strings.Length ? string.Empty : strings[index];

    private static string? HashAt(string?[] hashes, uint index)
        => index == InvalidIndex || index >= hashes.Length ? null : hashes[index];

    /// <summary>
    /// 校验"<paramref name="count"/> 个 <paramref name="unit"/> 字节的元素"确实在文件里，
    /// 返回其字节数。索引被写坏时数目可能大得离谱，不先校验就会拿它去分配数组。
    /// </summary>
    private static long CheckedBlock(uint count, int unit, ContainerBinaryReader reader, string what)
    {
        var total = (long)count * unit;
        if (total > int.MaxValue)
            throw new InvalidDataException(what + " 数量异常: " + count);
        if (reader.Position + total > reader.Length)
            throw new InvalidDataException(what + " 超出文件长度");
        return total;
    }
}