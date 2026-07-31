using UEModManager.Services.Telemetry;

namespace UEModManager.Core.Tests.Services.Telemetry;

/// <summary>
/// 账号标识（邮箱哈希）与设备标识的性质。
///
/// <para>
/// 这些用例钉的不是"算法实现对不对"，而是几条**改错了不会报错、只会让看板上的数字
/// 悄悄变得没有意义**的约定：大小写归一必须与登录链路一致、明文不得出现在结果里、
/// 设备标识必须是随机 UUID v4 而不是任何形式的机器指纹。
/// </para>
/// </summary>
public class TelemetryIdentityTests
{
    // ── 账号标识 ──

    [Fact]
    public void 邮箱哈希是32位小写十六进制()
    {
        var hash = TelemetryIdentity.HashEmail("player@example.com");

        Assert.NotNull(hash);
        Assert.Equal(32, hash!.Length);
        Assert.Matches("^[0-9a-f]{32}$", hash);
        Assert.True(TelemetryIdentity.IsValidAccountHash(hash));
    }

    /// <summary>
    /// 登录侧（CustomOtpService / LocalAuthService）把邮箱 <c>Trim().ToLowerInvariant()</c>
    /// 之后再比对，也就是说 <c>Foo@X.com</c> 与 <c>foo@x.com</c> 是同一个账号。
    /// 统计侧要是不这么做，同一个人换个大小写登录就被计成两个注册用户——
    /// 而这种失真在看板上完全看不出来。
    /// </summary>
    [Theory]
    [InlineData("player@example.com")]
    [InlineData("Player@Example.com")]
    [InlineData("PLAYER@EXAMPLE.COM")]
    [InlineData("  player@example.com  ")]
    [InlineData("\tplayer@example.com\r\n")]
    public void 大小写与空白不影响哈希_与登录链路的归一规则一致(string variant)
    {
        var expected = TelemetryIdentity.HashEmail("player@example.com");

        Assert.Equal(expected, TelemetryIdentity.HashEmail(variant));
    }

    [Fact]
    public void 不同邮箱得到不同哈希()
    {
        Assert.NotEqual(
            TelemetryIdentity.HashEmail("a@example.com"),
            TelemetryIdentity.HashEmail("b@example.com"));
    }

    /// <summary>哈希结果里绝不能残留明文的任何片段——这是整套方案对用户的核心承诺。</summary>
    [Fact]
    public void 哈希里不含明文邮箱的任何片段()
    {
        var hash = TelemetryIdentity.HashEmail("zhangsan@qq.com")!;

        Assert.DoesNotContain("zhangsan", hash);
        Assert.DoesNotContain("qq", hash);
        Assert.DoesNotContain("@", hash);
        Assert.DoesNotContain(".", hash);
    }

    /// <summary>
    /// 加盐挡的是通用彩虹表与跨库关联：裸 SHA-256 的常见邮箱早就被算过了。
    /// 这条用例钉住"确实加了盐"，改成裸哈希会立刻失败。
    /// </summary>
    [Fact]
    public void 加了盐_结果不等于裸SHA256前16字节()
    {
        var raw = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("player@example.com"));
        var bare = Convert.ToHexString(raw)[..32].ToLowerInvariant();

        Assert.NotEqual(bare, TelemetryIdentity.HashEmail("player@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空邮箱没有账号可统计_返回null(string? email)
    {
        Assert.Null(TelemetryIdentity.HashEmail(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]                                   // 太短
    [InlineData("9f86d081884c7d659a2feaa0c55ad0155")]     // 33 位
    [InlineData("zf86d081884c7d659a2feaa0c55ad015")]      // 非十六进制
    public void 账号标识格式校验拒绝不合法的值(string? hash)
    {
        Assert.False(TelemetryIdentity.IsValidAccountHash(hash));
    }

    /// <summary>宽进严出：大写十六进制也收（服务端正则同样带 /i），发出去时统一小写。</summary>
    [Fact]
    public void 账号标识大小写都收()
    {
        Assert.True(TelemetryIdentity.IsValidAccountHash("9F86D081884C7D659A2FEAA0C55AD015"));
    }

    // ── 设备标识 ──

    [Fact]
    public void 新设备标识是可被自己校验通过的小写UUID_v4()
    {
        var id = TelemetryIdentity.NewDeviceId();

        Assert.Equal(id.ToLowerInvariant(), id);
        Assert.True(TelemetryIdentity.TryNormalizeDeviceId(id, out var normalized));
        Assert.Equal(id, normalized);
    }

    /// <summary>
    /// 设备标识必须是随机的：连续生成不能重复。这条同时也把"哪天有人想改成机器码"钉死——
    /// 机器指纹在同一台机器上恒定，这条用例会立刻失败。
    /// </summary>
    [Fact]
    public void 设备标识每次生成都不同_不是机器指纹()
    {
        var ids = new HashSet<string>();
        for (var i = 0; i < 200; i++) Assert.True(ids.Add(TelemetryIdentity.NewDeviceId()));
    }

    [Fact]
    public void 大写与前后空白的设备标识被归一为小写()
    {
        var id = TelemetryIdentity.NewDeviceId();

        Assert.True(TelemetryIdentity.TryNormalizeDeviceId($"  {id.ToUpperInvariant()}  ", out var normalized));
        Assert.Equal(id, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("{3f2a1b4c-5d6e-4f70-8912-abcdef012345}")]           // 带花括号，服务端正则不认
    [InlineData("3f2a1b4c5d6e4f708912abcdef012345")]                 // 无连字符，服务端正则不认
    [InlineData("3f2a1b4c-5d6e-1f70-8912-abcdef012345")]             // v1，不是本程序生成的
    [InlineData("3f2a1b4c-5d6e-4f70-c912-abcdef012345")]             // 变体位不对
    [InlineData("00000000-0000-0000-0000-000000000000")]             // Guid.Empty
    public void 不合法的设备标识一律拒绝_与服务端正则同口径(string? raw)
    {
        Assert.False(TelemetryIdentity.TryNormalizeDeviceId(raw, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }
}
