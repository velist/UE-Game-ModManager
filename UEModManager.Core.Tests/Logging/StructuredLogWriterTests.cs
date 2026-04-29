using System.IO;
using UEModManager.Logging;

namespace UEModManager.Core.Tests.Logging;

public class StructuredLogWriterTests
{
    // ─── Parse (纯函数) ───

    [Fact]
    public void Parse_PlainMessage_DefaultsToInfoNoCategory()
    {
        var (level, category, message) = StructuredLogWriter.Parse("hello world");

        Assert.Equal("INFO", level);
        Assert.Null(category);
        Assert.Equal("hello world", message);
    }

    [Fact]
    public void Parse_LevelOnly_ExtractsAndUppercases()
    {
        var (level, category, message) = StructuredLogWriter.Parse("[fatal] something blew up");

        Assert.Equal("FATAL", level);
        Assert.Null(category);
        Assert.Equal("something blew up", message);
    }

    [Fact]
    public void Parse_CategoryOnly_DefaultLevelInfo()
    {
        var (level, category, message) = StructuredLogWriter.Parse("[App] launching");

        Assert.Equal("INFO", level);
        Assert.Equal("App", category);
        Assert.Equal("launching", message);
    }

    [Fact]
    public void Parse_LevelAndCategory_BothExtracted()
    {
        var (level, category, message) = StructuredLogWriter.Parse("[ERROR] [Auth] login failed");

        Assert.Equal("ERROR", level);
        Assert.Equal("Auth", category);
        Assert.Equal("login failed", message);
    }

    [Fact]
    public void Parse_EmptyString_DefaultsToInfoEmpty()
    {
        var (level, category, message) = StructuredLogWriter.Parse("");

        Assert.Equal("INFO", level);
        Assert.Null(category);
        Assert.Equal("", message);
    }

    [Fact]
    public void Parse_OnlyBracketsNoMatch_ReturnsAsMessage()
    {
        // [] 没有内容，不匹配 CategoryPrefix（要求至少 1 个非] 字符）
        var (level, category, message) = StructuredLogWriter.Parse("[] something");

        Assert.Equal("INFO", level);
        Assert.Null(category);
        Assert.Equal("[] something", message);
    }

    [Fact]
    public void Parse_NestedBrackets_OnlyOuterCategoryExtracted()
    {
        // [Tag] 后还有内容包含 []，应只取首个作为 category
        var (level, category, message) = StructuredLogWriter.Parse("[App] config=[a,b,c]");

        Assert.Equal("INFO", level);
        Assert.Equal("App", category);
        Assert.Equal("config=[a,b,c]", message);
    }

    [Theory]
    [InlineData("[FATAL]", "FATAL")]
    [InlineData("[Error]", "ERROR")]
    [InlineData("[warn]", "WARN")]
    [InlineData("[INFO]", "INFO")]
    [InlineData("[debug]", "DEBUG")]
    [InlineData("[TRACE]", "TRACE")]
    public void Parse_AllSupportedLevels(string prefix, string expectedLevel)
    {
        var (level, _, _) = StructuredLogWriter.Parse($"{prefix} msg");
        Assert.Equal(expectedLevel, level);
    }

    // ─── Output formatting (集成) ───

    [Fact]
    public void WriteLine_ProducesTimestampedLine()
    {
        var inner = new StringWriter();
        var writer = new StructuredLogWriter(inner);

        writer.WriteLine("[App] starting up");
        writer.Flush();

        var output = inner.ToString().Trim();
        // 期望：2026-04-28T15:23:01.123Z [INFO] [App] starting up
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[INFO\] \[App\] starting up$", output);
    }

    [Fact]
    public void WriteLine_ErrorLevel_FormattedCorrectly()
    {
        var inner = new StringWriter();
        var writer = new StructuredLogWriter(inner);

        writer.WriteLine("[FATAL] [App] crashed: System.Exception");
        writer.Flush();

        var output = inner.ToString().Trim();
        Assert.Contains("[FATAL] [App] crashed: System.Exception", output);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", output);
    }

    [Fact]
    public void WriteLine_PlainMessage_GetsInfoLevel()
    {
        var inner = new StringWriter();
        var writer = new StructuredLogWriter(inner);

        writer.WriteLine("just plain text");
        writer.Flush();

        var output = inner.ToString().Trim();
        Assert.Contains("[INFO] just plain text", output);
    }

    [Fact]
    public void Write_Char_BuffersUntilNewline()
    {
        var inner = new StringWriter();
        var writer = new StructuredLogWriter(inner);

        writer.Write('[');
        writer.Write('A');
        writer.Write(']');
        writer.Write(' ');
        writer.Write('h');
        writer.Write('i');
        writer.Write('\n');
        writer.Flush();

        var output = inner.ToString().Trim();
        Assert.Contains("[INFO] [A] hi", output);
    }

    [Fact]
    public void Write_String_AccumulatedAcrossCalls_ThenFlushedOnNewline()
    {
        var inner = new StringWriter();
        var writer = new StructuredLogWriter(inner);

        writer.Write("[App] part1 ");
        writer.Write("part2");
        writer.WriteLine();
        writer.Flush();

        var output = inner.ToString().Trim();
        Assert.Contains("[INFO] [App] part1 part2", output);
    }

    [Fact]
    public void Dispose_FlushesPendingBuffer()
    {
        var inner = new StringWriter();
        var writer = new StructuredLogWriter(inner);

        writer.Write("[App] never flushed manually");
        writer.Dispose();

        var output = inner.ToString().Trim();
        // Dispose 应当 flush 残留缓冲
        Assert.Contains("never flushed manually", output);
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StructuredLogWriter(null!));
    }
}
