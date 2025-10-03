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
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "UEModManager/1.7.37");
        }

        /// <summary>
        /// 云端登录
        /// </summary>
        public async Task<CloudAuthResult> LoginAsync(string email, string password)
        {
            try
            {
                _logger.LogInformation($"尝试云端登录: {email}");

                var loginRequest = new
                {
                    email = email,
                    password = password,
                    device_info = GetDeviceInfo(),
                    app_version = "1.7.37"
                };

                var json = JsonSerializer.Serialize(loginRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/auth/login", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var loginResponse = JsonSerializer.Deserialize<CloudLoginResponse>(responseContent);
                    
                    if (loginResponse != null && loginResponse.Success)
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
        /// 使用激活码登录
        /// </summary>
        public async Task<CloudAuthResult> LoginWithActivationCodeAsync(string email, string activationCode)
        {
            try
            {
                _logger.LogInformation($"开始云端激活码认证: {email}");

                // 模拟激活码认证逻辑
                // 在实际环境中，这里应该调用真实的云端API
                await Task.Delay(1000); // 模拟网络请求

                // 简单的激活码验证逻辑（实际应该是API验证）
                if (activationCode == "123456" || activationCode == DateTime.Now.ToString("HHmmss"))
                {
                    var mockUser = new CloudUser
                    {
                        Id = new Random().Next(10000, 99999), // Use int instead of string
                        Email = email,
                        DisplayName = email.Split('@')[0],
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now,
                        LastLoginAt = DateTime.Now,
                        IsActive = true
                    };

                    _currentUser = mockUser;
                    _accessToken = "mock_activation_token_" + Guid.NewGuid().ToString();
                    _tokenExpiresAt = DateTime.Now.AddHours(24);

                    _logger.LogInformation($"云端激活码认证成功: {email}");
                    AuthStateChanged?.Invoke(this, new CloudAuthEventArgs(CloudAuthEventType.SignedIn, _currentUser));

                    return CloudAuthResult.Success("激活码认证成功", _currentUser);
                }
                else
                {
                    _logger.LogWarning($"云端激活码认证失败: {email} - 激活码无效");
                    return CloudAuthResult.Failed("激活码无效或已过期");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"云端激活码认证异常: {email}");
                return CloudAuthResult.Failed("激活码认证过程中发生异常");
            }
        }

        /// <summary>
        /// 云端注册
        /// </summary>
        public async Task<CloudAuthResult> RegisterAsync(string email, string password, string? username = null)
        {
            try
            {
                _logger.LogInformation($"尝试云端注册: {email}");

                var registerRequest = new
                {
                    email = email,
                    password = password,
                    username = username ?? email.Split('@')[0],
                    device_info = GetDeviceInfo(),
                    app_version = "1.7.37"
                };

                var json = JsonSerializer.Serialize(registerRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/auth/register", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var registerResponse = JsonSerializer.Deserialize<CloudRegisterResponse>(responseContent);
                    
                    if (registerResponse != null && registerResponse.Success)
                    {
                        _logger.LogInformation($"云端注册成功: {email}");
                        
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
                if (IsConnected)
                {
                    var response = await _httpClient.PostAsync("/api/auth/logout", null);
                    _logger.LogInformation($"云端登出响应: {response.StatusCode}");
                }

                // 清除本地状态
                var user = _currentUser;
                _accessToken = null;
                _currentUser = null;
                _tokenExpiresAt = DateTime.MinValue;
                _httpClient.DefaultRequestHeaders.Authorization = null;

                OnAuthStateChanged(new CloudAuthEventArgs(CloudAuthEventType.SignedOut, null));
                _logger.LogInformation("云端登出完成");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "云端登出异常");
                return false;
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

                var response = await _httpClient.GetAsync("/api/auth/validate");
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var validateResponse = JsonSerializer.Deserialize<CloudValidateResponse>(content);
                    
                    if (validateResponse != null && validateResponse.Valid)
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
                await LogoutAsync();
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "令牌验证异常");
                return false;
            }
        }

        /// <summary>
        /// 获取用户偏好设置
        /// </summary>
        public async Task<UserPreferences?> GetUserPreferencesAsync()
        {
            try
            {
                if (!IsConnected)
                {
                    return null;
                }

                var response = await _httpClient.GetAsync("/api/user/preferences");
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var preferencesResponse = JsonSerializer.Deserialize<CloudPreferencesResponse>(content);
                    
                    if (preferencesResponse != null && preferencesResponse.Success)
                    {
                        return ConvertToLocalPreferences(preferencesResponse.Preferences);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取云端用户偏好设置失败");
                return null;
            }
        }

        /// <summary>
        /// 更新用户偏好设置
        /// </summary>
        public async Task<bool> UpdateUserPreferencesAsync(UserPreferences preferences)
        {
            try
            {
                if (!IsConnected)
                {
                    return false;
                }

                var cloudPreferences = ConvertToCloudPreferences(preferences);
                var json = JsonSerializer.Serialize(cloudPreferences);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PutAsync("/api/user/preferences", content);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("云端用户偏好设置更新成功");
                    return true;
                }

                _logger.LogWarning($"云端用户偏好设置更新失败: {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新云端用户偏好设置失败");
                return false;
            }
        }

        /// <summary>
        /// 刷新访问令牌
        /// </summary>
        public async Task<bool> RefreshTokenAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync("/api/auth/refresh", null);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var refreshResponse = JsonSerializer.Deserialize<CloudRefreshResponse>(content);
                    
                    if (refreshResponse != null && refreshResponse.Success)
                    {
                        _accessToken = refreshResponse.AccessToken;
                        _tokenExpiresAt = DateTime.Now.AddSeconds(refreshResponse.ExpiresIn);
                        
                        _httpClient.DefaultRequestHeaders.Authorization = 
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
                        
                        _logger.LogInformation("访问令牌刷新成功");
                        return true;
                    }
                }

                _logger.LogWarning("访问令牌刷新失败");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新访问令牌异常");
                return false;
            }
        }

        #region 私有方法

        /// <summary>
        /// 获取设备信息
        /// </summary>
        private static object GetDeviceInfo()
        {
            return new
            {
                platform = Environment.OSVersion.Platform.ToString(),
                version = Environment.OSVersion.Version.ToString(),
                machine_name = Environment.MachineName,
                user_name = Environment.UserName,
                processor_count = Environment.ProcessorCount,
                working_set = Environment.WorkingSet
            };
        }

        /// <summary>
        /// 解析错误响应
        /// </summary>
        private async Task<string> ParseErrorResponse(string responseContent)
        {
            try
            {
                var errorResponse = JsonSerializer.Deserialize<CloudErrorResponse>(responseContent);
                return errorResponse?.Message ?? "未知错误";
            }
            catch
            {
                return responseContent.Length > 200 ? responseContent.Substring(0, 200) + "..." : responseContent;
            }
        }

        /// <summary>
        /// 转换为本地偏好设置
        /// </summary>
        private UserPreferences ConvertToLocalPreferences(CloudUserPreferences cloudPrefs)
        {
            return new UserPreferences
            {
                UserId = _currentUser?.Id ?? 0,
                DefaultGamePath = cloudPrefs.DefaultGamePath,
                Language = cloudPrefs.Language,
                Theme = cloudPrefs.Theme,
                AutoCheckUpdates = cloudPrefs.AutoCheckUpdates,
                AutoBackup = cloudPrefs.AutoBackup,
                ShowNotifications = cloudPrefs.ShowNotifications,
                MinimizeToTray = cloudPrefs.MinimizeToTray,
                EnableCloudSync = cloudPrefs.EnableCloudSync,
                UpdatedAt = DateTime.Now
            };
        }

        /// <summary>
        /// 转换为云端偏好设置
        /// </summary>
        private CloudUserPreferences ConvertToCloudPreferences(UserPreferences localPrefs)
        {
            return new CloudUserPreferences
            {
                DefaultGamePath = localPrefs.DefaultGamePath,
                Language = localPrefs.Language,
                Theme = localPrefs.Theme,
                AutoCheckUpdates = localPrefs.AutoCheckUpdates,
                AutoBackup = localPrefs.AutoBackup,
                ShowNotifications = localPrefs.ShowNotifications,
                MinimizeToTray = localPrefs.MinimizeToTray,
                EnableCloudSync = localPrefs.EnableCloudSync,
                UpdatedAt = localPrefs.UpdatedAt
            };
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
        public int MaxRetryAttempts { get; set; } = 3;
        public bool EnableDetailedLogging { get; set; } = true;
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
    }

    #endregion
}


