using System;
using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

public class ImportFileKindClassifierTests
{
    private static readonly string[] UeDirectImport = [".pak", ".utoc", ".ucas"];

    [Theory]
    [InlineData("foo.zip")]
    [InlineData("foo.rar")]
    [InlineData("foo.7z")]
    [InlineData("FOO.ZIP")]
    public void Classify_Compressed_Wins(string path)
    {
        var kind = ImportFileKindClassifier.Classify(path, UeDirectImport);
        Assert.Equal(ImportFileKind.Compressed, kind);
    }

    [Theory]
    [InlineData("MyMod.pak")]
    [InlineData("MyMod.utoc")]
    [InlineData("MyMod.UCAS")] // 大小写不敏感
    public void Classify_DirectImport_Wins(string path)
    {
        var kind = ImportFileKindClassifier.Classify(path, UeDirectImport);
        Assert.Equal(ImportFileKind.DirectImport, kind);
    }

    [Theory]
    [InlineData("loader.dll")]
    [InlineData("plugin.exe")]
    [InlineData("Engine.ini")]
    [InlineData("anything.unknown")]
    [InlineData("noext")]
    public void Classify_FallbackToPlugin(string path)
    {
        var kind = ImportFileKindClassifier.Classify(path, UeDirectImport);
        Assert.Equal(ImportFileKind.Plugin, kind);
    }

    [Fact]
    public void Classify_CompressedBeatsDirectImport()
    {
        // .zip 永远是压缩包，即使 directImport 误把 .zip 加进去
        var directImport = new[] { ".zip", ".pak" };
        var kind = ImportFileKindClassifier.Classify("foo.zip", directImport);
        Assert.Equal(ImportFileKind.Compressed, kind);
    }

    [Fact]
    public void Classify_EmptyDirectImport_AllNonCompressedFallToPlugin()
    {
        Assert.Equal(ImportFileKind.Plugin,
            ImportFileKindClassifier.Classify("foo.pak", Array.Empty<string>()));
        Assert.Equal(ImportFileKind.Compressed,
            ImportFileKindClassifier.Classify("foo.zip", Array.Empty<string>()));
    }

    [Fact]
    public void Classify_NullDirectImport_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ImportFileKindClassifier.Classify("foo.pak", null!));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("README")]
    [InlineData("说明与事项.docx")]
    [InlineData("下载地址.url")]
    [InlineData("preview.png")]
    [InlineData("安装教程.mp4")]
    public void Classify_明显不是MOD的文件被跳过(string path)
        => Assert.Equal(ImportFileKind.NotAMod, ImportFileKindClassifier.Classify(path, UeDirectImport));

    [Fact]
    public void Classify_引擎认的格式优先于跳过规则()
        => Assert.Equal(ImportFileKind.DirectImport, ImportFileKindClassifier.Classify("t_hero.png", new[] { ".png" }));
}
