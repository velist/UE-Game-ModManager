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

    [Fact]
    public void 解析线上真实响应_带data包装层()
    {
        // 2026-10-23 从 api.modmanger.com/app/update 实抓的原样响应。
        // 以前的用例按扁平格式手写，解析器从没见过真实格式，更新提示因此从未生效过。
        var info = UpdateCheck.Parse("{\"code\":200,\"data\":{\"latest\":\"2.1.1\",\"mandatory\":false,\"notes\":\"\"}}");

        Assert.NotNull(info);
        Assert.Equal("2.1.1", info!.Value.Latest);
    }

    [Theory]
    [InlineData("{\"code\":200,\"data\":null}")]
    [InlineData("{\"code\":200,\"data\":{}}")]
    [InlineData("{\"code\":200,\"data\":\"2.1.2\"}")]
    public void data包装层里缺版本号返回null(string json) => Assert.Null(UpdateCheck.Parse(json));

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
