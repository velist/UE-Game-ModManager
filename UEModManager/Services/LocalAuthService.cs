using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UEModManager.Data;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 本地认证服务
    /// </summary>
    public class LocalAuthService
    {
        private readonly LocalDbContext _dbContext;
        private readonly ILogger<LocalAuthService> _logger;
        private LocalUser? _currentUser;
        private UserSession? _currentSession;
        
        // 安全配置常量
        private const int MAX_FAILED_ATTEMPTS = 5;
        private const int LOCKOUT_DURATION_MINUTES = 30;
        private const int MIN_PASSWORD_LENGTH = 8;
        private const int SESSION_TIMEOUT_DAYS = 30;

        public LocalAuthService(LocalDbContext dbContext, ILogger<LocalAuthService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public LocalUser? CurrentUser => _currentUser;
        public bool IsLoggedIn => _currentUser != null && _currentSession?.IsActive == true;

        public event EventHandler<LocalAuthEventArgs>? AuthStateChanged;

        /// <summary>
        /// 用户注册
        /// </summary>
        public async Task<LocalAuthResult> RegisterAsync(string email, string password, string? username = null)
        {
            try
            {
                // 检查邮箱是否已存在
                var existingUser = await _dbContext.Users
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());
                
                if (existingUser != null)
                {
                    return LocalAuthResult.Failed("该邮箱已被注册");
                }

                // 验证密码强度
                // 验证密码强度
                var passwordValidation = ValidatePasswordStrength(password);
                if (!passwordValidation.IsValid)
                {
                    return LocalAuthResult.Failed($"密码不符合安全要求: {passwordValidation.Message}");
                }

                // 创建新用户
                var user = new LocalUser
                {
                    Email = email.ToLower(),
                    Username = username ?? email.Split('@')[0],
                    PasswordHash = HashPassword(password),
                    CreatedAt = DateTime.Now,
                    LastLoginAt = DateTime.Now,
                    IsActive = true
                };

                _dbContext.Users.Add(user);
                await _dbContext.SaveChangesAsync();

                // 创建默认偏好设置
                var preferences = new UserPreferences
                {
                    UserId = user.Id,
                    User = user,
                    Language = "zh-CN",
                    Theme = "Dark",
                    AutoCheckUpdates = true,
                    AutoBackup = true,
                    ShowNotifications = true,
                    EnableCloudSync = false
                };

                _dbContext.UserPreferences.Add(preferences);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation($"用户注册成功: {email}");
                
                // 自动登录
                var loginResult = await LoginAsync(email, password);
                return loginResult.IsSuccess 
                    ? LocalAuthResult.Success("注册成功并已自动登录") 
                    : LocalAuthResult.Success("注册成功，请登录");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"用户注册失败: {email}");
                return LocalAuthResult.Failed($"注册失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 用户登录
        /// </summary>
        public async Task<LocalAuthResult> LoginAsync(string email, string password)
        {
            try
            {
                var user = await _dbContext.Users
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower() && u.IsActive);

                if (user == null)
                {
                    // 记录失败尝试（即使用户不存在，也要记录IP）
                    await RecordFailedLoginAttemptAsync(email);
                    return LocalAuthResult.Failed("邮箱或密码错误");
                }

                // 检查账户是否被锁定
                if (await IsAccountLockedAsync(user))
                {
                    var lockoutEndTime = await GetLockoutEndTimeAsync(user);
                    var remainingMinutes = (int)(lockoutEndTime - DateTime.Now).TotalMinutes;
                    return LocalAuthResult.Failed($"账户已被锁定，请在 {remainingMinutes} 分钟后重试");
                }

                if (!VerifyPassword(password, user.PasswordHash))
                {
                    await RecordFailedLoginAttemptAsync(email, user.Id);
                    var remainingAttempts = await GetRemainingAttemptsAsync(user);
                    
                    if (remainingAttempts <= 0)
                    {
                        await LockAccountAsync(user);
                        return LocalAuthResult.Failed($"密码错误次数过多，账户已被锁定 {LOCKOUT_DURATION_MINUTES} 分钟");
                    }
                    
                    return LocalAuthResult.Failed($"邮箱或密码错误，还有 {remainingAttempts} 次尝试机会");
                }

                // 登录成功，清除失败记录
                await ClearFailedLoginAttemptsAsync(user);

                // 清理过期会话
                await CleanupExpiredSessionsAsync(user.Id);

                // 创建新会话
                var session = new UserSession
                {
                    UserId = user.Id,
                    User = user,
                    SessionToken = GenerateSessionToken(),
                    CreatedAt = DateTime.Now,
                    ExpiresAt = DateTime.Now.AddDays(SESSION_TIMEOUT_DAYS),
                    LastAccessAt = DateTime.Now,
                    IsActive = true,
                    DeviceInfo = GetDeviceFingerprint()
                };

                _dbContext.UserSessions.Add(session);

                // 更新最后登录时间
                user.LastLoginAt = DateTime.Now;
                await _dbContext.SaveChangesAsync();

                // 设置当前用户和会话
                _currentUser = user;
                _currentSession = session;

                _logger.LogInformation($"用户登录成功: {email}");
                OnAuthStateChanged(new LocalAuthEventArgs(LocalAuthEventType.SignedIn, user));

                return LocalAuthResult.Success("登录成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"用户登录失败: {email}");
                return LocalAuthResult.Failed($"登录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 用户登出
        /// </summary>
        public async Task<bool> LogoutAsync()
        {
            try
            {
                if (_currentSession != null)
                {
                    _currentSession.IsActive = false;
                    await _dbContext.SaveChangesAsync();
                }

                var user = _currentUser;
                _currentUser = null;
                _currentSession = null;

                // 清除记住我令牌，避免再次自动登录到上一个账号
                try { await ClearRememberMeTokenAsync(); } catch { }

                _logger.LogInformation($"用户登出: {user?.Email}");
                OnAuthStateChanged(new LocalAuthEventArgs(LocalAuthEventType.SignedOut, null));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户登出失败");
                return false;
            }
        }

        /// <summary>
        /// 恢复会话（应用启动时调用）
        /// </summary>
        public async Task<bool> RestoreSessionAsync()
        {
            try
            {
                // 查找最新的活跃会话
                var session = await _dbContext.UserSessions
                    .Include(s => s.User)
                    .Where(s => s.IsActive && s.ExpiresAt > DateTime.Now)
                    .OrderByDescending(s => s.LastAccessAt)
                    .FirstOrDefaultAsync();

                if (session?.User != null)
                {
                    _currentUser = session.User;
                    _currentSession = session;

                    // 更新最后访问时间
                    session.LastAccessAt = DateTime.Now;
                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation($"会话恢复成功: {session.User.Email}");
                    OnAuthStateChanged(new LocalAuthEventArgs(LocalAuthEventType.SessionRestored, session.User));
                    
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "会话恢复失败");
                return false;
            }
        }

        /// <summary>
        /// 更改密码
        /// </summary>
        public async Task<LocalAuthResult> ChangePasswordAsync(string currentPassword, string newPassword)
        {
            try
            {
                if (_currentUser == null)
                    return LocalAuthResult.Failed("用户未登录");

                if (!VerifyPassword(currentPassword, _currentUser.PasswordHash))
                    return LocalAuthResult.Failed("当前密码错误");

                var passwordValidation = ValidatePasswordStrength(newPassword);
                if (!passwordValidation.IsValid)
                    return LocalAuthResult.Failed($"新密码不符合安全要求: {passwordValidation.Message}");

                _currentUser.PasswordHash = HashPassword(newPassword);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation($"用户密码修改成功: {_currentUser.Email}");
                return LocalAuthResult.Success("密码修改成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "修改密码失败");
                return LocalAuthResult.Failed($"修改密码失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取用户偏好设置
        /// </summary>
        public async Task<UserPreferences?> GetUserPreferencesAsync()
        {
            if (_currentUser == null) return null;

            return await _dbContext.UserPreferences
                .FirstOrDefaultAsync(p => p.UserId == _currentUser.Id);
        }

        /// <summary>
        /// 更新用户偏好设置
        /// </summary>
        public async Task<bool> UpdateUserPreferencesAsync(UserPreferences preferences)
        {
            try
            {
                if (_currentUser == null) return false;

                preferences.UserId = _currentUser.Id;
                preferences.UpdatedAt = DateTime.Now;

                _dbContext.UserPreferences.Update(preferences);
                await _dbContext.SaveChangesAsync();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户偏好设置失败");
                return false;
            }
        }

        #region 私有方法

        private static string HashPassword(string password)
        {
            // 生成随机盐值
            var salt = GenerateSalt();
            
            // 使用PBKDF2进行密码哈希
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
            var hash = pbkdf2.GetBytes(32);
            
            // 组合盐值和哈希值
            var hashBytes = new byte[48]; // 16字节盐值 + 32字节哈希值
            Array.Copy(salt, 0, hashBytes, 0, 16);
            Array.Copy(hash, 0, hashBytes, 16, 32);
            
            return Convert.ToBase64String(hashBytes);
        }

        private static bool VerifyPassword(string password, string hashedPassword)
        {
            try
            {
                var hashBytes = Convert.FromBase64String(hashedPassword);
                
                // 检查是否是新格式（PBKDF2）
                if (hashBytes.Length == 48)
                {
                    // 提取盐值和哈希值
                    var salt = new byte[16];
                    var storedHash = new byte[32];
                    Array.Copy(hashBytes, 0, salt, 0, 16);
                    Array.Copy(hashBytes, 16, storedHash, 0, 32);
                    
                    // 使用相同参数计算哈希
                    using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
                    var computedHash = pbkdf2.GetBytes(32);
                    
                    return CryptographicOperations.FixedTimeEquals(storedHash, computedHash);
                }
                else
                {
                    // 兼容旧格式（SHA256）
                    using var sha256 = SHA256.Create();
                    var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password + "UEModManager_Salt_2024"));
                    var oldHash = Convert.ToBase64String(hashedBytes);
                    return hashedPassword == oldHash;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < MIN_PASSWORD_LENGTH)
                return false;

            // 检查密码复杂度
            bool hasLower = false, hasUpper = false, hasDigit = false, hasSpecial = false;
            foreach (char c in password)
            {
                if (char.IsLower(c)) hasLower = true;
                else if (char.IsUpper(c)) hasUpper = true;
                else if (char.IsDigit(c)) hasDigit = true;
                else if (!char.IsLetterOrDigit(c)) hasSpecial = true;
            }

            // 至少满足3个条件
            int criteriaCount = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0);
            return criteriaCount >= 3;
        }

        private static string GenerateSessionToken()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private async Task CleanupExpiredSessionsAsync(int userId)
        {
            var expiredSessions = await _dbContext.UserSessions
                .Where(s => s.UserId == userId && (s.ExpiresAt < DateTime.Now || !s.IsActive))
                .ToListAsync();

            if (expiredSessions.Any())
            {
                _dbContext.UserSessions.RemoveRange(expiredSessions);
                await _dbContext.SaveChangesAsync();
            }
        }

        private void OnAuthStateChanged(LocalAuthEventArgs e)
        {
            AuthStateChanged?.Invoke(this, e);
        }

        #endregion

        #region 账户安全相关方法

        /// <summary>
        /// 记录失败的登录尝试
        /// </summary>
        private async Task RecordFailedLoginAttemptAsync(string email, int? userId = null)
        {
            try
            {
                var attempt = new FailedLoginAttempt
                {
                    Email = email,
                    UserId = userId,
                    AttemptTime = DateTime.Now,
                    IpAddress = "127.0.0.1", // 本地应用，使用占位符
                    UserAgent = Environment.OSVersion.ToString()
                };

                _dbContext.FailedLoginAttempts.Add(attempt);
                await _dbContext.SaveChangesAsync();
                
                _logger.LogWarning($"记录失败登录尝试: {email}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"记录失败登录尝试失败: {email}");
            }
        }

        /// <summary>
        /// 检查账户是否被锁定
        /// </summary>
        private async Task<bool> IsAccountLockedAsync(LocalUser user)
        {
            var lockoutEndTime = await GetLockoutEndTimeAsync(user);
            return lockoutEndTime > DateTime.Now;
        }

        /// <summary>
        /// 获取账户锁定结束时间
        /// </summary>
        private async Task<DateTime> GetLockoutEndTimeAsync(LocalUser user)
        {
            var recentAttempts = await _dbContext.FailedLoginAttempts
                .Where(a => (a.UserId == user.Id || a.Email.ToLower() == user.Email.ToLower()) && 
                           a.AttemptTime > DateTime.Now.AddHours(-24))
                .OrderByDescending(a => a.AttemptTime)
                .Take(MAX_FAILED_ATTEMPTS)
                .ToListAsync();

            if (recentAttempts.Count >= MAX_FAILED_ATTEMPTS)
            {
                var lastAttempt = recentAttempts.First();
                return lastAttempt.AttemptTime.AddMinutes(LOCKOUT_DURATION_MINUTES);
            }

            return DateTime.MinValue;
        }

        /// <summary>
        /// 获取剩余尝试次数
        /// </summary>
        private async Task<int> GetRemainingAttemptsAsync(LocalUser user)
        {
            var recentFailedCount = await _dbContext.FailedLoginAttempts
                .CountAsync(a => (a.UserId == user.Id || a.Email.ToLower() == user.Email.ToLower()) && 
                                a.AttemptTime > DateTime.Now.AddMinutes(-LOCKOUT_DURATION_MINUTES));

            return Math.Max(0, MAX_FAILED_ATTEMPTS - recentFailedCount);
        }

        /// <summary>
        /// 锁定账户
        /// </summary>
        private async Task LockAccountAsync(LocalUser user)
        {
            try
            {
                // 记录锁定事件
                _logger.LogWarning($"账户被锁定: {user.Email}, 锁定时长: {LOCKOUT_DURATION_MINUTES} 分钟");
                
                // 这里可以添加额外的锁定逻辑，比如发送通知等
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"锁定账户时发生错误: {user.Email}");
            }
        }

        /// <summary>
        /// 清除失败登录尝试记录
        /// </summary>
        private async Task ClearFailedLoginAttemptsAsync(LocalUser user)
        {
            try
            {
                var attempts = await _dbContext.FailedLoginAttempts
                    .Where(a => a.UserId == user.Id || a.Email.ToLower() == user.Email.ToLower())
                    .ToListAsync();

                if (attempts.Any())
                {
                    _dbContext.FailedLoginAttempts.RemoveRange(attempts);
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"清除失败登录记录时发生错误: {user.Email}");
            }
        }

        /// <summary>
        /// 生成盐值
        /// </summary>
        private static byte[] GenerateSalt()
        {
            var salt = new byte[16];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(salt);
            return salt;
        }

        /// <summary>
        /// 获取设备指纹
        /// </summary>
        private static string GetDeviceFingerprint()
        {
            try
            {
                var machineInfo = $"{Environment.MachineName}_{Environment.UserName}_{Environment.OSVersion}";
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(machineInfo));
                return Convert.ToBase64String(hash)[..16]; // 取前16个字符作为指纹
            }
            catch
            {
                return Environment.MachineName;
            }
        }

        /// <summary>
        /// 清理过期的失败登录记录
        /// </summary>
        public async Task CleanupExpiredFailedAttemptsAsync()
        {
            try
            {
                var expiredAttempts = await _dbContext.FailedLoginAttempts
                    .Where(a => a.AttemptTime < DateTime.Now.AddDays(-7)) // 保留7天记录
                    .ToListAsync();

                if (expiredAttempts.Any())
                {
                    _dbContext.FailedLoginAttempts.RemoveRange(expiredAttempts);
                    await _dbContext.SaveChangesAsync();
                    _logger.LogInformation($"清理了 {expiredAttempts.Count} 条过期的失败登录记录");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理过期失败登录记录时发生错误");
            }
        }

        /// <summary>
        /// 验证密码强度
        /// </summary>
        public static PasswordValidationResult ValidatePasswordStrength(string password)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(password))
            {
                return new PasswordValidationResult(false, "密码不能为空");
            }

            if (password.Length < MIN_PASSWORD_LENGTH)
            {
                errors.Add($"密码长度至少需要{MIN_PASSWORD_LENGTH}位");
            }

            bool hasLower = password.Any(char.IsLower);
            bool hasUpper = password.Any(char.IsUpper);
            bool hasDigit = password.Any(char.IsDigit);
            bool hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));

            var criteriaCount = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0);

            if (criteriaCount < 3)
            {
                var missing = new List<string>();
                if (!hasLower) missing.Add("小写字母");
                if (!hasUpper) missing.Add("大写字母");
                if (!hasDigit) missing.Add("数字");
                if (!hasSpecial) missing.Add("特殊字符");

                errors.Add($"密码需要包含以下至少3种类型的字符：小写字母、大写字母、数字、特殊字符。当前缺少：{string.Join("、", missing)}");
            }

            // 检查常见弱密码模式
            if (HasCommonWeakPatterns(password))
            {
                errors.Add("密码不能包含常见的弱密码模式（如连续数字、重复字符等）");
            }

            return errors.Any() 
                ? new PasswordValidationResult(false, string.Join("; ", errors))
                : new PasswordValidationResult(true, "密码强度良好");
        }

        /// <summary>
        /// 检查常见弱密码模式
        /// </summary>
        private static bool HasCommonWeakPatterns(string password)
        {
            // 检查连续重复字符（超过2个）
            for (int i = 0; i < password.Length - 2; i++)
            {
                if (password[i] == password[i + 1] && password[i + 1] == password[i + 2])
                {
                    return true;
                }
            }

            // 检查连续数字或字母
            var consecutive = new[] { "123", "234", "345", "456", "567", "678", "789", "890",
                                     "abc", "bcd", "cde", "def", "efg", "fgh", "ghi", "hij", "ijk", "jkl", "klm", "lmn", "mno", "nop", "opq", "pqr", "qrs", "rst", "stu", "tuv", "uvw", "vwx", "wxy", "xyz" };
            
            var lowerPassword = password.ToLower();
            return consecutive.Any(pattern => lowerPassword.Contains(pattern));
        }

        #endregion

        /// <summary>
        /// 保存记住我令牌（安全版本）
        /// </summary>
        public async Task<bool> SaveRememberMeTokenAsync(string email, bool rememberMe)
        {
            try
            {
                if (!rememberMe)
                {
                    await ClearRememberMeTokenAsync();
                    return true;
                }

                // 生成安全的记住我令牌
                var token = GenerateRememberMeToken();
                var encryptedToken = EncryptToken(token);
                
                // 保存到安全的配置文件
                var tokenData = new
                {
                    Email = email,
                    Token = encryptedToken,
                    ExpiresAt = DateTime.Now.AddDays(30), // 30天有效期
                    CreatedAt = DateTime.Now,
                    DeviceFingerprint = GetDeviceFingerprint()
                };

                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configDir = Path.Combine(appDataPath, "UEModManager", ".secure");
                Directory.CreateDirectory(configDir);

                var tokenFile = Path.Combine(configDir, "remember.dat");
                var json = System.Text.Json.JsonSerializer.Serialize(tokenData);
                
                // 使用文件加密保存
                await File.WriteAllTextAsync(tokenFile, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
                
                _logger.LogInformation($"记住我令牌已保存: {email}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存记住我令牌失败");
                return false;
            }
        }

        /// <summary>
        /// 验证记住我令牌并自动登录
        /// </summary>
        public async Task<LocalAuthResult> ValidateRememberMeTokenAsync()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var tokenFile = Path.Combine(appDataPath, "UEModManager", ".secure", "remember.dat");

                if (!File.Exists(tokenFile))
                    return LocalAuthResult.Failed("未找到记住我令牌");

                var encryptedData = await File.ReadAllTextAsync(tokenFile);
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(encryptedData));
                
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var email = root.GetProperty("Email").GetString();
                var encryptedToken = root.GetProperty("Token").GetString();
                var expiresAt = root.GetProperty("ExpiresAt").GetDateTime();
                var deviceFingerprint = root.GetProperty("DeviceFingerprint").GetString();

                if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(encryptedToken))
                {
                    await ClearRememberMeTokenAsync();
                    return LocalAuthResult.Failed("令牌数据无效");
                }

                // 检查令牌是否过期
                if (expiresAt <= DateTime.Now)
                {
                    await ClearRememberMeTokenAsync();
                    return LocalAuthResult.Failed("记住我令牌已过期");
                }

                // 检查设备指纹
                if (deviceFingerprint != GetDeviceFingerprint())
                {
                    await ClearRememberMeTokenAsync();
                    _logger.LogWarning($"设备指纹不匹配，清除记住我令牌: {email}");
                    return LocalAuthResult.Failed("设备验证失败");
                }

                // 验证用户是否存在且活跃
                var user = await _dbContext.Users
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower() && u.IsActive);

                if (user == null)
                {
                    await ClearRememberMeTokenAsync();
                    return LocalAuthResult.Failed("用户账户不存在或已禁用");
                }

                // 解密并验证令牌
                try
                {
                    var decryptedToken = DecryptToken(encryptedToken);
                    if (string.IsNullOrEmpty(decryptedToken))
                    {
                        await ClearRememberMeTokenAsync();
                        return LocalAuthResult.Failed("令牌解密失败");
                    }
                }
                catch
                {
                    await ClearRememberMeTokenAsync();
                    return LocalAuthResult.Failed("令牌验证失败");
                }

                // 创建自动登录会话
                await CleanupExpiredSessionsAsync(user.Id);
                var session = new UserSession
                {
                    UserId = user.Id,
                    User = user,
                    SessionToken = GenerateSessionToken(),
                    CreatedAt = DateTime.Now,
                    ExpiresAt = DateTime.Now.AddDays(SESSION_TIMEOUT_DAYS),
                    LastAccessAt = DateTime.Now,
                    IsActive = true,
                    DeviceInfo = GetDeviceFingerprint()
                };

                _dbContext.UserSessions.Add(session);
                user.LastLoginAt = DateTime.Now;
                await _dbContext.SaveChangesAsync();

                _currentUser = user;
                _currentSession = session;

                _logger.LogInformation($"记住我自动登录成功: {email}");
                OnAuthStateChanged(new LocalAuthEventArgs(LocalAuthEventType.SessionRestored, user));

                return LocalAuthResult.Success("自动登录成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证记住我令牌失败");
                await ClearRememberMeTokenAsync();
                return LocalAuthResult.Failed("自动登录失败");
            }
        }

        /// <summary>
        /// 清除记住我令牌
        /// </summary>
        public async Task<bool> ClearRememberMeTokenAsync()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var tokenFile = Path.Combine(appDataPath, "UEModManager", ".secure", "remember.dat");

                if (File.Exists(tokenFile))
                {
                    File.Delete(tokenFile);
                    _logger.LogInformation("记住我令牌已清除");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清除记住我令牌失败");
                return false;
            }
        }

        /// <summary>
        /// 生成记住我令牌
        /// </summary>
        private static string GenerateRememberMeToken()
        {
            var tokenData = new
            {
                Random = GenerateSessionToken(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Machine = Environment.MachineName,
                User = Environment.UserName
            };

            var json = System.Text.Json.JsonSerializer.Serialize(tokenData);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>
        /// 加密令牌
        /// </summary>
        private static string EncryptToken(string token)
        {
            try
            {
                var key = GetEncryptionKey();
                using var aes = Aes.Create();
                aes.Key = key;
                aes.GenerateIV();

                using var encryptor = aes.CreateEncryptor();
                var tokenBytes = Encoding.UTF8.GetBytes(token);
                var encrypted = encryptor.TransformFinalBlock(tokenBytes, 0, tokenBytes.Length);

                // 组合IV和加密数据
                var result = new byte[aes.IV.Length + encrypted.Length];
                Array.Copy(aes.IV, 0, result, 0, aes.IV.Length);
                Array.Copy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

                return Convert.ToBase64String(result);
            }
            catch
            {
                throw new InvalidOperationException("令牌加密失败");
            }
        }

        /// <summary>
        /// 解密令牌
        /// </summary>
        private static string DecryptToken(string encryptedToken)
        {
            try
            {
                var key = GetEncryptionKey();
                var data = Convert.FromBase64String(encryptedToken);

                using var aes = Aes.Create();
                aes.Key = key;

                // 分离IV和加密数据
                var iv = new byte[16];
                var encrypted = new byte[data.Length - 16];
                Array.Copy(data, 0, iv, 0, 16);
                Array.Copy(data, 16, encrypted, 0, encrypted.Length);

                aes.IV = iv;
                using var decryptor = aes.CreateDecryptor();
                var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                throw new InvalidOperationException("令牌解密失败");
            }
        }

        /// <summary>
        /// 获取加密密钥（基于机器和用户信息）
        /// </summary>
        private static byte[] GetEncryptionKey()
        {
            var keySource = $"UEModManager_{Environment.MachineName}_{Environment.UserName}_SecureKey_2024";
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(keySource));
        }

        /// <summary>
        /// 重置密码（通过邮箱验证后直接设置新密码）
        /// </summary>
        public async Task<LocalAuthResult> ResetPasswordAsync(string email, string newPassword)
        {
            try
            {
                var user = await FindUserByEmailAsync(email);
                if (user == null)
                {
                    return LocalAuthResult.Failed("用户不存在");
                }

                // 验证密码强度
                var passwordValidation = ValidatePasswordStrength(newPassword);
                if (!passwordValidation.IsValid)
                {
                    return LocalAuthResult.Failed($"密码不符合安全要求: {passwordValidation.Message}");
                }

                // 更新密码
                user.PasswordHash = HashPassword(newPassword);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation($"密码重置成功: {email}");
                return LocalAuthResult.Success("密码重置成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"密码重置失败: {email}");
                return LocalAuthResult.Failed($"密码重置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重置密码（本地版本：显示提示信息 - 保留兼容性）
        /// </summary>
        public Task<LocalAuthResult> ResetPasswordAsync(string email)
        {
            // 本地SQLite版本暂不支持邮件重置，返回提示信息
            return Task.FromResult(LocalAuthResult.Failed("本地版本暂不支持邮件重置密码功能，请联系管理员重置或重新注册"));
        }

        /// <summary>
        /// 根据邮箱查找用户
        /// </summary>
        public async Task<LocalUser?> FindUserByEmailAsync(string email)
        {
            try
            {
                return await _dbContext.Users
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"查找用户失败: {email}");
                return null;
            }
        }

        /// <summary>
        /// 检查用户是否存在（用于智能登录/注册判断）
        /// </summary>
        public async Task<bool> UserExistsAsync(string email)
        {
            try
            {
                return await _dbContext.Users
                    .AnyAsync(u => u.Email.ToLower() == email.ToLower());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"检查用户是否存在失败: {email}");
                return false;
            }
        }

        /// <summary>
        /// 更新用户信息
        /// </summary>
        public async Task<bool> UpdateUserAsync(LocalUser user)
        {
            try
            {
                _dbContext.Users.Update(user);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation($"用户信息更新成功: {user.Email}");

                // 如果更新的是当前登录用户，触发状态变化事件以更新UI
                if (_currentUser != null && _currentUser.Id == user.Id)
                {
                    _currentUser = user;
                    OnAuthStateChanged(new LocalAuthEventArgs(LocalAuthEventType.UserUpdated, _currentUser));
                    _logger.LogInformation($"已触发用户状态更新事件: {user.Email}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"更新用户信息失败: {user.Email}");
                return false;
            }
        }

        #region 管理功能

        /// <summary>
        /// 获取用户总数
        /// </summary>
        public async Task<int> GetTotalUsersCountAsync()
        {
            try
            {
                return await _dbContext.Users.CountAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户总数失败");
                return 0;
            }
        }

        /// <summary>
        /// 获取活跃用户数（30天内登录过的）
        /// </summary>
        public async Task<int> GetActiveUsersCountAsync()
        {
            try
            {
                // 修复：使用本地时间保持与用户登录时间记录的一致性
                var thirtyDaysAgo = DateTime.Now.AddDays(-30);
                return await _dbContext.Users
                    .Where(u => u.LastLoginAt >= thirtyDaysAgo)
                    .CountAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取活跃用户数失败");
                return 0;
            }
        }

        /// <summary>
        /// 获取所有用户列表
        /// </summary>
        public async Task<IEnumerable<LocalUser>> GetAllUsersAsync()
        {
            try
            {
                return await _dbContext.Users
                    .OrderByDescending(u => u.CreatedAt)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户列表失败");
                return new List<LocalUser>();
            }
        }

        /// <summary>
        /// 确保默认管理员账户存在
        /// </summary>
        public async Task<bool> EnsureDefaultAdminAsync()
        {
            try
            {
                // 检查是否已有管理员
                var existingAdmin = await _dbContext.Users
                    .FirstOrDefaultAsync(u => u.IsAdmin);

                if (existingAdmin == null)
                {
                    // 创建默认管理员账户
                    var admin = new LocalUser
                    {
                        Email = "admin@uemodmanager.com",
                        Username = "Administrator",
                        PasswordHash = HashPassword("Admin@123456"),
                        CreatedAt = DateTime.Now,
                        LastLoginAt = DateTime.Now,
                        IsActive = true,
                        IsAdmin = true
                    };

                    _dbContext.Users.Add(admin);
                    
                    // 创建默认偏好设置
                    var preferences = new UserPreferences
                    {
                        UserId = admin.Id,
                        User = admin,
                        Language = "zh-CN",
                        Theme = "Dark",
                        AutoCheckUpdates = true,
                        AutoBackup = true,
                        ShowNotifications = true,
                        EnableCloudSync = false
                    };

                    _dbContext.UserPreferences.Add(preferences);
                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation("默认管理员账户创建成功: admin@uemodmanager.com");
                    return true;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建默认管理员账户失败");
                return false;
            }
        }

        /// <summary>
        /// 检查当前用户是否为管理员
        /// </summary>
        public bool IsCurrentUserAdmin()
        {
            return _currentUser?.IsAdmin == true;
        }

        /// <summary>
        /// 强制设置登录状态（供其他认证服务同步状态使用）
        /// </summary>
        public async Task<bool> ForceSetAuthStateAsync(string email, string? username = null)
        {
            try
            {
                // 查找或创建用户
                var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user == null)
                {
                    // 如果用户不存在，创建一个基础用户记录
                    user = new LocalUser
                    {
                        Email = email,
                        Username = username ?? email.Split('@')[0],
                        DisplayName = username ?? email.Split('@')[0],
                        IsActive = true,
                        CreatedAt = DateTime.Now,
                        LastLoginAt = DateTime.Now,
                        // 不设置密码哈希，因为这是通过其他认证服务验证的
                        PasswordHash = ""
                    };
                    _dbContext.Users.Add(user);
                    await _dbContext.SaveChangesAsync();
                    _logger.LogInformation($"为云端认证用户创建本地用户记录: {email}");
                }

                // 创建会话
                var session = new UserSession
                {
                    UserId = user.Id,
                    User = user,
                    SessionToken = GenerateSessionToken(),
                    CreatedAt = DateTime.Now,
                    ExpiresAt = DateTime.Now.AddDays(30),
                    LastAccessAt = DateTime.Now,
                    IsActive = true
                };

                _dbContext.UserSessions.Add(session);
                await _dbContext.SaveChangesAsync();

                // 设置内部状态
                _currentUser = user;
                _currentSession = session;

                _logger.LogInformation($"强制设置用户登录状态成功: {email}");
                OnAuthStateChanged(new LocalAuthEventArgs(LocalAuthEventType.SignedIn, user));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"强制设置登录状态失败: {email}");
                return false;
            }
        }

        /// 获取当前用户的个性签名（存储在 AppConfiguration，键=UserSignature.{UserId}）
        public async Task<string?> GetUserSignatureAsync()
        {
            try
            {
                if (_currentUser == null) return null;
                var key = $"UserSignature.{_currentUser.Id}";
                var cfg = await _dbContext.Configurations.FirstOrDefaultAsync(c => c.Key == key);
                return cfg?.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取用户签名失败");
                return null;
            }
        }

        /// 设置当前用户的个性签名（仅本地，不上云）
        public async Task<bool> SetUserSignatureAsync(string? signature)
        {
            try
            {
                if (_currentUser == null) return false;
                var key = $"UserSignature.{_currentUser.Id}";
                var cfg = await _dbContext.Configurations.FirstOrDefaultAsync(c => c.Key == key);
                if (cfg == null)
                {
                    cfg = new UEModManager.Models.AppConfiguration
                    {
                        Key = key,
                        Value = signature ?? string.Empty,
                        Description = "本地用户个性签名",
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };
                    _dbContext.Configurations.Add(cfg);
                }
                else
                {
                    cfg.Value = signature ?? string.Empty;
                    cfg.UpdatedAt = DateTime.Now;
                    _dbContext.Configurations.Update(cfg);
                }
                await _dbContext.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存用户签名失败");
                return false;
            }
        }

        #endregion
    }

    #region 认证相关数据类

    public enum LocalAuthEventType
    {
        SignedIn,
        SignedOut,
        SessionRestored,
        PasswordChanged,
        UserUpdated
    }

    public class LocalAuthEventArgs : EventArgs
    {
        public LocalAuthEventType EventType { get; }
        public LocalUser? User { get; }

        public LocalAuthEventArgs(LocalAuthEventType eventType, LocalUser? user)
        {
            EventType = eventType;
            User = user;
        }
    }

    public class LocalAuthResult
    {
        public bool IsSuccess { get; private set; }
        public string Message { get; private set; }
        public Exception? Exception { get; private set; }

        private LocalAuthResult(bool isSuccess, string message, Exception? exception = null)
        {
            IsSuccess = isSuccess;
            Message = message;
            Exception = exception;
        }

        public static LocalAuthResult Success(string message = "操作成功")
        {
            return new LocalAuthResult(true, message);
        }

        public static LocalAuthResult Failed(string message, Exception? exception = null)
        {
            return new LocalAuthResult(false, message, exception);
        }
    }

    public class PasswordValidationResult
    {
        public bool IsValid { get; }
        public string Message { get; }

        public PasswordValidationResult(bool isValid, string message)
        {
            IsValid = isValid;
            Message = message;
        }
    }

    #endregion
}
