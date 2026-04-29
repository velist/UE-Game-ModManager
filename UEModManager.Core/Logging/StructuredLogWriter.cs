using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UEModManager.Logging
{
    /// <summary>
    /// 结构化日志 TextWriter。包装底层 StreamWriter，拦截每行 Console 输出并改造为：
    ///
    ///     {ISO-8601 timestamp} [LEVEL] [Category] message
    ///
    /// 例：<c>2026-04-28T15:23:01.123Z [INFO] [App] 应用程序构造完成</c>
    ///
    /// 解析规则：
    /// - 行首 <c>[FATAL] / [ERROR] / [WARN]</c> 等关键字 → 提取为 LEVEL
    /// - 紧随其后的 <c>[Tag]</c> → 提取为 Category
    /// - 都没有 → LEVEL=INFO, Category=null
    ///
    /// 设计目标：
    /// - 业务代码 <c>Console.WriteLine($"[App] xxx")</c> 零改动即可获得时间戳和等级字段
    /// - 输出仍保持人类可读，可 <c>grep -E "WARN|ERROR"</c> 快速过滤
    /// - 诊断包导出时机器可解析
    /// </summary>
    public sealed class StructuredLogWriter : TextWriter
    {
        private static readonly Regex LevelPrefix = new(
            @"^\[(FATAL|ERROR|WARN|INFO|DEBUG|TRACE)\]\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CategoryPrefix = new(
            @"^\[([^\]]+)\]\s*",
            RegexOptions.Compiled);

        private readonly TextWriter _inner;
        private readonly StringBuilder _lineBuffer = new();
        private readonly object _lock = new();

        public StructuredLogWriter(TextWriter inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_lock)
            {
                if (value == '\n')
                {
                    FlushLine();
                }
                else if (value != '\r')
                {
                    _lineBuffer.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var ch in value) Write(ch);
        }

        public override void WriteLine(string? value)
        {
            // 优化路径：直接成行处理，避开逐字符循环
            lock (_lock)
            {
                if (_lineBuffer.Length > 0)
                {
                    var pending = _lineBuffer.ToString();
                    _lineBuffer.Clear();
                    EmitFormatted(pending + (value ?? ""));
                }
                else
                {
                    EmitFormatted(value ?? "");
                }
            }
        }

        public override void WriteLine() => WriteLine(string.Empty);

        public override void Flush()
        {
            lock (_lock)
            {
                if (_lineBuffer.Length > 0)
                {
                    FlushLine();
                }
            }
            _inner.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void FlushLine()
        {
            var line = _lineBuffer.ToString();
            _lineBuffer.Clear();
            EmitFormatted(line);
        }

        /// <summary>把一行原始文本格式化为结构化日志行并写入底层。</summary>
        internal void EmitFormatted(string raw)
        {
            var (level, category, message) = Parse(raw);
            var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var formatted = category != null
                ? $"{ts} [{level}] [{category}] {message}"
                : $"{ts} [{level}] {message}";

            _inner.WriteLine(formatted);
        }

        /// <summary>解析原始行，提取 level / category / message。纯函数，可单测。</summary>
        public static (string level, string? category, string message) Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return ("INFO", null, "");

            var rest = raw;
            string level = "INFO";
            string? category = null;

            var levelMatch = LevelPrefix.Match(rest);
            if (levelMatch.Success)
            {
                level = levelMatch.Groups[1].Value.ToUpperInvariant();
                rest = rest[levelMatch.Length..];
            }

            var catMatch = CategoryPrefix.Match(rest);
            if (catMatch.Success)
            {
                category = catMatch.Groups[1].Value;
                rest = rest[catMatch.Length..];
            }

            return (level, category, rest);
        }
    }
}
