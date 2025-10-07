using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// MailerSend邮件服务（主通道）
    /// 文档：https://developers.mailersend.com/api/v1/email.html
    /// </summary>
    public class MailerSendEmailService : IEmailSender
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<MailerSendEmailService> _logger;
        private readonly string _apiToken;
        private readonly string _fromEmail;
        private readonly string _fromName;

        public string ServiceName => "MailerSend";

        public MailerSendEmailService(
            ILogger<MailerSendEmailService> logger,
            string apiToken,
            string fromEmail,
            string fromName)
        {
            _logger = logger;
            _apiToken = apiToken;
            _fromEmail = fromEmail;
            _fromName = fromName;

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.mailersend.com/v1/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiToken}");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UEModManager/1.7.38");
        }

        public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? textContent = null)
        {
            try
            {
                var requestBody = new
                {
                    from = new
                    {
                        email = _fromEmail,
                        name = _fromName
                    },
                    to = new[]
                    {
                        new { email = to }
                    },
                    subject = subject,
                    html = htmlContent,
                    text = textContent ?? StripHtml(htmlContent)
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation($"[MailerSend] 发送邮件至 {to}");
                var response = await _httpClient.PostAsync("email", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation($"[MailerSend] 发送成功: {responseBody}");
                    return EmailSendResult.CreateSuccess();
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"[MailerSend] 发送失败 ({response.StatusCode}): {errorBody}");

                // 解析错误类型
                var errorType = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.TooManyRequests => EmailSendErrorType.RateLimit,
                    System.Net.HttpStatusCode.Unauthorized => EmailSendErrorType.AuthenticationFailed,
                    System.Net.HttpStatusCode.BadRequest => EmailSendErrorType.InvalidRecipient,
                    _ when (int)response.StatusCode >= 500 => EmailSendErrorType.ServerError,
                    _ => EmailSendErrorType.Unknown
                };

                // 解析Retry-After
                int? retryAfter = null;
                if (response.Headers.TryGetValues("Retry-After", out var values))
                {
                    foreach (var value in values)
                    {
                        if (int.TryParse(value, out var seconds))
                        {
                            retryAfter = seconds;
                            break;
                        }
                    }
                }

                return EmailSendResult.CreateFailure(
                    $"HTTP {(int)response.StatusCode}: {errorBody}",
                    errorType,
                    retryAfter
                );
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[MailerSend] 网络请求失败");
                return EmailSendResult.CreateFailure(ex.Message, EmailSendErrorType.NetworkError);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MailerSend] 发送邮件时发生未知错误");
                return EmailSendResult.CreateFailure(ex.Message, EmailSendErrorType.Unknown);
            }
        }

        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                // MailerSend没有专门的健康检查端点，尝试访问API根路径
                var response = await _httpClient.GetAsync("domains");
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MailerSend] 健康检查失败");
                return false;
            }
        }

        /// <summary>
        /// 简单的HTML标签移除（用于生成纯文本备份）
        /// </summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
        }
    }
}

