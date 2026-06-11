using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    /// <summary>
    /// Worker 邮件转发服务 — 通过 Cloudflare Worker (api.modmanger.com) 代理调 Brevo API。
    /// Brevo API Key 完全保留在 Worker 端,客户端不再持有任何凭据。
    /// 端点：POST {ApiBaseUrl}/email/send  body { to, subject, html, text }
    /// </summary>
    public class WorkerEmailService : IEmailSender
    {
        private readonly ILogger<WorkerEmailService> _logger;
        private readonly HttpClient _httpClient;

        public string ServiceName => "WorkerEmail";

        public WorkerEmailService(ILogger<WorkerEmailService> logger, string apiBaseUrl)
        {
            _logger = logger;
            var baseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
                ? "https://api.modmanger.com"
                : apiBaseUrl.TrimEnd('/');

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl + "/"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UEModManager/2.0.3-beta");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? textContent = null)
        {
            try
            {
                var requestBody = new
                {
                    to,
                    subject,
                    html = htmlContent,
                    text = textContent ?? string.Empty
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(requestBody, jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation($"[WorkerEmail] 通过 Worker 发送邮件至 {to}");
                var response = await _httpClient.PostAsync("email/send", content);
                var responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[WorkerEmail] 发送成功");

                    string? messageId = null;
                    try
                    {
                        var data = JsonSerializer.Deserialize<JsonElement>(responseText);
                        if (data.TryGetProperty("data", out var d) &&
                            d.TryGetProperty("messageId", out var m))
                        {
                            messageId = m.GetString();
                        }
                    }
                    catch { /* 响应解析失败不影响成功语义 */ }

                    return EmailSendResult.CreateSuccess(messageId);
                }

                _logger.LogError($"[WorkerEmail] 发送失败: {response.StatusCode} - {responseText}");

                var errorType = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.BadRequest => EmailSendErrorType.InvalidRecipient,
                    System.Net.HttpStatusCode.Forbidden => EmailSendErrorType.AuthenticationFailed,
                    System.Net.HttpStatusCode.TooManyRequests => EmailSendErrorType.RateLimit,
                    System.Net.HttpStatusCode.BadGateway => EmailSendErrorType.ServerError,
                    _ when (int)response.StatusCode >= 500 => EmailSendErrorType.ServerError,
                    _ => EmailSendErrorType.Unknown
                };

                int? retryAfter = null;
                if (response.Headers.TryGetValues("Retry-After", out var values))
                {
                    foreach (var v in values)
                    {
                        if (int.TryParse(v, out var seconds))
                        {
                            retryAfter = seconds;
                            break;
                        }
                    }
                }

                return EmailSendResult.CreateFailure($"HTTP {(int)response.StatusCode}: {responseText}", errorType, retryAfter);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[WorkerEmail] 网络请求失败");
                return EmailSendResult.CreateFailure(ex.Message, EmailSendErrorType.NetworkError);
            }
            catch (TaskCanceledException)
            {
                _logger.LogError("[WorkerEmail] 请求超时");
                return EmailSendResult.CreateFailure("请求超时", EmailSendErrorType.NetworkError);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WorkerEmail] 发送邮件时发生未知错误");
                return EmailSendResult.CreateFailure(ex.Message, EmailSendErrorType.Unknown);
            }
        }

        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                using var response = await _httpClient.GetAsync("health");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WorkerEmail] 健康检查失败");
                return false;
            }
        }
    }
}
