using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;
using UEModManager.Services.Conflict;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// <see cref="AssetConflictDetector"/> 的分组规则回归。
///
/// 每条用例对应一个"用户看到的面板会变成什么样"的结论：
/// 组数、成员数、重复与共享组件是否从冲突组里摘干净。
/// </summary>
public class AssetConflictDetectorTests
{
    private static readonly SharedComponentRule[] Rules =
    [
        new("UE4SS 运行时", ["ue4ss/"],
            ["dwmapi.dll", "ue4ss.dll", "mods.json", "mods.txt"]),
        new("CSharpLoader 运行时", ["csharploader/"], ["version.dll", "b1cs.ini"])
    ];

    /// <summary>
    /// 造一个 MOD 包索引。<paramref name="files"/> 是 (资产键, 指纹)。
    ///
    /// 同一个资产键出现多次时按 .uasset / .uexp / .ubulk 依次给扩展名 ——
    /// 真实 pak 里一个资产就是这三份文件共用一个键。
    /// </summary>
    private static ModAssetIndex Mod(string key, string? contentHash, params (string AssetKey, string? Fingerprint)[] files)
    {
        var suffixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AssetEntry>();
        foreach (var f in files)
        {
            var index = suffixCounts.GetValueOrDefault(f.AssetKey);
            suffixCounts[f.AssetKey] = index + 1;
            var extension = AssetExtensions[index % AssetExtensions.Length];

            entries.Add(new AssetEntry(
                AssetKey: f.AssetKey,
                DisplayPath: f.AssetKey + extension,
                Fingerprint: f.Fingerprint,
                FingerprintKind: f.Fingerprint is null ? FingerprintKind.None : FingerprintKind.ContentHash,
                Size: 10, DataOffset: 0, StoredSize: 10, CompressionMethod: 0,
                SourceFile: key + ".pak"));
        }

        return new ModAssetIndex(
            PackageKey: key,
            DisplayName: key,
            Kind: PackageKind.Mod,
            ContentHash: contentHash,
            Entries: entries,
            Status: IndexStatus.Complete,
            StatusDetail: null);
    }

    private static readonly string[] AssetExtensions = [".uasset", ".uexp", ".ubulk"];

    /// <summary>造一个插件包索引，路径是相对游戏根的文件路径。</summary>
    private static ModAssetIndex Plugin(string key, IndexStatus status = IndexStatus.Complete,
        params (string Path, string? Fingerprint)[] files)
        => new(
            PackageKey: key,
            DisplayName: key,
            Kind: PackageKind.Plugin,
            ContentHash: null,
            Entries: files.Select(f => new AssetEntry(
                AssetKey: f.Path,
                DisplayPath: f.Path,
                Fingerprint: f.Fingerprint,
                FingerprintKind: f.Fingerprint is null ? FingerprintKind.None : FingerprintKind.ContentHash,
                Size: 10, DataOffset: -1, StoredSize: -1, CompressionMethod: -1,
                SourceFile: key)).ToList(),
            Status: status,
            StatusDetail: null);

    [Fact]
    public void 两包同资产不同指纹成为一组()
    {
        var report = AssetConflictDetector.Build(
            [Mod("a", null, ("x/t_armor", "h1")), Mod("b", null, ("x/t_armor", "h2"))],
            []);

        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Members.Count);
        Assert.Single(group.SharedAssets);
        Assert.Equal(0, report.Stats.IdenticalOverlapsHidden);
    }

    [Fact]
    public void 两包同资产同指纹不成为组并计入隐藏()
    {
        var report = AssetConflictDetector.Build(
            [Mod("a", null, ("x/t_armor", "h1")), Mod("b", null, ("x/t_armor", "h1"))],
            []);

        Assert.Empty(report.Groups);
        Assert.Equal(1, report.Stats.IdenticalOverlapsHidden);
    }

    [Fact]
    public void 三包同资产是一个三人组而不是三组()
    {
        var report = AssetConflictDetector.Build(
            [
                Mod("a", null, ("x/t_armor", "h1")),
                Mod("b", null, ("x/t_armor", "h2")),
                Mod("c", null, ("x/t_armor", "h3"))
            ],
            []);

        var group = Assert.Single(report.Groups);
        Assert.Equal(3, group.Members.Count);
        // 用户要决定的是"这三个里留哪个"，不是"每对之间谁赢"。
        Assert.Equal(3, group.Members.Select(m => m.PackageKey).Distinct().Count());
    }

    [Fact]
    public void 同名不同路径不成为组()
    {
        var report = AssetConflictDetector.Build(
            [Mod("a", null, ("x/t_armor", "h1")), Mod("b", null, ("y/t_armor", "h2"))],
            []);

        Assert.Empty(report.Groups);
    }

    [Fact]
    public void 同一资产键下的多个文件全部参与比较()
    {
        // a 与 b 的 .uasset 相同，但纹理数据不同 —— 只比一条会把真实分歧误判成"内容相同"。
        var report = AssetConflictDetector.Build(
            [
                Mod("a", null, ("x/t_armor", "same"), ("x/t_armor", "data1")),
                Mod("b", null, ("x/t_armor", "same"), ("x/t_armor", "data2"))
            ],
            []);

        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Members.Count);
        Assert.Equal(0, report.Stats.IdenticalOverlapsHidden);
    }

    [Fact]
    public void 整包内容相同成为重复且不产生冲突组()
    {
        var report = AssetConflictDetector.Build(
            [
                Mod("a", "SAME", ("x/t_armor", "h1")),
                Mod("b", "SAME", ("x/t_armor", "h2"))
            ],
            []);

        Assert.Empty(report.Groups);
        var duplicate = Assert.Single(report.Duplicates);
        Assert.Equal("a", duplicate.Primary.PackageKey);
        Assert.Equal("b", Assert.Single(duplicate.Copies).PackageKey);
    }

    [Fact]
    public void 同内容共享组件不产生冲突组()
    {
        var report = AssetConflictDetector.Build(
            [
                Plugin("p1", IndexStatus.Complete, ("ue4ss/Mods/mods.json", "SAME")),
                Plugin("p2", IndexStatus.Complete, ("ue4ss/Mods/mods.json", "SAME"))
            ],
            Rules);

        Assert.Empty(report.Groups);
        var component = Assert.Single(report.SharedComponents);
        Assert.Equal("UE4SS 运行时", component.Name);
        Assert.Equal("内容相同", component.Reason);
        Assert.Equal(2, component.Providers.Count);
    }

    [Fact]
    public void 版本不同时选版本较高的()
    {
        var provenance = new Dictionary<string, SharedComponentProvenance>
        {
            ["p1"] = new("1.0.0.0", null),
            ["p2"] = new("1.2.0.0", null)
        };

        var report = AssetConflictDetector.Build(
            [
                Plugin("p1", IndexStatus.Complete, ("csharploader/version.dll", "h1")),
                Plugin("p2", IndexStatus.Complete, ("csharploader/version.dll", "h2"))
            ],
            Rules, provenance);

        Assert.Empty(report.Groups);
        var component = Assert.Single(report.SharedComponents);
        Assert.Equal("p2", component.ChosenPackageKey);
        Assert.Contains("版本较高", component.Reason);
        Assert.Contains("1.2.0.0", component.Reason);
    }

    [Fact]
    public void 版本号按数值比较而非字典序()
    {
        // 字典序里 "1.9" > "1.10"，数值比较相反 —— 用错会让用户留下旧版本。
        var provenance = new Dictionary<string, SharedComponentProvenance>
        {
            ["old"] = new("1.9.0.0", null),
            ["new"] = new("1.10.0.0", null)
        };

        var report = AssetConflictDetector.Build(
            [
                Plugin("old", IndexStatus.Complete, ("csharploader/version.dll", "h1")),
                Plugin("new", IndexStatus.Complete, ("csharploader/version.dll", "h2"))
            ],
            Rules, provenance);

        Assert.Equal("new", Assert.Single(report.SharedComponents).ChosenPackageKey);
    }

    [Fact]
    public void 没有版本时按修改时间取最新()
    {
        var provenance = new Dictionary<string, SharedComponentProvenance>
        {
            ["older"] = new(null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ["newer"] = new(null, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        var report = AssetConflictDetector.Build(
            [
                Plugin("older", IndexStatus.Complete, ("csharploader/version.dll", "h1")),
                Plugin("newer", IndexStatus.Complete, ("csharploader/version.dll", "h2"))
            ],
            Rules, provenance);

        var component = Assert.Single(report.SharedComponents);
        Assert.Equal("newer", component.ChosenPackageKey);
        Assert.Equal("文件较新", component.Reason);
    }

    [Theory]
    [InlineData(IndexStatus.Encrypted)]
    [InlineData(IndexStatus.Unsupported)]
    [InlineData(IndexStatus.Failed)]
    public void 索引读不出来的包进无法分析且不进任何组(IndexStatus status)
    {
        var report = AssetConflictDetector.Build(
            [Mod("a", null, ("x/t_armor", "h1")), Plugin("bad", status, ("x/t_armor", "h2"))],
            Rules);

        Assert.Empty(report.Groups);
        Assert.Equal("bad", Assert.Single(report.Unanalyzable).PackageKey);
    }

    [Fact]
    public void 指纹为空的重叠保守视为冲突()
    {
        // 报一个可能多余的冲突，好过漏报一个真实冲突。
        var report = AssetConflictDetector.Build(
            [Mod("a", null, ("x/t_armor", null)), Mod("b", null, ("x/t_armor", "h2"))],
            []);

        Assert.Single(report.Groups);
        Assert.Equal(0, report.Stats.IdenticalOverlapsHidden);
    }

    [Fact]
    public void 说明文件被丢弃且计入共享组件文件数()
    {
        var report = AssetConflictDetector.Build(
            [
                Plugin("p1", IndexStatus.Complete, ("说明与事项.txt", "h1"), ("ue4ss/Mods/a.lua", "x1")),
                Plugin("p2", IndexStatus.Complete, ("说明与事项.txt", "h2"), ("ue4ss/Mods/a.lua", "x2"))
            ],
            Rules);

        // 两个说明文件都丢弃；ue4ss 下的 a.lua 是共享组件的一部分，也不进冲突组。
        Assert.Empty(report.Groups);
        Assert.Equal(4, report.Stats.SharedComponentFilesHidden);
    }

    [Fact]
    public void 组按共同资源数降序()
    {
        var report = AssetConflictDetector.Build(
            [
                Mod("a", null, ("x/t_one", "h1"), ("x/t_two", "h1")),
                Mod("b", null, ("x/t_one", "h2"), ("x/t_two", "h2")),
                Mod("c", null, ("x/t_solo", "h1")),
                Mod("d", null, ("x/t_solo", "h2"))
            ],
            []);

        Assert.Equal(2, report.Groups.Count);
        Assert.Equal(2, report.Groups[0].SharedAssets.Count);
    }

    [Fact]
    public void 摘要按类别统计()
    {
        var report = AssetConflictDetector.Build(
            [
                Mod("a", null, ("x/t_a", "h1"), ("x/sk_b", "h1")),
                Mod("b", null, ("x/t_a", "h2"), ("x/sk_b", "h2"))
            ],
            []);

        var group = Assert.Single(report.Groups);
        Assert.Equal("1 张贴图、1 个模型", group.Summary.Summary);
    }

    [Fact]
    public void 空输入产出空报告()
    {
        var report = AssetConflictDetector.Build([], Rules);

        Assert.Empty(report.Groups);
        Assert.Empty(report.Duplicates);
        Assert.Empty(report.SharedComponents);
        Assert.Empty(report.Unanalyzable);
        Assert.Equal(0, report.Stats.EnabledMods);
    }

    [Fact]
    public void 成员按展示名排序()
    {
        var report = AssetConflictDetector.Build(
            [Mod("zebra", null, ("x/t_a", "h1")), Mod("alpha", null, ("x/t_a", "h2"))],
            []);

        var group = Assert.Single(report.Groups);
        Assert.Equal(["alpha", "zebra"], group.Members.Select(m => m.DisplayName));
    }

    [Fact]
    public void FindOverlaps只返回两个以上包提供的资产()
    {
        var overlaps = AssetConflictDetector.FindOverlaps(
            [
                Mod("a", null, ("x/shared", "h1"), ("x/only_a", "h1")),
                Mod("b", null, ("x/shared", "h2"))
            ]);

        var overlap = Assert.Single(overlaps);
        Assert.Equal("x/shared", overlap.AssetKey);
        Assert.Equal(2, overlap.Entries.Count);
    }
}