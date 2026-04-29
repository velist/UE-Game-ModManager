using UEModManager.Models;
using UEModManager.Services.Config;

namespace UEModManager.Core.Tests.Services.Config;

public class IniParserTests
{
    private readonly IniParser _parser = new();

    [Fact]
    public void Format_Is_Ini()
    {
        Assert.Equal(ConfigFormat.Ini, _parser.Format);
    }

    [Fact]
    public void Parse_Section_And_KeyValue()
    {
        var content = """
            [Engine]
            ResolutionX=1920
            ResolutionY=1080
            """;

        var entries = _parser.Parse(content);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("Engine", e.Section));
        Assert.Contains(entries, e => e.Key == "ResolutionX" && e.Value == "1920");
        Assert.Contains(entries, e => e.Key == "ResolutionY" && e.Value == "1080");
    }

    [Fact]
    public void Parse_Comment_Lines_AreCaptured_NotDiscarded()
    {
        // 当前实现把 ; 和 # 注释作为带 Comment 的占位 entry 保留
        var content = "; first comment\n# second comment\nKey=Value";

        var entries = _parser.Parse(content);

        var commentEntries = entries.Where(e => string.IsNullOrEmpty(e.Key) && e.Comment != null).ToList();
        Assert.Equal(2, commentEntries.Count);
        Assert.Contains(entries, e => e.Key == "Key" && e.Value == "Value");
    }

    [Fact]
    public void Parse_TrailingComment_OnValueLine_IsExtracted()
    {
        var entries = _parser.Parse("Key=Value ; trailing");

        var entry = Assert.Single(entries);
        Assert.Equal("Key", entry.Key);
        Assert.Equal("Value", entry.Value);
        Assert.Equal("trailing", entry.Comment);
    }

    [Fact]
    public void Parse_EmptyLines_AreSkipped()
    {
        var content = "\n\n[A]\n\nKey=V\n\n";

        var entries = _parser.Parse(content);

        Assert.Single(entries);
        Assert.Equal("A", entries[0].Section);
    }

    [Fact]
    public void Parse_CRLF_LineEndings_AreHandled()
    {
        var entries = _parser.Parse("[S]\r\nKey=Value\r\n");

        var entry = Assert.Single(entries);
        Assert.Equal("S", entry.Section);
        Assert.Equal("Key", entry.Key);
        Assert.Equal("Value", entry.Value);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesKeyValuePairs()
    {
        var input = new List<ConfigEntry>
        {
            new() { Section = "Engine", Key = "X", Value = "1" },
            new() { Section = "Engine", Key = "Y", Value = "2" },
            new() { Section = "Game",   Key = "Mode", Value = "Hard" },
        };

        var serialized = _parser.Serialize(input);
        var reparsed = _parser.Parse(serialized);

        Assert.Equal(3, reparsed.Where(e => !string.IsNullOrEmpty(e.Key)).Count());
        Assert.Equal("1", reparsed.First(e => e.FullKey == "Engine.X").Value);
        Assert.Equal("Hard", reparsed.First(e => e.FullKey == "Game.Mode").Value);
    }

    [Fact]
    public void CanParse_RecognizesIniFormat()
    {
        Assert.True(_parser.CanParse("[Section]\nKey=Value"));
        Assert.True(_parser.CanParse("Key=Value"));
        Assert.False(_parser.CanParse("not ini at all just text"));
        Assert.False(_parser.CanParse("{ \"json\": true }"));
    }
}
