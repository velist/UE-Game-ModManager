using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// 在内存里生成 v2 utoc 字节，供 <c>IoStoreTocReader</c> 读回。
///
/// 与 <see cref="PakFixtureBuilder"/> 同理，这个 builder 是格式理解的第二份实现：
/// 它按 §2.2 的定义**写出**索引（含一棵 FirstChild/NextSibling 串起来的目录树），
/// 读取器按同一份定义**读回**。只要两边对目录树或字符串表的理解有一处不一致，
/// 读回来的路径就会错，所以"能原样读回"是有效检验。
///
/// 只生成不带压缩块与签名的版本 —— 这两段读取器只是原样跳过，
/// 造出来也验证不了什么。
/// </summary>
internal static class UtocFixtureBuilder
{
    /// <summary>IoStore 目录魔数。</summary>
    internal static readonly byte[] Magic = "-==--==--==--==-"u8.ToArray();

    private const int HeaderSize = 144;
    private const int ChunkIdSize = 12;
    private const int ChunkMetaSize = 33;
    private const uint InvalidIndex = uint.MaxValue;

    /// <summary>一个待写入的条目：目录（相对挂载点，可为空）、文件名、ChunkHash、包 ID。</summary>
    internal sealed record FixtureChunk(string Directory, string FileName, byte[] ChunkHash, ulong PackageId);

    /// <summary>
    /// 生成一个 utoc。
    /// </summary>
    /// <param name="mountPoint">挂载点，形如 <c>../../../SB/Content/Art/Character/PC/</c>。</param>
    /// <param name="chunks">条目列表。</param>
    /// <param name="version">IoStore 版本。</param>
    /// <param name="encrypted">是否置 ContainerFlags 的 Encrypted 位。</param>
    /// <param name="withDirectoryIndex">
    /// 是否写目录索引。为 false 时把 DirectoryIndexSize 写 0 并省略整段，
    /// 用于验证读取器降级为 Partial 后按包 ID 出条目。
    /// </param>
    internal static byte[] Build(
        string mountPoint,
        IReadOnlyList<FixtureChunk> chunks,
        int version = 2,
        bool encrypted = false,
        bool withDirectoryIndex = true)
    {
        var chunkIds = new MemoryStream();
        var offsets = new MemoryStream();
        using (var idWriter = new BinaryWriter(chunkIds, Encoding.UTF8, leaveOpen: true))
        using (var offsetWriter = new BinaryWriter(offsets, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var chunk in chunks)
            {
                idWriter.Write(chunk.PackageId);
                idWriter.Write(0x02u);                  // 低 4 字节：chunk 类型
                // OffsetAndLengths 是 5 字节偏移 + 5 字节长度，读取器只跳过。
                offsetWriter.Write(new byte[10]);
            }
        }

        var metas = new MemoryStream();
        using (var metaWriter = new BinaryWriter(metas, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var chunk in chunks)
            {
                if (chunk.ChunkHash.Length != 32)
                    throw new InvalidOperationException("ChunkHash 必须是 32 字节");
                metaWriter.Write(chunk.ChunkHash);
                metaWriter.Write((byte)1);              // Flags
            }
        }

        var directoryIndex = withDirectoryIndex
            ? BuildDirectoryIndex(mountPoint, chunks)
            : [];

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write((byte)version);
        writer.Write(new byte[3]);
        writer.Write(HeaderSize);
        writer.Write(chunks.Count);                     // TocEntryCount
        writer.Write(0);                                // TocCompressedBlockEntryCount
        writer.Write(12);                               // TocCompressedBlockEntrySize
        writer.Write(0);                                // CompressionMethodNameCount
        writer.Write(32);                               // CompressionMethodNameLength
        writer.Write(65536);                            // CompressionBlockSize
        writer.Write(directoryIndex.Length);            // DirectoryIndexSize
        writer.Write(0);                                // PartitionCount
        writer.Write(0UL);                              // ContainerId
        writer.Write(new byte[16]);                     // EncryptionKeyGuid
        writer.Write((byte)(encrypted ? 1 << 1 : 1 << 3)); // ContainerFlags
        writer.Write(new byte[3]);
        writer.Write(0);                                // TocChunkPerfectHashSeedsCount
        writer.Write(0UL);                              // PartitionSize
        writer.Write(0);                                // TocChunksWithoutPerfectHashCount
        writer.Write(new byte[HeaderSize - 100]);       // 补齐到固定头长度

        writer.Write(chunkIds.ToArray());
        writer.Write(offsets.ToArray());
        writer.Write(directoryIndex);
        writer.Write(metas.ToArray());

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// 构造 FIoDirectoryIndexResource：目录树 + 文件链表 + 下沉的名字字符串表。
    ///
    /// 目录与文件项里的 Name 都是字符串表下标（不是偏移），
    /// 所以目录名和文件名共用同一张表。
    /// </summary>
    private static byte[] BuildDirectoryIndex(string mountPoint, IReadOnlyList<FixtureChunk> chunks)
    {
        // ── ① 建目录树 ──
        var root = new Node(string.Empty);
        for (var i = 0; i < chunks.Count; i++)
        {
            var node = root;
            foreach (var segment in chunks[i].Directory.Split(
                '/', StringSplitOptions.RemoveEmptyEntries))
                node = node.Child(segment);
            node.Files.Add(i);
        }

        // ── ② 深度优先编号：0 号固定是根 ──
        var directories = new List<Node>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node.Index = directories.Count;
            directories.Add(node);
            // 逆序入栈，出栈后即按加入顺序排列。
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }

        // 兄弟链要在编号之后再接：编号是后序分配的。
        foreach (var node in directories)
            for (var i = 0; i < node.Children.Count; i++)
                node.Children[i].NextSibling = i + 1 < node.Children.Count
                    ? node.Children[i + 1]
                    : null;

        // ── ③ 文件表：同一目录下的文件用 NextFile 串起来 ──
        var files = new List<(int Chunk, int Next)>();
        foreach (var node in directories)
        {
            for (var i = 0; i < node.Files.Count; i++)
            {
                var next = i + 1 < node.Files.Count ? files.Count + 1 : -1;
                files.Add((node.Files[i], next is < 0 ? -1 : next));
            }
            node.FirstFile = node.Files.Count > 0 ? files.Count - node.Files.Count : -1;
        }

        // ── ④ 字符串表 ──
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint Intern(string value)
        {
            if (value.Length == 0) return InvalidIndex;
            if (stringIndex.TryGetValue(value, out var existing)) return existing;
            var index = (uint)strings.Count;
            strings.Add(value);
            stringIndex[value] = index;
            return index;
        }

        foreach (var node in directories)
        {
            node.NameIndex = Intern(node.Name);
            foreach (var child in node.Children) Intern(child.Name);
        }

        // ── ⑤ 落盘 ──
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write(EncodeFString(mountPoint));
        writer.Write(directories.Count);
        foreach (var node in directories)
        {
            writer.Write(node.NameIndex);
            writer.Write(node.Children.Count > 0 ? (uint)node.Children[0].Index : InvalidIndex);
            writer.Write(node.NextSiblingIndex);
            writer.Write(node.FirstFile < 0 ? InvalidIndex : (uint)node.FirstFile);
        }

        writer.Write(files.Count);
        foreach (var (chunk, next) in files)
        {
            writer.Write(Intern(chunks[chunk].FileName));
            writer.Write(next < 0 ? InvalidIndex : (uint)next);
            writer.Write((uint)chunk);                  // UserData = TOC 条目下标
        }

        writer.Write(strings.Count);
        foreach (var value in strings) writer.Write(EncodeFString(value));

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

    private sealed class Node(string name)
    {
        public string Name { get; } = name;
        public List<Node> Children { get; } = [];
        public List<int> Files { get; } = [];
        public int Index { get; set; }
        public int FirstFile { get; set; } = -1;
        public uint NameIndex { get; set; } = InvalidIndex;
        public Node? NextSibling { get; set; }

        public Node Child(string name)
        {
            var existing = Children.FirstOrDefault(c => c.Name == name);
            if (existing != null) return existing;
            var created = new Node(name);
            Children.Add(created);
            return created;
        }

        /// <summary>下一个兄弟的编号；最后一个兄弟为 <see cref="InvalidIndex"/>。</summary>
        public uint NextSiblingIndex => NextSibling is null ? InvalidIndex : (uint)NextSibling.Index;
    }
}