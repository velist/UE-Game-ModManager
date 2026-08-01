using System;
using System.Text;

namespace UEModManager.Services.Telemetry;

/// <summary>
/// 一次上报的完整内容。<b>字段清单就是隐私承诺的全文</b>——这个类型里有什么，
/// 发出去的就是什么，没有别的地方能往请求体里塞东西。
///
/// <para>
/// 刻意<b>不含</b>：IP（服务端也不落库）、机器名、用户名、安装路径、游戏库、MOD 名称、
/// 明文邮箱。每多一个字段，合规叙事就弱一分，而它们一个都不服务于"注册数"和"在线数"。
/// </para>
/// </summary>
/// <param name="DeviceId">随机 UUID v4，见 <see cref="TelemetryIdentity.NewDeviceId"/>。</param>
/// <param name="AppVersion">应用版本，用于版本分布（三个指标里唯一能直接改变代码决策的那个）。</param>
/// <param name="OsVersion">Windows 版本号，如 <c>10.0.26200</c>。</param>
/// <param name="AccountHash">账号标识；未登录或本次会话已报过时为 <c>null</c>。</param>
public readonly record struct TelemetryReport(
    string DeviceId,
    string AppVersion,
    string OsVersion,
    string? AccountHash)
{
    /// <summary>
    /// 请求体的字节上限。<b>必须与 Worker 侧的 <c>TELEMETRY_MAX_BODY</c> 保持一致</b>：
    /// 服务端超出即静默丢弃，客户端这边先卡一道是为了让"发了但从来不计数"这种最难查的
    /// 故障在本地就变成一条日志。
    /// </summary>
    public const int MaxBodyBytes = 256;

    /// <summary>版本号字段的上限（服务端同样会截断）。</summary>
    public const int MaxVersionLength = 16;

    /// <summary>系统版本字段的上限（服务端同样会截断）。</summary>
    public const int MaxOsLength = 32;

    /// <summary>
    /// 构造一次上报。清洗规则与服务端一一对应：只留可打印 ASCII、按长度截断。
    ///
    /// <para>
    /// 为什么客户端也要做一遍服务端已经会做的事：服务端的截断是防御，客户端的截断是
    /// <b>让本地日志里记下来的东西和实际发出去的东西一致</b>。两边不一致时，排障的人会
    /// 对着一条"上报成功"的日志想不明白看板上为什么是另一个版本号。
    /// </para>
    /// </summary>
    /// <returns>设备标识不合法时返回 <c>false</c>——没有设备标识的上报对服务端毫无意义。</returns>
    public static bool TryCreate(
        string? deviceId,
        string? appVersion,
        string? osVersion,
        string? accountHash,
        out TelemetryReport report)
    {
        report = default;
        if (!TelemetryIdentity.TryNormalizeDeviceId(deviceId, out var id)) return false;

        report = new TelemetryReport(
            id,
            Clip(appVersion, MaxVersionLength),
            Clip(osVersion, MaxOsLength),
            TelemetryIdentity.IsValidAccountHash(accountHash) ? accountHash!.ToLowerInvariant() : null);
        return true;
    }

    /// <summary>
    /// 序列化成请求体。
    ///
    /// <para>
    /// 字段名全是单字母（d/v/o/a），不是为了省流量——是为了让整个请求体在最坏情况下也稳稳
    /// 待在 <see cref="MaxBodyBytes"/> 以内（实测满载约 140 字节）。这个上限是服务端拒绝
    /// 超长 body 的依据，两边必须能同时成立。
    /// </para>
    /// </summary>
    public string ToJson()
    {
        // 手写而不是 JsonSerializer：字段少、全部已清洗成可打印 ASCII，没有转义面；
        // 更重要的是这样"发出去的到底是哪几个字段"在一眼之内可读完，
        // 而序列化器会随对象形状变化而变化——隐私承诺不该依赖一个可以被反射改变的东西。
        var sb = new StringBuilder(MaxBodyBytes);
        sb.Append("{\"d\":\"").Append(DeviceId)
          .Append("\",\"v\":\"").Append(AppVersion)
          .Append("\",\"o\":\"").Append(OsVersion).Append('"');
        if (AccountHash != null) sb.Append(",\"a\":\"").Append(AccountHash).Append('"');
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>请求体的字节数（UTF-8）。全部字段都是 ASCII，所以字节数等于字符数。</summary>
    public int ByteCount => Encoding.UTF8.GetByteCount(ToJson());

    /// <summary>
    /// 只保留可打印 ASCII（再去掉引号与反斜杠）后截断。
    ///
    /// <para>
    /// 版本号与系统版本本来就只会是 <c>2.0.5</c> / <c>10.0.26200</c> 这种形状，这里防的是
    /// 两件事：一是某些定制系统的 <c>OSVersion</c> 里带非 ASCII，直接进看板会变成乱码；
    /// 二是有人手改配置往里塞控制字符或超长串——虽然服务端也会挡，但让脏数据在最靠近
    /// 来源的地方就消失，比一路带到数据库门口再拦要好排查得多。
    /// </para>
    ///
    /// <para>
    /// <b><c>"</c> 和 <c>\</c> 必须一起剔掉</b>，否则 <see cref="ToJson"/> 会拼出一段
    /// 非法 JSON——而服务端对解析失败的处理是"静默丢弃并返回与成功完全相同的响应"，
    /// 于是客户端日志里是一条成功、看板上永远少这台机器，是最难查的那种故障。
    /// 剔掉而不是转义，是因为版本号里出现这两个字符本身就说明数据已经不对了。
    /// </para>
    /// </summary>
    private static string Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(max);
        foreach (var c in value)
        {
            if (c is < ' ' or > '~' or '"' or '\\') continue;
            sb.Append(c);
            if (sb.Length == max) break;
        }
        return sb.ToString();
    }
}
