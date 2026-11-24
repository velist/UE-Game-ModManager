using UEModManager.Models;
using UEModManager.Services.DeploymentPlanning;
using UEModManager.Services.InstallLayout;

namespace UEModManager.Core.Tests.Services.InstallLayout;

/// <summary>
/// 夹具取自两份真实诊断包（2.1.3，影之刃零）里 packages.json 的 Artifacts 路径与 transactions 的落盘位置，
/// 路径换成了中性的 <c>D:\Games\PBZ</c>。
/// </summary>
internal static class PbzFixtures
{
    public const string InstallRoot = @"D:\Games\PBZ";
    public const string LauncherDir = InstallRoot + @"\steam";
    public const string BinaryDir = InstallRoot + @"\platform\steam";

    public static readonly GameInstallLayout Layout = new("steam", @"platform\steam");

    /// <summary>UE 的 UE4SS 约定 + 影之刃零的本体目录锚点（与主程序 GetInstallLayoutRules 给出的一致）。</summary>
    public static readonly InstallLayoutRules Rules = new()
    {
        BinaryAnchors = [["platform", "steam"]],
        LoaderDirectories = ["ue4ss"],
        ScriptModsDirectory = @"ue4ss\Mods",
        ScriptModMarkers = ["Scripts/main.lua", "dlls/main.dll"],
        LoaderScriptExtensions = [".lua"],
        SingletonConfigFileNames = ["Engine.ini", "Scalability.ini", "GameUserSettings.ini", "Game.ini", "Input.ini"],
    };

    /// <summary>Nexus「PBZ_AIO_1.8.2」里 PBZCompassMap 这个 UE4SS Lua MOD 的全部文件（相对 MOD 文件夹）。</summary>
    public static readonly string[] CompassMapFiles =
    [
        "enabled.txt", "minimap_mask.png", "points.txt", "rematch_logo.png",
        @"compass\arrow.png", @"compass\band.png", @"compass\dot.png", @"compass\drop.png",
        @"compass\glyph_E.png", @"compass\glyph_N.png", @"compass\glyph_S.png", @"compass\glyph_W.png", @"compass\seal.png",
        @"Scripts\bossplaces.lua", @"Scripts\lang_en.lua", @"Scripts\lang_ja.lua", @"Scripts\lang_zh.lua",
        @"Scripts\main.lua", @"Scripts\questdetails.lua", @"Scripts\roadbook.lua", @"Scripts\roadbook_en.lua",
        @"Scripts\roadbook_ja.lua", @"Scripts\roadbook_zh.lua",
    ];

    public static List<string> CompassMapUnder(string prefix)
        => CompassMapFiles.Select(f => prefix + @"PBZCompassMap\" + f).ToList();

    /// <summary>「2. UE4SS Companion Mod」：顶层直接是 MOD 文件夹，没有 ue4ss/Mods 这层。</summary>
    public static readonly string[] LooseScriptMod =
    [
        @"AMDFidelityOptimizer\enabled.txt",
        @"AMDFidelityOptimizer\Scripts\main.lua",
    ];

    /// <summary>「1. Choose Your GPU Profile」：三套互斥的 Engine.ini / Scalability.ini。</summary>
    public static readonly string[] GpuProfileVariants =
    [
        @"Tier 1 - 8GB VRAM (RX 6600, 6650 XT, 7600)\Engine.ini",
        @"Tier 1 - 8GB VRAM (RX 6600, 6650 XT, 7600)\Scalability.ini",
        @"Tier 2 - 12GB VRAM (RX 6700, 6750 XT, 7700 XT)\Engine.ini",
        @"Tier 2 - 12GB VRAM (RX 6700, 6750 XT, 7700 XT)\Scalability.ini",
        @"Tier 3 - 16GB+ VRAM (RX 6800, 6900, 7800, 7900 XT-XTX)\Engine.ini",
        @"Tier 3 - 16GB+ VRAM (RX 6800, 6900, 7800, 7900 XT-XTX)\Scalability.ini",
    ];

    public static Package MakePackage(
        string key, IEnumerable<string> targets, PackageKind kind = PackageKind.Plugin, string? targetRootPath = null)
        => new()
        {
            PackageKey = key, DisplayName = key, Kind = kind, HostGameName = "影之刃零",
            PreservesInstallLayout = true, TargetRootPath = targetRootPath,
            Artifacts = targets.Select(t => new PackageArtifact
            {
                RelativeSourcePath = key + @"\files\" + t, RelativeTargetPath = t,
                FileName = Path.GetFileName(t), FileHash = "h-" + t,
                ArtifactType = kind == PackageKind.Config ? ArtifactType.ConfigFile : ArtifactType.PluginFile,
            }).ToList()
        };
}

public class PluginRootResolverTests
{
    private static Func<string, bool> Exists(params string[] directories)
        => path => directories.Contains(path, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void 游戏路径是启动壳目录时_插件根改到本体目录()
    {
        var result = PluginRootResolver.Resolve(PbzFixtures.LauncherDir, PbzFixtures.Layout, Exists(PbzFixtures.BinaryDir));

        Assert.Equal(PbzFixtures.BinaryDir, result.PluginRoot);
        Assert.Equal(PbzFixtures.InstallRoot, result.InstallRoot);
        Assert.True(result.Redirected);
    }

    [Fact]
    public void 游戏路径已经是本体目录时_原样使用_不会被误判成启动壳目录()
    {
        // platform\steam 也以 steam 结尾：找不到启动壳时自动检测会退回本体。
        var result = PluginRootResolver.Resolve(PbzFixtures.BinaryDir, PbzFixtures.Layout, Exists(PbzFixtures.BinaryDir));

        Assert.Equal(PbzFixtures.BinaryDir, result.PluginRoot);
        Assert.Equal(PbzFixtures.InstallRoot, result.InstallRoot);
        Assert.False(result.Redirected);
    }

    [Fact]
    public void 本体目录不存在时_不改道()
    {
        var result = PluginRootResolver.Resolve(PbzFixtures.LauncherDir, PbzFixtures.Layout, Exists());

        Assert.Equal(PbzFixtures.LauncherDir, result.PluginRoot);
        Assert.False(result.Redirected);
    }

    [Theory]
    [InlineData(@"D:\Games\BlackMythWukong\b1\Binaries\Win64")]
    [InlineData(@"D:\Games\PBZ\steam\")]
    public void 没有目录约定的游戏_插件根就是游戏路径(string exeDir)
    {
        var result = PluginRootResolver.Resolve(exeDir, null, _ => true);

        Assert.Equal(exeDir.TrimEnd('\\'), result.PluginRoot);
        Assert.False(result.Redirected);
    }

    [Fact]
    public void 目录名只是恰好叫steam但层级对不上时_不改道()
    {
        var result = PluginRootResolver.Resolve(@"D:\steam", PbzFixtures.Layout, _ => true);

        // 剥掉 steam 之后只剩盘符根，platform\steam 存在与否由探测决定；这里探测恒真，仍应给出合法路径。
        Assert.Equal(@"D:\platform\steam", result.PluginRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 游戏路径未配置时_返回空串(string? exeDir)
        => Assert.Equal(string.Empty, PluginRootResolver.Resolve(exeDir, PbzFixtures.Layout, _ => true).PluginRoot);
}

public class InstallLayoutNormalizerTests
{
    private static readonly List<string> Expected = PbzFixtures.CompassMapUnder(@"ue4ss\Mods\");

    /// <summary>
    /// 诊断包里同一个 MOD 被玩家反复导入出的各种入库形状：压缩包带一层游戏安装目录名、
    /// 从安装根开始、platform 被当包装目录剥掉、多包一层。每一种都必须归到同一个位置。
    /// </summary>
    [Theory]
    [InlineData(@"PBZ\platform\steam\ue4ss\Mods\")]
    [InlineData(@"platform\steam\ue4ss\Mods\")]
    [InlineData(@"steam\ue4ss\Mods\")]
    [InlineData(@"PBZ_AIO_1.8.2\PBZ\platform\steam\ue4ss\Mods\")]
    [InlineData(@"ue4ss\Mods\")]
    [InlineData(@"Mods\")]
    [InlineData("")]
    public void 各种入库形状都归位到ue4ss的Mods目录(string prefix)
        => Assert.Equal(Expected, InstallLayoutNormalizer.Normalize(PbzFixtures.CompassMapUnder(prefix), PbzFixtures.Rules));

    [Fact]
    public void 归位是幂等的()
    {
        var once = InstallLayoutNormalizer.Normalize(PbzFixtures.CompassMapUnder(@"PBZ\platform\steam\ue4ss\Mods\"), PbzFixtures.Rules);
        Assert.Equal(once, InstallLayoutNormalizer.Normalize(once, PbzFixtures.Rules));
    }

    [Fact]
    public void 散装的UE4SS_MOD文件夹_收进ue4ss的Mods目录()
    {
        var result = InstallLayoutNormalizer.Normalize(PbzFixtures.LooseScriptMod, PbzFixtures.Rules);

        Assert.Equal(
            [@"ue4ss\Mods\AMDFidelityOptimizer\enabled.txt", @"ue4ss\Mods\AMDFidelityOptimizer\Scripts\main.lua"],
            result);
    }

    [Fact]
    public void Cpp脚本MOD按dlls的main_dll识别()
    {
        var result = InstallLayoutNormalizer.Normalize([@"MyCppMod\dlls\main.dll", @"MyCppMod\enabled.txt"], PbzFixtures.Rules);

        Assert.Equal([@"ue4ss\Mods\MyCppMod\dlls\main.dll", @"ue4ss\Mods\MyCppMod\enabled.txt"], result);
    }

    [Fact]
    public void UE4SS运行时本身_代理DLL留在插件根_ue4ss目录原样()
    {
        // 诊断包里的「UE4SS For Phantom Blade Zero」：dwmapi.dll 与 ue4ss/ 并列。
        string[] runtime =
        [
            "dwmapi.dll", @"ue4ss\UE4SS.dll", @"ue4ss\UE4SS-settings.ini", @"ue4ss\Mods\mods.txt",
            @"ue4ss\Mods\BPModLoaderMod\Scripts\main.lua", @"ue4ss\Mods\shared\UEHelpers\UEHelpers.lua",
        ];

        Assert.Equal(runtime, InstallLayoutNormalizer.Normalize(runtime, PbzFixtures.Rules));
    }

    [Fact]
    public void 从安装根打包的UE4SS运行时_代理DLL剥到插件根()
    {
        var result = InstallLayoutNormalizer.Normalize(
            [@"PBZ\platform\steam\dwmapi.dll", @"PBZ\platform\steam\ue4ss\UE4SS.dll"], PbzFixtures.Rules);

        Assert.Equal(["dwmapi.dll", @"ue4ss\UE4SS.dll"], result);
    }

    [Fact]
    public void 与加载器无关的文件不动()
    {
        string[] paths = [@"CSharpLoader\Mods\X\config.json", "version.dll", @"Tier 1\Engine.ini"];

        Assert.Equal(paths, InstallLayoutNormalizer.Normalize(paths, PbzFixtures.Rules));
    }

    [Fact]
    public void 没有规则的引擎_原样返回()
    {
        string[] paths = [@"AMDFidelityOptimizer\Scripts\main.lua"];

        Assert.Same(paths, InstallLayoutNormalizer.Normalize(paths, InstallLayoutRules.Empty));
    }

    [Fact]
    public void NormalizePackage_老版本入库的包_得到归位后的副本_原包不变()
    {
        var legacy = PbzFixtures.MakePackage("PBZ", PbzFixtures.CompassMapUnder(@"platform\steam\ue4ss\Mods\"));

        var normalized = InstallLayoutNormalizer.NormalizePackage(legacy, PbzFixtures.Rules);

        Assert.NotSame(legacy, normalized);
        Assert.Equal(Expected, normalized.Artifacts.Select(a => a.RelativeTargetPath));
        Assert.Equal(legacy.Artifacts.Select(a => a.RelativeSourcePath), normalized.Artifacts.Select(a => a.RelativeSourcePath));
        Assert.Equal(legacy.Artifacts.Select(a => a.Id), normalized.Artifacts.Select(a => a.Id));
        Assert.StartsWith(@"platform\steam\", legacy.Artifacts[0].RelativeTargetPath);
        Assert.True(normalized.PreservesInstallLayout);
    }

    [Fact]
    public void NormalizePackage_已在正确位置的包_返回原实例()
    {
        var package = PbzFixtures.MakePackage("ok", Expected);

        Assert.Same(package, InstallLayoutNormalizer.NormalizePackage(package, PbzFixtures.Rules));
    }

    [Fact]
    public void NormalizePackage_玩家指定了安装子目录_不动()
    {
        var package = PbzFixtures.MakePackage("custom", PbzFixtures.LooseScriptMod, targetRootPath: @"ue4ss\Mods");

        Assert.Same(package, InstallLayoutNormalizer.NormalizePackage(package, PbzFixtures.Rules));
    }

    [Fact]
    public void NormalizePackage_MOD包不动()
    {
        var mod = PbzFixtures.MakePackage("pak", [@"platform\steam\x_P.pak"], PackageKind.Mod);

        Assert.Same(mod, InstallLayoutNormalizer.NormalizePackage(mod, PbzFixtures.Rules));
    }

    [Fact]
    public void NormalizePackages_方案条目指定了安装子目录的包不动()
    {
        var package = PbzFixtures.MakePackage("custom", PbzFixtures.LooseScriptMod);
        var profile = new InstanceProfile
        {
            HostGameName = "影之刃零", Name = "p",
            Packages = [new ProfilePackageEntry { PackageKey = "custom", IsEnabled = true, TargetRootPath = "somewhere" }]
        };

        var result = InstallLayoutNormalizer.NormalizePackages(
            new Dictionary<string, Package> { ["custom"] = package }, profile, PbzFixtures.Rules);

        Assert.Same(package, result["custom"]);
    }

    [Fact]
    public void NormalizePackage_安装树里的图片不再当预览图跳过_包根上的图片仍是预览图()
    {
        // 诊断包里 PBZCompassMap 的 11 张 png 全被记成 PreviewImage：23 个文件只部署了 12 个。
        var package = PbzFixtures.MakePackage("PBZ",
            [@"ue4ss\Mods\PBZCompassMap\compass\arrow.png", @"ue4ss\Mods\PBZCompassMap\Scripts\main.lua", "cover.png"]);
        package.Artifacts = package.Artifacts.Select(a => new PackageArtifact
        {
            Id = a.Id, RelativeSourcePath = a.RelativeSourcePath, RelativeTargetPath = a.RelativeTargetPath,
            FileName = a.FileName, FileHash = a.FileHash,
            ArtifactType = a.FileName.EndsWith(".png") ? ArtifactType.PreviewImage : a.ArtifactType
        }).ToList();

        var normalized = InstallLayoutNormalizer.NormalizePackage(package, InstallLayoutRules.Empty);

        Assert.Equal(ArtifactType.Other, normalized.Artifacts[0].ArtifactType);
        Assert.Equal(ArtifactType.PluginFile, normalized.Artifacts[1].ArtifactType);
        Assert.Equal(ArtifactType.PreviewImage, normalized.Artifacts[2].ArtifactType);
    }

    [Fact]
    public void EffectiveArtifactType_不保留安装布局的包_图片仍是预览图()
        => Assert.Equal(ArtifactType.PreviewImage,
            InstallLayoutNormalizer.EffectiveArtifactType(ArtifactType.PreviewImage, @"a\b.png", preservesInstallLayout: false));

    [Fact]
    public void ScriptModFolders_按标志文件认出MOD文件夹_用于保护里面的enabled_txt()
    {
        var folders = InstallLayoutNormalizer.ScriptModFolders(
            [@"AMDFidelityOptimizer\Scripts\main.lua", @"AMDFidelityOptimizer\enabled.txt", @"readme.txt"], PbzFixtures.Rules);

        Assert.Equal(["AMDFidelityOptimizer"], folders);
        Assert.True(InstallLayoutNormalizer.IsInsideAny(@"AMDFidelityOptimizer\enabled.txt", folders));
        Assert.False(InstallLayoutNormalizer.IsInsideAny("readme.txt", folders));
        Assert.False(InstallLayoutNormalizer.IsInsideAny("AMDFidelityOptimizer", folders));
    }

    [Fact]
    public void WithoutArtifacts_保留包的身份_不带任何文件()
    {
        var package = PbzFixtures.MakePackage("cfg", PbzFixtures.GpuProfileVariants, PackageKind.Config);

        var empty = InstallLayoutNormalizer.WithoutArtifacts(package);

        Assert.Empty(empty.Artifacts);
        Assert.Equal(package.PackageKey, empty.PackageKey);
        Assert.Equal(PackageKind.Config, empty.Kind);
        Assert.Equal(6, package.Artifacts.Count);
    }

    /// <summary>
    /// 根因的回归测试：真实包（老版本入库的形状）+ 游戏路径记成启动壳目录，
    /// 最终必须落到 <c>…\PBZ\platform\steam\ue4ss\Mods\PBZCompassMap\…</c>。
    /// </summary>
    [Theory]
    [InlineData(@"PBZ\platform\steam\ue4ss\Mods\")]
    [InlineData(@"platform\steam\ue4ss\Mods\")]
    [InlineData(@"steam\ue4ss\Mods\")]
    public void 真实包结构_按插件根部署_落到游戏读取的UE4SS目录(string legacyPrefix)
    {
        var pluginRoot = PluginRootResolver.Resolve(
            PbzFixtures.LauncherDir, PbzFixtures.Layout, _ => true).PluginRoot;
        var package = InstallLayoutNormalizer.NormalizePackage(
            PbzFixtures.MakePackage("PBZ_AIO_1.8.2", PbzFixtures.CompassMapUnder(legacyPrefix)), PbzFixtures.Rules);
        var modPath = PbzFixtures.InstallRoot + @"\ProjectPBZ\Content\Paks\~mods";

        var targets = package.Artifacts
            .Select(a => DeploymentTargetPathBuilder.ComputeTargetPath(a, package, null, modPath, pluginRoot))
            .ToList();

        Assert.Contains(@"D:\Games\PBZ\platform\steam\ue4ss\Mods\PBZCompassMap\Scripts\main.lua", targets);
        Assert.Contains(@"D:\Games\PBZ\platform\steam\ue4ss\Mods\PBZCompassMap\enabled.txt", targets);
        Assert.All(targets, t => Assert.StartsWith(@"D:\Games\PBZ\platform\steam\ue4ss\Mods\PBZCompassMap\", t));
    }
}

public class InstallPlacementAnalyzerTests
{
    [Fact]
    public void 多套互斥的配置方案_判为多选一_并列出各方案()
    {
        var package = PbzFixtures.MakePackage("1. Choose Your GPU Profile", PbzFixtures.GpuProfileVariants, PackageKind.Config);

        var issue = InstallPlacementAnalyzer.Analyze(package, PbzFixtures.Rules);

        Assert.NotNull(issue);
        Assert.Equal(PlacementIssueKind.ChoiceVariants, issue.Kind);
        Assert.Equal(3, issue.Variants.Count);
        Assert.StartsWith("Tier 1", issue.Variants[0]);
    }

    [Fact]
    public void 只有一份配置_不是多选一()
    {
        var package = PbzFixtures.MakePackage("cfg", [@"Config\Engine.ini", @"Config\Scalability.ini"], PackageKind.Config);

        Assert.Null(InstallPlacementAnalyzer.Analyze(package, PbzFixtures.Rules));
    }

    [Fact]
    public void 只导入了Scripts文件夹里的零散lua_判为缺MOD文件夹()
    {
        // 诊断包里玩家的尝试之一：把 Scripts 文件夹单独拖进来，十个 lua 被平铺到了游戏路径下。
        var package = PbzFixtures.MakePackage("Scripts", ["main.lua", "bossplaces.lua", "lang_zh.lua"]);

        var issue = InstallPlacementAnalyzer.Analyze(package, PbzFixtures.Rules);

        Assert.Equal(PlacementIssueKind.LooseLoaderScripts, issue?.Kind);
    }

    [Fact]
    public void 归位后的脚本MOD_没有问题()
    {
        var package = InstallLayoutNormalizer.NormalizePackage(
            PbzFixtures.MakePackage("amd", PbzFixtures.LooseScriptMod), PbzFixtures.Rules);

        Assert.Null(InstallPlacementAnalyzer.Analyze(package, PbzFixtures.Rules));
    }

    [Fact]
    public void 不含脚本的插件_没有问题()
        => Assert.Null(InstallPlacementAnalyzer.Analyze(
            PbzFixtures.MakePackage("proxy", ["dwmapi.dll", @"ue4ss\UE4SS.dll"]), PbzFixtures.Rules));

    [Fact]
    public void 玩家指定了安装子目录_不判()
        => Assert.Null(InstallPlacementAnalyzer.Analyze(
            PbzFixtures.MakePackage("Scripts", ["main.lua"], targetRootPath: @"ue4ss\Mods\X\Scripts"), PbzFixtures.Rules));

    [Fact]
    public void MOD包与没有规则的引擎_不判()
    {
        Assert.Null(InstallPlacementAnalyzer.Analyze(
            PbzFixtures.MakePackage("pak", ["x_P.pak"], PackageKind.Mod), PbzFixtures.Rules));
        Assert.Null(InstallPlacementAnalyzer.Analyze(
            PbzFixtures.MakePackage("Scripts", ["main.lua"]), InstallLayoutRules.Empty));
    }
}

public class DeploymentDriftDetectorTests
{
    private static OwnedTarget T(string path, string key) => new(path, key);

    [Fact]
    public void 老版本装在启动壳目录下的文件_判为装错位置()
    {
        // 诊断包最后一次部署：文件落在 PBZ\steam\platform\steam\ue4ss\…，而现在该在 PBZ\platform\steam\ue4ss\…
        var managed = PbzFixtures.CompassMapUnder(PbzFixtures.LauncherDir + @"\platform\steam\ue4ss\Mods\").Select(p => T(p, "PBZ"));
        var expected = PbzFixtures.CompassMapUnder(PbzFixtures.BinaryDir + @"\ue4ss\Mods\").Select(p => T(p, "PBZ"));

        var drift = DeploymentDriftDetector.Detect(managed, expected);

        Assert.True(drift.HasDrift);
        Assert.Equal(PbzFixtures.CompassMapFiles.Length, drift.Misplaced.Count);
        Assert.Equal(PbzFixtures.CompassMapFiles.Length, drift.Missing.Count);
        Assert.Equal(["PBZ"], drift.PackageKeys);
    }

    [Fact]
    public void 已装位置与应装位置一致_没有问题()
    {
        var targets = PbzFixtures.CompassMapUnder(PbzFixtures.BinaryDir + @"\ue4ss\Mods\").Select(p => T(p, "PBZ")).ToList();

        Assert.False(DeploymentDriftDetector.Detect(targets, targets).HasDrift);
    }

    [Fact]
    public void 路径比较不分大小写()
        => Assert.False(DeploymentDriftDetector.Detect(
            [T(@"D:\Games\PBZ\Platform\Steam\dwmapi.dll", "ue4ss")], [T(@"d:\games\pbz\platform\steam\DWMAPI.DLL", "UE4SS")]).HasDrift);

    [Fact]
    public void 玩家停用了MOD但还没重新部署_不算装错位置()
        => Assert.False(DeploymentDriftDetector.Detect([T(@"D:\g\a.dll", "A")], []).HasDrift);

    [Fact]
    public void 启用了但还没部署_不算装错位置()
        => Assert.False(DeploymentDriftDetector.Detect([], [T(@"D:\g\a.dll", "A")]).HasDrift);

    [Fact]
    public void 只报装错位置的那个包_别的包不受牵连()
    {
        var drift = DeploymentDriftDetector.Detect(
            [T(@"D:\g\steam\a.lua", "A"), T(@"D:\g\mods\b.pak", "B"), T(@"D:\g\old\c.dll", "C")],
            [T(@"D:\g\platform\steam\a.lua", "A"), T(@"D:\g\mods\b.pak", "B")]);

        Assert.Equal(["A"], drift.PackageKeys);
        Assert.Equal([@"D:\g\steam\a.lua"], drift.Misplaced);
        Assert.Equal([@"D:\g\platform\steam\a.lua"], drift.Missing);
    }

    [Fact]
    public void 空输入不抛()
        => Assert.False(DeploymentDriftDetector.Detect(null, null).HasDrift);
}

public class InstallLayoutRulesTests
{
    [Fact]
    public void AnchorNames_合并引擎锚点_本体目录首段_加载器目录()
    {
        var names = PbzFixtures.Rules.AnchorNames(["natives"]);

        Assert.Contains("platform", names);
        Assert.Contains("UE4SS", names);
        Assert.Contains("natives", names);
        Assert.DoesNotContain("steam", names);
    }
}
