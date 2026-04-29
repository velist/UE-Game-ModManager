using System;
using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

public class PreviewImageSelectorTests
{
    [Fact]
    public void Select_PreviewPrefix_PreferredOverOtherImages()
    {
        var files = new[]
        {
            "/tmp/random.jpg",
            "/tmp/preview.png",
            "/tmp/screenshot.png",
        };

        var pick = PreviewImageSelector.Select(files);

        Assert.Equal("/tmp/preview.png", pick);
    }

    [Fact]
    public void Select_PreviewPrefix_CaseInsensitive()
    {
        var files = new[] { "/tmp/PREVIEW.JPG" };
        Assert.Equal("/tmp/PREVIEW.JPG", PreviewImageSelector.Select(files));
    }

    [Fact]
    public void Select_PreviewWithSuffix_StillWins()
    {
        var files = new[]
        {
            "/tmp/foo.png",
            "/tmp/preview-large.jpg",
        };

        Assert.Equal("/tmp/preview-large.jpg", PreviewImageSelector.Select(files));
    }

    [Fact]
    public void Select_NoPreviewPrefix_FallsBackToAnyImage()
    {
        var files = new[]
        {
            "/tmp/readme.txt",
            "/tmp/screenshot.png",
            "/tmp/foo.dll",
        };

        Assert.Equal("/tmp/screenshot.png", PreviewImageSelector.Select(files));
    }

    [Fact]
    public void Select_NoImage_ReturnsNull()
    {
        var files = new[]
        {
            "/tmp/readme.txt",
            "/tmp/foo.dll",
            "/tmp/bar.pak",
        };

        Assert.Null(PreviewImageSelector.Select(files));
    }

    [Fact]
    public void Select_Empty_ReturnsNull()
    {
        Assert.Null(PreviewImageSelector.Select(Array.Empty<string>()));
    }

    [Fact]
    public void Select_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PreviewImageSelector.Select(null!));
    }

    [Theory]
    [InlineData("/tmp/preview.jpg")]
    [InlineData("/tmp/preview.jpeg")]
    [InlineData("/tmp/preview.png")]
    [InlineData("/tmp/preview.bmp")]
    [InlineData("/tmp/preview.gif")]
    [InlineData("/tmp/preview.webp")]
    public void Select_PreviewPrefixWithVariousImageExtensions(string path)
    {
        Assert.Equal(path, PreviewImageSelector.Select(new[] { path }));
    }

    [Fact]
    public void Select_FirstImageInListOrder_WhenNoPreviewPrefix()
    {
        var files = new[]
        {
            "/tmp/zzz.png",   // 排序靠后但在列表中第一个
            "/tmp/aaa.jpg",
        };

        Assert.Equal("/tmp/zzz.png", PreviewImageSelector.Select(files));
    }
}
