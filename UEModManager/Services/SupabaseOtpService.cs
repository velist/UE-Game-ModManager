using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// 通过 Supabase GoTrue 提供的 OTP/Magic Link 邮件验证码能力实现免密登录。
    /// 仅使用 Supabase 的 /auth/v1/otp 与 /auth/v1/verify 接口，不依赖本地 SMTP。
    /// </summary>
    public class SupabaseOtpService
    {
        private readonly ILogger<SupabaseOtpService> _logger;
        private readonly HttpClient _http;
        private readonly string _baseAuth;
        private readonly string _apiKey;

        public SupabaseOtpService(ILogger<SupabaseOtpService> logger)
        {
            _logger = logger;
            _http = new HttpClient();
            var url = SupabaseConfig.SupabaseUrl?.TrimEnd('/') ?? string.Empty;
            _baseAuth = string.IsNullOrWhiteSpace(url) ? string.Empty : $"{url}/auth/v1/";
            _apiKey = !string.IsNullOrWhiteSpace(SupabaseConfig.SupabaseServiceKey)
                ? SupabaseConfig.SupabaseServiceKey
                : SupabaseConfig.SupabaseKey;

            if (!string.IsNullOrWhiteSpace(_baseAuth))
            {
                _http.BaseAddress = new Uri(_baseAuth);
            }
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                _http.DefaultRequestHeaders.Remove("apikey");
                _http.DefaultRequestHeaders.TryAddWithoutValidation("apikey", _apiKey);
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<(bool ok, string message)> SendEmailOtpAsync(string email, bool createUser = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_baseAuth) || string.IsNullOrWhiteSpace(_apiKey))
                    return (false, "未配置 Supabase URL 或 API Key");

                var payload = new { email, create_user = createUser };
                var json = JsonSerializer.Serialize(payload);
                var resp = await _http.PostAsync("otp", new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[OTP] 邮件验证码已发送 -> {Email}", email);
                    return (true, "验证码已发送");
                }
                if ((int)resp.StatusCode == 429)
                {
                    var secs = GetRetryAfterSeconds(resp) ?? 60;
                    _logger.LogWarning("[OTP] 发送频率受限，{Seconds}s 后可重试。Body={Body}", secs, body);
                    return (false, $"HTTP 429: rate_limit_seconds={secs}; {body}");
                }
                _logger.LogWarning("[OTP] 发送失败 {Status}: {Body}", resp.StatusCode, body);
                return (false, $"HTTP {(int)resp.StatusCode}: {body}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OTP] 发送异常");
                return (false, ex.Message);
            }
        }

        public async Task<(bool ok, string message)> VerifyEmailOtpAsync(string email, string token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_baseAuth) || string.IsNullOrWhiteSpace(_apiKey))
                    return (false, "未配置 Supabase URL 或 API Key");

                var payload = new { type = "email", email, token };
                var json = JsonSerializer.Serialize(payload);
                var resp = await _http.PostAsync("verify", new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[OTP] 验证成功 -> {Email}", email);
                    return (true, "验证成功");
                }
                _logger.LogWarning("[OTP] 验证失败 {Status}: {Body}", resp.StatusCode, body);
                return (false, $"HTTP {(int)resp.StatusCode}: {body}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OTP] 验证异常");
                return (false, ex.Message);
            }
        }

        private static int? GetRetryAfterSeconds(HttpResponseMessage resp)
        {
            try
            {
                if (resp.Headers.TryGetValues("Retry-After", out var values))
                {
                    var v = System.Linq.Enumerable.FirstOrDefault(values);
                    if (int.TryParse(v, out var secs)) return secs;
                    if (DateTimeOffset.TryParse(v, out var when))
                    {
                        var diff = when - DateTimeOffset.UtcNow;
                        var s = (int)Math.Ceiling(Math.Max(diff.TotalSeconds, 0));
                        return s > 0 ? s : 60;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 发送 Magic Link（邮件一键登录链接）。
        /// 说明：桌面端无法直接接管浏览器回调，建议设置 redirect_to 为你的站点页面。
        /// 用户点击链接后在浏览器完成登录。如需在桌面端同步会话，可改用“验证码登录”。
        /// </summary>
        public async Task<(bool ok, string message)> SendMagicLinkAsync(string email, string? redirectTo = null, bool createUser = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_baseAuth) || string.IsNullOrWhiteSpace(_apiKey))
                    return (false, "未配置 Supabase URL 或 API Key");

                // GoTrue 支持 POST /magiclink
                // 文档中常用字段：email, create_user, (可选) redirect_to
                var payload = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["email"] = email,
                    ["create_user"] = createUser
                };
                if (!string.IsNullOrWhiteSpace(redirectTo))
                {
                    payload["redirect_to"] = redirectTo;
                }

                var json = JsonSerializer.Serialize(payload);
                var resp = await _http.PostAsync("magiclink", new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[MagicLink] 已发送 -> {Email}", email);
                    return (true, "登录链接已发送");
                }
                if ((int)resp.StatusCode == 429)
                {
                    var secs = GetRetryAfterSeconds(resp) ?? 60;
                    _logger.LogWarning("[MagicLink] 发送频率受限，{Seconds}s 后可重试。Body={Body}", secs, body);
                    return (false, $"HTTP 429: rate_limit_seconds={secs}; {body}");
                }
                _logger.LogWarning("[MagicLink] 发送失败 {Status}: {Body}", resp.StatusCode, body);
                return (false, $"HTTP {(int)resp.StatusCode}: {body}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MagicLink] 发送异常");
                return (false, ex.Message);
            }
        }
    }
}
