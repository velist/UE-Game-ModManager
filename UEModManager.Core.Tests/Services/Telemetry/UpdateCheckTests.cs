using UEModManager.Services.Telemetry;

namespace UEModManager.Core.Tests.Services.Telemetry;

public class UpdateCheckTests
{
    [Fact]
    public void 解析服务端响应()
    {
        var info = UpdateCheck.Parse("{\"ok\":true,\"latest\":\"2.1.2\",\"mandatory\":false,\"notes\":\"修复若干问题\"}");

        Assert.NotNull(info);
        Assert.Equal("2.1.2", info!.Value.Latest);
        Assert.False(info.Value.Mandatory);
        Assert.Equal("修复若干问题", info.Value.Notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"latest\":3}")]
    [InlineData("{\"latest\":\"abc\"}")]
    [InlineData("{}")]
    public void 坏数据返回null(string? json) => Assert.Null(UpdateCheck.Parse(json));

    [Theory]
    [InlineData("2.1.1", "2.1.2", true)]
    [InlineData("2.1.1", "2.2", true)]
    [InlineData("2.1.1", "v3.0.0", true)]
    [InlineData("2.1.1", "2.1.1", false)]
    [InlineData("2.1.1", "2.0.5", false)]
    [InlineData("", "2.1.2", false)]
    [InlineData("2.1.1", "garbage", false)]
    public void 版本比较(string current, string latest, bool expected)
        => Assert.Equal(expected, UpdateCheck.IsNewer(current, latest));
}
