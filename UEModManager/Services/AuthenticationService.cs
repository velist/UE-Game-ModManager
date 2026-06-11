using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UEModManager.Services
{
    public class AuthenticationService
    {
        private readonly LocalAuthService _localAuthService;
        private readonly ILogger<AuthenticationService> _logger;
        private static readonly HttpClient _http = new();
        private const string WorkerApiBase = "https://api.modmanger.com";

        public AuthenticationService(LocalAuthService localAuthService, ILogger<AuthenticationService> logger)
        {
            _localAuthService = localAuthService;
            _logger = logger;
        }

        public bool IsLoggedIn => _localAuthService.IsLoggedIn;
        public event EventHandler<AuthEventArgs>? AuthStateChanged;

        public async Task<AuthenticationResult> SignUpAsync(string email, string password, string? username = null)
        {
            var result = await _localAuthService.RegisterAsync(email, password, username);
            if (!result.IsSuccess)
                return AuthenticationResult.Failed(result.Message);

            OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedUp));
            return AuthenticationResult.Success(result.Message);
        }

        public async Task<AuthenticationResult> SignInAsync(string email, string password)
        {
            var result = await _localAuthService.LoginAsync(email, password);
            if (!result.IsSuccess)
                return AuthenticationResult.Failed(result.Message);

            OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedIn));
            return AuthenticationResult.Success(result.Message);
        }

        public async Task<bool> SignOutAsync()
        {
            var result = await _localAuthService.LogoutAsync();
            if (result)
                OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedOut));
            return result;
        }

        public async Task<AuthenticationResult> ResetPasswordAsync(string email)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { email, redirect_to = $"{WorkerApiBase}/reset-password" });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var resp = await _http.PostAsync($"{WorkerApiBase}/auth/reset", content, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    var msg = string.IsNullOrWhiteSpace(text) ? $"HTTP {(int)resp.StatusCode}" : text;
                    _logger.LogWarning("[ForgotPassword] 网关返回非成功: {Status} - {Message}", (int)resp.StatusCode, msg);
                    return AuthenticationResult.Failed($"密码重置失败：{msg}\n\n提示：您可以先使用验证码登录，然后在设置中修改密码");
                }

                if (TryOpenLinkOnlyReset(text, out var message))
                    return AuthenticationResult.Success(message);

                _logger.LogInformation("[ForgotPassword] 密码重置请求已处理: {Email}", email);
                return AuthenticationResult.Success("重置邮件已发送，请检查您的邮箱");
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "[ForgotPassword] 请求超时");
                return AuthenticationResult.Failed("请求超时，请稍后重试");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ForgotPassword] 发送失败: {Email}", email);
                return AuthenticationResult.Failed($"发送失败：{ex.Message}");
            }
        }

        public Task<bool> UpdateUserProfileAsync(string? username = null, string? fullName = null)
            => Task.FromResult(false);

        public string GetUserDisplayName()
        {
            var user = _localAuthService.CurrentUser;
            if (user == null) return "未登录";
            if (!string.IsNullOrWhiteSpace(user.DisplayName)) return user.DisplayName;
            if (!string.IsNullOrWhiteSpace(user.Username)) return user.Username;
            return user.Email;
        }

        private static bool TryOpenLinkOnlyReset(string responseText, out string message)
        {
            message = "";
            try
            {
                using var jdoc = JsonDocument.Parse(responseText);
                var root = jdoc.RootElement;
                var channelUsed = root.TryGetProperty("channelUsed", out var ch) ? ch.GetString() : "";
                if (channelUsed != "link_only")
                    return false;

                if (!root.TryGetProperty("data", out var dataElem) ||
                    !dataElem.TryGetProperty("link", out var linkElem))
                    return false;

                var link = linkElem.GetString();
                if (string.IsNullOrWhiteSpace(link))
                    return false;

                try
                {
                    Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                    message = "已打开重置页面，请在浏览器中完成密码重置";
                }
                catch
                {
                    message = $"无法自动打开浏览器，请手动复制此链接重置密码：\n\n{link}";
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnAuthStateChanged(AuthEventArgs e)
        {
            AuthStateChanged?.Invoke(this, e);
        }
    }

    public enum AuthChangeEventType
    {
        SignedUp,
        SignedIn,
        SignedOut,
        PasswordRecovery,
        TokenRefreshed
    }

    public class AuthenticationResult
    {
        public bool IsSuccess { get; private set; }
        public string Message { get; private set; }
        public Exception? Exception { get; private set; }

        private AuthenticationResult(bool isSuccess, string message, Exception? exception = null)
        {
            IsSuccess = isSuccess;
            Message = message;
            Exception = exception;
        }

        public static AuthenticationResult Success(string message = "操作成功")
            => new(true, message);

        public static AuthenticationResult Failed(string message, Exception? exception = null)
            => new(false, message, exception);
    }

    public class AuthEventArgs : EventArgs
    {
        public AuthChangeEventType EventType { get; }
        public object? Session { get; }

        public AuthEventArgs(AuthChangeEventType eventType, object? session = null)
        {
            EventType = eventType;
            Session = session;
        }
    }
}
