using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services;

/// <summary>
/// Worker 邮箱登录协议。客户端不能选择主题/正文或生成验证码；服务端负责发送和一次性核验。
/// </summary>
public sealed class WorkerEmailService : IDisposable
{
    private readonly ILogger<WorkerEmailService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private static string AppVersion => typeof(WorkerEmailService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public WorkerEmailService(ILogger<WorkerEmailService> logger, string apiBaseUrl)
        : this(CreateClient(apiBaseUrl), logger)
    {
        _ownsClient = true;
    }

    public WorkerEmailService(HttpClient httpClient, ILogger<WorkerEmailService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress ??= new Uri("https://api.modmanger.com/");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"UEModManager/{AppVersion}");
    }

    public async Task<OtpRequestResult> RequestLoginCodeAsync(string email)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/auth/otp/request", new
            {
                email = email.Trim().ToLowerInvariant(), purpose = "login"
            });
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var body = document.RootElement;
            var message = ReadString(body, "message") ?? "验证码发送失败，请稍后重试";
            var retryAfter = ReadRetryAfter(response, body);
            var challenge = ReadString(body, "challenge_id");
            var expiresIn = ReadInteger(body, "expires_in");
            if (response.IsSuccessStatusCode && IsTrue(body, "success") &&
                challenge is { Length: 64 } && IsHex(challenge) && expiresIn > 0)
            {
                return new(true, message, challenge, retryAfter, expiresIn.Value);
            }
            return new(false, response.IsSuccessStatusCode ? "认证服务响应无效，请稍后重试" : message,
                RetryAfterSeconds: retryAfter);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "[WorkerOTP] 无法获取验证码");
            return new(false, "无法连接验证码服务，请稍后重试");
        }
    }

    public async Task<OtpVerificationResult> VerifyLoginCodeAsync(string email, string challengeId, string code)
    {
        try
        {
            var normalizedEmail = email.Trim().ToLowerInvariant();
            using var response = await _httpClient.PostAsJsonAsync("/api/auth/otp/verify", new
            {
                email = normalizedEmail, purpose = "login", challenge_id = challengeId, code = code.Trim()
            });
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var body = document.RootElement;
            var verifiedEmail = ReadString(body, "verified_email");
            if (response.IsSuccessStatusCode && IsTrue(body, "success") &&
                verifiedEmail == normalizedEmail && ReadString(body, "purpose") == "login")
            {
                return new(true, "验证成功", verifiedEmail);
            }
            return new(false, ReadString(body, "message") ?? "验证码验证失败，请重新获取");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "[WorkerOTP] 无法验证验证码");
            return new(false, "无法连接验证码服务，请稍后重试");
        }
    }

    private static HttpClient CreateClient(string apiBaseUrl) => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        BaseAddress = new Uri((string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.modmanger.com" : apiBaseUrl.TrimEnd('/')) + "/"),
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static string? ReadString(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? ReadInteger(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static bool IsTrue(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsHex(string value)
    {
        foreach (var character in value) if (!char.IsAsciiHexDigitLower(character)) return false;
        return true;
    }

    private static int? ReadRetryAfter(HttpResponseMessage response, JsonElement body)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        var retry = seconds.HasValue ? (int)Math.Ceiling(seconds.Value) : ReadInteger(body, "retry_after");
        return retry > 0 ? retry : null;
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}

public sealed record OtpRequestResult(bool Success, string Message, string? ChallengeId = null,
    int? RetryAfterSeconds = null, int ExpiresInSeconds = 0);

public sealed record OtpVerificationResult(bool Success, string Message, string? VerifiedEmail = null);
