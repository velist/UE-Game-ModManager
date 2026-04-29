using UEModManager.Models;

namespace UEModManager.Core.Tests.Models;

/// <summary>
/// PackageKind 枚举行为基线测试 — 验证测试管道贯通 + Domain 项目可被引用。
/// </summary>
public class PackageKindTests
{
    [Fact]
    public void EnumValues_AreStable()
    {
        Assert.Equal(0, (int)PackageKind.Mod);
        Assert.Equal(1, (int)PackageKind.Plugin);
        Assert.Equal(2, (int)PackageKind.Config);
    }

    [Theory]
    [InlineData(PackageKind.Mod)]
    [InlineData(PackageKind.Plugin)]
    [InlineData(PackageKind.Config)]
    public void RoundTrip_ToStringAndParse(PackageKind kind)
    {
        var s = kind.ToString();
        var parsed = Enum.Parse<PackageKind>(s);

        Assert.Equal(kind, parsed);
    }
}
