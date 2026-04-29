using UEModManager.Models;
using UEModManager.Services.Config;

namespace UEModManager.Core.Tests.Services.Config;

public class JsonConfigParserTests
{
    private readonly JsonConfigParser _parser = new();

    [Fact]
    public void Format_Is_Json()
    {
        Assert.Equal(ConfigFormat.Json, _parser.Format);
    }

    [Fact]
    public void Parse_FlatObject_ProducesSectionlessEntries()
    {
        var entries = _parser.Parse("""{"name":"A","count":3}""");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("", e.Section));
        Assert.Contains(entries, e => e.Key == "name" && e.Value == "A");
        Assert.Contains(entries, e => e.Key == "count" && e.Value == "3");
    }

    [Fact]
    public void Parse_NestedObject_FlattensToDottedSection()
    {
        var entries = _parser.Parse("""{"engine":{"resolution":{"x":1920}}}""");

        var entry = Assert.Single(entries);
        Assert.Equal("engine.resolution", entry.Section);
        Assert.Equal("x", entry.Key);
        Assert.Equal("1920", entry.Value);
    }

    [Fact]
    public void Parse_Array_GeneratesIndexedKeys()
    {
        var entries = _parser.Parse("""{"items":["a","b","c"]}""");

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Key == "items[0]" && e.Value == "a");
        Assert.Contains(entries, e => e.Key == "items[2]" && e.Value == "c");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmpty_NoThrow()
    {
        var entries = _parser.Parse("not json at all { ");

        Assert.Empty(entries);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesScalarValues()
    {
        var input = new List<ConfigEntry>
        {
            new() { Section = "engine", Key = "x", Value = "1920" },
            new() { Section = "engine", Key = "y", Value = "1080" },
            new() { Section = "",       Key = "name", Value = "Demo" },
        };

        var json = _parser.Serialize(input);
        var reparsed = _parser.Parse(json);

        Assert.Equal("1920", reparsed.First(e => e.FullKey == "engine.x").Value);
        Assert.Equal("Demo", reparsed.First(e => e.Key == "name").Value);
    }

    [Fact]
    public void CanParse_RecognizesJsonStart()
    {
        Assert.True(_parser.CanParse("{ \"x\": 1 }"));
        Assert.True(_parser.CanParse("[1, 2]"));
        Assert.True(_parser.CanParse("   {whitespace ok}"));
        Assert.False(_parser.CanParse("Key=Value"));
        Assert.False(_parser.CanParse("plain text without brackets"));
        // 注：CanParse 仅看首字符，"[Section]" 会被识别为 JSON。
        // 生产环境靠 ConfigMergeEngine.DetectFormat 按扩展名优先选 parser，CanParse 仅是兜底。
    }
}
