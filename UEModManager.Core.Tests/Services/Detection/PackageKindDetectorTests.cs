using System.Linq;
using UEModManager.Models;
using UEModManager.Services.Detection;

namespace UEModManager.Core.Tests.Services.Detection;

public class PackageKindDetectorTests
{
    // ─── DetectByExtension ───

    [Theory]
    [InlineData("file.pak", PackageKind.Mod)]
    [InlineData("file.ucas", PackageKind.Mod)]
    [InlineData("file.utoc", PackageKind.Mod)]
    [InlineData("foo.zip", PackageKind.Mod)]
    [InlineData("UNKNOWN.xyz", PackageKind.Mod)]
    public void DetectByExtension_Mod(string path, PackageKind expected)
        => Assert.Equal(expected, PackageKindDetector.DetectByExtension(path));

    [Theory]
    [InlineData("plug.dll")]
    [InlineData("PLUG.EXE")]
    [InlineData("lib.so")]
    [InlineData("dinput8.asi")]      // ASI 加载器插件
    [InlineData("main.LUA")]         // UE4SS / REFramework 脚本
    public void DetectByExtension_Plugin(string path)
        => Assert.Equal(PackageKind.Plugin, PackageKindDetector.DetectByExtension(path));

    [Theory]
    [InlineData("conf.ini")]
    [InlineData("conf.cfg")]
    [InlineData("conf.toml")]
    [InlineData("conf.yaml")]
    [InlineData("conf.yml")]
    public void DetectByExtension_Config(string path)
        => Assert.Equal(PackageKind.Config, PackageKindDetector.DetectByExtension(path));

    [Fact]
    public void DetectByExtension_NormalJson_IsConfig()
        => Assert.Equal(PackageKind.Config, PackageKindDetector.DetectByExtension("settings.json"));

    [Fact]
    public void DetectByExtension_CnsJson_IsMod()
        => Assert.Equal(PackageKind.Mod, PackageKindDetector.DetectByExtension("dekcnsdata.json"));

    [Fact]
    public void DetectByExtension_CnsKeywordJson_IsMod()
        => Assert.Equal(PackageKind.Mod, PackageKindDetector.DetectByExtension("my_cns_pack.json"));

    // ─── AggregateFromFiles ───

    [Fact]
    public void Aggregate_Empty_ReturnsModDefault()
        => Assert.Equal(PackageKind.Mod, PackageKindDetector.AggregateFromFiles(Array.Empty<string>()));

    [Fact]
    public void Aggregate_AllSame_ReturnsThatKind()
    {
        var result = PackageKindDetector.AggregateFromFiles(new[] { "a.dll", "b.dll" });
        Assert.Equal(PackageKind.Plugin, result);
    }

    [Fact]
    public void Aggregate_ModAndPlugin_PrefersMod()
    {
        var result = PackageKindDetector.AggregateFromFiles(new[] { "a.dll", "b.pak" });
        Assert.Equal(PackageKind.Mod, result);
    }

    [Fact]
    public void Aggregate_ConfigAndPlugin_PrefersPlugin()
    {
        var result = PackageKindDetector.AggregateFromFiles(new[] { "a.dll", "b.ini" });
        Assert.Equal(PackageKind.Plugin, result);
    }

    [Fact]
    public void Aggregate_OnlyConfigs_ReturnsConfig()
    {
        var result = PackageKindDetector.AggregateFromFiles(new[] { "a.ini", "b.toml" });
        Assert.Equal(PackageKind.Config, result);
    }

    [Fact]
    public void Aggregate_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => PackageKindDetector.AggregateFromFiles(null!));

    // ─── IsModJsonFile ───

    [Theory]
    [InlineData("dekcnsmod.json", true)]
    [InlineData("my_cns_data.json", true)]
    [InlineData("CNS_PROFILE.json", true)]
    [InlineData("settings.json", false)]
    [InlineData("config.json", false)]
    public void IsModJsonFile_DetectsCnsKeywords(string path, bool expected)
        => Assert.Equal(expected, PackageKindDetector.IsModJsonFile(path));

    // ─── KindToArtifactType ───

    [Theory]
    [InlineData(PackageKind.Mod, ArtifactType.ModFile)]
    [InlineData(PackageKind.Plugin, ArtifactType.PluginFile)]
    [InlineData(PackageKind.Config, ArtifactType.ConfigFile)]
    public void KindToArtifactType_KnownKinds(PackageKind kind, ArtifactType expected)
        => Assert.Equal(expected, PackageKindDetector.KindToArtifactType(kind));

    // ─── ClassifyPayload：非 MOD 载荷是插件还是配置 ───

    [Fact]
    public void ClassifyPayload_纯Lua脚本是插件而不是配置()
    {
        // UE4SS 脚本 MOD 通常只有 Scripts/main.lua + enabled.txt，没有 DLL。
        // 以前"没 DLL 就算配置"，玩家看到的类型是"配置"。
        var kind = PackageKindDetector.ClassifyPayload(new[] { "ue4ss/Mods/MyMod/Scripts/main.lua", "ue4ss/Mods/MyMod/enabled.txt" });
        Assert.Equal(PackageKind.Plugin, kind);
    }

    [Fact]
    public void ClassifyPayload_含DLL是插件()
        => Assert.Equal(PackageKind.Plugin, PackageKindDetector.ClassifyPayload(new[] { "dwmapi.dll", "UE4SS-settings.ini" }));

    [Fact]
    public void ClassifyPayload_只有配置文件是配置()
        => Assert.Equal(PackageKind.Config, PackageKindDetector.ClassifyPayload(new[] { "Engine.ini", "notes.txt" }));

    [Fact]
    public void ClassifyPayload_拒绝null()
        => Assert.Throws<System.ArgumentNullException>(() => PackageKindDetector.ClassifyPayload(null!));

    // ─── PackageKindText：三处界面共用的叫法 ───

    [Theory]
    [InlineData(PackageKind.Mod, "MOD", "MOD 详情")]
    [InlineData(PackageKind.Plugin, "插件", "插件详情")]
    [InlineData(PackageKind.Config, "配置", "配置详情")]
    public void PackageKindText_标签与标题(PackageKind kind, string label, string title)
    {
        Assert.Equal(label, PackageKindText.Label(kind));
        Assert.Equal(title, PackageKindText.DetailTitle(kind));
        Assert.False(string.IsNullOrWhiteSpace(PackageKindText.Explain(kind)));
    }

    [Fact]
    public void PackageKindText_三类解释互不相同()
    {
        var all = new[] { PackageKind.Mod, PackageKind.Plugin, PackageKind.Config }.Select(PackageKindText.Explain).ToList();
        Assert.Equal(3, all.Distinct().Count());
    }
}
