using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 云端认证服务
    /// </summary>
    public class CloudAuthService
    {
        private static readonly string AppVersion =
            typeof(CloudAuthService).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        private readonly HttpClient _httpClient;
        private readonly ILogger<CloudAuthService> _logger;
        private readonly CloudConfig _config;
        
        private CloudUser? _currentUser;
        private string? _accessToken;
        private DateTime _tokenExpiresAt;

        public bool IsConnected => !string.IsNullOrEmpty(_accessToken) && _tokenExpiresAt > DateTime.Now;
        public CloudUser? CurrentUser => _currentUser;

        public event EventHandler<CloudAuthEventArgs>? AuthStateChanged;

        public CloudAuthService(HttpClient httpClient, ILogger<CloudAuthService> logger, CloudConfig config)
        {
            _httpClient = httpClient;
            _logger = logger;
            _config = config;

            // 配置HTTP客户端
            _httpClient.BaseAddress = new Uri(_config.ApiBaseUrl);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", $"UEModManager/{AppVersion}");

            // CloudConfig.RequestTimeoutSeconds 此前从未被读取，实际生效的是 HttpClient
            // 默认的 100 秒超时。由于会话恢复在主窗口显示之前同步执行，网关黑洞时
            // 应用会在首屏出现前假死最长 100 秒。此处让配置真正生效。
            _httpClient.Timeout = TimeSpan.FromSeconds(
                _config.RequestTimeoutSeconds > 0 ? _config.RequestTimeoutSeconds : 30);
        }

        /// <summary>
        /// 云端登录
        /// </summary>
        public async Task<CloudAuthResult> LoginAsync(string email, string password)
        {
            try
            {
                email = email.Trim().ToLowerInvariant();
                _logger.LogInformation($"尝试云端登录: {email}");

                var loginRequest = new
                {
                    email = email,
                    password = password
                };

                var json = JsonSerializer.Serialize(loginRequest);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync("/api/auth/login", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var loginResponse = JsonSerializer.Deserialize<CloudLoginResponse>(responseContent);
                    
                    if (loginResponse is { Success: true, User: not null, ExpiresIn: > 0 } &&
                        !string.IsNullOrWhiteSpace(loginResponse.AccessToken) &&
                        string.Equals(loginResponse.User.Email, email, StringComparison.OrdinalIgnoreCase))
                    {
                        _accessToken = loginResponse.AccessToken;
                        _tokenExpiresAt = DateTime.Now.AddSeconds(loginResponse.ExpiresIn);
                        _currentUser = loginResponse.User;

                        // 更新HTTP客户端授权头
                        _httpClient.DefaultRequestHeaders.Authorization = 
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                        _logger.LogInformation($"云端登录成功: {email}");
                        OnAuthStateChanged(new CloudAuthEventArgs(CloudAuthEventType.SignedIn, _currentUser));

                        return CloudAuthResult.Success("云端登录成功", _currentUser);
                    }
                    else
                    {
                        var error = loginResponse?.Message ?? "未知错误";
                        _logger.LogWarning($"云端登录失败: {error}");
                        return CloudAuthResult.Failed(error);
                    }
                }
                else
                {
                    var error = await ParseErrorResponse(responseContent);
                    _logger.LogWarning($"云端登录HTTP错误: {response.StatusCode} - {error}");
                    return CloudAuthResult.Failed($"登录失败: {error}");
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "云端登录网络异常");
                return CloudAuthResult.Failed("网络连接异常，请检查网络设置");
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "云端登录请求超时");
                return CloudAuthResult.Failed("请求超时，请重试");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "云端登录未知异常");
                return CloudAuthResult.Failed("登录过程中发生异常");
            }
        }

        /// <summary>
        /// 云端注册
        /// </summary>
        public async Task<CloudAuthResult> RegisterAsync(string email, string password, string? username = null)
        {
            try
            {
                email = email.Trim().ToLowerInvariant();
                _logger.LogInformation($"尝试云端注册: {email}");

                var registerRequest = new
                {
                    email = email,
                    password = password,
                    username = username ?? email.Split('@')[0]
                };

                var json = JsonSerializer.Serialize(registerRequest);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync("/api/auth/register", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var registerResponse = JsonSerializer.Deserialize<CloudRegisterResponse>(responseContent);
                    
                    if (registerResponse != null && registerResponse.Success)
                    {
                        _logger.LogInformation($"云端注册成功: {email}");

                        if (registerResponse.VerificationRequired)
                        {
                            return CloudAuthResult.AwaitingVerification(
                                string.IsNullOrWhiteSpace(registerResponse.Message)
                                    ? "注册请求已受理，请查收验证邮件后登录" : registerResponse.Message);
                        }
                        
                        // 注册成功后自动登录
                        return await LoginAsync(email, password);
                    }
                    else
                    {
                        var error = registerResponse?.Message ?? "注册失败";
                        _logger.LogWarning($"云端注册失败: {error}");
                        return CloudAuthResult.Failed(error);
                    }
                }
                else
                {
                    var error = await ParseErrorResponse(responseContent);
                    _logger.LogWarning($"云端注册HTTP错误: {response.StatusCode} - {error}");
                    return CloudAuthResult.Failed($"注册失败: {error}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"云端注册异常: {email}");
                return CloudAuthResult.Failed("注册过程中发生异常");
            }
        }

        /// <summary>
        /// 云端登出
        /// </summary>
        public async Task<bool> LogoutAsync()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_accessToken))
                {
                    using var response = await _httpClient.PostAsync("/api/auth/logout", null);
                    _logger.LogInformation($"云端登出响应: {response.StatusCode}");
                    return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "云端登出异常");
                return false;
            }
            finally
            {
                // A failed revocation request must not keep this desktop signed in.
                ClearSession(CloudAuthEventType.SignedOut);
            }
        }

        /// <summary>
        /// 验证令牌有效性
        /// </summary>
        public async Task<bool> ValidateTokenAsync()
        {
            try
            {
                if (!IsConnected)
                {
                    return false;
                }

                using var response = await _httpClient.GetAsync("/api/auth/validate");
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var validateResponse = JsonSerializer.Deserialize<CloudValidateResponse>(content);
                    
                    if (validateResponse is { Valid: true, User: not null })
                    {
                        // 更新用户信息
                        if (validateResponse.User != null)
                        {
                            _currentUser = validateResponse.User;
                        }
                        
                        _logger.LogInformation("令牌验证成功");
                        return true;
                    }
                }

                _logger.LogWarning("令牌验证失败，清除本地状态");
                ClearSession(CloudAuthEventType.SessionExpired);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "令牌验证异常");
                return false;
            }
        }
        #region 私有方法

        private void ClearSession(CloudAuthEventType eventType)
        {
            _accessToken = null;
            _currentUser = null;
            _tokenExpiresAt = DateTime.MinValue;
            _httpClient.DefaultRequestHeaders.Authorization = null;
            OnAuthStateChanged(new CloudAuthEventArgs(eventType, null));
        }

        /// <summary>
        /// 解析错误响应
        /// </summary>
        private Task<string> ParseErrorResponse(string responseContent)
        {
            try
            {
                var errorResponse = JsonSerializer.Deserialize<CloudErrorResponse>(responseContent);
                return Task.FromResult(errorResponse?.Message ?? "未知错误");
            }
            catch
            {
                return Task.FromResult(responseContent.Length > 200 ? responseContent.Substring(0, 200) + "..." : responseContent);
            }
        }

        private void OnAuthStateChanged(CloudAuthEventArgs e)
        {
            AuthStateChanged?.Invoke(this, e);
        }

        #endregion
    }

    #region 云端认证相关数据类

    public class CloudConfig
    {
        public string ApiBaseUrl { get; set; } = "https://api.modmanger.com";
        public int RequestTimeoutSeconds { get; set; } = 30;
    }

    public enum CloudAuthEventType
    {
        SignedIn,
        SignedOut,
        TokenRefreshed,
        SessionExpired
    }

    public class CloudAuthEventArgs : EventArgs
    {
        public CloudAuthEventType EventType { get; }
        public CloudUser? User { get; }

        public CloudAuthEventArgs(CloudAuthEventType eventType, CloudUser? user)
        {
            EventType = eventType;
            User = user;
        }
    }

    public class CloudAuthResult
    {
        public bool IsSuccess { get; private set; }
        public string Message { get; private set; }
        public CloudUser? User { get; private set; }
        public Exception? Exception { get; private set; }
        public bool RequiresEmailVerification { get; private set; }

        private CloudAuthResult(bool isSuccess, string message, CloudUser? user = null, Exception? exception = null)
        {
            IsSuccess = isSuccess;
            Message = message;
            User = user;
            Exception = exception;
        }

        public static CloudAuthResult Success(string message, CloudUser? user = null)
        {
            return new CloudAuthResult(true, message, user);
        }

        public static CloudAuthResult Failed(string message, Exception? exception = null)
        {
            return new CloudAuthResult(false, message, null, exception);
        }

        public static CloudAuthResult AwaitingVerification(string message) =>
            new(true, message) { RequiresEmailVerification = true };
    }

    #endregion
}


