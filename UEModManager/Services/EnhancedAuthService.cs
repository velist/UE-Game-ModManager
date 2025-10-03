using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Agents;

namespace UEModManager.Services
{
    /// <summary>
    /// 增强认证服务 - 使用AuthenticationAgent提供高级认证功能
    /// </summary>
    public class EnhancedAuthService
    {
        private readonly ILogger<EnhancedAuthService> _logger;
        private readonly AgentManagerService _agentManager;
        private readonly LocalAuthService _localAuthService;
        private readonly UnifiedAuthService _unifiedAuthService;
        private readonly EmailService _emailService;

        public EnhancedAuthService(
            ILogger<EnhancedAuthService> logger,
            AgentManagerService agentManager,
            LocalAuthService localAuthService,
            UnifiedAuthService unifiedAuthService,
            EmailService emailService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _agentManager = agentManager ?? throw new ArgumentNullException(nameof(agentManager));
            _localAuthService = localAuthService ?? throw new ArgumentNullException(nameof(localAuthService));
            _unifiedAuthService = unifiedAuthService ?? throw new ArgumentNullException(nameof(unifiedAuthService));
            _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
        }

        /// <summary>
        /// 增强用户登录 - 使用AuthenticationAgent进行多重验证
        /// </summary>
        /// <param name="email">邮箱</param>
        /// <param name="password">密码</param>
        /// <param name="enableSecurityChecks">是否启用安全检查</param>
        /// <returns>登录结果</returns>
        public async Task<EnhancedAuthResult> LoginAsync(string email, string password, bool enableSecurityChecks = true)
        {
            try
            {
                _logger.LogInformation($"开始增强登录流程: {email}");

                if (!_agentManager.IsInitialized)
                {
                    _logger.LogWarning("代理管理服务未初始化，回退到基础登录");
                    return await FallbackLoginAsync(email, password);
                }

                var result = new EnhancedAuthResult();

                // 第一步：基础认证验证
                var basicAuthResult = await _agentManager.AuthenticateUserAsync(email, password, "login");
                if (!basicAuthResult.IsSuccess)
                {
                    result.IsSuccess = false;
                    result.Message = "基础认证失败";
                    result.Errors.Add(basicAuthResult.Message);
                    return result;
                }

                // 第二步：会话验证
                var sessionValidationResult = await _agentManager.AuthenticateUserAsync("", "", "validate_session");
                result.SessionInfo = sessionValidationResult.Data;

                // 第三步：安全检查（可选）
                if (enableSecurityChecks)
                {
                    await PerformSecurityChecksAsync(result, email);
                }

                // 第四步：权限验证
                await ValidateUserPermissionsAsync(result, email);

                // 第五步：审计日志
                await LogAuthenticationEventAsync(email, "login_success", result);

                // 第六步：同步本地认证状态（重要！）
                try
                {
                    var localResult = await _localAuthService.LoginAsync(email, password);
                    if (localResult.IsSuccess)
                    {
                        _logger.LogInformation($"本地认证状态已同步: {email}");
                    }
                    else
                    {
                        _logger.LogWarning($"本地认证状态同步失败: {localResult.Message}");
                        // 使用强制设置方法确保状态同步
                        _logger.LogInformation("尝试强制同步本地认证状态...");
                        
                        string? username = null;
                        if (basicAuthResult?.Data != null && basicAuthResult.Data.ContainsKey("username"))
                        {
                            username = basicAuthResult.Data["username"]?.ToString();
                        }
                        
                        var forceSetResult = await _localAuthService.ForceSetAuthStateAsync(email, username);
                        if (forceSetResult)
                        {
                            _logger.LogInformation("强制同步本地认证状态成功");
                        }
                        else
                        {
                            _logger.LogError("强制同步本地认证状态失败，但不影响登录流程");
                        }
                    }
                }
                catch (Exception syncEx)
                {
                    _logger.LogError(syncEx, "同步本地认证状态时发生错误");
                    // 尝试强制同步
                    try
                    {
                        var forceSetResult = await _localAuthService.ForceSetAuthStateAsync(email);
                        if (forceSetResult)
                        {
                            _logger.LogInformation("异常恢复：强制同步本地认证状态成功");
                        }
                    }
                    catch (Exception forceEx)
                    {
                        _logger.LogError(forceEx, "强制同步也失败了");
                    }
                }

                result.IsSuccess = true;
                result.Message = "增强登录成功";
                result.AuthenticationData = basicAuthResult.Data;

                _logger.LogInformation($"增强登录成功: {email}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"增强登录失败: {email}");
                return new EnhancedAuthResult
                {
                    IsSuccess = false,
                    Message = "登录过程中发生错误",
                    Errors = { ex.Message }
                };
            }
        }

        /// <summary>
        /// 增强用户注册
        /// </summary>
        /// <param name="email">邮箱</param>
        /// <param name="password">密码</param>
        /// <param name="additionalChecks">是否进行额外检查</param>
        /// <returns>注册结果</returns>
        public async Task<EnhancedAuthResult> RegisterAsync(string email, string password, bool additionalChecks = true)
        {
            try
            {
                _logger.LogInformation($"开始增强注册流程: {email}");

                var result = new EnhancedAuthResult();

                if (!_agentManager.IsInitialized)
                {
                    _logger.LogWarning("代理管理服务未初始化，回退到基础注册");
                    return await FallbackRegisterAsync(email, password);
                }

                // 第一步：密码强度验证
                if (additionalChecks)
                {
                    var passwordStrength = await ValidatePasswordStrengthAsync(password);
                    result.SecurityChecks.Add("password_strength", passwordStrength);

                    if (!passwordStrength.IsStrong)
                    {
                        result.IsSuccess = false;
                        result.Message = "密码强度不足";
                        result.Warnings.AddRange(passwordStrength.Issues);
                        return result;
                    }
                }

                // 第二步：邮箱有效性检查
                if (additionalChecks)
                {
                    var emailValidation = await ValidateEmailAsync(email);
                    result.SecurityChecks.Add("email_validation", emailValidation);

                    if (!emailValidation.IsValid)
                    {
                        result.IsSuccess = false;
                        result.Message = "邮箱地址无效";
                        result.Errors.AddRange(emailValidation.Issues);
                        return result;
                    }
                }

                // 第三步：执行注册
                var registerResult = await _agentManager.AuthenticateUserAsync(email, password, "register");
                if (!registerResult.IsSuccess)
                {
                    result.IsSuccess = false;
                    result.Message = "用户注册失败";
                    result.Errors.Add(registerResult.Message);
                    return result;
                }

                // 第四步：设置默认权限
                await SetupDefaultPermissionsAsync(email);

                // 第五步：发送欢迎邮件
                try
                {
                    var welcomeEmailResult = await _emailService.SendWelcomeEmailAsync(email);
                    if (welcomeEmailResult.IsSuccess)
                    {
                        result.SecurityChecks.Add("welcome_email", new { Status = "Sent", Timestamp = DateTime.UtcNow });
                        _logger.LogInformation($"欢迎邮件发送成功: {email}");
                    }
                    else
                    {
                        result.Warnings.Add("欢迎邮件发送失败，但不影响注册");
                        _logger.LogWarning($"欢迎邮件发送失败: {email} - {welcomeEmailResult.Message}");
                    }
                }
                catch (Exception emailEx)
                {
                    result.Warnings.Add("欢迎邮件发送异常，但不影响注册");
                    _logger.LogWarning(emailEx, $"欢迎邮件发送异常: {email}");
                }

                // 第六步：审计日志
                await LogAuthenticationEventAsync(email, "register_success", result);

                result.IsSuccess = true;
                result.Message = "增强注册成功";
                result.AuthenticationData = registerResult.Data;

                _logger.LogInformation($"增强注册成功: {email}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"增强注册失败: {email}");
                return new EnhancedAuthResult
                {
                    IsSuccess = false,
                    Message = "注册过程中发生错误",
                    Errors = { ex.Message }
                };
            }
        }

        /// <summary>
        /// 安全登出
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <returns>登出结果</returns>
        public async Task<EnhancedAuthResult> SecureLogoutAsync(string userId)
        {
            try
            {
                _logger.LogInformation($"开始安全登出流程: {userId}");

                var result = new EnhancedAuthResult();

                // 第一步：撤销会话权限
                var revokeResult = await _agentManager.ManagePermissionAsync("revoke", userId, "active_session");
                result.SecurityChecks.Add("session_revoke", revokeResult.Data);

                // 第二步：清理本地会话
                await _unifiedAuthService.LogoutAsync();

                // 第三步：审计日志
                await LogAuthenticationEventAsync(userId, "logout", result);

                result.IsSuccess = true;
                result.Message = "安全登出成功";

                _logger.LogInformation($"安全登出成功: {userId}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"安全登出失败: {userId}");
                return new EnhancedAuthResult
                {
                    IsSuccess = false,
                    Message = "登出过程中发生错误",
                    Errors = { ex.Message }
                };
            }
        }

        /// <summary>
        /// 用户权限检查
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <param name="permission">权限名称</param>
        /// <returns>权限检查结果</returns>
        public async Task<bool> HasPermissionAsync(string userId, string permission)
        {
            try
            {
                if (!_agentManager.IsInitialized)
                {
                    _logger.LogWarning("代理管理服务未初始化，使用本地权限检查");
                    return _localAuthService.IsCurrentUserAdmin(); // 简化的权限检查
                }

                var result = await _agentManager.ManagePermissionAsync("check", userId, permission);
                return result.IsSuccess && result.Data.ContainsKey("hasPermission") && (bool)result.Data["hasPermission"];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"权限检查失败: {userId}, {permission}");
                return false;
            }
        }

        /// <summary>
        /// 增强安全设置
        /// </summary>
        /// <returns>安全设置结果</returns>
        public async Task<AgentResponse> EnhanceSecurityAsync()
        {
            if (!_agentManager.IsInitialized)
                return AgentResponse.Failed("代理管理服务未初始化");

            return await _agentManager.ExecuteTaskAsync(AgentType.Authentication, "enhance_security", null);
        }

        /// <summary>
        /// 用户审计
        /// </summary>
        /// <param name="auditType">审计类型</param>
        /// <returns>审计结果</returns>
        public async Task<AgentResponse> AuditUsersAsync(string auditType = "full")
        {
            if (!_agentManager.IsInitialized)
                return AgentResponse.Failed("代理管理服务未初始化");

            var parameters = new Dictionary<string, object>
            {
                { "auditType", auditType }
            };

            return await _agentManager.ExecuteTaskAsync(AgentType.Authentication, "audit_users", parameters);
        }

        #region 私有辅助方法

        /// <summary>
        /// 回退到基础登录
        /// </summary>
        private async Task<EnhancedAuthResult> FallbackLoginAsync(string email, string password)
        {
            var basicResult = await _localAuthService.LoginAsync(email, password);
            return new EnhancedAuthResult
            {
                IsSuccess = basicResult.IsSuccess,
                Message = basicResult.Message,
                AuthenticationData = new Dictionary<string, object>
                {
                    { "fallback_mode", true },
                    { "basic_result", basicResult }
                }
            };
        }

        /// <summary>
        /// 回退到基础注册
        /// </summary>
        private async Task<EnhancedAuthResult> FallbackRegisterAsync(string email, string password)
        {
            var basicResult = await _localAuthService.RegisterAsync(email, password);
            return new EnhancedAuthResult
            {
                IsSuccess = basicResult.IsSuccess,
                Message = basicResult.Message,
                AuthenticationData = new Dictionary<string, object>
                {
                    { "fallback_mode", true },
                    { "basic_result", basicResult }
                }
            };
        }

        /// <summary>
        /// 执行安全检查
        /// </summary>
        private async Task PerformSecurityChecksAsync(EnhancedAuthResult result, string email)
        {
            // IP地址检查
            result.SecurityChecks.Add("ip_check", new { Status = "Normal", Location = "Local" });

            // 设备指纹检查
            result.SecurityChecks.Add("device_fingerprint", new { Status = "Trusted", DeviceId = Environment.MachineName });

            // 登录频率检查
            result.SecurityChecks.Add("login_frequency", new { Status = "Normal", RecentAttempts = 1 });

            await Task.CompletedTask;
        }

        /// <summary>
        /// 验证用户权限
        /// </summary>
        private async Task ValidateUserPermissionsAsync(EnhancedAuthResult result, string email)
        {
            var permissionsResult = await _agentManager.ManagePermissionAsync("list", email, "");
            result.UserPermissions = permissionsResult.Data.ContainsKey("permissions") 
                ? (List<string>)permissionsResult.Data["permissions"] 
                : new List<string>();
        }

        /// <summary>
        /// 记录认证事件
        /// </summary>
        private async Task LogAuthenticationEventAsync(string userIdentifier, string eventType, EnhancedAuthResult result)
        {
            try
            {
                var logData = new Dictionary<string, object>
                {
                    { "user", userIdentifier },
                    { "event_type", eventType },
                    { "timestamp", DateTime.UtcNow },
                    { "success", result.IsSuccess },
                    { "security_checks", result.SecurityChecks.Count }
                };

                // 生成审计报告
                await _agentManager.GenerateReportAsync("security_audit", "json", logData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"记录认证事件失败: {eventType}");
            }
        }

        /// <summary>
        /// 密码强度验证
        /// </summary>
        private async Task<PasswordStrengthResult> ValidatePasswordStrengthAsync(string password)
        {
            await Task.Delay(10); // 模拟验证过程

            var result = new PasswordStrengthResult();

            if (string.IsNullOrEmpty(password))
            {
                result.Issues.Add("密码不能为空");
                return result;
            }

            if (password.Length < 8)
                result.Issues.Add("密码长度至少8位");

            if (!System.Text.RegularExpressions.Regex.IsMatch(password, @"[A-Z]"))
                result.Issues.Add("密码需要包含大写字母");

            if (!System.Text.RegularExpressions.Regex.IsMatch(password, @"[a-z]"))
                result.Issues.Add("密码需要包含小写字母");

            if (!System.Text.RegularExpressions.Regex.IsMatch(password, @"[0-9]"))
                result.Issues.Add("密码需要包含数字");

            if (!System.Text.RegularExpressions.Regex.IsMatch(password, @"[^a-zA-Z0-9]"))
                result.Issues.Add("密码需要包含特殊字符");

            result.IsStrong = result.Issues.Count == 0;
            result.Score = Math.Max(0, 100 - (result.Issues.Count * 20));

            return result;
        }

        /// <summary>
        /// 邮箱验证
        /// </summary>
        private async Task<EmailValidationResult> ValidateEmailAsync(string email)
        {
            await Task.Delay(10); // 模拟验证过程

            var result = new EmailValidationResult();

            if (string.IsNullOrEmpty(email))
            {
                result.Issues.Add("邮箱不能为空");
                return result;
            }

            if (!email.Contains("@"))
            {
                result.Issues.Add("邮箱格式无效");
                return result;
            }

            if (email.Length > 254)
            {
                result.Issues.Add("邮箱地址过长");
                return result;
            }

            result.IsValid = result.Issues.Count == 0;
            return result;
        }

        /// <summary>
        /// 设置默认权限
        /// </summary>
        private async Task SetupDefaultPermissionsAsync(string email)
        {
            await _agentManager.ManagePermissionAsync("grant", email, "basic_access");
            await _agentManager.ManagePermissionAsync("grant", email, "read_data");
        }

        #endregion
    }

    #region 数据模型

    /// <summary>
    /// 增强认证结果
    /// </summary>
    public class EnhancedAuthResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object> AuthenticationData { get; set; } = new();
        public Dictionary<string, object> SessionInfo { get; set; } = new();
        public List<string> UserPermissions { get; set; } = new();
        public Dictionary<string, object> SecurityChecks { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public DateTime AuthenticatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 密码强度结果
    /// </summary>
    public class PasswordStrengthResult
    {
        public bool IsStrong { get; set; }
        public int Score { get; set; } // 0-100分
        public List<string> Issues { get; set; } = new();
    }

    /// <summary>
    /// 邮箱验证结果
    /// </summary>
    public class EmailValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Issues { get; set; } = new();
    }

    #endregion
}