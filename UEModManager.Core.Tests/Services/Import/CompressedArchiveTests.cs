using UEModManager.Services.Import;

namespace UEModManager.Core.Tests.Services.Import;

public class CompressedArchiveTests
{
    [Theory]
    [InlineData("foo.zip")]
    [InlineData("foo.rar")]
    [InlineData("foo.7z")]
    [InlineData("FOO.ZIP")]
    [InlineData("a/b/c.7Z")]
    [InlineData("D:\\path\\with space\\test.rar")]
    public void IsCompressed_KnownExtensions_True(string path)
    {
        Assert.True(CompressedArchive.IsCompressed(path));
    }

    [Theory]
    [InlineData("foo.pak")]
    [InlineData("foo.dll")]
    [InlineData("foo.json")]
    [InlineData("foo.ini")]
    [InlineData("foo.tar")]
    [InlineData("foo.gz")]
    [InlineData("foo.tar.gz")] // 仅看最后一段扩展名
    [InlineData("foo")]        // 无扩展名
    [InlineData("")]
    [InlineData(null)]
    public void IsCompressed_NotCompressed_False(string? path)
    {
        Assert.False(CompressedArchive.IsCompressed(path));
    }

    [Fact]
    public void Extensions_AreLowercaseWithDot()
    {
        foreach (var ext in CompressedArchive.Extensions)
        {
            Assert.StartsWith(".", ext);
            Assert.Equal(ext.ToLowerInvariant(), ext);
        }
    }

    [Fact]
    public void Extensions_HasZipRarSevenZ()
    {
        Assert.Contains(".zip", CompressedArchive.Extensions);
        Assert.Contains(".rar", CompressedArchive.Extensions);
        Assert.Contains(".7z", CompressedArchive.Extensions);
        Assert.Equal(3, CompressedArchive.Extensions.Count);
    }
}
