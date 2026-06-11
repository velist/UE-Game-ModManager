using UEModManager.Services.Security;

namespace UEModManager.Core.Tests.Services.Security;

public class PathSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Content/Paks/~mods", "Content|Paks|~mods")]
    [InlineData("/Content/Paks", "Content|Paks")]
    [InlineData("\\Content\\Paks", "Content|Paks")]
    [InlineData("Content/./Paks", "Content|Paks")]
    public void SanitizeRelative_NormalizesSafeRelativePaths(string? input, string expectedParts)
    {
        var expected = expectedParts.Replace('|', Path.DirectorySeparatorChar);

        var result = PathSanitizer.SanitizeRelative(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("..\\foo")]
    [InlineData("../foo")]
    [InlineData("foo\\..\\..\\bar")]
    [InlineData("C:\\evil")]
    [InlineData("C:evil")]
    [InlineData("\\\\server\\share")]
    [InlineData("//server/share")]
    public void SanitizeRelative_RejectsTraversalAndAbsolutePaths(string input)
    {
        Assert.Throws<ArgumentException>(() => PathSanitizer.SanitizeRelative(input));
    }

    [Fact]
    public void SafeCombine_ReturnsPathInsideBaseDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "uemodmanager-base");

        var result = PathSanitizer.SafeCombine(baseDir, "Content/Paks");

        Assert.Equal(Path.Combine(baseDir, "Content", "Paks"), result);
    }

    [Fact]
    public void SafeCombine_RejectsPathOutsideBaseDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "uemodmanager-base");

        Assert.Throws<ArgumentException>(() => PathSanitizer.SafeCombine(baseDir, "..\\outside"));
    }
}
