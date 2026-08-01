using System;
using System.Security.Cryptography;
using System.Text;

namespace UEModManager.Services.Telemetry;

/// <summary>
/// 用量统计里两个标识符的计算：设备标识与账号标识。纯函数，不碰 IO。
///
/// <para><b>整体原则：能不出本机的就不出本机。</b></para>
/// 这里要回答的只有"有多少台机器""有多少个人"，两者都只需要一个**能去重但不能反查**的
/// 字符串。所以设备标识是随机数、账号标识是哈希，服务端两样都收得到，却两样都不认识。
/// </summary>
public static class TelemetryIdentity
{
    /// <summary>
    /// 邮箱哈希的固定盐。
    ///
    /// <para>
    /// 它<b>不是</b>密钥——盐就编译在客户端里，任何人反编译都能拿到，
    /// 因此它挡不住"拿着一份邮箱名单来比对"的定向攻击。它挡的是另外两件更现实的事：
    /// <list type="number">
    /// <item>通用彩虹表。裸 SHA-256 的常见邮箱早就被算过了，加盐让那些现成的表全部失效。</item>
    /// <item>跨库关联。别处泄露的"邮箱→SHA256"表拿到我们的库里对不上号，
    /// 我们这张表也没法反过来去别人的库里认人。</item>
    /// </list>
    /// 要真正做到不可反查得上 HMAC + 服务端密钥，但那样客户端就得先把明文邮箱发给服务端，
    /// 与"明文邮箱不出本机"直接冲突。在"服务端从不接触明文"和"哈希绝对不可反查"之间，
    /// 前者是更实在的保护。
    /// </para>
    ///
    /// <para><b>改这个常量会让全部历史注册数清零</b>（旧哈希与新哈希对不上，同一个人被计成两个）。</para>
    /// </summary>
    private const string EmailSalt = "UEModManager.usage.v1";

    /// <summary>
    /// 账号标识的长度（十六进制字符数）。
    /// 128 bit：按生日界，百万量级的账号发生一次碰撞的概率在 1e-27 数量级，
    /// 远低于"服务器掉一次数据"的概率，够用；同时把 payload 压掉一半。
    /// </summary>
    public const int AccountHashLength = 32;

    /// <summary>
    /// 把邮箱算成上报用的账号标识。<b>明文邮箱到此为止，绝不出本机。</b>
    ///
    /// <para>
    /// 规范化用 <c>Trim().ToLowerInvariant()</c>，与登录链路
    /// （<c>CustomOtpService</c> / <c>LocalAuthService</c>）完全一致。这不是可选项：
    /// 登录侧把 <c>Foo@x.com</c> 和 <c>foo@x.com</c> 当同一个账号，统计侧要是不这么做，
    /// 同一个人换个大小写登录就会被计成两个注册用户，而这种失真事后完全无法察觉。
    /// </para>
    /// </summary>
    /// <returns>32 位小写十六进制；邮箱为空白时返回 <c>null</c>（没有可统计的账号）。</returns>
    public static string? HashEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var normalized = email.Trim().ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(EmailSalt + "\n" + normalized);
        var digest = SHA256.HashData(bytes);

        var sb = new StringBuilder(AccountHashLength);
        // 每字节两位十六进制，取前 AccountHashLength/2 字节。
        for (var i = 0; i < AccountHashLength / 2; i++) sb.Append(digest[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// 账号标识的格式校验。
    ///
    /// <para>
    /// 大小写都收（服务端那条正则也带 <c>/i</c>，收下后同样归一为小写）。
    /// 本程序只会产出小写，收大小写是为了"宽进严出"：万一有一环把值大写化了，
    /// 结果应该是照常计数，而不是这台机器从此在看板上消失。
    /// </para>
    /// </summary>
    public static bool IsValidAccountHash(string? hash)
        => hash is { Length: AccountHashLength } && IsHex(hash);

    /// <summary>
    /// 生成一个新的设备标识。
    ///
    /// <para>
    /// <b>刻意是随机数，不是机器码。</b>MachineGuid / MAC / 硬盘序列号 / CPU ID 与特定设备
    /// 强绑定、可跨应用关联、重装系统后依然稳定，实践中通常被认定为个人信息——用了它们，
    /// 这套统计的性质就从"基础计数"滑向"设备追踪"。<c>LocalAuthService</c> 里确实有一处
    /// <c>MachineName_UserName_OSVersion</c> 的机器指纹拼接，那是本地加密用途，
    /// <b>绝不要</b>复用到上报路径上。
    /// </para>
    ///
    /// <para>
    /// 代价要说清楚：重装系统或换机会产生新 ID，累计设备数因此偏高。这是隐私换准确度的
    /// 自觉取舍，可以接受——这个数字本来就是趋势参考而不是精确统计。
    /// </para>
    /// </summary>
    public static string NewDeviceId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    /// <summary>
    /// 校验并归一设备标识。<b>必须是 UUID v4</b>（第三段首位为 4、第四段首位为 8/9/a/b）——
    /// 服务端用同一条正则，不合法的一律静默丢弃。
    ///
    /// <para>
    /// 收得这么紧是为了让"设备文件被手改过 / 被半截写坏"这种情况在客户端就现形并重新生成，
    /// 而不是把一个服务端注定要丢弃的值一路发出去，最后表现为"这台机器怎么从来不计数"。
    /// </para>
    /// </summary>
    public static bool TryNormalizeDeviceId(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        // Guid.TryParseExact 的 "D" 只认 8-4-4-4-12，不接受带花括号或省略连字符的写法。
        if (!Guid.TryParseExact(trimmed, "D", out var guid)) return false;

        var text = guid.ToString("D").ToLowerInvariant();
        // 版本位与变体位：Guid.NewGuid() 恒为 v4，非 v4 说明这个值不是本程序生成的。
        if (text[14] != '4') return false;
        if (text[19] is not ('8' or '9' or 'a' or 'b')) return false;

        normalized = text;
        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')) continue;
            return false;
        }
        return true;
    }
}
