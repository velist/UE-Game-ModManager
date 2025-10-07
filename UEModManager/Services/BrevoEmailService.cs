using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// Brevo (Sendinblue) SMTP 邮件服务（备用通道）
    /// 文档：https://developers.brevo.com/docs/smtp-integration
    /// </summary>
    public class BrevoEmailService : IEmailSender
    {
        private readonly ILogger<BrevoEmailService> _logger;
        private readonly string _smtpLogin;
        private readonly string _smtpKey;
        private readonly string _fromEmail;
        private readonly string _fromName;

        private const string SmtpHost = "smtp-relay.brevo.com";
        private const int SmtpPort = 587; // STARTTLS

        public string ServiceName => "Brevo";

        public BrevoEmailService(
            ILogger<BrevoEmailService> logger,
            string smtpLogin,
            string smtpKey,
            string fromEmail,
            string fromName)
        {
            _logger = logger;
            _smtpLogin = smtpLogin ?? string.Empty;
            _smtpKey = smtpKey ?? string.Empty;
            _fromEmail = fromEmail ?? "noreply@modmanger.com";
            _fromName = fromName ?? "爱酱工作室";

            // 🔍 详细调试日志：显示SMTP认证参数
            var loginMasked = _smtpLogin.Length > 10 ? _smtpLogin.Substring(0, 10) + "..." : _smtpLogin;
            var keyPrefix = _smtpKey.Length >= 8 ? _smtpKey.Substring(0, 8) : _smtpKey;
            _logger.LogInformation($"[Brevo] 构造函数 - Login:'{loginMasked}' (长度:{_smtpLogin.Length}), Key前8位:'{keyPrefix}' (长度:{_smtpKey.Length})");

            // ⚠️ 验证SMTP_LOGIN格式
            if (!string.IsNullOrWhiteSpace(_smtpLogin) && !_smtpLogin.Contains("@"))
            {
                _logger.LogWarning($"[Brevo] ⚠️ SMTP_LOGIN '{loginMasked}' 不包含@符号，可能不是有效的邮箱地址");
            }
        }

        public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? textContent = null)
        {
            try
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(_fromEmail, _fromName),
                    Subject = subject,
                    Body = htmlContent,
                    IsBodyHtml = true
                };
                message.To.Add(new MailAddress(to));

                if (!string.IsNullOrEmpty(textContent))
                {
                    var plainView = AlternateView.CreateAlternateViewFromString(textContent, null, "text/plain");
                    message.AlternateViews.Add(plainView);
                }

                using var client = new SmtpClient(SmtpHost, SmtpPort)
                {
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(_smtpLogin, _smtpKey),
                    EnableSsl = true,
                    Timeout = 30000
                };

                if (string.IsNullOrWhiteSpace(_smtpLogin) || string.IsNullOrWhiteSpace(_smtpKey))
                {
                    _logger.LogWarning("[Brevo] SMTP凭据缺失(smtpLogin/smtpKey)，可能导致认证失败");
                }

                _logger.LogInformation($"[Brevo] 发送邮件至 {to}");
                await client.SendMailAsync(message);
                _logger.LogInformation("[Brevo] 发送成功");
                return EmailSendResult.CreateSuccess();
            }
            catch (SmtpException ex)
            {
                _logger.LogError(ex, $"[Brevo] SMTP错误: {ex.StatusCode}");
                var errorType = ex.StatusCode switch
                {
                    SmtpStatusCode.MailboxBusy => EmailSendErrorType.RateLimit,
                    SmtpStatusCode.MailboxUnavailable => EmailSendErrorType.InvalidRecipient,
                    SmtpStatusCode.ExceededStorageAllocation => EmailSendErrorType.RateLimit,
                    _ when ex.Message.Contains("authenticate", StringComparison.OrdinalIgnoreCase) => EmailSendErrorType.AuthenticationFailed,
                    _ when ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) => EmailSendErrorType.RateLimit,
                    _ => EmailSendErrorType.ServerError
                };
                int? retryAfter = errorType == EmailSendErrorType.RateLimit ? 300 : null;
                return EmailSendResult.CreateFailure($"SMTP {ex.StatusCode}: {ex.Message}", errorType, retryAfter);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Brevo] 发送邮件时发生未知错误");
                return EmailSendResult.CreateFailure(ex.Message, EmailSendErrorType.Unknown);
            }
        }

                public async Task<bool> HealthCheckAsync()
        {
            if (string.IsNullOrWhiteSpace(_smtpLogin) || string.IsNullOrWhiteSpace(_smtpKey))
            {
                _logger.LogWarning("[Brevo] 健康检查: 缺少SMTP凭据，标记为不可用");
                return false;
            }
            try
            {
                using var client = new SmtpClient(SmtpHost, SmtpPort)
                {
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(_smtpLogin, _smtpKey),
                    EnableSsl = true,
                    Timeout = 10000
                };
                // 无专用NOOP，连接建立即认为可用
                await Task.Delay(50);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Brevo] 健康检查失败");
                return false;
            }
        }
    }
}

