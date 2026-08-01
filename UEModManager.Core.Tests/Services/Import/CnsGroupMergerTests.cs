using System.Collections.Generic;
using System.Linq;
using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

/// <summary>
/// 剑星 CNS 分组合并。这段逻辑此前从未被测试覆盖，也正因为如此，在 v2.0 导入链路重写时
/// 被整条丢掉而无人察觉。这些用例的目的是把"什么该合并、什么不该合并"写死成可执行的说明。
/// </summary>
public class CnsGroupMergerTests
{
    // ─── 该合并的情形 ───

    [Fact]
    public void Merge_ConfigAndContentShareKeyword_MergedIntoOneGroup()
    {
        // CNS 的典型形态：配置与本体文件名不同源，按基础名分组会拆成两个包
        var groups = Groups(
            ("DekCNS-Nier2B", new[] { @"C:\tmp\DekCNS-Nier2B.json" }),
            ("Nier2B_P", new[] { @"C:\tmp\Nier2B_P.pak" }));

        var merged = CnsGroupMerger.Merge(groups);

        var group = Assert.Single(merged);
        Assert.Equal("DekCNS-Nier2B", group.Key); // 配置组的桶名保留，包名另由 SelectGroupName 按 .pak 决定
        Assert.Equal(
            new[] { @"C:\tmp\DekCNS-Nier2B.json", @"C:\tmp\Nier2B_P.pak" },
            group.Value);
    }

    [Fact]
    public void Merge_ContentGroupWithSiblingFiles_AllFilesMovedIntoConfigGroup()
    {
        // .pak 组通常还带 .ucas/.utoc，合并时必须整组搬过去
        var groups = Groups(
            ("DekCNS-Nier2B", new[] { @"C:\tmp\DekCNS-Nier2B.json" }),
            ("Nier2B_P", new[] { @"C:\tmp\Nier2B_P.pak", @"C:\tmp\Nier2B_P.ucas", @"C:\tmp\Nier2B_P.utoc" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(4, Assert.Single(merged).Value.Count);
    }

    [Fact]
    public void Merge_PartialKeywordOverlap_StillMerges()
    {
        // 关键词互为子串（nier2b ⊂ nier2bswimsuit）得 2 分，刚好达到阈值
        var groups = Groups(
            ("DekCNS-Nier2B", new[] { @"C:\tmp\DekCNS-Nier2B.json" }),
            ("Nier2BSwimsuit", new[] { @"C:\tmp\Nier2BSwimsuit.pak" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Single(merged);
    }

    [Fact]
    public void Merge_MultipleCandidates_PicksHighestScore()
    {
        var groups = Groups(
            ("DekCNS-Nier2B-Swimsuit", new[] { @"C:\tmp\DekCNS-Nier2B-Swimsuit.json" }),
            ("Nier2BOther", new[] { @"C:\tmp\Nier2BOther.pak" }),          // 仅子串命中
            ("Nier2B-Swimsuit", new[] { @"C:\tmp\Nier2B-Swimsuit.pak" }));  // 两个关键词完全命中

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(2, merged.Count);
        Assert.Contains(@"C:\tmp\Nier2B-Swimsuit.pak", merged["DekCNS-Nier2B-Swimsuit"]);
        Assert.True(merged.ContainsKey("Nier2BOther")); // 落选者原样保留
    }

    [Fact]
    public void Merge_TwoConfigs_EachClaimsAtMostOneContentGroup()
    {
        var groups = Groups(
            ("DekCNS-Nier2B", new[] { @"C:\tmp\DekCNS-Nier2B.json" }),
            ("DekCNS-Eve", new[] { @"C:\tmp\DekCNS-Eve.json" }),
            ("Nier2B_P", new[] { @"C:\tmp\Nier2B_P.pak" }),
            ("EveDress_P", new[] { @"C:\tmp\EveDress_P.pak" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(2, merged.Count);
        Assert.Contains(@"C:\tmp\Nier2B_P.pak", merged["DekCNS-Nier2B"]);
        Assert.Contains(@"C:\tmp\EveDress_P.pak", merged["DekCNS-Eve"]);
    }

    // ─── 不该合并的情形 ───

    [Fact]
    public void Merge_ConfigNameWithoutCnsMarker_NotMerged()
    {
        // 普通 MOD 也可能带 .json（如引擎配置），不含 cns 字样就不参与 CNS 合并
        var groups = Groups(
            ("Settings", new[] { @"C:\tmp\Settings.json" }),
            ("Settings_P", new[] { @"C:\tmp\Settings_P.pak" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_NoKeywordOverlap_NotMerged()
    {
        var groups = Groups(
            ("DekCNS-Nier2B", new[] { @"C:\tmp\DekCNS-Nier2B.json" }),
            ("CompletelyUnrelated", new[] { @"C:\tmp\CompletelyUnrelated.pak" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_CandidateWithoutPak_NotMerged()
    {
        // 候选组必须含 .pak：只有 .ucas/.utoc 的组不是本体
        var groups = Groups(
            ("DekCNS-Nier2B", new[] { @"C:\tmp\DekCNS-Nier2B.json" }),
            ("Nier2B_P", new[] { @"C:\tmp\Nier2B_P.ucas", @"C:\tmp\Nier2B_P.utoc" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_ShortKeywordsOnly_BelowThreshold_NotMerged()
    {
        // 长度小于 3 的片段不参与打分，这里两侧只剩 "v2"/"ab"，总分 0
        var groups = Groups(
            ("cns-v2", new[] { @"C:\tmp\cns-v2.json" }),
            ("ab", new[] { @"C:\tmp\ab.pak" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_GroupWithoutJson_LeftUntouched()
    {
        var groups = Groups(
            ("Nier2B_P", new[] { @"C:\tmp\Nier2B_P.pak" }),
            ("Eve_P", new[] { @"C:\tmp\Eve_P.pak" }));

        var merged = CnsGroupMerger.Merge(groups);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(CnsGroupMerger.Merge(Groups()));
    }

    [Fact]
    public void Merge_DoesNotMutateInput()
    {
        // 旧实现 new Dictionary<>(groups) 只复制了字典，AddRange 会就地改到入参的 List 上
        var groups = Groups(
            ("DekCNS-Nier2B", new[] { @"C:\tmp\DekCNS-Nier2B.json" }),
            ("Nier2B_P", new[] { @"C:\tmp\Nier2B_P.pak" }));

        CnsGroupMerger.Merge(groups);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups["DekCNS-Nier2B"]);
        Assert.Single(groups["Nier2B_P"]);
    }

    [Fact]
    public void Merge_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CnsGroupMerger.Merge(null!));
    }

    // ─── 关键词提取 ───

    [Fact]
    public void ExtractKeywords_StripsCnsDecorationAndLowercases()
    {
        Assert.Equal(new[] { "nier2b", "swimsuit" },
            CnsGroupMerger.ExtractKeywords("DekCNS-Nier2B-Swimsuit"));
    }

    [Fact]
    public void ExtractKeywords_DropsFragmentsShorterThanThree()
    {
        Assert.Equal(new[] { "swimsuit" },
            CnsGroupMerger.ExtractKeywords("v2-ab-swimsuit"));
    }

    [Fact]
    public void ExtractKeywords_StripsPatchSuffixAnywhere_LegacyQuirkPinned()
    {
        // "_p" 是全串替换而非去后缀，"my_part" 会被削成 "myart"。
        // 这是 v1.8 的原始行为，CNS 匹配整体是模糊打分，改动它会静默改变既有 MOD 的匹配结果。
        Assert.Equal(new[] { "myart" }, CnsGroupMerger.ExtractKeywords("my_part"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ExtractKeywords_EmptyName_ReturnsEmpty(string? name)
    {
        Assert.Empty(CnsGroupMerger.ExtractKeywords(name));
    }

    // ─── 打分与命名判定 ───

    [Fact]
    public void ScoreKeywordMatch_ExactBeatsPartial()
    {
        var exact = CnsGroupMerger.ScoreKeywordMatch(new[] { "nier2b" }, new[] { "nier2b" });
        var partial = CnsGroupMerger.ScoreKeywordMatch(new[] { "nier2b" }, new[] { "nier2bswimsuit" });

        Assert.Equal(3, exact);
        Assert.Equal(2, partial);
        Assert.Equal(0, CnsGroupMerger.ScoreKeywordMatch(new[] { "nier2b" }, new[] { "unrelated" }));
    }

    [Theory]
    [InlineData("DekCNS-Nier2B", true)]
    [InlineData("Nier2B.dekcns", true)]
    [InlineData("something-CNS-config", true)]
    [InlineData("Settings", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeCnsConfigName(string? name, bool expected)
    {
        Assert.Equal(expected, CnsGroupMerger.LooksLikeCnsConfigName(name));
    }

    private static Dictionary<string, List<string>> Groups(params (string Key, string[] Files)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Files.ToList(), StringComparer.OrdinalIgnoreCase);
}
