using UEModManager.Services.Detection;

namespace UEModManager.Core.Tests.Services.Detection;

public class GameNameNormalizerTests
{
    [Theory]
    [InlineData("剑星（CNS）", "剑星(CNS)")]
    [InlineData("黑神话：悟空", "黑神话:悟空")]
    [InlineData("光与影·33号远征队", "光与影33号远征队")]
    [InlineData("（剑星：CNS·v2）", "(剑星:CNSv2)")]
    public void Normalize_FullWidthSymbols_Replaced(string input, string expected)
    {
        Assert.Equal(expected, GameNameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("  Stellar Blade  ", "Stellar Blade")]
    [InlineData("\t剑星\n", "剑星")]
    public void Normalize_TrimsWhitespace(string input, string expected)
    {
        Assert.Equal(expected, GameNameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, GameNameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_NoSymbols_PassesThrough()
    {
        Assert.Equal("PlainGame", GameNameNormalizer.Normalize("PlainGame"));
    }

    [Fact]
    public void Normalize_HalfWidthSymbols_PreservedAsIs()
    {
        Assert.Equal("剑星(CNS):v2", GameNameNormalizer.Normalize("剑星(CNS):v2"));
    }
}
