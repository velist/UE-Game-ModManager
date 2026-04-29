using UEModManager.Models;
using UEModManager.Services.Config;

namespace UEModManager.Core.Tests.Services.Config;

/// <summary>
/// ConfigMerger 纯函数测试。覆盖 4 种合并策略 + 来源追踪 + 冲突检测。
/// </summary>
public class ConfigMergerTests
{
    private static readonly IReadOnlyDictionary<ConfigFormat, IConfigParser> AllParsers
        = new Dictionary<ConfigFormat, IConfigParser>
        {
            [ConfigFormat.Ini] = new IniParser(),
            [ConfigFormat.Json] = new JsonConfigParser(),
            [ConfigFormat.Cfg] = new CfgParser(),
        };

    private static ConfigMergeSource Source(string key, int prio, string? path = null)
        => new()
        {
            PackageKey = key,
            DisplayName = key.ToUpperInvariant(),
            Priority = prio,
            SourceFilePath = path ?? $"/fake/{key}.ini",
        };

    // ─── DetectFormat ───

    [Theory]
    [InlineData("path/to/file.ini", ConfigFormat.Ini)]
    [InlineData("X.JSON", ConfigFormat.Json)]
    [InlineData("config.yaml", ConfigFormat.Yaml)]
    [InlineData("config.yml", ConfigFormat.Yaml)]
    [InlineData("a.toml", ConfigFormat.Toml)]
    [InlineData("a.cfg", ConfigFormat.Cfg)]
    [InlineData("noext", ConfigFormat.Unknown)]
    [InlineData("a.unknown", ConfigFormat.Unknown)]
    public void DetectFormat_ByExtension(string path, ConfigFormat expected)
    {
        Assert.Equal(expected, ConfigMerger.DetectFormat(path));
    }

    [Fact]
    public void DetectFormatByContent_ChoosesFirstParserThatCanParse()
    {
        Assert.Equal(ConfigFormat.Json, ConfigMerger.DetectFormatByContent("{ \"x\": 1 }", AllParsers));
        Assert.Equal(ConfigFormat.Ini, ConfigMerger.DetectFormatByContent("[Section]\nKey=Value", AllParsers));
        Assert.Equal(ConfigFormat.Unknown, ConfigMerger.DetectFormatByContent("totally random text 123", AllParsers));
    }

    // ─── ReplaceFile 策略 ───

    [Fact]
    public void ReplaceFile_TakesHighestPrioritySourceContent()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Config/Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.ReplaceFile,
            Sources = [Source("hi", 0), Source("lo", 10)],
        };
        var contents = new Dictionary<string, string>
        {
            ["hi"] = "[Hi]\nKey=Winner",
            ["lo"] = "[Lo]\nKey=Loser",
        };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        Assert.Equal("[Hi]\nKey=Winner", result.MergedContent);
        Assert.Single(result.EntrySourceMap);
        Assert.Equal("hi", result.EntrySourceMap[0].SourcePackageKey);
    }

    [Fact]
    public void ReplaceFile_NoSources_FallsBackToBaseContent()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.ReplaceFile,
            BaseContent = "[Base]\nKey=Original",
            Sources = [],
        };

        var result = ConfigMerger.Merge(plan, new Dictionary<string, string>(), AllParsers);

        Assert.True(result.Success);
        Assert.Equal("[Base]\nKey=Original", result.MergedContent);
    }

    [Fact]
    public void UnknownFormat_DegradesToFileReplace()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "config.unknown",
            Format = ConfigFormat.Unknown,
            Strategy = ConfigMergeStrategy.MergeByKey,
            Sources = [Source("only", 0)],
        };
        var contents = new Dictionary<string, string> { ["only"] = "raw bytes" };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        Assert.Equal("raw bytes", result.MergedContent);
    }

    // ─── MergeByKey 策略 ───

    [Fact]
    public void MergeByKey_HighPriorityValue_OverridesLowPriority()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.MergeByKey,
            BaseContent = "",
            Sources = [Source("low", 10), Source("hi", 0)],
        };
        var contents = new Dictionary<string, string>
        {
            ["low"] = "[Engine]\nResolutionX=1280",
            ["hi"]  = "[Engine]\nResolutionX=1920",
        };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        Assert.Contains("ResolutionX=1920", result.MergedContent);
        Assert.DoesNotContain("ResolutionX=1280", result.MergedContent);
    }

    [Fact]
    public void MergeByKey_MultipleSourcesOnSameKey_ProducesConflictRecord()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.MergeByKey,
            Sources = [Source("a", 5), Source("b", 0)],  // b 优先级高
        };
        var contents = new Dictionary<string, string>
        {
            ["a"] = "[Engine]\nFOV=90",
            ["b"] = "[Engine]\nFOV=120",
        };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("Engine.FOV", conflict.FullKey);
        Assert.Equal("b", conflict.WinnerPackageKey);
        Assert.Equal("120", conflict.WinnerValue);
        Assert.Single(conflict.Losers);
        Assert.Equal("a", conflict.Losers[0].PackageKey);
    }

    [Fact]
    public void MergeByKey_NonOverlappingKeys_NoConflict_BothPreserved()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.MergeByKey,
            Sources = [Source("a", 0), Source("b", 10)],
        };
        var contents = new Dictionary<string, string>
        {
            ["a"] = "[Engine]\nWidth=1920",
            ["b"] = "[Engine]\nHeight=1080",
        };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        Assert.Empty(result.Conflicts);
        Assert.Contains("Width=1920", result.MergedContent);
        Assert.Contains("Height=1080", result.MergedContent);
    }

    [Fact]
    public void MergeByKey_TracksEntrySources()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.MergeByKey,
            Sources = [Source("a", 0), Source("b", 10)],
        };
        var contents = new Dictionary<string, string>
        {
            ["a"] = "[S]\nKeyA=1",
            ["b"] = "[S]\nKeyB=2",
        };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        Assert.Equal("a", result.EntrySourceMap.First(e => e.Key == "KeyA").SourcePackageKey);
        Assert.Equal("b", result.EntrySourceMap.First(e => e.Key == "KeyB").SourcePackageKey);
    }

    // ─── Patch 策略 ───

    [Fact]
    public void Patch_PreservesUnmodifiedBaseKeys()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.Patch,
            BaseContent = "[E]\nA=1\nB=2\nC=3",
            Sources = [Source("p", 0)],
        };
        var contents = new Dictionary<string, string>
        {
            ["p"] = "[E]\nB=99",  // 仅改 B
        };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        Assert.Contains("A=1", result.MergedContent);
        Assert.Contains("B=99", result.MergedContent);
        Assert.Contains("C=3", result.MergedContent);
        Assert.DoesNotContain("B=2", result.MergedContent);
    }

    // ─── Append 策略 ───

    [Fact]
    public void Append_AddsAllSourceEntriesAfterBase()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "settings.cfg",
            Format = ConfigFormat.Cfg,
            Strategy = ConfigMergeStrategy.Append,
            BaseContent = "base=v",
            Sources = [Source("a", 0), Source("b", 10)],
        };
        var contents = new Dictionary<string, string>
        {
            ["a"] = "extra1=A",
            ["b"] = "extra2=B",
        };

        var result = ConfigMerger.Merge(plan, contents, AllParsers);

        Assert.True(result.Success);
        Assert.Contains("base=v", result.MergedContent);
        Assert.Contains("extra1=A", result.MergedContent);
        Assert.Contains("extra2=B", result.MergedContent);
    }

    // ─── 错误路径 ───

    [Fact]
    public void NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConfigMerger.Merge(null!, new Dictionary<string, string>(), AllParsers));
    }

    [Fact]
    public void MissingSourceContent_IsSilentlySkipped()
    {
        var plan = new ConfigMergePlan
        {
            TargetRelativePath = "Engine.ini",
            Format = ConfigFormat.Ini,
            Strategy = ConfigMergeStrategy.MergeByKey,
            Sources = [Source("missing", 0)],  // 没在 contents 字典里
        };

        var result = ConfigMerger.Merge(plan, new Dictionary<string, string>(), AllParsers);

        Assert.True(result.Success);
        Assert.Empty(result.EntrySourceMap);
    }
}
