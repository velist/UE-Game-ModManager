using System;
using System.IO;

namespace UEModManager.Services.Import;

/// <summary>
/// 一次导入的累计输出预算。限制实际写入，不信任压缩包元数据；清理部分文件不退回预算。
/// 同一导入顺序提取各条目，预算无需跨线程共享。
/// </summary>
internal sealed class ExtractionBudget(long maximumBytes)
{
    public long MaximumBytes { get; } = maximumBytes >= 0
        ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    public long WrittenBytes { get; private set; }
    public long RemainingBytes => MaximumBytes - WrittenBytes;
    public bool IsExceeded { get; private set; }

    public Stream Limit(Stream destination) => new BudgetedWriteStream(destination, this);

    private sealed class BudgetedWriteStream(Stream destination, ExtractionBudget budget) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (budget.IsExceeded) throw LimitExceeded();
            var allowed = (int)Math.Min(buffer.Length, budget.RemainingBytes);
            if (allowed > 0)
            {
                destination.Write(buffer[..allowed]);
                budget.WrittenBytes += allowed;
            }
            if (allowed < buffer.Length)
            {
                budget.IsExceeded = true;
                throw LimitExceeded();
            }
        }

        private InvalidDataException LimitExceeded()
            => new($"解压体积超过上限 {budget.MaximumBytes} 字节");

        // 包装流不拥有 destination；提取入口负责按正常 using 顺序关闭实体文件。
    }
}
