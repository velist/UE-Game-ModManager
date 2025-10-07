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
    /// 仅使用 Cloudflare Workers 的 OTP 服务：
    /// - 发送验证码/魔法链接：走 Workers（Workers 持有 Brevo/Supabase Service Key）
    /// - 验证验证码：走 Supabase /auth/v1/verify（仅需 anon key，不是敏感信息）
    /// </summary>
    public class WorkerOtpService
    {
        private readonly ILogger<WorkerOtpService> _logger;
        private readonly HttpClient _http;
        private readonly CloudConfig _cloud;

        public WorkerOtpService(ILogger<WorkerOtpService> logger, HttpClient httpClient, CloudConfig cloudConfig)
        {
            _logger = logger;
            _http = httpClient;
            _cloud = cloudConfig;
            if (_http.BaseAddress == null)
            {
                _http.BaseAddress = new Uri(_cloud.ApiBaseUrl.TrimEnd('/'));
            }
        }

                        public async Task<(bool Success, string Message, int? RetryAfterSeconds)> SendOtpAsync(string email, string type = "email")
        {
            try
            {
                // 1) 首选：Workers 下发 OTP（服务端持有 service key + Brevo）
                var payload = JsonSerializer.Serialize(new { email, type = "email" });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(_cloud.RequestTimeoutSeconds));
                var resp = await _http.PostAsync("/auth/otp/send", content, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);

                int? retry = null;
                if (resp.Headers.TryGetValues("Retry-After", out var values))
                {
                    if (int.TryParse(System.Linq.Enumerable.FirstOrDefault(values), out var sec)) retry = sec;
                }

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[WorkerOTP] OTP 发送成功（Workers）");
                    return (true, "验证码已发送，请查收邮箱", retry);
                }

                // 429 频率限制：直接告知前端等待（若无 Retry-After，默认 60s）
                if ((int)resp.StatusCode == 429)
                {
                    var msg429 = TryExtractMessage(text) ?? "发送过于频繁，请稍后重试";
                    if (retry == null || retry <= 0) retry = 60;
                    _logger.LogWarning($"[WorkerOTP] 频率限制：{msg429}，等待 {retry}s");
                    return (false, msg429, retry);
                }

                // 2) 5xx 或 generate_link 失败：本地回退调用 Supabase OTP（anon）
                if ((int)resp.StatusCode >= 500 || text.Contains("generate_link_failed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var supaUrl = (UEModManager.Services.SupabaseConfig.SupabaseUrl ?? string.Empty).TrimEnd('/') + "/auth/v1/otp";
                        var anon = UEModManager.Services.SupabaseConfig.SupabaseKey;
                        using var req = new HttpRequestMessage(HttpMethod.Post, supaUrl);
                        req.Headers.Accept.Clear();
                        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        req.Headers.TryAddWithoutValidation("apikey", anon);
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", anon);
                        req.Content = new StringContent(JsonSerializer.Serialize(new { email, type = "email", create_user = true }), Encoding.UTF8, "application/json");
                        using var ctsOtp = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(_cloud.RequestTimeoutSeconds));
                        var respOtp = await new HttpClient().SendAsync(req, ctsOtp.Token);
                        var textOtp = await respOtp.Content.ReadAsStringAsync(ctsOtp.Token);

                        if (respOtp.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("[WorkerOTP] 本地回退：Supabase OTP 发送成功（anon）");
                            return (true, "验证码已发送，请查收邮箱", null);
                        }
                        else if ((int)respOtp.StatusCode == 429)
                        {
                            var retry2 = 60; // Supabase 通常不返回 Retry-After，这里设定默认60s
                            var msg2 = TryExtractMessage(textOtp) ?? "发送过于频繁，请稍后重试";
                            _logger.LogWarning($"[WorkerOTP] 本地回退 Supabase OTP 频率限制：{msg2}，等待 {retry2}s");
                            return (false, msg2, retry2);
                        }
                        else
                        {
                            var msg2 = TryExtractMessage(textOtp) ?? $"HTTP {(int)respOtp.StatusCode}";
                            _logger.LogWarning($"[WorkerOTP] 本地回退 Supabase OTP 失败: {(int)respOtp.StatusCode} - {msg2}");
                            return (false, msg2, null);
                        }
                    }
                    catch (Exception supaEx)
                    {
                        _logger.LogWarning(supaEx, "[WorkerOTP] 本地回退 Supabase OTP 异常");
                        // 继续走下方通用失败返回
                    }
                }

                // 其它错误：透传错误与重试建议
                var msg = TryExtractMessage(text) ?? $"HTTP {(int)resp.StatusCode}";
                _logger.LogWarning($"[WorkerOTP] 发送失败: {(int)resp.StatusCode} - {msg}");
                return (false, msg, retry);
            }
            catch (TaskCanceledException)
            {
                return (false, "请求超时，请稍后重试", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WorkerOTP] 发送异常");
                return (false, ex.Message, null);
            }
        }public async Task<(bool Success, string Message)> VerifyOtpAsync(string email, string otp)
        {
            try
            {
                var url = (SupabaseConfig.SupabaseUrl ?? string.Empty).TrimEnd('/') + "/auth/v1/verify";
                if (string.IsNullOrWhiteSpace(url))
                    return (false, "未配置 Supabase URL");

                var anon = SupabaseConfig.SupabaseKey;
                if (string.IsNullOrWhiteSpace(anon))
                    return (false, "未配置 Supabase Anon Key");

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.TryAddWithoutValidation("apikey", anon);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", anon);
                req.Content = new StringContent(JsonSerializer.Serialize(new { type = "email", email, token = otp }), Encoding.UTF8, "application/json");

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(_cloud.RequestTimeoutSeconds));
                var resp = await new HttpClient().SendAsync(req, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[WorkerOTP] 验证成功");
                    return (true, "验证成功");
                }
                else
                {
                    var msg = TryExtractMessage(text) ?? $"HTTP {(int)resp.StatusCode}";
                    return (false, msg);
                }
            }
            catch (TaskCanceledException)
            {
                return (false, "验证超时，请重试");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WorkerOTP] 验证异常");
                return (false, ex.Message);
            }
        }

        private static string? TryExtractMessage(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
                if (doc.RootElement.TryGetProperty("msg", out var m2)) return m2.GetString();
                return null;
            }
            catch { return null; }
        }
    }
}



