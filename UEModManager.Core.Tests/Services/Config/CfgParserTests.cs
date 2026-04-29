using UEModManager.Models;
using UEModManager.Services.Config;

namespace UEModManager.Core.Tests.Services.Config;

public class CfgParserTests
{
    private readonly CfgParser _parser = new();

    [Fact]
    public void Format_Is_Cfg()
    {
        Assert.Equal(ConfigFormat.Cfg, _parser.Format);
    }

    [Fact]
    public void Parse_SimpleKeyValue_NoSection()
    {
        var entries = _parser.Parse("port=8080\nhost=localhost");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("", e.Section));
        Assert.Contains(entries, e => e.Key == "port" && e.Value == "8080");
        Assert.Contains(entries, e => e.Key == "host" && e.Value == "localhost");
    }

    [Fact]
    public void Parse_SupportsThreeCommentStyles()
    {
        var entries = _parser.Parse("; semicolon\n# hash\n// slashes\nKey=V");

        var commentLines = entries.Where(e => string.IsNullOrEmpty(e.Key) && e.Comment != null).ToList();
        Assert.Equal(3, commentLines.Count);
        Assert.Contains(entries, e => e.Key == "Key" && e.Value == "V");
    }

    [Fact]
    public void Serialize_RoundTrip()
    {
        var input = new List<ConfigEntry>
        {
            new() { Key = "a", Value = "1" },
            new() { Key = "b", Value = "two" },
        };

        var serialized = _parser.Serialize(input);
        var reparsed = _parser.Parse(serialized);

        Assert.Equal(2, reparsed.Count);
        Assert.Equal("1", reparsed.First(e => e.Key == "a").Value);
        Assert.Equal("two", reparsed.First(e => e.Key == "b").Value);
    }

    [Fact]
    public void CanParse_RejectsIniSectionsAndJson()
    {
        Assert.True(_parser.CanParse("port=8080"));
        Assert.False(_parser.CanParse("[Section]"));
        Assert.False(_parser.CanParse("{ }"));
    }

    [Fact]
    public void Parse_MalformedLine_WithoutEquals_IsSkipped()
    {
        var entries = _parser.Parse("orphan_line_no_equals\nport=80");

        var entry = Assert.Single(entries);
        Assert.Equal("port", entry.Key);
    }
}
