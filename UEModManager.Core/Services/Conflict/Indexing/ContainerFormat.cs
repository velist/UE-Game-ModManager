using System;
using System.Buffers.Binary;
using System.IO;

namespace UEModManager.Services.Conflict.Indexing;

/// <summary>
/// 容器格式识别。
/// </summary>
public enum ContainerKind
{
    /// <summary>认不出来（不是索引载体，或格式不在支持范围内）。</summary>
    Unknown,

    /// <summary>UE .pak。</summary>
    Pak,

    /// <summary>UE IoStore .utoc。</summary>
    IoStoreToc
}

/// <summary>
/// 按文件内容（而不是扩展名）判定容器格式。
/// </summary>
public static class ContainerFormat
{
    /// <summary>IoStore 目录魔数：<c>-==--==--==--==-</c>。</summary>
    public static readonly byte[] IoStoreMagic = "-==--==--==--==-"u8.ToArray();

    /// <summary>pak 页脚魔数 <c>0x5A6F12E1</c>（小端字节序 <c>E1 12 6F 5A</c>）。</summary>
    public const uint PakMagic = 0x5A6F12E1;

    /// <summary>页脚魔数只可能出现在文件末尾这么多字节内。</summary>
    public const int PakFooterSearchWindow = 300;

    /// <summary>
    /// 判定 <paramref name="stream"/> 的容器格式。
    ///
    /// 先看头 16 字节是否为 utoc 魔数，再看末尾是否有 pak 魔数。
    /// <c>.ucas</c> 不是索引载体，两侧都不命中会落回 <see cref="ContainerKind.Unknown"/>。
    /// </summary>
    public static ContainerKind Detect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length == 0) return ContainerKind.Unknown;

        if (HasIoStoreMagic(stream)) return ContainerKind.IoStoreToc;
        if (FindPakFooter(stream) >= 0) return ContainerKind.Pak;
        return ContainerKind.Unknown;
    }

    /// <summary>头 16 字节是否为 utoc 魔数。</summary>
    public static bool HasIoStoreMagic(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < IoStoreMagic.Length) return false;

        var position = stream.Position;
        try
        {
            stream.Position = 0;
            var buffer = new byte[IoStoreMagic.Length];
            if (stream.Read(buffer, 0, buffer.Length) != buffer.Length) return false;
            return buffer.AsSpan().SequenceEqual(IoStoreMagic);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// 从文件末尾向前找 pak 页脚魔数，返回其偏移；找不到返回 -1。
    /// 魔数可能落在搜索窗口内的任何位置，因此逐字节回溯而不是按固定偏移对齐。
    /// </summary>
    public static long FindPakFooter(Stream stream)
    {
        if (!stream.CanSeek) return -1;

        var position = stream.Position;
        try
        {
            var length = stream.Length;
            if (length < 4) return -1;

            var window = (int)Math.Min(PakFooterSearchWindow, length);
            stream.Position = length - window;
            var buffer = new byte[window];
            var read = stream.Read(buffer, 0, window);
            if (read < 4) return -1;

            const byte b0 = (byte)(PakMagic & 0xFF);
            const byte b1 = (byte)((PakMagic >> 8) & 0xFF);
            const byte b2 = (byte)((PakMagic >> 16) & 0xFF);
            const byte b3 = (byte)((PakMagic >> 24) & 0xFF);

            for (var i = read - 4; i >= 0; i--)
            {
                if (buffer[i] == b0 && buffer[i + 1] == b1 && buffer[i + 2] == b2 && buffer[i + 3] == b3)
                    return length - read + i;
            }

            return -1;
        }
        finally
        {
            stream.Position = position;
        }
    }
}