using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace UEModManager.Services.Conflict.Indexing;

/// <summary>
/// 容器索引解析用的二进制读取器。
///
/// 容器的整数一律小端；<c>FString</c> 是"int32 长度 + 内容"，长度正负决定编码：
/// 正数是 <c>len</c> 个 UTF-8 字节（含结尾 <c>\0</c>），负数是 <c>-len</c> 个 UTF-16 字符
/// （含结尾 <c>\0</c>）。这不是通用序列化格式，所以不放在公共 API 上。
/// </summary>
internal sealed class ContainerBinaryReader(Stream stream)
{
    private readonly Stream _stream = stream;

    public long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public long Length => _stream.Length;

    public byte ReadByte()
    {
        var value = _stream.ReadByte();
        if (value < 0) throw new EndOfStreamException("读取容器索引时意外到达文件末尾");
        return (byte)value;
    }

    public byte[] ReadBytes(int count)
    {
        if (count < 0) throw new InvalidDataException($"容器索引里的长度字段为负: {count}");
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = _stream.Read(buffer, offset, count - offset);
            if (read <= 0) throw new EndOfStreamException("读取容器索引时意外到达文件末尾");
            offset += read;
        }
        return buffer;
    }

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));

    public void Skip(long count)
    {
        if (count < 0) throw new InvalidDataException($"容器索引里的跳过长度为负: {count}");
        System.ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length);
        Position += count;
    }

    /// <summary>读取容器格式的 FString。</summary>
    public string ReadFString()
    {
        var length = ReadInt32();
        if (length == 0) return string.Empty;

        if (length > 0)
        {
            var bytes = ReadBytes(length);
            // 结尾的 \0 不计入内容
            var count = bytes.Length > 0 && bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
            return Encoding.UTF8.GetString(bytes, 0, count);
        }

        var charCount = -length;
        var raw = ReadBytes(charCount * 2);
        var usable = charCount;
        if (usable > 0 && raw[(usable - 1) * 2] == 0 && raw[(usable - 1) * 2 + 1] == 0) usable--;
        return Encoding.Unicode.GetString(raw, 0, usable * 2);
    }
}
