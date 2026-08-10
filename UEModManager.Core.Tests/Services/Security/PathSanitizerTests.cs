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

    [Theory]
    [InlineData("MyMod")]
    [InlineData("剑星 CNS 补丁")]
    [InlineData("mod.v1.2")]
    public void SanitizeSegment_AcceptsPlainNames(string input)
    {
        Assert.Equal(input, PathSanitizer.SanitizeSegment(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("..\\..\\Startup")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("C:evil")]
    [InlineData("bad|name")]
    [InlineData("bad:name")]
    [InlineData("bad?name")]
    [InlineData("bad*name")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT1.log")]
    [InlineData("trailing ")]
    [InlineData("trailing.")]
    public void SanitizeSegment_RejectsAnythingButASingleSafeName(string? input)
    {
        Assert.Throws<ArgumentException>(() => PathSanitizer.SanitizeSegment(input));
    }
}
