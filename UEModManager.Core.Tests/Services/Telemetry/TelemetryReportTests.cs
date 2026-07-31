using System.Text.Json;
using UEModManager.Services.Telemetry;

namespace UEModManager.Core.Tests.Services.Telemetry;

/// <summary>
/// 上报体的构造。
///
/// <para>
/// 这里钉两类东西：一是<b>隐私承诺</b>——发出去的字段只能是那四个，多一个都不行；
/// 二是<b>与服务端的契约</b>——字段名、长度上限、体积上限、UUID 格式，任何一处两边对不上，
/// 表现都是"客户端日志显示成功、看板上数字不涨"，是最难查的那种故障。
/// </para>
/// </summary>
public class TelemetryReportTests
{
    private const string ValidDeviceId = "3f2a1b4c-5d6e-4f70-8912-abcdef012345";
    private const string ValidAccountHash = "9f86d081884c7d659a2feaa0c55ad015";

    private static TelemetryReport Create(
        string? deviceId = ValidDeviceId,
        string? version = "2.0.5",
        string? os = "10.0.26200",
        string? accountHash = null)
    {
        Assert.True(TelemetryReport.TryCreate(deviceId, version, os, accountHash, out var report));
        return report;
    }

    [Fact]
    public void 心跳只发设备标识_版本_系统版本三个字段()
    {
        var json = JsonDocument.Parse(Create().ToJson()).RootElement;

        Assert.Equal(3, json.EnumerateObject().Count());
        Assert.Equal(ValidDeviceId, json.GetProperty("d").GetString());
        Assert.Equal("2.0.5", json.GetProperty("v").GetString());
        Assert.Equal("10.0.26200", json.GetProperty("o").GetString());
    }

    [Fact]
    public void 带账号时第四个字段是哈希_而不是明文邮箱()
    {
        var json = JsonDocument.Parse(Create(accountHash: ValidAccountHash).ToJson()).RootElement;

        Assert.Equal(4, json.EnumerateObject().Count());
        Assert.Equal(ValidAccountHash, json.GetProperty("a").GetString());
    }

    /// <summary>
    /// 反向锁：把整个请求体拆开，确认字段名只能是那四个、且每个值都恰好等于我们放进去的东西。
    /// 将来有人往 <see cref="TelemetryReport"/> 上加字段时，这条最容易先响。
    ///
    /// <para>
    /// 用"字段名白名单 + 值逐一比对"而不是"搜关键词"：机器名/用户名可能短到只有一两个字符
    /// （本机 <c>Environment.UserName</c> 就是单字母），在十六进制哈希里必然误命中，
    /// 那样的断言会变成一条随机失败的噪声。
    /// </para>
    /// </summary>
    [Fact]
    public void 请求体的字段是封闭集合_没有第五个字段()
    {
        var accountHash = TelemetryIdentity.HashEmail("zhangsan@qq.com")!;
        var report = Create(accountHash: accountHash);
        var json = JsonDocument.Parse(report.ToJson()).RootElement;

        var names = json.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "a", "d", "o", "v" }, names);

        Assert.Equal(ValidDeviceId, json.GetProperty("d").GetString());
        Assert.Equal("2.0.5", json.GetProperty("v").GetString());
        Assert.Equal("10.0.26200", json.GetProperty("o").GetString());
        Assert.Equal(accountHash, json.GetProperty("a").GetString());
    }

    /// <summary>明文邮箱到客户端为止：请求体里连一个 <c>@</c> 都不该出现。</summary>
    [Fact]
    public void 请求体里没有明文邮箱()
    {
        var json = Create(accountHash: TelemetryIdentity.HashEmail("zhangsan@qq.com")).ToJson();

        Assert.DoesNotContain("@", json);
        Assert.DoesNotContain("zhangsan", json);
        Assert.DoesNotContain("qq.com", json);
    }

    /// <summary>不含任何路径：设备标识与版本号里都不可能出现盘符或分隔符。</summary>
    [Fact]
    public void 请求体里没有本机路径()
    {
        var json = Create().ToJson();

        Assert.DoesNotContain(":\\", json);
        Assert.DoesNotContain("/", json);
    }

    [Fact]
    public void 未登录时不带账号字段_而不是发空字符串()
    {
        var json = Create(accountHash: null).ToJson();

        Assert.DoesNotContain("\"a\"", json);
    }

    /// <summary>不合法的账号哈希被当作"没有账号"处理，绝不原样发出去。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("player@example.com")]
    public void 不合法的账号标识被丢弃而不是原样发出(string accountHash)
    {
        var json = Create(accountHash: accountHash).ToJson();

        Assert.DoesNotContain("\"a\"", json);
        Assert.DoesNotContain("player", json);
    }

    // ── 与服务端的契约 ──

    [Fact]
    public void 没有设备标识就构造不出上报()
    {
        Assert.False(TelemetryReport.TryCreate("garbage", "2.0.5", "10.0", null, out _));
        Assert.False(TelemetryReport.TryCreate(null, "2.0.5", "10.0", null, out _));
    }

    [Fact]
    public void 满载的请求体也不超过服务端的体积上限()
    {
        var report = Create(
            version: new string('9', 100),
            os: new string('8', 100),
            accountHash: ValidAccountHash);

        Assert.True(report.ByteCount <= TelemetryReport.MaxBodyBytes,
            $"满载 {report.ByteCount} 字节，超过服务端上限 {TelemetryReport.MaxBodyBytes}");
    }

    [Fact]
    public void 版本与系统版本按服务端同样的上限截断()
    {
        var report = Create(version: new string('9', 100), os: new string('8', 100));

        Assert.Equal(TelemetryReport.MaxVersionLength, report.AppVersion.Length);
        Assert.Equal(TelemetryReport.MaxOsLength, report.OsVersion.Length);
    }

    [Fact]
    public void 非ASCII被剥离_定制系统的版本号不会把看板搞成乱码()
    {
        Assert.Equal("2.05", Create(version: "2.0中5").AppVersion);
        Assert.Equal("100", Create(os: "1\t0\n0").OsVersion);
    }

    /// <summary>
    /// 引号和反斜杠必须剔掉，否则手写的 <c>ToJson</c> 会拼出非法 JSON。
    /// 而服务端对解析失败的处理是"静默丢弃 + 返回与成功完全相同的响应"——
    /// 客户端会看到一条成功日志，看板上却永远少这台机器。
    /// </summary>
    [Fact]
    public void 引号与反斜杠被剔掉_拼出来的仍是合法JSON()
    {
        var report = Create(version: "2\".0\\5", os: "w\"in\\");
        var json = report.ToJson();

        var parsed = JsonDocument.Parse(json).RootElement;      // 非法 JSON 会在这里抛
        Assert.Equal("2.05", parsed.GetProperty("v").GetString());
        Assert.Equal("win", parsed.GetProperty("o").GetString());
    }

    [Fact]
    public void 设备标识大小写归一后再发_服务端不会把同一台机器计成两台()
    {
        Assert.True(TelemetryReport.TryCreate(
            ValidDeviceId.ToUpperInvariant(), "2.0.5", "10.0", ValidAccountHash.ToUpperInvariant(), out var report));

        Assert.Equal(ValidDeviceId, report.DeviceId);
        Assert.Equal(ValidAccountHash, report.AccountHash);
    }

    [Fact]
    public void 版本与系统版本缺失时发空串_而不是null导致请求体非法()
    {
        var json = JsonDocument.Parse(Create(version: null, os: null).ToJson()).RootElement;

        Assert.Equal("", json.GetProperty("v").GetString());
        Assert.Equal("", json.GetProperty("o").GetString());
    }
}
