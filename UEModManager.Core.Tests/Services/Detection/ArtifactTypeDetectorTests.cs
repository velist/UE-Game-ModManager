using UEModManager.Models;
using UEModManager.Services.Detection;

namespace UEModManager.Core.Tests.Services.Detection;

public class ArtifactTypeDetectorTests
{
    private static readonly HashSet<string> UeModExt =
        new(StringComparer.OrdinalIgnoreCase) { ".pak", ".ucas", ".utoc" };

    // ─── DetectForMigration ───

    [Fact]
    public void Migration_PluginContext_AlwaysReturnsPluginFile()
    {
        Assert.Equal(ArtifactType.PluginFile,
            ArtifactTypeDetector.DetectForMigration("anything.pak", isPluginContext: true));
        Assert.Equal(ArtifactType.PluginFile,
            ArtifactTypeDetector.DetectForMigration("README.txt", isPluginContext: true));
    }

    [Theory]
    [InlineData("conf.ini", ArtifactType.ConfigFile)]
    [InlineData("conf.cfg", ArtifactType.ConfigFile)]
    [InlineData("conf.json", ArtifactType.ConfigFile)] // 迁移时 .json 不区分 CNS
    [InlineData("conf.toml", ArtifactType.ConfigFile)]
    [InlineData("conf.yaml", ArtifactType.ConfigFile)]
    [InlineData("conf.yml", ArtifactType.ConfigFile)]
    public void Migration_ConfigExtensions(string path, ArtifactType expected)
        => Assert.Equal(expected, ArtifactTypeDetector.DetectForMigration(path, false));

    [Theory]
    [InlineData("plug.dll")]
    [InlineData("PLUG.EXE")]
    [InlineData("lib.so")]
    public void Migration_PluginExtensions(string path)
        => Assert.Equal(ArtifactType.PluginFile,
            ArtifactTypeDetector.DetectForMigration(path, false));

    [Theory]
    [InlineData("preview.png")]
    [InlineData("img.JPG")]
    [InlineData("anim.gif")]
    [InlineData("modern.webp")]
    public void Migration_ImageExtensions(string path)
        => Assert.Equal(ArtifactType.PreviewImage,
            ArtifactTypeDetector.DetectForMigration(path, false));

    [Theory]
    [InlineData("data.pak")]
    [InlineData("data.unknown")]
    [InlineData("README.txt")]
    public void Migration_UnknownExt_FallsBackToModFile(string path)
        => Assert.Equal(ArtifactType.ModFile,
            ArtifactTypeDetector.DetectForMigration(path, false));

    // ─── DetectForImport ───

    [Fact]
    public void Import_Image_IsPreviewImage()
        => Assert.Equal(ArtifactType.PreviewImage,
            ArtifactTypeDetector.DetectForImport("preview.png", UeModExt));

    [Theory]
    [InlineData("plug.dll")]
    [InlineData("plug.exe")]
    [InlineData("lib.so")]
    public void Import_PluginExtensions(string path)
        => Assert.Equal(ArtifactType.PluginFile,
            ArtifactTypeDetector.DetectForImport(path, UeModExt));

    [Theory]
    [InlineData("conf.ini")]
    [InlineData("conf.cfg")]
    [InlineData("conf.toml")]
    public void Import_ConfigExtensions(string path)
        => Assert.Equal(ArtifactType.ConfigFile,
            ArtifactTypeDetector.DetectForImport(path, UeModExt));

    [Fact]
    public void Import_JsonNotInConfigList_BecomesOther_NotConfig()
    {
        // 注意：导入分支不把 .json 当 Config（与迁移分支不同；导入时 .json 走 PackageKindDetector 路径）
        Assert.Equal(ArtifactType.Other,
            ArtifactTypeDetector.DetectForImport("conf.json", UeModExt));
    }

    [Theory]
    [InlineData("file.pak")]
    [InlineData("FILE.UCAS")]
    [InlineData("file.utoc")]
    public void Import_ModExtensionList_HitsModFile(string path)
        => Assert.Equal(ArtifactType.ModFile,
            ArtifactTypeDetector.DetectForImport(path, UeModExt));

    [Fact]
    public void Import_UnknownExt_IsOther()
        => Assert.Equal(ArtifactType.Other,
            ArtifactTypeDetector.DetectForImport("data.xyz", UeModExt));

    [Fact]
    public void Import_NullModExtensions_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            ArtifactTypeDetector.DetectForImport("a.pak", null!));

    // ─── IsImageExtension ───

    [Theory]
    [InlineData(".png", true)]
    [InlineData(".PNG", true)]
    [InlineData(".webp", true)]
    [InlineData(".pak", false)]
    [InlineData("", false)]
    public void IsImageExtension_AcceptsExtForm(string ext, bool expected)
        => Assert.Equal(expected, ArtifactTypeDetector.IsImageExtension(ext));

    [Theory]
    [InlineData("path/to/img.png", true)]
    [InlineData("path/to/file.dll", false)]
    public void IsImageExtension_AcceptsFullPath(string path, bool expected)
        => Assert.Equal(expected, ArtifactTypeDetector.IsImageExtension(path));
}
