using UEModManager.Models;
using UEModManager.Services.Conflict;

namespace UEModManager.Core.Tests.Services.Conflict;

/// <summary>
/// ConflictDetector 端到端测试。
///
/// 行为：在"每包独立子目录"的部署模型下，仍能检测**加载顺序冲突**——
/// 即多个包声明同名 RelativeTargetPath，引擎按加载顺序会有覆盖的情况。
/// 关键是 <see cref="ConflictDetector.ComputeLoadConflictKey"/> 用"无 PackageKey 子目录"的规范化路径作 dict key，
/// 与 <see cref="ConflictDetector.ComputeTargetPath"/>（含 PackageKey 子目录，用于实际部署）解耦。
///
/// 设计来源：docs/findings/2026-04-28-conflict-detector-noop-by-design.md（已修复）。
/// </summary>
public class ConflictDetectorTests
{
    private const string ModPath = "C:/Games/Demo/Mods";
    private const string GamePath = "C:/Games/Demo";

    private static Package MakeMod(string key, string display, params (string rel, string? hash)[] artifacts)
        => new()
        {
            PackageKey = key,
            DisplayName = display,
            Kind = PackageKind.Mod,
            HostGameName = "demo",
            Artifacts = artifacts.Select(a => new PackageArtifact
            {
                RelativeSourcePath = a.rel,
                RelativeTargetPath = a.rel,
                ArtifactType = ArtifactType.ModFile,
                FileHash = a.hash,
                FileSize = 100
            }).ToList(),
        };

    private static InstanceProfile MakeProfile(params (string key, int prio, bool enabled)[] entries)
        => new()
        {
            Name = "TestProfile",
            HostGameName = "demo",
            Packages = entries.Select(e => new ProfilePackageEntry
            {
                PackageKey = e.key,
                Priority = e.prio,
                IsEnabled = e.enabled
            }).ToList(),
        };

    // ─── 主路径：加载顺序冲突 ───

    [Fact]
    public void DetectConflicts_TwoPackagesSameRelativePath_ProducesLoadOrderConflict()
    {
        var profile = MakeProfile(("low", 10, true), ("high", 0, true));
        var packages = new Dictionary<string, Package>
        {
            ["low"]  = MakeMod("low",  "LOW",  ("shared.pak", "h-low")),
            ["high"] = MakeMod("high", "HIGH", ("shared.pak", "h-high")),
        };

        var conflicts = ConflictDetector.DetectConflicts(profile, packages, ModPath, GamePath);

        var c = Assert.Single(conflicts);
        Assert.Equal(ConflictType.LoadOrder, c.Type);
        Assert.Equal("high", c.WinnerPackageKey);
        Assert.Single(c.Losers);
        Assert.Equal("low", c.Losers[0].PackageKey);
        Assert.Equal(ConflictSeverity.Warning, c.Severity);  // 不同 hash
    }

    [Fact]
    public void DetectConflicts_SameContent_SeverityIsInfo()
    {
        // 两个包提供哈希相同的同名文件 → 仅是重复，不影响行为
        var profile = MakeProfile(("a", 0, true), ("b", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("shared.pak", "same-hash")),
            ["b"] = MakeMod("b", "B", ("shared.pak", "same-hash")),
        };

        var c = Assert.Single(ConflictDetector.DetectConflicts(profile, packages, ModPath, GamePath));
        Assert.Equal(ConflictSeverity.Info, c.Severity);
    }

    [Fact]
    public void DetectConflicts_NonOverlappingFiles_NoConflict()
    {
        var profile = MakeProfile(("a", 0, true), ("b", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("a.pak", "h1")),
            ["b"] = MakeMod("b", "B", ("b.pak", "h2")),
        };

        var conflicts = ConflictDetector.DetectConflicts(profile, packages, ModPath, GamePath);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void DetectConflicts_RecordsHostAndProfileContext()
    {
        var profile = MakeProfile(("a", 0, true), ("b", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("x.pak", "h1")),
            ["b"] = MakeMod("b", "B", ("x.pak", "h2")),
        };

        var c = Assert.Single(ConflictDetector.DetectConflicts(profile, packages, ModPath, GamePath));
        Assert.Equal(profile.Id, c.ProfileId);
        Assert.Equal("demo", c.HostGameName);
    }

    [Fact]
    public void DetectConflicts_UserOverrideApplied()
    {
        var profile = MakeProfile(("a", 0, true), ("b", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("shared.pak", "h1")),
            ["b"] = MakeMod("b", "B", ("shared.pak", "h2")),
        };
        var loadKey = ConflictDetector.ComputeLoadConflictKey(
            packages["a"].Artifacts[0], packages["a"], profile.Packages[0], ModPath, GamePath);
        var overrides = new Dictionary<string, string> { [loadKey] = "b" };

        var c = Assert.Single(ConflictDetector.DetectConflicts(
            profile, packages, ModPath, GamePath, overrides));
        Assert.Equal("b", c.WinnerPackageKey);
        Assert.True(c.IsUserOverride);
        Assert.Equal(ResolutionMethod.UserOverride, c.Resolution);
    }

    // ─── 边界 ───

    [Fact]
    public void DetectConflicts_NoEnabledPackages_ReturnsEmpty()
    {
        var profile = MakeProfile(("a", 0, false));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("file.pak", "h"))
        };

        Assert.Empty(ConflictDetector.DetectConflicts(profile, packages, ModPath, GamePath));
    }

    [Fact]
    public void DetectConflicts_PackageNotInDictionary_IsSilentlySkipped()
    {
        var profile = MakeProfile(("ghost", 0, true), ("a", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("file.pak", "h1")),
        };

        Assert.Empty(ConflictDetector.DetectConflicts(profile, packages, ModPath, GamePath));
    }

    // ─── CollectOwners 行为 ───

    [Fact]
    public void CollectOwners_KeyDoesNotIncludePackageKeySubdir()
    {
        var profile = MakeProfile(("a", 0, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("shared.pak", "h"))
        };

        var owners = ConflictDetector.CollectOwners(profile, packages, ModPath, GamePath);

        var key = Assert.Single(owners.Keys);
        Assert.DoesNotContain("/a/", key.Replace('\\', '/'));   // 不含 PackageKey 子目录
        Assert.EndsWith("shared.pak", key);
    }

    [Fact]
    public void CollectOwners_DifferentRelativePaths_GetSeparateEntries()
    {
        var profile = MakeProfile(("a", 0, true), ("b", 1, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = MakeMod("a", "A", ("alpha.pak", "ha")),
            ["b"] = MakeMod("b", "B", ("beta.pak",  "hb")),
        };

        var owners = ConflictDetector.CollectOwners(profile, packages, ModPath, GamePath);

        Assert.Equal(2, owners.Count);
        Assert.All(owners.Values, list => Assert.Single(list));
    }

    [Fact]
    public void CollectOwners_SkipsPreviewImageArtifacts()
    {
        var profile = MakeProfile(("a", 0, true));
        var packages = new Dictionary<string, Package>
        {
            ["a"] = new()
            {
                PackageKey = "a", DisplayName = "A", HostGameName = "demo", Kind = PackageKind.Mod,
                Artifacts =
                [
                    new() { RelativeSourcePath = "p.png", RelativeTargetPath = "p.png", ArtifactType = ArtifactType.PreviewImage, FileHash = "h" },
                    new() { RelativeSourcePath = "real.pak", RelativeTargetPath = "real.pak", ArtifactType = ArtifactType.ModFile, FileHash = "h2" }
                ]
            },
        };

        var entry = Assert.Single(ConflictDetector.CollectOwners(profile, packages, ModPath, GamePath));
        Assert.EndsWith("real.pak", entry.Key);
    }

    // ─── ComputeTargetPath（实际部署路径，含 PackageKey）───

    [Fact]
    public void ComputeTargetPath_Mod_PutsUnderPackageKeySubdir()
    {
        var pkg = MakeMod("foo", "FOO", ("file.pak", "h"));
        var entry = new ProfilePackageEntry { PackageKey = "foo" };

        var result = ConflictDetector.ComputeTargetPath(
            pkg.Artifacts[0], pkg, entry, ModPath, GamePath);

        Assert.Contains(Path.Combine("Mods", "foo", "file.pak"), result);
    }

    [Fact]
    public void ComputeTargetPath_Plugin_PutsUnderGamePluginPackageKeySubdir()
    {
        var pkg = new Package
        {
            PackageKey = "myplugin", DisplayName = "MyPlugin",
            HostGameName = "demo", Kind = PackageKind.Plugin,
            PluginTargetPath = "Plugins",
            Artifacts =
            [
                new() { RelativeSourcePath = "x.dll", RelativeTargetPath = "x.dll", ArtifactType = ArtifactType.PluginFile, FileHash = "h" }
            ]
        };
        var entry = new ProfilePackageEntry { PackageKey = "myplugin" };

        var result = ConflictDetector.ComputeTargetPath(
            pkg.Artifacts[0], pkg, entry, ModPath, GamePath);

        Assert.Contains(Path.Combine("Plugins", "myplugin", "x.dll"), result);
    }

    // ─── ComputeLoadConflictKey（冲突归一化路径，无 PackageKey）───

    [Fact]
    public void ComputeLoadConflictKey_Mod_OmitsPackageKey()
    {
        var pkg = MakeMod("foo", "FOO", ("file.pak", "h"));
        var entry = new ProfilePackageEntry { PackageKey = "foo" };

        var key = ConflictDetector.ComputeLoadConflictKey(
            pkg.Artifacts[0], pkg, entry, ModPath, GamePath);

        Assert.Contains(Path.Combine("Mods", "file.pak"), key);
        Assert.DoesNotContain(Path.Combine("Mods", "foo"), key);  // 关键断言
    }

    [Fact]
    public void ComputeLoadConflictKey_DifferentPluginTargetPath_DistinctKeys()
    {
        // 两个 plugin 装到不同 PluginTargetPath，即使同名也不算冲突
        var pkgA = new Package
        {
            PackageKey = "a", DisplayName = "A", HostGameName = "demo", Kind = PackageKind.Plugin,
            PluginTargetPath = "Plugins/Custom",
            Artifacts = [new() { RelativeSourcePath = "x.dll", RelativeTargetPath = "x.dll", ArtifactType = ArtifactType.PluginFile, FileHash = "h" }]
        };
        var pkgB = new Package
        {
            PackageKey = "b", DisplayName = "B", HostGameName = "demo", Kind = PackageKind.Plugin,
            PluginTargetPath = "Plugins/Other",
            Artifacts = [new() { RelativeSourcePath = "x.dll", RelativeTargetPath = "x.dll", ArtifactType = ArtifactType.PluginFile, FileHash = "h" }]
        };

        var keyA = ConflictDetector.ComputeLoadConflictKey(
            pkgA.Artifacts[0], pkgA, new ProfilePackageEntry { PackageKey = "a" }, ModPath, GamePath);
        var keyB = ConflictDetector.ComputeLoadConflictKey(
            pkgB.Artifacts[0], pkgB, new ProfilePackageEntry { PackageKey = "b" }, ModPath, GamePath);

        Assert.NotEqual(keyA, keyB);
    }

    // ─── ComputeRelativePath（保留的辅助函数）───

    [Fact]
    public void ComputeRelativePath_UnderModPath_ReturnsRelative()
    {
        var rel = ConflictDetector.ComputeRelativePath(
            Path.Combine(ModPath, "sub", "file.pak"), ModPath, GamePath);
        Assert.Equal(Path.Combine("sub", "file.pak"), rel);
    }

    [Fact]
    public void ComputeRelativePath_UnderGamePath_FallbackToGameRelative()
    {
        var rel = ConflictDetector.ComputeRelativePath(
            Path.Combine(GamePath, "Plugins", "x.dll"), ModPath, GamePath);
        Assert.Equal(Path.Combine("Plugins", "x.dll"), rel);
    }

    [Fact]
    public void ComputeRelativePath_Foreign_ReturnsAbsoluteAsIs()
    {
        var rel = ConflictDetector.ComputeRelativePath(
            "C:/totally/elsewhere/file.txt", ModPath, GamePath);
        Assert.Equal("C:/totally/elsewhere/file.txt", rel);
    }

    [Fact]
    public void ComputeRelativePath_EmptyBasePaths_ReturnsAbsolute()
    {
        var rel = ConflictDetector.ComputeRelativePath("/abs/file", "", "");
        Assert.Equal("/abs/file", rel);
    }

    // ─── 错误路径 ───

    [Fact]
    public void DetectConflicts_NullProfile_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConflictDetector.DetectConflicts(null!, new Dictionary<string, Package>(), ModPath, GamePath));
    }

    [Fact]
    public void DetectConflicts_NullPackagesDict_Throws()
    {
        var profile = MakeProfile();
        Assert.Throws<ArgumentNullException>(() =>
            ConflictDetector.DetectConflicts(profile, null!, ModPath, GamePath));
    }
}
