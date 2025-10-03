using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// 混合OTP服务：结合Supabase和Brevo的优势
    /// - 使用Supabase存储用户数据和管理认证
    /// - 使用Brevo发送邮件（更可靠的邮件送达率）
    /// - 验证码通过Supabase验证，确保数据一致性
    /// </summary>
    public class HybridOtpService
    {
        private readonly ILogger<HybridOtpService> _logger;
        private readonly HttpClient _http;
        private readonly IEmailSender _brevoSender;
        private readonly string _supabaseAuthUrl;
        private readonly string _supabaseKey;
        private readonly ConcurrentDictionary<string, OtpRecord> _otpCache;

        private const int OTP_EXPIRY_MINUTES = 10;
        private const int OTP_LENGTH = 6;

        public HybridOtpService(
            ILogger<HybridOtpService> logger,
            IEmailSender brevoSender)
        {
            _logger = logger;
            _brevoSender = brevoSender;
            _http = new HttpClient();
            _otpCache = new ConcurrentDictionary<string, OtpRecord>();

            // 配置Supabase连接
            var baseUrl = SupabaseConfig.SupabaseUrl?.TrimEnd('/') ?? string.Empty;
            _supabaseAuthUrl = $"{baseUrl}/auth/v1/";
            _supabaseKey = !string.IsNullOrWhiteSpace(SupabaseConfig.SupabaseServiceKey)
                ? SupabaseConfig.SupabaseServiceKey
                : SupabaseConfig.SupabaseKey;

            if (!string.IsNullOrWhiteSpace(_supabaseAuthUrl))
            {
                _http.BaseAddress = new Uri(_supabaseAuthUrl);
            }

            if (!string.IsNullOrWhiteSpace(_supabaseKey))
            {
                _http.DefaultRequestHeaders.Remove("apikey");
                _http.DefaultRequestHeaders.TryAddWithoutValidation("apikey", _supabaseKey);
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _supabaseKey);
            }

            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger.LogInformation("[HybridOTP] 混合OTP服务已初始化 (Supabase认证 + Brevo邮件)");
        }

        /// <summary>
        /// 发送OTP验证码：通过Brevo发送，同时在Supabase注册用户
        /// </summary>
        public async Task<(bool Success, string Message, int? RetryAfterSeconds)> SendOtpAsync(string email)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_supabaseAuthUrl) || string.IsNullOrWhiteSpace(_supabaseKey))
                {
                    return (false, "未配置Supabase URL或API Key", null);
                }

                var normalizedEmail = email.Trim().ToLowerInvariant();

                // 检查频率限制（本地缓存）
                if (_otpCache.TryGetValue(normalizedEmail, out var existingRecord))
                {
                    var timeSinceLastSend = DateTime.UtcNow - existingRecord.CreatedAt;
                    if (timeSinceLastSend.TotalSeconds < 60)
                    {
                        var waitSeconds = (int)(60 - timeSinceLastSend.TotalSeconds);
                        _logger.LogWarning($"[HybridOTP] {email} 发送频率过快，需等待 {waitSeconds}秒");
                        return (false, $"请等待 {waitSeconds} 秒后重试", waitSeconds);
                    }
                }

                // 1. 首先确保用户在Supabase中存在
                await EnsureUserExistsInSupabase(email);

                // 2. 生成验证码
                var otp = GenerateOtp();
                _logger.LogInformation($"[HybridOTP] 为 {email} 生成验证码: {otp}");

                // 3. 通过Brevo发送邮件
                var subject = "【UEModManager】验证码登录";
                var htmlContent = GenerateOtpEmailHtml(otp);
                var textContent = $"您的验证码是：{otp}\n\n此验证码将在 {OTP_EXPIRY_MINUTES} 分钟内有效。\n\n如果这不是您本人操作，请忽略此邮件。\n\n爱酱工作室";

                var emailResult = await _brevoSender.SendEmailAsync(email, subject, htmlContent, textContent);

                if (!emailResult.Success)
                {
                    _logger.LogError($"[HybridOTP] Brevo邮件发送失败: {emailResult.ErrorMessage}");
                    return (false, $"邮件发送失败: {emailResult.ErrorMessage}", emailResult.RetryAfterSeconds);
                }

                // 4. 将验证码存储到本地缓存（用于验证）
                var record = new OtpRecord
                {
                    Email = normalizedEmail,
                    Otp = otp,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(OTP_EXPIRY_MINUTES),
                    Attempts = 0
                };

                _otpCache[normalizedEmail] = record;

                // 5. 同时调用Supabase的OTP接口，在Supabase中也创建验证码记录
                try
                {
                    var payload = new { email, create_user = true };
                    var json = JsonSerializer.Serialize(payload);
                    var resp = await _http.PostAsync("otp", new StringContent(json, Encoding.UTF8, "application/json"));
                    var body = await resp.Content.ReadAsStringAsync();

                    if (resp.IsSuccessStatusCode)
                    {
                        _logger.LogInformation($"[HybridOTP] Supabase OTP记录已创建");
                    }
                    else
                    {
                        _logger.LogWarning($"[HybridOTP] Supabase OTP创建失败 {resp.StatusCode}: {body}（不影响Brevo邮件）");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HybridOTP] Supabase OTP创建异常（不影响Brevo邮件）");
                }

                _logger.LogInformation($"[HybridOTP] 验证码已通过Brevo发送至 {email}");
                return (true, "验证码已发送，请查收邮件", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HybridOTP] 发送验证码异常");
                return (false, $"发送失败: {ex.Message}", null);
            }
        }

        /// <summary>
        /// 验证OTP：优先使用本地缓存，失败则尝试Supabase验证
        /// </summary>
        public async Task<(bool Success, string Message)> VerifyOtpAsync(string email, string otp)
        {
            try
            {
                var normalizedEmail = email.Trim().ToLowerInvariant();
                var normalizedOtp = otp.Trim();

                // 1. 首先尝试本地缓存验证（更快）
                if (_otpCache.TryGetValue(normalizedEmail, out var record))
                {
                    // 检查是否过期
                    if (DateTime.UtcNow > record.ExpiresAt)
                    {
                        _otpCache.TryRemove(normalizedEmail, out _);
                        _logger.LogWarning($"[HybridOTP] {email} 本地缓存验证码已过期");
                    }
                    else if (record.Attempts >= 5)
                    {
                        _otpCache.TryRemove(normalizedEmail, out _);
                        _logger.LogWarning($"[HybridOTP] {email} 本地验证次数超限");
                        return (false, "验证次数过多，请重新获取验证码");
                    }
                    else
                    {
                        record.Attempts++;

                        if (record.Otp == normalizedOtp)
                        {
                            _otpCache.TryRemove(normalizedEmail, out _);
                            _logger.LogInformation($"[HybridOTP] {email} 本地缓存验证成功");

                            // 验证成功后，确保用户在Supabase中已认证
                            await AuthenticateWithSupabase(email, normalizedOtp);

                            return (true, "验证成功");
                        }
                        else
                        {
                            var remainingAttempts = 5 - record.Attempts;
                            _logger.LogWarning($"[HybridOTP] {email} 本地验证码错误 (剩余尝试: {remainingAttempts})");
                            return (false, $"验证码错误，剩余尝试次数: {remainingAttempts}");
                        }
                    }
                }

                // 2. 本地缓存未找到或已过期，尝试Supabase验证（备用）
                _logger.LogInformation($"[HybridOTP] 本地缓存未找到，尝试Supabase验证");
                var supabaseResult = await VerifyWithSupabase(email, normalizedOtp);

                return supabaseResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HybridOTP] 验证码验证异常");
                return (false, $"验证失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 确保用户在Supabase中存在
        /// </summary>
        private async Task EnsureUserExistsInSupabase(string email)
        {
            try
            {
                // 通过Supabase Admin API创建或更新用户
                var payload = new
                {
                    email,
                    email_confirm = false, // 通过OTP验证后再确认
                    user_metadata = new
                    {
                        source = "UEModManager",
                        created_at = DateTime.UtcNow.ToString("o")
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var resp = await _http.PostAsync("admin/users", new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"[HybridOTP] 用户 {email} 已在Supabase中创建/更新");
                }
                else if ((int)resp.StatusCode == 422)
                {
                    _logger.LogInformation($"[HybridOTP] 用户 {email} 已存在于Supabase");
                }
                else
                {
                    _logger.LogWarning($"[HybridOTP] 创建Supabase用户失败 {resp.StatusCode}: {body}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[HybridOTP] 确保Supabase用户存在时发生异常");
            }
        }

        /// <summary>
        /// 使用Supabase验证OTP
        /// </summary>
        private async Task<(bool Success, string Message)> VerifyWithSupabase(string email, string token)
        {
            try
            {
                var payload = new { type = "email", email, token };
                var json = JsonSerializer.Serialize(payload);
                var resp = await _http.PostAsync("verify", new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"[HybridOTP] Supabase验证成功 -> {email}");
                    return (true, "验证成功");
                }

                _logger.LogWarning($"[HybridOTP] Supabase验证失败 {resp.StatusCode}: {body}");
                return (false, $"验证失败");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HybridOTP] Supabase验证异常");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 验证成功后与Supabase建立认证会话
        /// </summary>
        private async Task AuthenticateWithSupabase(string email, string token)
        {
            try
            {
                // 通过Supabase verify接口建立会话
                var result = await VerifyWithSupabase(email, token);
                if (result.Success)
                {
                    _logger.LogInformation($"[HybridOTP] Supabase会话已建立 -> {email}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HybridOTP] 建立Supabase会话失败（不影响登录）");
            }
        }

        /// <summary>
        /// 生成6位数字验证码
        /// </summary>
        private static string GenerateOtp()
        {
            var random = new Random();
            return random.Next(100000, 999999).ToString();
        }

        /// <summary>
        /// 生成OTP邮件HTML内容（使用CustomOtpService的模板）
        /// </summary>
        private static string GenerateOtpEmailHtml(string otp)
        {
            // 不添加空格，直接显示6位数字验证码
            var otpWithSpaces = otp;

            return $@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>邮箱验证码登录</title>
</head>
<body style=""font-family: -apple-system, 'Segoe UI', Arial, Helvetica, sans-serif; background-color:#0B1426; color:#ffffff; margin:0; padding:20px;"">
  <div style=""max-width:560px; margin:24px auto; padding:32px; background-color:#121a2b; border-radius:10px; border:1px solid #233048;"">

    <h2 style=""margin:0 0 20px 0; font-size:26px; color:#FFFFFF; font-weight:600;"">邮箱验证码登录</h2>

    <p style=""color:#E5E7EB; font-size:15px; line-height:1.6; margin:0 0 24px 0;"">
      您正在通过邮箱验证码登录 <strong style=""color:#FBBF24;"">UEModManager</strong>，请在 {OTP_EXPIRY_MINUTES} 分钟内输入以下验证码完成验证：
    </p>

    <table width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:28px 0;"">
      <tr>
        <td align=""center"">
          <table cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background-color:#1A2433; border-radius:10px; border:2px solid #FBBF24;"">
            <tr>
              <td align=""center"" style=""padding:30px 50px;"">
                <font size=""7"" color=""#FBBF24"" face=""Courier New, Monaco, monospace"">
                  <b>
                    <span style=""font-size:48px; color:#FBBF24; font-weight:bold; letter-spacing:12px; font-family:'Courier New', Monaco, monospace; display:inline-block; line-height:1.2;"">
                      {otpWithSpaces}
                    </span>
                  </b>
                </font>
              </td>
            </tr>
          </table>
        </td>
      </tr>
    </table>

    <div style=""background-color:#2A1F0D; border-left:4px solid #FBBF24; padding:16px; border-radius:6px; margin:24px 0;"">
      <p style=""margin:0 0 12px 0; color:#FBBF24; font-weight:600; font-size:15px;"">
        ⚠️ 重要提示
      </p>
      <ul style=""color:#E5E7EB; font-size:14px; line-height:1.8; margin:0; padding-left:20px;"">
        <li>此验证码将在 <strong style=""color:#FBBF24;"">{OTP_EXPIRY_MINUTES} 分钟</strong>内有效</li>
        <li>请勿将验证码透露给任何人</li>
        <li>如非本人操作，请忽略此邮件</li>
      </ul>
    </div>

    <div style=""margin-top:32px; padding-top:20px; border-top:1px solid #233048; text-align:center;"">
      <p style=""margin:0; font-size:14px; color:#D1D5DB; line-height:1.6;"">
        此致，<br>
        <strong style=""color:#FBBF24;"">爱酱工作室</strong> | UEModManager
      </p>
      <p style=""margin:12px 0 0 0; font-size:12px; color:#6B7280;"">
        这是一封自动发送的邮件，请勿直接回复
      </p>
    </div>

  </div>
</body>
</html>";
        }

        /// <summary>
        /// 清理过期的验证码记录
        /// </summary>
        public void CleanupExpiredOtps()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = new System.Collections.Generic.List<string>();

            foreach (var kvp in _otpCache)
            {
                if (now > kvp.Value.ExpiresAt)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                _otpCache.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0)
            {
                _logger.LogInformation($"[HybridOTP] 清理了 {expiredKeys.Count} 个过期验证码");
            }
        }

        /// <summary>
        /// OTP记录
        /// </summary>
        private class OtpRecord
        {
            public string Email { get; set; } = string.Empty;
            public string Otp { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public DateTime ExpiresAt { get; set; }
            public int Attempts { get; set; }
        }
    }
}
