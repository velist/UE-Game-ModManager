using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 统一认证服务 - 整合本地和云端认证
    /// </summary>
    public class UnifiedAuthService
    {
        private readonly LocalAuthService _localAuthService;
        private readonly CloudAuthService _cloudAuthService;
        private readonly ILogger<UnifiedAuthService> _logger;
        private readonly HttpClient _httpClient;

        // 认证模式
        public enum AuthMode
        {
            OfflineOnly,    // 仅离线模式
            OnlineOnly,     // 仅在线模式  
            Hybrid          // 混合模式（推荐）
        }

        public AuthMode CurrentMode { get; private set; } = AuthMode.Hybrid;
        public bool IsOnline => _cloudAuthService?.IsConnected ?? false;
        public LocalUser? CurrentUser => _localAuthService?.CurrentUser;

        public UnifiedAuthService(
            LocalAuthService localAuthService,
            CloudAuthService cloudAuthService,
            ILogger<UnifiedAuthService> logger,
            HttpClient httpClient)
        {
            _localAuthService = localAuthService;
            _cloudAuthService = cloudAuthService;
            _logger = logger;
            _httpClient = httpClient;

            // 监听认证状态变化
            _localAuthService.AuthStateChanged += OnLocalAuthStateChanged;
            _cloudAuthService.AuthStateChanged += OnCloudAuthStateChanged;
        }

        /// <summary>
        /// 统一登录 - 自动选择最佳认证方式
        /// </summary>
        public async Task<UnifiedAuthResult> LoginAsync(string email, string password, bool rememberMe = false)
        {
            try
            {
                // 根据当前模式和网络状态选择认证方式
                switch (CurrentMode)
                {
                    case AuthMode.OfflineOnly:
                        return await LoginOfflineAsync(email, password, rememberMe);

                    case AuthMode.OnlineOnly:
                        return await LoginOnlineAsync(email, password, rememberMe);

                    case AuthMode.Hybrid:
                        return await LoginHybridAsync(email, password, rememberMe);

                    default:
                        return UnifiedAuthResult.Failed("未知的认证模式");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"统一登录失败: {email}");
                return UnifiedAuthResult.Failed("登录过程中发生异常");
            }
        }

        /// <summary>
        /// 混合模式登录 - 优先在线，离线备用
        /// </summary>
        private async Task<UnifiedAuthResult> LoginHybridAsync(string email, string password, bool rememberMe)
        {
            // 首先检查网络连接
            var isOnlineAvailable = await CheckNetworkConnectivity();

            if (isOnlineAvailable)
            {
                try
                {
                    _logger.LogInformation("尝试云端登录...");
                    var cloudResult = await _cloudAuthService.LoginAsync(email, password);
                    
                    if (cloudResult.IsSuccess)
                    {
                        // 云端登录成功，同步到本地
                        await SyncUserToLocal(cloudResult.User, password);
                        
                        // 保存记住我令牌
                        if (rememberMe)
                        {
                            await _localAuthService.SaveRememberMeTokenAsync(email, true);
                        }
                        
                        _logger.LogInformation($"混合模式登录成功（云端）: {email}");
                        return UnifiedAuthResult.Success("云端登录成功", AuthSource.Cloud, _localAuthService.CurrentUser);
                    }
                    else
                    {
                        _logger.LogWarning($"云端登录失败，尝试本地登录: {cloudResult.Message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "云端登录异常，尝试本地登录");
                }
            }

            // 云端登录失败或无网络，尝试本地登录
            _logger.LogInformation("尝试本地登录...");
            var localResult = await _localAuthService.LoginAsync(email, password);
            
            if (localResult.IsSuccess)
            {
                // 保存记住我令牌
                if (rememberMe)
                {
                    await _localAuthService.SaveRememberMeTokenAsync(email, true);
                }
                
                _logger.LogInformation($"混合模式登录成功（本地）: {email}");
                return UnifiedAuthResult.Success("本地登录成功", AuthSource.Local, _localAuthService.CurrentUser);
            }

            return UnifiedAuthResult.Failed($"登录失败: {localResult.Message}");
        }

        /// <summary>
        /// 仅在线登录
        /// </summary>
        private async Task<UnifiedAuthResult> LoginOnlineAsync(string email, string password, bool rememberMe)
        {
            if (!await CheckNetworkConnectivity())
            {
                return UnifiedAuthResult.Failed("网络连接不可用，无法进行在线登录");
            }

            var result = await _cloudAuthService.LoginAsync(email, password);
            if (result.IsSuccess)
            {
                // 同步到本地缓存
                await SyncUserToLocal(result.User, password);
                
                if (rememberMe)
                {
                    await _localAuthService.SaveRememberMeTokenAsync(email, true);
                }
            }

            return UnifiedAuthResult.FromCloudResult(result, AuthSource.Cloud);
        }

        /// <summary>
        /// 仅离线登录
        /// </summary>
        private async Task<UnifiedAuthResult> LoginOfflineAsync(string email, string password, bool rememberMe)
        {
            var result = await _localAuthService.LoginAsync(email, password);
            
            if (result.IsSuccess && rememberMe)
            {
                await _localAuthService.SaveRememberMeTokenAsync(email, true);
            }

            return UnifiedAuthResult.FromLocalResult(result, AuthSource.Local);
        }

        /// <summary>
        /// 统一注册
        /// </summary>
        public async Task<UnifiedAuthResult> RegisterAsync(string email, string password, string? username = null)
        {
            try
            {
                switch (CurrentMode)
                {
                    case AuthMode.OfflineOnly:
                        var localResult = await _localAuthService.RegisterAsync(email, password, username);
                        return UnifiedAuthResult.FromLocalResult(localResult, AuthSource.Local);

                    case AuthMode.OnlineOnly:
                        if (!await CheckNetworkConnectivity())
                        {
                            return UnifiedAuthResult.Failed("网络连接不可用，无法进行在线注册");
                        }
                        var cloudResult = await _cloudAuthService.RegisterAsync(email, password, username);
                        if (cloudResult.IsSuccess)
                        {
                            await SyncUserToLocal(cloudResult.User, password);
                        }
                        return UnifiedAuthResult.FromCloudResult(cloudResult, AuthSource.Cloud);

                    case AuthMode.Hybrid:
                        return await RegisterHybridAsync(email, password, username);

                    default:
                        return UnifiedAuthResult.Failed("未知的认证模式");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"统一注册失败: {email}");
                return UnifiedAuthResult.Failed("注册过程中发生异常");
            }
        }

        /// <summary>
        /// 混合模式注册
        /// </summary>
        private async Task<UnifiedAuthResult> RegisterHybridAsync(string email, string password, string? username)
        {
            var isOnlineAvailable = await CheckNetworkConnectivity();

            if (isOnlineAvailable)
            {
                try
                {
                    // 优先云端注册
                    var cloudResult = await _cloudAuthService.RegisterAsync(email, password, username);
                    if (cloudResult.IsSuccess)
                    {
                        await SyncUserToLocal(cloudResult.User, password);
                        return UnifiedAuthResult.FromCloudResult(cloudResult, AuthSource.Cloud);
                    }
                    else
                    {
                        _logger.LogWarning($"云端注册失败: {cloudResult.Message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "云端注册异常，使用本地注册");
                }
            }

            // 本地注册备用
            var localResult = await _localAuthService.RegisterAsync(email, password, username);
            return UnifiedAuthResult.FromLocalResult(localResult, AuthSource.Local);
        }
        /// <summary>
        /// 统一登出
        /// </summary>
        public async Task<bool> LogoutAsync()
        {
            try
            {
                var localLogout = await _localAuthService.LogoutAsync();
                var cloudLogout = true;

                if (IsOnline)
                {
                    cloudLogout = await _cloudAuthService.LogoutAsync();
                }

                await _localAuthService.ClearRememberMeTokenAsync();
                
                _logger.LogInformation("统一登出完成");
                return localLogout && cloudLogout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "统一登出失败");
                return false;
            }
        }

        /// <summary>
        /// 尝试恢复会话
        /// </summary>
        public async Task<UnifiedAuthResult> RestoreSessionAsync()
        {
            try
            {
                // 首先尝试记住我令牌
                var rememberMeResult = await _localAuthService.ValidateRememberMeTokenAsync();
                if (rememberMeResult.IsSuccess)
                {
                    _logger.LogInformation("记住我令牌恢复会话成功");
                    return UnifiedAuthResult.FromLocalResult(rememberMeResult, AuthSource.Local);
                }

                // 尝试恢复本地会话
                var localRestore = await _localAuthService.RestoreSessionAsync();
                if (localRestore)
                {
                    _logger.LogInformation("本地会话恢复成功");
                    
                    // 如果在线，尝试验证云端会话
                    if (await CheckNetworkConnectivity())
                    {
                        try
                        {
                            var cloudRestore = await _cloudAuthService.ValidateTokenAsync();
                            if (cloudRestore)
                            {
                                _logger.LogInformation("云端会话验证成功");
                                return UnifiedAuthResult.Success("会话恢复成功", AuthSource.Hybrid, _localAuthService.CurrentUser);
                            }
                            else
                            {
                                // 云端token验证失败（可能过期），清理本地状态
                                _logger.LogWarning("云端会话验证失败，清理本地会话状态");
                                await _localAuthService.LogoutAsync();
                                return UnifiedAuthResult.Failed("云端会话已过期，请重新登录");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "云端会话验证异常，清理本地会话状态");
                            await _localAuthService.LogoutAsync();
                            return UnifiedAuthResult.Failed("会话验证异常，请重新登录");
                        }
                    }

                    return UnifiedAuthResult.Success("本地会话恢复成功", AuthSource.Local, _localAuthService.CurrentUser);
                }

                return UnifiedAuthResult.Failed("无可恢复的会话");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "会话恢复失败");
                return UnifiedAuthResult.Failed("会话恢复异常");
            }
        }
        /// <summary>
        /// 切换认证模式
        /// </summary>
        public async Task<bool> SetAuthModeAsync(AuthMode mode)
        {
            try
            {
                CurrentMode = mode;
                _logger.LogInformation($"认证模式已切换为: {mode}");

                // 保存模式设置
                await SaveAuthModeAsync(mode);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"切换认证模式失败: {mode}");
                return false;
            }
        }

        #region 私有方法

        /// <summary>
        /// 检查网络连接
        /// </summary>
        private async Task<bool> CheckNetworkConnectivity()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await _httpClient.GetAsync("https://www.baidu.com", cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 同步用户数据到本地
        /// </summary>
        private async Task SyncUserToLocal(CloudUser cloudUser, string password)
        {
            try
            {
                // 检查本地是否已有该用户
                var existingUser = await _localAuthService.FindUserByEmailAsync(cloudUser.Email);
                
                if (existingUser == null)
                {
                    // 创建新的本地用户
                    await _localAuthService.RegisterAsync(cloudUser.Email, password, cloudUser.Username);
                }
                else
                {
                    // 更新现有用户信息
                    existingUser.Username = cloudUser.Username;
                    existingUser.DisplayName = cloudUser.DisplayName;
                    existingUser.Avatar = cloudUser.Avatar;
                    existingUser.LastLoginAt = DateTime.Now;

                    await _localAuthService.UpdateUserAsync(existingUser);

                    // 强制设置登录状态，确保 _currentUser 和会话被正确设置
                    await _localAuthService.ForceSetAuthStateAsync(cloudUser.Email, cloudUser.Username);
                }

                _logger.LogInformation($"用户数据已同步到本地: {cloudUser.Email}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"同步用户到本地失败: {cloudUser.Email}");
            }
        }

        /// <summary>
        /// 保存认证模式设置
        /// </summary>
        private async Task SaveAuthModeAsync(AuthMode mode)
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configDir = Path.Combine(appDataPath, "UEModManager");
                Directory.CreateDirectory(configDir);

                var configFile = Path.Combine(configDir, "auth_config.json");
                var config = new { AuthMode = mode.ToString() };
                var json = JsonSerializer.Serialize(config);
                
                await File.WriteAllTextAsync(configFile, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存认证模式设置失败");
            }
        }

        /// <summary>
        /// 加载认证模式设置
        /// </summary>
        private async Task LoadAuthModeAsync()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configFile = Path.Combine(appDataPath, "UEModManager", "auth_config.json");

                if (File.Exists(configFile))
                {
                    var json = await File.ReadAllTextAsync(configFile);
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("AuthMode", out var modeElement))
                    {
                        if (Enum.TryParse<AuthMode>(modeElement.GetString(), out var mode))
                        {
                            CurrentMode = mode;
                            _logger.LogInformation($"已加载认证模式: {mode}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载认证模式设置失败，使用默认设置");
            }
        }

        private void OnLocalAuthStateChanged(object? sender, LocalAuthEventArgs e)
        {
            _logger.LogInformation($"本地认证状态变化: {e.EventType}");
        }

        private void OnCloudAuthStateChanged(object? sender, CloudAuthEventArgs e)
        {
            _logger.LogInformation($"云端认证状态变化: {e.EventType}");
        }

        #endregion

        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("开始初始化统一认证服务");

                await LoadAuthModeAsync();
                _logger.LogInformation($"统一认证服务初始化完成，当前模式: {CurrentMode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "统一认证服务初始化失败");
                CurrentMode = AuthMode.OfflineOnly;
                _logger.LogInformation("已切换到离线模式");
            }
        }
    }

    #region 结果类和枚举

    public enum AuthSource
    {
        Local,
        Cloud,
        Hybrid
    }

    public class UnifiedAuthResult
    {
        public bool IsSuccess { get; private set; }
        public string Message { get; private set; }
        public AuthSource Source { get; private set; }
        public LocalUser? User { get; private set; }
        public Exception? Exception { get; private set; }

        private UnifiedAuthResult(bool isSuccess, string message, AuthSource source, LocalUser? user = null, Exception? exception = null)
        {
            IsSuccess = isSuccess;
            Message = message;
            Source = source;
            User = user;
            Exception = exception;
        }

        public static UnifiedAuthResult Success(string message, AuthSource source, LocalUser? user = null)
        {
            return new UnifiedAuthResult(true, message, source, user);
        }

        public static UnifiedAuthResult Failed(string message, AuthSource source = AuthSource.Local, Exception? exception = null)
        {
            return new UnifiedAuthResult(false, message, source, null, exception);
        }

        public static UnifiedAuthResult FromLocalResult(LocalAuthResult localResult, AuthSource source)
        {
            return new UnifiedAuthResult(localResult.IsSuccess, localResult.Message, source, null, localResult.Exception);
        }

        public static UnifiedAuthResult FromCloudResult(CloudAuthResult cloudResult, AuthSource source)
        {
            return new UnifiedAuthResult(cloudResult.IsSuccess, cloudResult.Message, source, null, cloudResult.Exception);
        }
    }

    #endregion
}
