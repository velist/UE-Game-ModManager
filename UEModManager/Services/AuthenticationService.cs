using System;
using System.Threading.Tasks;
using Supabase;
using Supabase.Gotrue;
using Microsoft.Extensions.Logging;
using System.Net.Http;

using System.Text.Json;
using System.Diagnostics;
namespace UEModManager.Services
{
    public class AuthenticationService
    {
        private readonly Supabase.Client _supabaseClient;
        private readonly ILogger<AuthenticationService> _logger;
        private static readonly HttpClient _http = new HttpClient();
        private const string WorkerApiBase = "https://api.modmanger.com";
        private bool _isInitialized = false;
        
        // 演示模式状态
        private bool _demoModeLoggedIn = false;
        private string? _demoUserEmail = null;
        private bool _isDemoMode = false;

        public AuthenticationService(Supabase.Client supabaseClient, ILogger<AuthenticationService> logger)
        {
            _supabaseClient = supabaseClient;
            _logger = logger;
            CheckAndLoadConfiguration();
        }

        private void CheckAndLoadConfiguration()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configFile = System.IO.Path.Combine(appDataPath, "UEModManager", "supabase_config.json");
                
                if (System.IO.File.Exists(configFile))
                {
                    var json = System.IO.File.ReadAllText(configFile);
                    dynamic config = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    _isDemoMode = config?.demoMode ?? true;
                    
                    if (_isDemoMode)
                    {
                        _logger.LogInformation("认证服务运行在演示模式");
                    }
                    else
                    {
                        _logger.LogInformation("认证服务运行在云端模式");
                    }
                }
                else
                {
                    // 默认使用演示模式
                    _isDemoMode = true;
                    _logger.LogInformation("未找到配置文件，使用演示模式");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载配置失败，使用演示模式");
                _isDemoMode = true;
            }
        }

        private async Task<bool> EnsureInitializedAsync()
        {
            if (_isInitialized) return true;

            try
            {
                await _supabaseClient.InitializeAsync();
                _isInitialized = true;
                _logger.LogInformation("Supabase客户端初始化成功");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supabase客户端初始化失败");
                return false;
            }
        }

        public User? CurrentUser => _isInitialized ? _supabaseClient.Auth.CurrentUser : null;
        
        public bool IsLoggedIn 
        { 
            get 
            {
                if (IsInDemoMode()) 
                    return _demoModeLoggedIn;
                
                // 如果还未初始化，检查是否有保存的会话
                if (!_isInitialized)
                {
                    try
                    {
                        var sessionHandler = new SupabaseSessionHandler();
                        var savedSession = sessionHandler.LoadSession();
                        return savedSession != null;
                    }
                    catch
                    {
                        return false;
                    }
                }
                
                return CurrentUser != null;
            }
        }

        public event EventHandler<AuthEventArgs>? AuthStateChanged;

        public async Task<AuthenticationResult> SignUpAsync(string email, string password, string? username = null)
        {
            try
            {
                // 演示模式：模拟注册成功
                if (IsInDemoMode())
                {
                    await Task.Delay(1000); // 模拟网络延迟
                    _logger.LogInformation($"[演示模式] 用户注册成功: {email}");
                    OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedUp, null));
                    return AuthenticationResult.Success("注册成功！(演示模式)");
                }

                // 确保Supabase客户端已初始化
                if (!await EnsureInitializedAsync())
                {
                    return AuthenticationResult.Failed("网络连接失败，请检查网络设置");
                }

                var options = new SignUpOptions();
                if (!string.IsNullOrEmpty(username))
                {
                    options.Data = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["username"] = username
                    };
                }

                var session = await _supabaseClient.Auth.SignUp(email, password, options);
                
                if (session?.User != null)
                {
                    _logger.LogInformation($"用户注册成功: {email}");
                    OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedUp, session));
                    return AuthenticationResult.Success("注册成功，请查看邮箱进行验证");
                }
                
                return AuthenticationResult.Failed("注册失败：未知错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"注册失败: {email}");
                return AuthenticationResult.Failed($"注册失败：{ex.Message}");
            }
        }

        public async Task<AuthenticationResult> SignInAsync(string email, string password)
        {
            try
            {
                // 演示模式：模拟登录成功
                if (IsInDemoMode())
                {
                    await Task.Delay(1000); // 模拟网络延迟
                    
                    // 设置演示模式登录状态
                    _demoModeLoggedIn = true;
                    _demoUserEmail = email;
                    
                    _logger.LogInformation($"[演示模式] 用户登录成功: {email}");
                    OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedIn, null));
                    return AuthenticationResult.Success("登录成功！(演示模式)");
                }

                // 确保Supabase客户端已初始化
                if (!await EnsureInitializedAsync())
                {
                    return AuthenticationResult.Failed("网络连接失败，请检查网络设置");
                }

                var session = await _supabaseClient.Auth.SignIn(email, password);
                
                if (session?.User != null)
                {
                    _logger.LogInformation($"用户登录成功: {email}");
                    OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedIn, session));
                    return AuthenticationResult.Success("登录成功");
                }
                
                return AuthenticationResult.Failed("登录失败：邮箱或密码错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"登录失败: {email}");
                return AuthenticationResult.Failed($"登录失败：{ex.Message}");
            }
        }

        public async Task<bool> SignOutAsync()
        {
            try
            {
                if (IsInDemoMode())
                {
                    // 清除演示模式状态
                    _demoModeLoggedIn = false;
                    _demoUserEmail = null;
                    
                    _logger.LogInformation("[演示模式] 用户登出成功");
                    OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedOut, null));
                    return true;
                }

                // 确保Supabase客户端已初始化
                if (!await EnsureInitializedAsync())
                {
                    _logger.LogWarning("无法初始化Supabase客户端进行登出");
                    return false;
                }

                await _supabaseClient.Auth.SignOut();
                _logger.LogInformation("用户登出成功");
                OnAuthStateChanged(new AuthEventArgs(AuthChangeEventType.SignedOut, null));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "登出失败");
                return false;
            }
        }
        public async Task<AuthenticationResult> ResetPasswordAsync(string email)
        {
            try
            {
                // Cloudflare Workers URL for password reset
                var redirectTo = "https://api.modmanger.com/reset-password";
                var payload = JsonSerializer.Serialize(new { email, redirect_to = redirectTo });
                using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var requestUri = $"{WorkerApiBase}/auth/reset";

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                var resp = await _http.PostAsync(requestUri, content, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    try
                    {
                        using var jdoc = JsonDocument.Parse(text);
                        var root = jdoc.RootElement;
                        var channelUsed = root.TryGetProperty("channelUsed", out var ch) ? ch.GetString() : "";

                        // 处理 link_only 模式（邮件发送失败，但链接已生成）
                        if (channelUsed == "link_only")
                        {
                            if (root.TryGetProperty("data", out var dataElem) &&
                                dataElem.TryGetProperty("link", out var linkElem))
                            {
                                var link = linkElem.GetString();
                                if (!string.IsNullOrWhiteSpace(link))
                                {
                                    try
                                    {
                                        var psi = new ProcessStartInfo(link!) { UseShellExecute = true };
                                        Process.Start(psi);
                                        _logger.LogInformation($"[ForgotPassword] 已打开重置链接: {link}");
                                        return AuthenticationResult.Success("已打开重置页面，请在浏览器中完成密码重置");
                                    }
                                    catch (Exception openEx)
                                    {
                                        _logger.LogWarning(openEx, $"[ForgotPassword] 打开浏览器失败，链接: {link}");
                                        // 打开浏览器失败，返回链接让用户手动复制
                                        return AuthenticationResult.Success($"无法自动打开浏览器，请手动复制此链接重置密码：\n\n{link}");
                                    }
                                }
                            }
                        }
                        // 处理 brevo 模式（邮件发送成功）或 supabase 模式
                        else if (channelUsed == "brevo" || channelUsed == "supabase")
                        {
                            _logger.LogInformation($"[ForgotPassword] 已通过 {channelUsed} 发送重置邮件: {email}");
                            return AuthenticationResult.Success("重置邮件已发送，请检查您的邮箱");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogWarning(parseEx, $"[ForgotPassword] 解析响应失败: {text}");
                    }

                    // 默认成功消息（兜底）
                    _logger.LogInformation($"[ForgotPassword] 密码重置请求已处理: {email}");
                    return AuthenticationResult.Success("密码重置请求已发送，请检查您的邮箱或浏览器");
                }
                else
                {
                    var msg = string.IsNullOrWhiteSpace(text) ? $"HTTP {(int)resp.StatusCode}" : text;
                    _logger.LogWarning($"[ForgotPassword] 网关返回非成功: {(int)resp.StatusCode} - {msg}");
                    return AuthenticationResult.Failed($"密码重置失败：{msg}\n\n提示：您可以先使用验证码登录，然后在设置中修改密码");
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "[ForgotPassword] 请求超时");
                return AuthenticationResult.Failed("请求超时，请稍后重试");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ForgotPassword] 发送失败: {email}");
                return AuthenticationResult.Failed($"发送失败：{ex.Message}");
            }
        }
public async Task<bool> UpdateUserProfileAsync(string? username = null, string? fullName = null)
        {
            try
            {
                // 确保Supabase客户端已初始化
                if (!await EnsureInitializedAsync())
                {
                    return false;
                }

                if (CurrentUser == null) return false;

                var attributes = new UserAttributes();
                var userData = new System.Collections.Generic.Dictionary<string, object>();
                
                if (!string.IsNullOrEmpty(username))
                    userData.Add("username", username);
                    
                if (!string.IsNullOrEmpty(fullName))
                    userData.Add("full_name", fullName);

                if (userData.Count > 0)
                {
                    attributes.Data = userData;
                    var user = await _supabaseClient.Auth.Update(attributes);
                    _logger.LogInformation("用户资料更新成功");
                    return user != null;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户资料失败");
                return false;
            }
        }

        public string GetUserDisplayName()
        {
            if (IsInDemoMode())
            {
                return _demoUserEmail ?? "演示用户";
            }

            if (CurrentUser?.UserMetadata == null) return "未知用户";
            
            if (CurrentUser.UserMetadata.ContainsKey("username"))
                return CurrentUser.UserMetadata["username"]?.ToString() ?? CurrentUser.Email ?? "未知用户";
                
            if (CurrentUser.UserMetadata.ContainsKey("full_name"))
                return CurrentUser.UserMetadata["full_name"]?.ToString() ?? CurrentUser.Email ?? "未知用户";
                
            return CurrentUser.Email ?? "未知用户";
        }

        private bool IsInDemoMode()
        {
            // 使用配置文件中的设置
            return _isDemoMode;
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
        {
            return new AuthenticationResult(true, message);
        }

        public static AuthenticationResult Failed(string message, Exception? exception = null)
        {
            return new AuthenticationResult(false, message, exception);
        }
    }

    public class AuthEventArgs : EventArgs
    {
        public AuthChangeEventType EventType { get; }
        public Session? Session { get; }

        public AuthEventArgs(AuthChangeEventType eventType, Session? session)
        {
            EventType = eventType;
            Session = session;
        }
    }
}