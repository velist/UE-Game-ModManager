using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// 自定义OTP验证码服务
    /// 使用MailerSend/Brevo发送验证码，不依赖Supabase邮件功能
    /// </summary>
    public class CustomOtpService
    {
        private readonly ILogger<CustomOtpService> _logger;
        private readonly IEmailSender _emailSender;
        private readonly ConcurrentDictionary<string, OtpRecord> _otpStore;

        private const int OtpLength = 6;
        private const int OtpValidityMinutes = 10;
        private const int MaxAttemptsPerEmail = 5;

        public CustomOtpService(
            ILogger<CustomOtpService> logger,
            IEmailSender emailSender)
        {
            _logger = logger;
            _emailSender = emailSender;
            _otpStore = new ConcurrentDictionary<string, OtpRecord>();
        }

        /// <summary>
        /// 发送OTP验证码到邮箱
        /// </summary>
        public async Task<(bool Success, string Message, int? RetryAfterSeconds)> SendOtpAsync(string email)
        {
            try
            {
                // 检查发送频率限制
                var normalizedEmail = email.Trim().ToLowerInvariant();
                if (_otpStore.TryGetValue(normalizedEmail, out var existingRecord))
                {
                    var timeSinceLastSend = DateTime.UtcNow - existingRecord.CreatedAt;
                    if (timeSinceLastSend.TotalSeconds < 60)
                    {
                        var waitSeconds = (int)(60 - timeSinceLastSend.TotalSeconds);
                        _logger.LogWarning($"[CustomOTP] {MaskEmail(email)} 发送频率过快，需等待 {waitSeconds}秒");
                        return (false, $"请等待 {waitSeconds} 秒后重试", waitSeconds);
                    }
                }

                // 生成6位数字验证码
                var otp = GenerateOtp();
                _logger.LogInformation($"[CustomOTP] 为 {MaskEmail(email)} 生成验证码（已脱敏）");

                // 生成邮件内容
                var subject = "【UEModManager】验证码登录";
                var htmlContent = GenerateOtpEmailHtml(otp);
                var textContent = $"您的验证码是：{otp}\n\n此验证码将在 {OtpValidityMinutes} 分钟内有效。\n\n如果这不是您本人操作，请忽略此邮件。\n\n爱酱工作室";

                // 发送邮件
                var result = await _emailSender.SendEmailAsync(email, subject, htmlContent, textContent);

                if (result.Success)
                {
                    // 存储验证码记录
                    var record = new OtpRecord
                    {
                        Otp = otp,
                        Email = normalizedEmail,
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddMinutes(OtpValidityMinutes),
                        Attempts = 0
                    };

                    _otpStore[normalizedEmail] = record;
                    _logger.LogInformation($"[CustomOTP] 验证码已发送至 {MaskEmail(email)}，有效期 {OtpValidityMinutes} 分钟");

                    return (true, "验证码已发送，请查收邮件", null);
                }
                else
                {
                    _logger.LogError($"[CustomOTP] 邮件发送失败: {result.ErrorMessage}");
                    return (false, $"邮件发送失败: {result.ErrorMessage}", result.RetryAfterSeconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CustomOTP] 发送验证码异常");
                return (false, $"发送失败: {ex.Message}", null);
            }
        }

        /// <summary>
        /// 验证OTP验证码
        /// </summary>
        public (bool Success, string Message) VerifyOtp(string email, string otp)
        {
            try
            {
                var normalizedEmail = email.Trim().ToLowerInvariant();
                var normalizedOtp = otp.Trim();

                if (!_otpStore.TryGetValue(normalizedEmail, out var record))
                {
                    _logger.LogWarning($"[CustomOTP] {MaskEmail(email)} 无验证码记录");
                    return (false, "验证码不存在或已过期");
                }

                // 检查是否过期
                if (DateTime.UtcNow > record.ExpiresAt)
                {
                    _otpStore.TryRemove(normalizedEmail, out _);
                    _logger.LogWarning($"[CustomOTP] {MaskEmail(email)} 验证码已过期");
                    return (false, "验证码已过期，请重新获取");
                }

                // 检查尝试次数
                record.Attempts++;
                if (record.Attempts > MaxAttemptsPerEmail)
                {
                    _otpStore.TryRemove(normalizedEmail, out _);
                    _logger.LogWarning($"[CustomOTP] {MaskEmail(email)} 验证次数超限");
                    return (false, "验证次数过多，请重新获取验证码");
                }

                // 验证验证码
                if (record.Otp == normalizedOtp)
                {
                    _otpStore.TryRemove(normalizedEmail, out _);
                    _logger.LogInformation($"[CustomOTP] {MaskEmail(email)} 验证成功");
                    return (true, "验证成功");
                }
                else
                {
                    _logger.LogWarning($"[CustomOTP] {MaskEmail(email)} 验证码错误 (剩余尝试: {MaxAttemptsPerEmail - record.Attempts})");
                    return (false, $"验证码错误，剩余尝试次数: {MaxAttemptsPerEmail - record.Attempts}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CustomOTP] 验证码验证异常");
                return (false, $"验证失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成6位数字验证码
        /// </summary>
        private static string GenerateOtp()
        {
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var number = BitConverter.ToUInt32(bytes, 0) % 1000000;
            return number.ToString("D6");
        }

        private static string MaskEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return "<empty>";
            }

            var trimmed = email.Trim();
            var atIndex = trimmed.IndexOf('@');
            if (atIndex <= 0 || atIndex == trimmed.Length - 1)
            {
                return "***";
            }

            return $"{trimmed[0]}***{trimmed[atIndex..]}";
        }

        /// <summary>
        /// 生成OTP邮件HTML内容（使用font标签+内联CSS双重保险，最佳邮件客户端兼容性）
        /// </summary>
        private static string GenerateOtpEmailHtml(string otp)
        {
            // 不添加空格，直接显示6位数字验证码（便于复制粘贴）
            var otpWithSpaces = otp;

            return $@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>邮箱验证码登录</title>
</head>
<body style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, Helvetica, sans-serif; background-color:#030303; color:#f4f4f5; margin:0; padding:24px;"">
  <div style=""max-width:580px; margin:24px auto; background:linear-gradient(180deg,#111217 0%,#080a10 100%); border:1px solid #27272a; border-radius:22px; overflow:hidden; box-shadow:0 24px 80px rgba(0,0,0,0.45);"">
    <div style=""height:4px; background:linear-gradient(90deg,#06b6d4 0%,#67e8f9 45%,#22c55e 100%); line-height:4px; font-size:0;"">&nbsp;</div>

    <div style=""padding:34px 34px 26px 34px; background:radial-gradient(circle at 18% 0%,rgba(6,182,212,0.22),transparent 34%),radial-gradient(circle at 86% 12%,rgba(103,232,249,0.14),transparent 28%);"">
      <p style=""margin:0 0 12px 0; color:#67e8f9; font-size:13px; font-weight:700; letter-spacing:0.16em; text-transform:uppercase;"">UEModManager</p>
      <h1 style=""margin:0; color:#ffffff; font-size:28px; line-height:1.3; font-weight:800;"">邮箱验证码登录</h1>
      <p style=""margin:14px 0 0 0; color:#a1a1aa; font-size:15px; line-height:1.7;"">
        您正在登录 <strong style=""color:#67e8f9;"">爱酱MOD管理器</strong>，请在 {OtpValidityMinutes} 分钟内输入以下验证码完成验证。
      </p>
    </div>

    <div style=""padding:0 34px 30px 34px;"">
      <table width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:0 0 24px 0;"">
        <tr>
          <td align=""center"" style=""padding:28px 18px; background-color:#0b1117; border:1px solid rgba(6,182,212,0.55); border-radius:18px; box-shadow:inset 0 0 0 1px rgba(103,232,249,0.10);"">
            <font size=""7"" color=""#67e8f9"" face=""Courier New, Monaco, monospace"">
              <b>
                <span style=""font-size:48px; color:#67e8f9; font-weight:800; letter-spacing:12px; font-family:'Courier New', Monaco, monospace; display:inline-block; line-height:1.2; text-shadow:0 0 22px rgba(103,232,249,0.35);"">
                  {otpWithSpaces}
                </span>
              </b>
            </font>
          </td>
        </tr>
      </table>

      <div style=""background-color:rgba(15,15,17,0.86); border:1px solid #27272a; border-left:4px solid #06b6d4; padding:16px 18px; border-radius:14px; margin:0 0 24px 0;"">
        <p style=""margin:0 0 10px 0; color:#67e8f9; font-weight:700; font-size:15px;"">安全提示</p>
        <ul style=""color:#d4d4d8; font-size:14px; line-height:1.8; margin:0; padding-left:20px;"">
          <li>验证码将在 <strong style=""color:#67e8f9;"">{OtpValidityMinutes} 分钟</strong>内有效</li>
          <li>请勿将验证码透露给任何人</li>
          <li>如非本人操作，请忽略此邮件</li>
        </ul>
      </div>

      <div style=""padding-top:20px; border-top:1px solid #27272a; text-align:center;"">
        <p style=""margin:0; font-size:14px; color:#d4d4d8; line-height:1.7;"">
          此致，<br>
          <strong style=""color:#67e8f9;"">爱酱工作室</strong> | UEModManager
        </p>
        <p style=""margin:12px 0 0 0; font-size:12px; color:#71717a;"">
          这是一封自动发送的邮件，请勿直接回复
        </p>
      </div>
    </div>
  </div>
</body>
</html>";
        }

        /// <summary>
        /// 清理过期的验证码记录（定期调用）
        /// </summary>
        public void CleanupExpiredOtps()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = new System.Collections.Generic.List<string>();

            foreach (var kvp in _otpStore)
            {
                if (now > kvp.Value.ExpiresAt)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                _otpStore.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0)
            {
                _logger.LogInformation($"[CustomOTP] 清理了 {expiredKeys.Count} 个过期验证码");
            }
        }
    }

    /// <summary>
    /// OTP记录
    /// </summary>
    internal class OtpRecord
    {
        public string Otp { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int Attempts { get; set; }
    }
}
