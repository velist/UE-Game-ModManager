using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// Brevo API 邮件服务（使用REST API而非SMTP）
    /// 与SMTP方式相比，API方式更快速，但需要设置正确的字符编码
    /// 文档：https://developers.brevo.com/reference/sendtransacemail
    /// </summary>
    public class BrevoApiEmailService : IEmailSender
    {
        private readonly ILogger<BrevoApiEmailService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _fromEmail;
        private readonly string _fromName;

        public string ServiceName => "Brevo-API";

        public BrevoApiEmailService(
            ILogger<BrevoApiEmailService> logger,
            string apiKey,
            string fromEmail,
            string fromName)
        {
            _logger = logger;
            _fromEmail = fromEmail;
            _fromName = fromName;

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.brevo.com/v3/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
            _httpClient.DefaultRequestHeaders.Add("accept", "application/json");
        }

        public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? textContent = null)
        {
            try
            {
                var requestBody = new
                {
                    sender = new
                    {
                        email = _fromEmail,
                        name = _fromName
                    },
                    to = new[]
                    {
                        new { email = to }
                    },
                    subject = subject,
                    htmlContent = htmlContent,
                    textContent = textContent ?? StripHtml(htmlContent),
                    charset = "utf-8"  // 明确设置UTF-8编码，避免中文乱码
                };

                // 使用 UTF-8 编码序列化JSON
                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var jsonContent = JsonSerializer.Serialize(requestBody, jsonOptions);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                _logger.LogInformation($"[Brevo-API] 发送邮件至 {to}");
                var response = await _httpClient.PostAsync("smtp/email", content);
                var responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"[Brevo-API] 发送成功");

                    // 解析响应获取 messageId
                    var responseData = JsonSerializer.Deserialize<JsonElement>(responseText);
                    var messageId = responseData.GetProperty("messageId").GetString();

                    return EmailSendResult.CreateSuccess(messageId);
                }
                else
                {
                    _logger.LogError($"[Brevo-API] 发送失败: {response.StatusCode} - {responseText}");

                    var errorType = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized => EmailSendErrorType.AuthenticationFailed,
                        System.Net.HttpStatusCode.Forbidden => EmailSendErrorType.AuthenticationFailed,
                        System.Net.HttpStatusCode.BadRequest => EmailSendErrorType.InvalidRecipient,
                        System.Net.HttpStatusCode.TooManyRequests => EmailSendErrorType.RateLimit,
                        _ => EmailSendErrorType.ServerError
                    };

                    // 检查是否有 retry-after 头
                    int? retryAfter = null;
                    if (response.Headers.TryGetValues("Retry-After", out var values))
                    {
                        var retryAfterValue = string.Join("", values);
                        if (int.TryParse(retryAfterValue, out var seconds))
                        {
                            retryAfter = seconds;
                        }
                    }

                    return EmailSendResult.CreateFailure(
                        $"HTTP {(int)response.StatusCode}: {responseText}",
                        errorType,
                        retryAfter
                    );
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[Brevo-API] 网络请求失败");
                return EmailSendResult.CreateFailure(ex.Message, EmailSendErrorType.NetworkError);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "[Brevo-API] 请求超时");
                return EmailSendResult.CreateFailure("请求超时", EmailSendErrorType.NetworkError);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Brevo-API] 发送邮件时发生未知错误");
                return EmailSendResult.CreateFailure(ex.Message, EmailSendErrorType.Unknown);
            }
        }

        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                // Brevo API 没有专门的健康检查端点，使用账户信息端点
                var response = await _httpClient.GetAsync("account");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Brevo-API] 健康检查失败");
                return false;
            }
        }

        /// <summary>
        /// 简单的HTML标签剥离（用于生成纯文本版本）
        /// </summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }
    }
}
