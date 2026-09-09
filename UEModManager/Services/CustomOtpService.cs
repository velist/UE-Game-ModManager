using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services;

/// <summary>
/// 邮箱登录流程。内存中仅保留服务器返回的 opaque challenge ID；
/// 验证码、有效期、尝试次数与单次消费均由 Worker 持久化核验。
/// </summary>
public sealed class CustomOtpService
{
    private readonly ILogger<CustomOtpService> _logger;
    private readonly WorkerEmailService _worker;
    private readonly Dictionary<string, string> _challenges = new();
    private readonly object _challengeGate = new();

    public CustomOtpService(ILogger<CustomOtpService> logger, WorkerEmailService worker)
    {
        _logger = logger;
        _worker = worker;
    }

    public async Task<(bool Success, string Message, int? RetryAfterSeconds)> SendOtpAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return (false, "请输入邮箱地址", null);
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var result = await _worker.RequestLoginCodeAsync(normalizedEmail);
        if (result.Success && result.ChallengeId != null)
        {
            lock (_challengeGate) _challenges[normalizedEmail] = result.ChallengeId;
            _logger.LogInformation("[CustomOTP] 服务器已发送登录验证码");
        }
        return (result.Success, result.Message, result.RetryAfterSeconds);
    }

    public async Task<(bool Success, string Message)> VerifyOtpAsync(string email, string otp)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otp)) return (false, "请输入邮箱和验证码");
        var normalizedEmail = email.Trim().ToLowerInvariant();
        string? challenge;
        lock (_challengeGate) _challenges.TryGetValue(normalizedEmail, out challenge);
        if (challenge == null) return (false, "请先获取验证码");
        var result = await _worker.VerifyLoginCodeAsync(normalizedEmail, challenge, otp);
        if (result.Success)
        {
            lock (_challengeGate)
            {
                // A delayed response must not discard a newly requested code.
                if (_challenges.GetValueOrDefault(normalizedEmail) == challenge) _challenges.Remove(normalizedEmail);
            }
            _logger.LogInformation("[CustomOTP] 服务器已验证邮箱登录");
        }
        return (result.Success, result.Message);
    }
}
