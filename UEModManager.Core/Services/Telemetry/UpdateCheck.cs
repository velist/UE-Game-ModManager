using System;
using System.Text.Json;

namespace UEModManager.Services.Telemetry;

/// <summary>服务端告知的最新版本。</summary>
/// <param name="Latest">最新版本号（如 2.1.2）。</param>
/// <param name="Mandatory">是否必须更新（目前只用于提示措辞）。</param>
/// <param name="Notes">版本说明，可能为空。</param>
public readonly record struct UpdateInfo(string Latest, bool Mandatory, string Notes);

/// <summary>
/// 检查更新的纯逻辑：解析 <c>/app/update</c> 的响应、比较版本号。
///
/// 心跳请求一直带回 <c>{latest, mandatory, notes}</c>，但客户端以前从没读过，
/// 设置项写着"检查更新"，用户却永远收不到新版本提示。
/// </summary>
public static class UpdateCheck
{
    /// <summary>响应体上限：正常响应只有几十字节，超过就当异常输入丢弃。</summary>
    public const int MaxResponseBytes = 4096;

    /// <summary>解析响应；任何格式问题都返回 null（更新提示不值得为坏数据报错）。</summary>
    public static UpdateInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // Worker 的统一响应是 {"code":200,"data":{...}}，版本信息在 data 里；扁平格式也照收
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind != JsonValueKind.Object) return null;
                root = data;
            }
            if (!root.TryGetProperty("latest", out var latest) || latest.ValueKind != JsonValueKind.String) return null;
            var version = latest.GetString()!.Trim();
            if (!TryParseVersion(version, out _)) return null;
            var mandatory = root.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True;
            var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : string.Empty;
            if (notes.Length > 300) notes = notes[..300];
            return new UpdateInfo(version, mandatory, notes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><paramref name="latest"/> 是否比 <paramref name="current"/> 新；任一侧不是合法版本号都返回 false。</summary>
    public static bool IsNewer(string? current, string? latest)
        => TryParseVersion(current, out var c) && TryParseVersion(latest, out var l) && l > c;

    /// <summary>接受 "2.1.1" / "v2.1.1" / "2.1"，统一补成三段比较。</summary>
    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(t, out var v)) return false;
        version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        return true;
    }
}
