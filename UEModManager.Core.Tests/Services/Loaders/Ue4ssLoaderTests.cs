using UEModManager.Models;
using UEModManager.Services.InstallLayout;
using UEModManager.Services.Loaders;

namespace UEModManager.Core.Tests.Services.Loaders;

/// <summary>
/// 夹具照 2026-11-25 实际下载的三份官方发布包（v4.0.0 / v3.0.1 / v2.5.2 Xinput）的条目清单，
/// 发布 JSON 照 api.github.com 返回的 v4.0.0 一条裁剪而来。
/// </summary>
public class Ue4ssLoaderTests
{
    /// <summary>UE4SS_v4.0.0.zip（节选，结构完整）。</summary>
    internal static readonly string[] V4Entries =
    [
        "ue4ss/", "dwmapi.dll", "ue4ss/Mods/", "ue4ss/LICENSE", "ue4ss/UE4SS-settings.ini", "ue4ss/UE4SS.dll",
        "ue4ss/Mods/BPModLoaderMod/", "ue4ss/Mods/mods.txt", "ue4ss/Mods/BPModLoaderMod/Scripts/main.lua",
        "ue4ss/Mods/Keybinds/Scripts/main.lua", "ue4ss/Mods/shared/UEHelpers/UEHelpers.lua",
        "ue4ss/UE4SS_SDK_Backends/UE4SS.json",
    ];

    /// <summary>UE4SS_v3.0.1.zip：平铺结构。</summary>
    internal static readonly string[] V3Entries =
    [
        "Mods/", "Changelog.md", "dwmapi.dll", "README.md", "UE4SS-settings.ini", "UE4SS.dll",
        "Mods/mods.txt", "Mods/BPModLoaderMod/Scripts/main.lua", "Mods/shared/UEHelpers/UEHelpers.lua",
    ];

    /// <summary>UE4SS_Xinput_v2.5.2.zip：没有 UE4SS.dll，xinput1_3.dll 自己就是本体。</summary>
    internal static readonly string[] V2XinputEntries =
    [
        "Changelog.md", "Mods/", "Mods/mods.txt", "Mods/Keybinds/Scripts/main.lua", "Readme.md",
        "UE4SS-settings.ini", "xinput1_3.dll",
    ];

    internal static readonly InstallLayoutRules UnrealRules = new()
    {
        LoaderDirectories = [Ue4ssLoader.LoaderDirectoryName],
        ScriptModsDirectory = Ue4ssLoader.SubfolderModsDirectory,
        ScriptModsDirectoryAliases = [Ue4ssLoader.SubfolderModsDirectory, Ue4ssLoader.FlatModsDirectory],
        LoaderCoreFileNames = [Ue4ssLoader.CoreFileName],
        LoaderSettingsFileNames = [Ue4ssLoader.SettingsFileName],
        ScriptModEnableMarker = Ue4ssLoader.EnableMarkerFileName,
        ScriptModListFileNames = ["mods.txt", "mods.json"],
        ScriptModMarkers = ["Scripts/main.lua", "dlls/main.dll"],
        LoaderScriptExtensions = [".lua"],
    };

    private static Ue4ssDetection Detect(params string[] files) => Ue4ssLoader.Detect(files);

    // ─── 检测 ───

    [Fact]
    public void 空目录_未安装_脚本MOD目录按现行结构()
    {
        var d = Detect();

        Assert.Equal(Ue4ssInstallState.NotInstalled, d.State);
        Assert.Equal(Ue4ssLayout.Subfolder, d.Layout);
        Assert.Equal(@"ue4ss\Mods", d.ScriptModsDirectory);
        Assert.Null(d.ProxyFileName);
    }

    [Fact]
    public void 只有管理器装的脚本MOD_仍是未安装()
    {
        // ue4ss\Mods\X 是管理器自己部署脚本 MOD 时建出来的，不能据此说「装了一半」
        var d = Detect(@"ue4ss\Mods\MyMod\Scripts\main.lua", @"ue4ss\Mods\MyMod\enabled.txt", "Game-Win64-Shipping.exe");

        Assert.Equal(Ue4ssInstallState.NotInstalled, d.State);
        Assert.Equal(@"ue4ss\Mods", d.ScriptModsDirectory);
    }

    [Fact]
    public void 官方v4包_已安装_现行结构()
    {
        var d = Detect(V4Entries.Where(e => !e.EndsWith('/')).ToArray());

        Assert.Equal(Ue4ssInstallState.Installed, d.State);
        Assert.Equal(Ue4ssLayout.Subfolder, d.Layout);
        Assert.Equal(@"ue4ss\Mods", d.ScriptModsDirectory);
        Assert.Equal("dwmapi.dll", d.ProxyFileName);
    }

    [Fact]
    public void 官方v3包_已安装_老式结构_MOD在exe旁的Mods()
    {
        var d = Detect(V3Entries.Where(e => !e.EndsWith('/')).ToArray());

        Assert.Equal(Ue4ssInstallState.Installed, d.State);
        Assert.Equal(Ue4ssLayout.Flat, d.Layout);
        Assert.Equal("Mods", d.ScriptModsDirectory);
    }

    [Fact]
    public void v2的Xinput版_没有UE4SSdll也算已安装()
    {
        var d = Detect(V2XinputEntries.Where(e => !e.EndsWith('/')).ToArray());

        Assert.Equal(Ue4ssInstallState.Installed, d.State);
        Assert.Equal(Ue4ssLayout.Flat, d.Layout);
        Assert.Equal("Mods", d.ScriptModsDirectory);
        Assert.Equal("xinput1_3.dll", d.ProxyFileName);
    }

    [Theory]
    [InlineData("xinput1_3.dll")]
    [InlineData("version.dll")]
    [InlineData("d3d11.dll")]
    [InlineData("WINMM.DLL")]
    public void 代理DLL改了名_照样认作已安装(string proxy)
    {
        var d = Detect(proxy, "UE4SS.dll", "UE4SS-settings.ini", @"Mods\mods.txt");

        Assert.Equal(Ue4ssInstallState.Installed, d.State);
        Assert.Equal(proxy, d.ProxyFileName, ignoreCase: true);
    }

    [Fact]
    public void 两种结构都在_生效的是现行结构()
    {
        // 代理 DLL 先找 ue4ss\UE4SS.dll（proxy_generator/main.cpp load_ue4ss_dll）
        var d = Detect("dwmapi.dll", "UE4SS.dll", @"Mods\mods.txt", @"ue4ss\UE4SS.dll", @"ue4ss\Mods\mods.txt");

        Assert.Equal(Ue4ssLayout.Subfolder, d.Layout);
        Assert.Equal(@"ue4ss\Mods", d.ScriptModsDirectory);
    }

    [Fact]
    public void 现行结构但没有ue4ss的Mods而exe旁有Mods_UE4SS会退回去读后者()
    {
        // UE4SSProgram.cpp setup_mod_directory_path 的 legacy 回退
        var d = Detect("dwmapi.dll", @"ue4ss\UE4SS.dll", @"ue4ss\UE4SS-settings.ini", @"Mods\Old\Scripts\main.lua");

        Assert.Equal(Ue4ssInstallState.Installed, d.State);
        Assert.Equal(Ue4ssLayout.Subfolder, d.Layout);
        Assert.Equal("Mods", d.ScriptModsDirectory);
    }

    [Fact]
    public void 有本体没代理DLL_残缺_缺代理()
    {
        var d = Detect(@"ue4ss\UE4SS.dll", @"ue4ss\UE4SS-settings.ini");

        Assert.Equal(Ue4ssInstallState.Incomplete, d.State);
        Assert.Equal(Ue4ssProblem.MissingProxy, d.Problem);
        Assert.Equal(@"ue4ss\Mods", d.ScriptModsDirectory);
    }

    [Theory]
    [InlineData(new[] { "dwmapi.dll", @"ue4ss\UE4SS-settings.ini", @"ue4ss\Mods\mods.txt" }, Ue4ssLayout.Subfolder, @"ue4ss\Mods")]
    [InlineData(new[] { "dwmapi.dll", "UE4SS-settings.ini", @"Mods\mods.txt" }, Ue4ssLayout.Flat, "Mods")]
    [InlineData(new[] { "dwmapi.dll" }, Ue4ssLayout.Subfolder, @"ue4ss\Mods")]
    public void 有代理或设置文件但本体不在_残缺_缺本体(string[] files, Ue4ssLayout layout, string mods)
    {
        var d = Detect(files);

        Assert.Equal(Ue4ssInstallState.Incomplete, d.State);
        Assert.Equal(Ue4ssProblem.MissingCore, d.Problem);
        Assert.Equal(layout, d.Layout);
        Assert.Equal(mods, d.ScriptModsDirectory);
    }

    [Fact]
    public void 代理DLL叫不常见的名字_按内容认出来后算已安装()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UE4SS.dll", "UE4SS-settings.ini", "bink2w64.dll" };

        // 只看名字认不出来
        Assert.Equal(Ue4ssProblem.MissingProxy, Ue4ssLoader.Detect(files.Contains, _ => false).Problem);

        var d = Ue4ssLoader.Detect(files.Contains, _ => false, ["bink2w64.dll"]);
        Assert.Equal(Ue4ssInstallState.Installed, d.State);
        Assert.Equal("bink2w64.dll", d.ProxyFileName);
    }

    [Fact]
    public void LooksLikeProxy_认的是代理里的宽字符文件名()
    {
        byte[] proxy = [1, 2, 3, .. System.Text.Encoding.Unicode.GetBytes(@"ue4ss\UE4SS.dll"), 0, 0, 9];

        Assert.True(Ue4ssLoader.LooksLikeProxy(proxy));
        Assert.False(Ue4ssLoader.LooksLikeProxy(System.Text.Encoding.ASCII.GetBytes("UE4SS.dll")));
        Assert.False(Ue4ssLoader.LooksLikeProxy(System.Text.Encoding.Unicode.GetBytes("dinput8.dll ReShade")));
        Assert.False(Ue4ssLoader.LooksLikeProxy([]));
    }

    [Fact]
    public void 别的工具的代理DLL单独存在_不算UE4SS()
    {
        // 光有 dinput8.dll / version.dll 说明不了什么（ReShade、其它加载器都用），不能报成残缺
        Assert.Equal(Ue4ssInstallState.NotInstalled, Detect("dinput8.dll", "version.dll").State);
    }

    [Fact]
    public void override指到别处_按已安装算_不打扰玩家()
    {
        var d = Detect("dwmapi.dll", "override.txt");

        Assert.Equal(Ue4ssInstallState.Installed, d.State);
    }

    [Fact]
    public void Detect_空参数_抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => Ue4ssLoader.Detect((IEnumerable<string>)null!));
        Assert.Throws<ArgumentNullException>(() => Ue4ssLoader.Detect(null!, _ => false));
        Assert.Throws<ArgumentNullException>(() => Ue4ssLoader.Detect(_ => false, null!));
    }

    // ─── 套进归位规则 ───

    [Fact]
    public void ApplyTo_老式结构_脚本MOD目录换成Mods_其余不变()
    {
        var rules = Ue4ssLoader.ApplyTo(UnrealRules, Detect("dwmapi.dll", "UE4SS.dll"));

        Assert.Equal("Mods", rules.ScriptModsDirectory);
        Assert.Equal(UnrealRules.LoaderDirectories, rules.LoaderDirectories);
        Assert.Equal(["Mods", @"ue4ss\Mods"], rules.AllScriptModsDirectories);
    }

    [Fact]
    public void ApplyTo_未安装或现行结构_规则原样返回()
    {
        Assert.Same(UnrealRules, Ue4ssLoader.ApplyTo(UnrealRules, Detect()));
        Assert.Same(UnrealRules, Ue4ssLoader.ApplyTo(UnrealRules, Detect("dwmapi.dll", @"ue4ss\UE4SS.dll", @"ue4ss\Mods\mods.txt")));
    }

    [Fact]
    public void ApplyTo_没有UE4SS约定的引擎_不动()
    {
        Assert.Same(InstallLayoutRules.Empty, Ue4ssLoader.ApplyTo(InstallLayoutRules.Empty, Detect("dwmapi.dll", "UE4SS.dll")));
    }

    // ─── 哪些包算脚本 MOD ───

    [Fact]
    public void ScriptModNames_认两种目录下的脚本MOD文件夹()
    {
        Assert.Equal(["A"], Ue4ssLoader.ScriptModNames([@"ue4ss\Mods\A\Scripts\main.lua", @"ue4ss\Mods\A\enabled.txt"], UnrealRules));
        Assert.Equal(["B"], Ue4ssLoader.ScriptModNames([@"Mods\B\dlls\main.dll"], UnrealRules));
    }

    [Fact]
    public void ScriptModNames_pak类MOD与配置_不算()
    {
        Assert.Empty(Ue4ssLoader.ScriptModNames([@"Content\Paks\~mods\x.pak", "Engine.ini", @"Mods\readme.txt"], UnrealRules));
    }

    [Fact]
    public void ScriptModNames_UE4SS本体自带的示例MOD_不算()
    {
        Assert.Empty(Ue4ssLoader.ScriptModNames(V4Entries.Where(e => !e.EndsWith('/')), UnrealRules));
        Assert.Empty(Ue4ssLoader.ScriptModNames(V3Entries.Where(e => !e.EndsWith('/')), UnrealRules));
    }

    // ─── 发布解析 ───

    private const string V4ReleaseJson = """
        {
          "tag_name": "v4.0.0", "name": "v4.0.0", "draft": false, "prerelease": false,
          "assets": [
            { "name": "zDEV-UE4SS_v4.0.0.zip", "size": 39134774,
              "digest": "sha256:02401c207b8fecfa0f38e8bf4df1d83ee87d7e190af2f02ca5d4bc7cd7c2ef8c",
              "url": "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/737650301",
              "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/zDEV-UE4SS_v4.0.0.zip" },
            { "name": "UE4SS_v4.0.0.zip", "size": 8758673,
              "digest": "sha256:251AD0D1381A241750E036F5918CBF2B5D7EC58EBC608CB2DD09D459D0C81E45",
              "url": "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/737650298",
              "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/UE4SS_v4.0.0.zip" },
            { "name": "zCustomGameConfigs.zip", "size": 423798,
              "url": "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/737650297",
              "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/zCustomGameConfigs.zip" }
          ]
        }
        """;

    [Fact]
    public void ParseLatestRelease_只取普通包_不取zDEV()
    {
        var release = Ue4ssLoader.ParseLatestRelease(V4ReleaseJson);

        Assert.NotNull(release);
        Assert.Equal("v4.0.0", release!.Tag);
        Assert.Equal("v4.0.0", release.DisplayVersion);
        Assert.Equal("UE4SS_v4.0.0.zip", release.AssetName);
        Assert.Equal(8758673, release.Size);
        Assert.Equal("251ad0d1381a241750e036f5918cbf2b5d7ec58ebc608cb2dd09d459d0c81e45", release.Sha256);
        Assert.Equal("https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/737650298", release.AssetApiUrl);
        Assert.EndsWith("/v4.0.0/UE4SS_v4.0.0.zip", release.BrowserDownloadUrl);
    }

    [Fact]
    public void ParseLatestRelease_列表里跳过预发布与实验版()
    {
        var json = """
            [
              { "tag_name": "experimental-latest", "draft": false, "prerelease": true,
                "assets": [ { "name": "UE4SS_v4.0.0-15-gb5daa4cc.zip", "size": 1,
                  "url": "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/1",
                  "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/experimental-latest/UE4SS_v4.0.0-15-gb5daa4cc.zip" } ] },
              { "tag_name": "v9.9.9", "draft": true, "prerelease": false,
                "assets": [ { "name": "UE4SS_v9.9.9.zip", "size": 1,
                  "url": "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/2",
                  "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v9.9.9/UE4SS_v9.9.9.zip" } ] },
              { "tag_name": "v3.0.1", "draft": false, "prerelease": false,
                "assets": [ { "name": "UE4SS_v3.0.1.zip", "size": 5523402,
                  "url": "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/3",
                  "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/UE4SS_v3.0.1.zip" } ] }
            ]
            """;

        var release = Ue4ssLoader.ParseLatestRelease(json);

        Assert.Equal("v3.0.1", release!.Tag);
        Assert.Null(release.Sha256);
    }

    [Theory]
    [InlineData("UE4SS_v4.0.0.zip", true)]
    [InlineData("ue4ss_v10.2.33.zip", true)]
    [InlineData("zDEV-UE4SS_v4.0.0.zip", false)]
    [InlineData("UE4SS_v4.0.0-15-gb5daa4cc.zip", false)]
    [InlineData("UE4SS_Xinput_v2.5.2.zip", false)]
    [InlineData("zCustomGameConfigs.zip", false)]
    [InlineData("UE4SS_v4.0.0.zip.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStandardAssetName_只认正式版普通包(string? name, bool expected)
        => Assert.Equal(expected, Ue4ssLoader.IsStandardAssetName(name));

    [Fact]
    public void ParseLatestRelease_下载地址被引到别处_不采用()
    {
        var json = V4ReleaseJson
            .Replace("https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/737650298", "https://evil.example/a.zip")
            .Replace("https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/UE4SS_v4.0.0.zip", "https://github.com/someone-else/RE-UE4SS/releases/download/v4.0.0/UE4SS_v4.0.0.zip");

        Assert.Null(Ue4ssLoader.ParseLatestRelease(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    public void ParseLatestRelease_坏数据_返回null(string? json)
        => Assert.Null(Ue4ssLoader.ParseLatestRelease(json));

    [Theory]
    [InlineData("https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/assets/1", true)]
    [InlineData("https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/UE4SS_v4.0.0.zip", true)]
    [InlineData("http://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/UE4SS_v4.0.0.zip", false)]
    [InlineData("https://github.com.evil.example/UE4SS-RE/RE-UE4SS/x.zip", false)]
    [InlineData("https://github.com/UE4SS-RE/RE-UE4SS-fork/x.zip", false)]
    [InlineData("https://api.github.com/repos/praydog/REFramework-nightly/releases/assets/1", false)]
    [InlineData(null, false)]
    public void IsOfficialUrl_只认官方仓库的https地址(string? url, bool expected)
        => Assert.Equal(expected, Ue4ssLoader.IsOfficialUrl(url));

    // ─── 压缩包校验 ───

    [Fact]
    public void ValidateArchiveEntries_三份官方包都通过()
    {
        Assert.Null(Ue4ssLoader.ValidateArchiveEntries(V4Entries));
        Assert.Null(Ue4ssLoader.ValidateArchiveEntries(V3Entries));
        Assert.Null(Ue4ssLoader.ValidateArchiveEntries(V2XinputEntries));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("ue4ss/../../evil.dll")]
    [InlineData("/abs/evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"ue4ss\..\..\evil.dll")]
    public void ValidateArchiveEntries_越界路径_拒收(string evil)
        => Assert.NotNull(Ue4ssLoader.ValidateArchiveEntries([.. V4Entries, evil]));

    [Fact]
    public void ValidateArchiveEntries_缺本体或缺代理_拒收()
    {
        Assert.NotNull(Ue4ssLoader.ValidateArchiveEntries(["dwmapi.dll", "ue4ss/UE4SS-settings.ini"]));
        Assert.NotNull(Ue4ssLoader.ValidateArchiveEntries(["ue4ss/UE4SS.dll", "ue4ss/UE4SS-settings.ini"]));
        Assert.NotNull(Ue4ssLoader.ValidateArchiveEntries(["readme.txt"]));
        Assert.NotNull(Ue4ssLoader.ValidateArchiveEntries([]));
        Assert.Throws<ArgumentNullException>(() => Ue4ssLoader.ValidateArchiveEntries(null!));
    }

    // ─── 脚本 MOD 的落位跟着结构走（InstallLayoutNormalizer）───

    private static readonly InstallLayoutRules FlatRules = UnrealRules with { ScriptModsDirectory = Ue4ssLoader.FlatModsDirectory };

    [Fact]
    public void 老式结构_按现行结构打包的脚本MOD_挪进Mods()
    {
        var result = InstallLayoutNormalizer.Normalize(
            [@"ue4ss\Mods\MyMod\Scripts\main.lua", @"ue4ss\Mods\MyMod\enabled.txt", @"ue4ss\Mods\shared\lib.lua"], FlatRules);

        Assert.Equal([@"Mods\MyMod\Scripts\main.lua", @"Mods\MyMod\enabled.txt", @"Mods\shared\lib.lua"], result);
    }

    [Fact]
    public void 老式结构_散装脚本MOD文件夹_装进Mods()
    {
        var result = InstallLayoutNormalizer.Normalize([@"MyMod\Scripts\main.lua", @"MyMod\data.txt"], FlatRules);

        Assert.Equal([@"Mods\MyMod\Scripts\main.lua", @"Mods\MyMod\data.txt"], result);
    }

    [Fact]
    public void 现行结构_按老式结构打包的脚本MOD_连同shared一起挪进ue4ss的Mods()
    {
        var result = InstallLayoutNormalizer.Normalize(
            [@"Mods\MyMod\Scripts\main.lua", @"Mods\shared\lib.lua", "dwmapi.ini"], UnrealRules);

        Assert.Equal([@"ue4ss\Mods\MyMod\Scripts\main.lua", @"ue4ss\Mods\shared\lib.lua", "dwmapi.ini"], result);
    }

    [Fact]
    public void 现行结构_没有脚本MOD的Mods文件夹_不动()
    {
        // Mods 这个名字太常见：里面没有脚本 MOD 文件夹就不是 UE4SS 的 Mods
        var paths = new[] { @"Mods\SomePak\x.pak", @"Mods\readme.txt" };

        Assert.Equal(paths, InstallLayoutNormalizer.Normalize(paths, UnrealRules));
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void UE4SS本体的包_两种规则下都原样保留(string[] entries)
    {
        var files = entries.Where(e => !e.EndsWith('/')).Select(e => e.Replace('/', '\\')).ToArray();

        Assert.Equal(files, InstallLayoutNormalizer.Normalize(files, UnrealRules));
        Assert.Equal(files, InstallLayoutNormalizer.Normalize(files, FlatRules));
        Assert.True(InstallLayoutNormalizer.IsLoaderRuntime(files, UnrealRules));
    }

    public static TheoryData<string[]> RuntimePackages() => new() { V4Entries, V3Entries, V2XinputEntries };

    [Fact]
    public void 归位是幂等的_并且两种结构之间可以来回换()
    {
        string[] modern = [@"ue4ss\Mods\MyMod\Scripts\main.lua", @"ue4ss\Mods\MyMod\enabled.txt"];

        var flat = InstallLayoutNormalizer.Normalize(modern, FlatRules);
        Assert.Equal(flat, InstallLayoutNormalizer.Normalize(flat, FlatRules));
        Assert.Equal(modern, InstallLayoutNormalizer.Normalize(flat, UnrealRules));
    }

    [Fact]
    public void 只带脚本的普通包_不算加载器本体()
    {
        Assert.False(InstallLayoutNormalizer.IsLoaderRuntime([@"ue4ss\Mods\A\Scripts\main.lua", @"ue4ss\Mods\A\dlls\main.dll"], UnrealRules));
        // 设置文件单独出现（给 UE4SS 换配置的包）也不算
        Assert.False(InstallLayoutNormalizer.IsLoaderRuntime([@"ue4ss\UE4SS-settings.ini"], UnrealRules));
        Assert.False(InstallLayoutNormalizer.IsLoaderRuntime(["x.dll"], InstallLayoutRules.Empty));
    }

    [Fact]
    public void 自带老式UE4SS的包_里面的脚本不报成零散脚本()
    {
        var package = new Package
        {
            PackageKey = "bundle", DisplayName = "bundle", Kind = PackageKind.Plugin, PreservesInstallLayout = true,
            Artifacts = V3Entries.Where(e => !e.EndsWith('/'))
                .Select(e => new PackageArtifact { RelativeTargetPath = e.Replace('/', '\\'), ArtifactType = ArtifactType.PluginFile })
                .ToList(),
        };

        Assert.Null(InstallPlacementAnalyzer.Analyze(package, UnrealRules));
    }

    // ─── enabled.txt ───

    [Fact]
    public void 缺启用标记的脚本MOD_补一个enabled()
    {
        var markers = InstallLayoutNormalizer.MissingEnableMarkers(
            [@"ue4ss\Mods\A\Scripts\main.lua", @"ue4ss\Mods\A\Scripts\util.lua", @"ue4ss\Mods\B\dlls\main.dll"], UnrealRules);

        Assert.Equal([@"ue4ss\Mods\A\enabled.txt", @"ue4ss\Mods\B\enabled.txt"], markers);
    }

    [Fact]
    public void 老式结构_标记补在Mods下()
    {
        Assert.Equal([@"Mods\A\enabled.txt"],
            InstallLayoutNormalizer.MissingEnableMarkers([@"Mods\A\Scripts\main.lua"], FlatRules));
    }

    [Fact]
    public void 已经带了enabled的_不重复补()
    {
        Assert.Empty(InstallLayoutNormalizer.MissingEnableMarkers(
            [@"ue4ss\Mods\A\Scripts\main.lua", @"ue4ss\Mods\A\ENABLED.TXT"], UnrealRules));
    }

    [Fact]
    public void 包里自带mods列表的_启用哪些由作者定_不补()
    {
        Assert.Empty(InstallLayoutNormalizer.MissingEnableMarkers(
            [@"ue4ss\Mods\A\Scripts\main.lua", @"ue4ss\Mods\mods.txt"], UnrealRules));
        Assert.Empty(InstallLayoutNormalizer.MissingEnableMarkers(
            [@"ue4ss\Mods\A\Scripts\main.lua", @"ue4ss\Mods\mods.json"], UnrealRules));
    }

    [Fact]
    public void UE4SS本体自带的示例MOD_不补_开不开由它的mods列表定()
    {
        var files = V4Entries.Where(e => !e.EndsWith('/')).Where(e => !e.EndsWith("mods.txt")).ToList();

        Assert.Empty(InstallLayoutNormalizer.MissingEnableMarkers(files, UnrealRules));
    }

    [Fact]
    public void 不在脚本MOD目录下的_以及没有这类约定的引擎_不补()
    {
        Assert.Empty(InstallLayoutNormalizer.MissingEnableMarkers([@"Somewhere\A\Scripts\main.lua"], UnrealRules));
        Assert.Empty(InstallLayoutNormalizer.MissingEnableMarkers([@"ue4ss\Mods\shared\lib.lua"], UnrealRules));
        Assert.Empty(InstallLayoutNormalizer.MissingEnableMarkers([@"ue4ss\Mods\A\Scripts\main.lua"], InstallLayoutRules.Empty));
        Assert.Throws<ArgumentNullException>(() => InstallLayoutNormalizer.MissingEnableMarkers(null!, UnrealRules));
    }
}
