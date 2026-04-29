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
}
