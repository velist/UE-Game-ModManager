using System.Text.RegularExpressions;

namespace UEModManager.Logging
{
    /// <summary>
    /// 日志脱敏器：在导出诊断包前清理潜在敏感信息。
    /// 纯函数，无 IO。
    ///
    /// 策略保守 — 宁可漏脱敏（保留可读性），不要误脱敏（破坏 grep）。
    /// 每条规则只针对明确格式的敏感模式，不做"看起来像"的猜测。
    /// </summary>
    public static class LogRedactor
    {
        // ─── Regex（按风险从高到低排序，避免相互覆盖）───

        // JWT: 三段以 . 分隔的 base64
        private static readonly Regex JwtRx = new(
            @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
            RegexOptions.Compiled);

        // Bearer / Authorization 头
        private static readonly Regex BearerRx = new(
            @"(?i)\b(Bearer|Authorization)[:\s]+[A-Za-z0-9_\-\.]{16,}",
            RegexOptions.Compiled);

        // API key / token / secret 字段（key=val、key: val、"key":"val" 都支持）
        // (?!\[REDACTED) 防止把已脱敏的 [REDACTED-XXX] 二次替换
        private static readonly Regex KeyValSecretRx = new(
            @"(?i)([""']?(?:api[_-]?key|access[_-]?key|secret|token|password|pwd)[""']?\s*[=:]\s*)([""']?)(?!\[REDACTED)[^""'\s,;}]{6,}",
            RegexOptions.Compiled);

        // 邮箱 — 保留 domain 用于诊断
        private static readonly Regex EmailRx = new(
            @"\b([A-Za-z0-9._%+-]+)@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b",
            RegexOptions.Compiled);

        // ─── 公共 API ───

        /// <summary>
        /// 对单行/多行文本做脱敏处理。返回新字符串。
        /// </summary>
        public static string Redact(string? content)
        {
            if (string.IsNullOrEmpty(content)) return content ?? "";

            var s = content;

            // JWT 完整字符串置换
            s = JwtRx.Replace(s, "[REDACTED-JWT]");

            // Bearer/Authorization 整段置换
            s = BearerRx.Replace(s, m => $"{m.Groups[1].Value}: [REDACTED]");

            // key=val 形式：保留 key=，置换 val
            s = KeyValSecretRx.Replace(s, m => $"{m.Groups[1].Value}{m.Groups[2].Value}[REDACTED]");

            // 邮箱：保留首字符 + domain
            s = EmailRx.Replace(s, m =>
            {
                var local = m.Groups[1].Value;
                var domain = m.Groups[2].Value;
                var firstChar = local.Length > 0 ? local[0].ToString() : "";
                return $"{firstChar}***@{domain}";
            });

            return s;
        }
    }
}
