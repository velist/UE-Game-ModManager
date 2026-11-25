using System;

namespace UEModManager.Services.Promo;

/// <summary>
/// 推广链接的放行规则：<b>只允许 https</b>。
///
/// <para>
/// 链接是原样交给系统去打开的（Shell 执行），而 Shell 执行什么都肯开：本地程序、
/// <c>file:</c>、各种自定义协议。所以放行条件写得很死，且按<b>原字符串</b>检查——
/// 检查的和最后打开的必须是同一串字符，不能先规范化再检查。
/// </para>
/// </summary>
public static class PromoLink
{
    private const string RequiredPrefix = "https://";

    /// <summary>这个链接能不能交给系统浏览器打开。</summary>
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        // 空白、控制字符、引号都不该出现在一个正常的网址里；出现了就不是我们写的那条链接
        foreach (var ch in url)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '"') return false;
        }

        if (!url.StartsWith(RequiredPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrEmpty(uri.Host);
    }
}
