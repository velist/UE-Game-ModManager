using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// 在内存里生成 v11 pak 字节，供 <c>PakIndexReader</c> 读回。
///
/// 这个 builder 本身就是格式理解的第二份实现：它按 §2.1 的定义**写出**索引，
/// 读取器按同一份定义**读回**。两边对格式的理解只要有一处不一致，
/// 读回来的路径或大小就会错 —— 所以"能原样读回"是对格式理解的有效检验，
/// 而不是自说自话的循环验证。
/// </summary>
internal static class PakFixtureBuilder
{
    /// <summary>pak 页脚魔数。</summary>
    private const uint Magic = 0x5A6F12E1;

    /// <summary>未压缩条目的 FPakEntry 头副本长度：Offset/Size/UncompressedSize/Method/Hash/Encrypted/BlockSize。</summary>
    private const int EntryHeaderSize = 8 + 8 + 8 + 4 + 20 + 1 + 4;

    /// <summary>页脚里压缩方法名的字节数（5 个定长 32 字节槽位）。</summary>
    private const int CompressionMethodNamesSize = 5 * 32;

    /// <summary>一个待写入的条目。</summary>
    internal sealed record FixtureEntry(string Directory, string FileName, byte[] Data);

    /// <summary>
    /// 生成一个 pak。
    /// </summary>
    /// <param name="mountPoint">挂载点，形如 <c>../../../b1/Content/00MainHZ/</c>。</param>
    /// <param name="entries">条目列表（目录 / 文件名 / 内容）。内容一律未压缩。</param>
    /// <param name="blackMythVariant">是否使用黑神话·悟空的 +2 字节页脚变体。</param>
    /// <param name="version">pak 版本。</param>
    /// <param name="encryptedIndex">bEncryptedIndex 的取值。</param>
    /// <param name="truncateBytes">
    /// 从末尾截掉的字节数，用于造"截断文件"。截断会让页脚魔数消失，
    /// 读取器应降级为 Failed 而不是抛异常。
    /// </param>
    internal static byte[] Build(
        string mountPoint,
        IReadOnlyList<FixtureEntry> entries,
        bool blackMythVariant = false,
        int version = 11,
        byte encryptedIndex = 0,
        int truncateBytes = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        // ── ① 数据区：每个条目前面放一份 FPakEntry 头副本 ──
        var placed = new List<(FixtureEntry Entry, long Offset, long Size)>();
        foreach (var entry in entries)
        {
            var offset = stream.Position;
            WriteEntryHeaderCopy(writer, offset, entry.Data.Length);
            writer.Write(entry.Data);
            placed.Add((entry, offset, entry.Data.Length));
        }

        // ── ② 编码条目表：每个条目 12 字节 ──
        var encoded = new MemoryStream();
        using (var encodedWriter = new BinaryWriter(encoded, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (_, offset, size) in placed)
            {
                // bit31/30/29 = Offset / UncompressedSize / Size 用 32 位；
                // bit23-28 CompressionMethodIndex = 0（未压缩）；bit22 未加密。
                const uint flags = (1u << 31) | (1u << 30) | (1u << 29);
                encodedWriter.Write(flags);
                encodedWriter.Write((uint)offset);
                encodedWriter.Write((uint)size);
                // 未压缩时不存在 Size 字段。
            }
        }
        var encodedBytes = encoded.ToArray();

        // ── ③ 目录索引内容 ──
        var directoryIndex = BuildDirectoryIndex(entries, encodedBytes);

        // ── ④ 主索引：固定字段宽度已知，可先算出目录索引的绝对偏移 ──
        var indexOffset = stream.Position;
        var indexSize = FStringSize(mountPoint)
                        + 4                                  // NumEntries
                        + 8                                  // PathHashSeed
                        + 4                                  // bHasPathHashIndex
                        + 4                                  // bHasFullDirectoryIndex
                        + 8 + 8 + 20                         // FullDirectoryIndexOffset/Size/Hash
                        + 4 + encodedBytes.Length            // EncodedPakEntriesSize + 内容
                        + 4;                                 // FilesNum
        var directoryIndexOffset = indexOffset + indexSize;

        writer.Write(EncodeFString(mountPoint));
        writer.Write(entries.Count);
        writer.Write(0UL);                                   // PathHashSeed
        writer.Write(0);                                     // bHasPathHashIndex = 0（本模块不读）
        writer.Write(1);                                     // bHasFullDirectoryIndex = 1
        writer.Write(directoryIndexOffset);
        writer.Write((long)directoryIndex.Length);
        writer.Write(new byte[20]);                          // FullDirectoryIndexHash
        writer.Write(encodedBytes.Length);
        writer.Write(encodedBytes);
        writer.Write(0);                                     // FilesNum（非编码条目数）

        // ── ⑤ 目录索引 ──
        writer.Write(directoryIndex);

        // ── ⑥ 页脚 ──
        writer.Write(new byte[16]);                          // EncryptionKeyGuid
        writer.Write(encryptedIndex);                        // bEncryptedIndex
        writer.Write(Magic);                                 // Magic
        writer.Write(version);                               // Version
        if (blackMythVariant) writer.Write((short)10);       // 变体多出的 2 字节
        writer.Write(indexOffset);                           // IndexOffset
        writer.Write((long)indexSize);                       // IndexSize
        writer.Write(new byte[20]);                          // IndexHash
        writer.Write(new byte[CompressionMethodNamesSize]);  // 压缩方法名

        writer.Flush();
        var bytes = stream.ToArray();
        stream.Position = 0;

        if (truncateBytes <= 0) return bytes;
        var keep = Math.Max(0, bytes.Length - truncateBytes);
        return bytes.Take(keep).ToArray();
    }

    /// <summary>
    /// 写条目头副本。字段顺序与 FPakEntry 一致：
    /// Offset / Size / UncompressedSize / Method / Hash / Encrypted / CompressionBlockSize。
    /// </summary>
    private static void WriteEntryHeaderCopy(BinaryWriter writer, long offset, long size)
    {
        writer.Write(offset);
        writer.Write(size);
        writer.Write(size);          // 未压缩：Size == UncompressedSize
        writer.Write(0);             // CompressionMethodIndex = 0
        writer.Write(new byte[20]);  // Hash
        writer.Write((byte)0);       // bEncrypted
        writer.Write(0u);            // CompressionBlockSize
        // 校验：写出的长度必须与 EntryHeaderSize 一致，否则读取器跳头时会错位。
        if (EntryHeaderSize != 8 + 8 + 8 + 4 + 20 + 1 + 4)
            throw new InvalidOperationException("条目头长度定义与写入不一致");
    }

    /// <summary>
    /// 构造 FullDirectoryIndex：按目录分组，目录名以 / 结尾，根目录是 /。
    /// </summary>
    private static byte[] BuildDirectoryIndex(IReadOnlyList<FixtureEntry> entries, byte[] encodedBytes)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 编码条目表里每个条目 12 字节，第 n 个的字节偏移即 EntryLocation。
        var locations = new Dictionary<(string Dir, string File), int>();
        var order = new List<(string Dir, string File)>();
        foreach (var entry in entries)
        {
            var key = (entry.Directory, entry.FileName);
            if (locations.ContainsKey(key)) continue;
            locations[key] = order.Count * 12;
            order.Add(key);
        }
        if (order.Count * 12 > encodedBytes.Length)
            throw new InvalidOperationException("编码条目表比目录索引声明的文件数还短");

        var byDirectory = order
            .GroupBy(k => k.Dir)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        writer.Write(byDirectory.Count);
        foreach (var directory in byDirectory)
        {
            // 目录名以 / 结尾；根目录写 "/" 本身。
            var name = string.IsNullOrEmpty(directory.Key) ? "/" : directory.Key + "/";
            writer.Write(EncodeFString(name));

            var files = directory.OrderBy(f => f.File, StringComparer.Ordinal).ToList();
            writer.Write(files.Count);
            foreach (var file in files)
            {
                writer.Write(EncodeFString(file.File));
                writer.Write(locations[file]);
            }
        }

        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>编码一个 FString：int32 长度 + UTF-8 内容（含结尾 \0）。</summary>
    internal static byte[] EncodeFString(string value)
    {
        var text = Encoding.UTF8.GetBytes(value + "\0");
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(text.Length);
        writer.Write(text);
        writer.Flush();
        return ms.ToArray();
    }

    private static int FStringSize(string value) => 4 + Encoding.UTF8.GetByteCount(value + "\0");
}