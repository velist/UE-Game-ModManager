using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using UEModManager.Services;
using UEModManager.Views;
using UEModManager.Data;

namespace UEModManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private IHost? _host;
        
        public IServiceProvider? ServiceProvider { get; private set; }

        private static string? _logFilePath;

        public App()
        {
            // 在构造阶段尽早挂接全局异常并重定向日志到文件，保证启动期崩溃可见
            try
            {
                AttachGlobalExceptionHandlers();
                SetupFileLogging();
                Console.WriteLine("[AppCtor] 应用程序构造完成，文件日志已准备");
            }
            catch { /* 忽略构造期异常，尽量不影响启动 */ }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // 再次确保异常挂接（防御重复初始化）
            AttachGlobalExceptionHandlers();

            Console.WriteLine("[App] OnStartup 开始");
            base.OnStartup(e);

            // 读取UI语言偏好并提前应用
            try
            {
                if (UEModManager.Services.UiPreferences.TryLoadEnglish(out var isEn))
                {
                    UEModManager.Services.LanguageManager.SetEnglish(isEn);
                    Console.WriteLine($"[App] 已应用UI语言偏好: {(isEn ? "EN" : "ZH")}");
                }
            }
            catch { }
            
            // 设置应用程序关闭模式为手动控制，防止窗口关闭时自动退出
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Console.WriteLine("[App] 应用程序关闭模式设置为 OnExplicitShutdown");

            // 🔒 临时禁用加密保护（v1.7.38调试）
            // try
            // {
            //     UEModManager.Security.SecretFileProtector.EnsureEncryptedFromPlain("brevo.env");
            //     UEModManager.Security.SecretFileProtector.EnsureEncryptedFromPlain("mailersend.env");
            //     Console.WriteLine("[App] 敏感配置文件加密保护已启用");
            // }
            // catch (Exception secEx)
            // {
            //     Console.WriteLine($"[App] 警告：配置文件加密失败: {secEx.Message}");
            // }

            try
            {
                Console.WriteLine("[App] 开始构建依赖注入容器");
                // 构建依赖注入容器
                _host = CreateHostBuilder().Build();
                ServiceProvider = _host.Services;

                // 启动主机
                await _host.StartAsync();

#if DEBUG
                // 在调试模式下运行本地存储测试
                try
                {
                    var logger = ServiceProvider.GetRequiredService<ILogger<App>>();
                    logger.LogInformation("开始运行本地存储系统测试...");
                    await UEModManager.Tests.LocalStorageTest.RunAllTestsAsync(ServiceProvider);
                }
                catch (Exception testEx)
                {
                    var logger = ServiceProvider.GetRequiredService<ILogger<App>>();
                    logger.LogError(testEx, "本地存储测试失败，但不影响应用程序启动");
                }
#endif

                // 显示认证窗口
                ShowAuthenticationWindow();
            }
            catch (Exception ex)
            {
                try { Console.WriteLine($"[FATAL] 应用程序启动失败: {ex}"); } catch { }
                try { MessageBox.Show($"应用程序启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
                Shutdown();
            }
        }

        private void AttachGlobalExceptionHandlers()
        {
            // 避免重复注册
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            try { Console.WriteLine($"[FATAL] AppDomain Unhandled: {((Exception)e.ExceptionObject)}"); } catch { }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try { Console.WriteLine($"[FATAL] Dispatcher Unhandled: {e.Exception}"); } catch { }
            e.Handled = true; // 阻止WPF默认崩溃
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            try { Console.WriteLine($"[FATAL] UnobservedTaskException: {e.Exception}"); } catch { }
            e.SetObserved();
        }

        private void SetupFileLogging()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _logFilePath = System.IO.Path.Combine(baseDir, "console.log");
                // 轮转旧日志
                if (System.IO.File.Exists(_logFilePath))
                {
                    var bak = System.IO.Path.Combine(baseDir, $"console_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    System.IO.File.Move(_logFilePath, bak, true);
                }
                var sw = new StreamWriter(System.IO.File.Open(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                Console.SetOut(sw);
                Console.SetError(sw);
                Console.WriteLine($"[App] 文件日志重定向 -> {_logFilePath}");
            }
            catch { /* 如果失败，不阻断启动 */ }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
            base.OnExit(e);
        }

        private IHostBuilder CreateHostBuilder()
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var dataPath = Path.Combine(appDirectory, "Data");
            Directory.CreateDirectory(dataPath);

            return Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // 注册本地SQLite数据库
                    services.AddDbContext<LocalDbContext>(options =>
                    {
                        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        var dbPath = Path.Combine(appDataPath, "UEModManager", "local.db");
                        options.UseSqlite($"Data Source={dbPath}");
                    });

                    // 注册本地认证服务
                    services.AddScoped<LocalAuthService>();

                    // 注册云端认证服务
                    services.AddScoped<CloudAuthService>();
                    services.AddSingleton(new UEModManager.Services.CloudConfig
                    {
                        ApiBaseUrl = "https://api.modmanger.com",
                        RequestTimeoutSeconds = 30,
                        MaxRetryAttempts = 3,
                        EnableDetailedLogging = true
                    });

                    // 注册统一认证服务
                    services.AddScoped<UnifiedAuthService>();

                    // 注册邮件服务
                    services.AddScoped<EmailService>();

                    // 注册默认数据库配置管理器（注入Supabase配置）
                    services.AddSingleton<DefaultDatabaseConfig>(provider =>
                    {
                        var logger = provider.GetRequiredService<ILogger<DefaultDatabaseConfig>>();
                        var pgConfig = provider.GetService<PostgreSQLConfig>();
                        return new DefaultDatabaseConfig(logger, pgConfig);
                    });

                    // 注册PostgreSQL配置 - 从supabase.env读取正确密钥
                    services.AddSingleton<PostgreSQLConfig>(provider =>
                    {
                        var supabasePassword = LoadSupabasePassword();
                        return new PostgreSQLConfig
                        {
                            Host = "db.oiatqeymovnyubrnlmlu.supabase.co",
                            Database = "postgres",
                            Username = "postgres",
                            Password = supabasePassword,
                            Port = 5432,
                            UseSsl = true,
                            ConnectionTimeout = 30,
                            Provider = DatabaseProvider.Supabase
                        };
                    });
                    
                    // 注册PostgreSQL认证服务（使用默认配置）
                    services.AddScoped<PostgreSQLAuthService>();

                    // 注册 Supabase REST 服务（使用 service key 访问 PostgREST）
                    services.AddHttpClient<SupabaseRestService>();
                    
                    // 注册本地缓存服务
                    services.AddScoped<LocalCacheService>();
                    
                    // 注册离线模式服务
                    services.AddScoped<OfflineModeService>();
                    
                    // 注册代理管理服务
                    services.AddSingleton<AgentManagerService>();
                    
                    // 注册增强认证服务
                    services.AddScoped<EnhancedAuthService>();
                    
                    // 保留Supabase服务作为备用 (未来用于云同步)
                    SupabaseConfig.ConfigureServices(services);

                    // 注册邮件发送服务（MailerSend + Brevo 双通道）
                    RegisterEmailServices(services);

                    // 注册混合OTP服务（推荐）：Supabase认证 + Brevo邮件发送
                    services.AddSingleton<HybridOtpService>();

                    // 注册自定义OTP服务（使用MailerSend/Brevo发送验证码，内存存储）
                    services.AddSingleton<CustomOtpService>();

                    // 保留 Supabase OTP 服务作为备用（仍使用Supabase邮件功能）
                    services.AddSingleton<SupabaseOtpService>();

                    // 注册窗口
                    services.AddTransient<MainWindow>();
                    services.AddTransient<LoginWindow>();
                    services.AddTransient<RegisterWindow>();
                    services.AddTransient<AuthSettingsWindow>();
                    services.AddTransient<EmailConfigWindow>();
                    services.AddTransient<AdminDashboardWindow>();

                    // 添加 HTTP 客户端（如果需要）
                    services.AddHttpClient();
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Information);
                });
        }

        private async void ShowAuthenticationWindow()
        {
            try
            {
                Console.WriteLine("[Auth] ShowAuthenticationWindow START");
                // 添加安全检查防止访问已释放的ServiceProvider
                if (ServiceProvider == null)
                {
                    MessageBox.Show("服务未正确初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown();
                    return;
                }

                // 初始化本地数据库
                Console.WriteLine("[Auth] Resolve LocalDbContext");
                var localDbContext = ServiceProvider.GetRequiredService<LocalDbContext>();
                Console.WriteLine("[Auth] EnsureDatabaseCreatedAsync");
                await localDbContext.EnsureDatabaseCreatedAsync();

                // 初始化默认管理员账户
                Console.WriteLine("[Auth] Resolve LocalAuthService");
                var localAuthService = ServiceProvider.GetRequiredService<LocalAuthService>();
                Console.WriteLine("[Auth] EnsureDefaultAdminAsync");
                await localAuthService.EnsureDefaultAdminAsync();

                // 初始化统一认证服务
                Console.WriteLine("[Auth] Resolve UnifiedAuthService");
                var unifiedAuthService = ServiceProvider.GetRequiredService<UnifiedAuthService>();
                Console.WriteLine("[Auth] UnifiedAuthService.InitializeAsync");
                await unifiedAuthService.InitializeAsync();
                // 强制切换为在线模式（Supabase 优先），避免离线导致回到旧账号
                try { Console.WriteLine("[Auth] SetAuthMode -> OnlineOnly"); await unifiedAuthService.SetAuthModeAsync(UEModManager.Services.UnifiedAuthService.AuthMode.OnlineOnly); } catch (Exception smEx) { Console.WriteLine($"[Auth] SetAuthMode failed: {smEx}"); }

                // 初始化代理管理服务
                Console.WriteLine("[Auth] Resolve AgentManagerService");
                var agentManagerService = ServiceProvider.GetRequiredService<AgentManagerService>();
                Console.WriteLine("[Auth] AgentManagerService.InitializeAsync");
                await agentManagerService.InitializeAsync();
                
                // 尝试恢复会话（自动登录）
                Console.WriteLine("[Auth] Try RestoreSession");
                var restoreResult = await unifiedAuthService.RestoreSessionAsync();
                if (restoreResult.IsSuccess)
                {
                    Console.WriteLine("[Auth] RestoreSession -> Success, show MainWindow");
                    ShowMainWindow();
                    return;
                }

                // 显示登录窗口
                Console.WriteLine("[Auth] Resolve LoginWindow");
                LoginWindow? loginWindow = null;
                try { loginWindow = ServiceProvider.GetRequiredService<LoginWindow>(); }
                catch (Exception resEx) { Console.WriteLine($"[Auth] Resolve LoginWindow failed: {resEx}"); throw; }
                Console.WriteLine("[Auth] Show LoginWindow");
                bool? result = null;
                try { result = loginWindow.ShowDialog(); }
                catch (Exception dlgEx) { Console.WriteLine($"[Auth] ShowDialog failed: {dlgEx}"); throw; }

                if (result == true)
                {
                    // 登录成功，显示主窗口
                    Console.WriteLine("[Auth] LoginWindow -> OK, ShowMainWindow");
                    ShowMainWindow();
                }
                else
                {
                    // 用户选择离线模式或关闭窗口
                    Console.WriteLine("[Auth] LoginWindow -> Cancel/Offline, ShowMainWindow");
                    ShowMainWindow();
                }
            }
            catch (ObjectDisposedException)
            {
                // ServiceProvider已被释放，直接退出
                Console.WriteLine("[Auth] ObjectDisposedException on ShowAuthenticationWindow");
                Shutdown();
            }
            catch (Exception ex)
            {
                try { Console.WriteLine($"[FATAL][Auth] ShowAuthenticationWindow failed: {ex}"); } catch { }
                MessageBox.Show($"认证窗口启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void ShowMainWindow()
        {
            try
            {
                Console.WriteLine("[App] ShowMainWindow 开始");

                // 添加安全检查防止访问已释放的ServiceProvider
                if (ServiceProvider == null)
                {
                    Console.WriteLine("[App] ServiceProvider 为空，退出应用程序");
                    MessageBox.Show("服务未正确初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown();
                    return;
                }

                Console.WriteLine("[App] 开始创建主窗口");
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                Console.WriteLine("[App] 主窗口创建完成");

                // 设置为应用程序主窗口
                MainWindow = mainWindow;
                Console.WriteLine("[App] 主窗口设置完成");

                // 显示主窗口
                mainWindow.Show();
                Console.WriteLine("[App] 主窗口显示完成");

                // 设置应用程序关闭模式为主窗口关闭时退出
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                Console.WriteLine("[App] 应用程序关闭模式已更改为 OnMainWindowClose");
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("[App] ServiceProvider 已被释放，退出应用程序");
                // ServiceProvider已被释放，直接退出
                Shutdown();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] 主窗口启动失败: {ex.Message}");
                Console.WriteLine($"[App] 异常详情: {ex}");
                MessageBox.Show($"主窗口启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        /// <summary>
        /// 注册邮件发送服务（仅Brevo API + SMTP）
        /// </summary>
        private static void RegisterEmailServices(IServiceCollection services)
        {
            // 加载Brevo配置
            var brevoConfig = LoadBrevoConfig();

            // 注册 Brevo API 服务（主通道，样式显示最佳）
            services.AddSingleton<BrevoApiEmailService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<BrevoApiEmailService>>();
                return new BrevoApiEmailService(
                    logger,
                    brevoConfig.ApiKey,
                    brevoConfig.FromEmail,
                    brevoConfig.FromName
                );
            });

            // 注册 Brevo SMTP 服务（备用通道）
            services.AddSingleton<BrevoEmailService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<BrevoEmailService>>();
                return new BrevoEmailService(
                    logger,
                    brevoConfig.SmtpLogin,
                    brevoConfig.SmtpKey,
                    brevoConfig.FromEmail,
                    brevoConfig.FromName
                );
            });

            // 注册故障切换服务（主服务）
            services.AddSingleton<FallbackEmailService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<FallbackEmailService>>();
                var senders = new List<IEmailSender>
                {
                    provider.GetRequiredService<BrevoApiEmailService>(),  // API 优先（样式最佳）
                    provider.GetRequiredService<BrevoEmailService>()      // SMTP 备用
                };
                return new FallbackEmailService(logger, senders);
            });

            // 注册IEmailSender接口（指向FallbackEmailService）
            services.AddSingleton<IEmailSender>(provider =>
                provider.GetRequiredService<FallbackEmailService>()
            );
        }

        /// <summary>
        /// 加载Brevo配置（v1.7.37工作版本）
        /// </summary>
        private static (string ApiKey, string SmtpLogin, string SmtpKey, string FromEmail, string FromName) LoadBrevoConfig()
        {
            try
            {
                // 优先：将明文迁移为加密文件（一次性）
                UEModManager.Security.SecretFileProtector.EnsureEncryptedFromPlain("brevo.env");

                // 加密文件优先
                if (UEModManager.Security.SecretFileProtector.TryLoadDecryptedText("brevo.env", out var decrypted))
                {
                    var config = ParseEnvFile(decrypted.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                    var apiKey = config.GetValueOrDefault("BREVO_API_KEY", "");
                    var smtpLogin = config.GetValueOrDefault("BREVO_SMTP_LOGIN", "");
                    var smtpKey = config.GetValueOrDefault("BREVO_SMTP_KEY", "");
                    var fromEmail = config.GetValueOrDefault("BREVO_FROM_EMAIL", "noreply@modmanger.com");
                    var fromName = config.GetValueOrDefault("BREVO_FROM_NAME", "爱酱工作室");

                    // 🔧 智能修正：当SMTP_LOGIN为"apikey"或为空时，使用FROM_EMAIL作为登录名
                    if (string.IsNullOrWhiteSpace(smtpLogin) || smtpLogin.Equals("apikey", StringComparison.OrdinalIgnoreCase))
                    {
                        // 使用Brevo分配的专用SMTP账户
                        smtpLogin = "984a39001@smtp-brevo.com"; // Brevo专用SMTP登录账户
                        Console.WriteLine($"[App] ⚠️ SMTP_LOGIN为占位符，使用Brevo专用SMTP账户: {smtpLogin}");
                    }

                    if (!string.IsNullOrWhiteSpace(apiKey) || (!string.IsNullOrWhiteSpace(smtpLogin) && !string.IsNullOrWhiteSpace(smtpKey)))
                    {
                        Console.WriteLine("[App] Brevo配置加载成功(加密): API=" + (string.IsNullOrWhiteSpace(apiKey)? "无" : "有") + ", SMTP=" + ((!string.IsNullOrWhiteSpace(smtpLogin) && !string.IsNullOrWhiteSpace(smtpKey))? "有" : "无"));
                        return (apiKey, smtpLogin, smtpKey, fromEmail, fromName);
                    }
                }

                // 兼容：若仍未命中，最后尝试明文文件（不建议长期存在）
                var plainCandidate = UEModManager.Security.SecretFileProtector.FindPlaintextCandidate("brevo.env");
                if (plainCandidate != null && File.Exists(plainCandidate))
                {
                    var lines = File.ReadAllLines(plainCandidate);
                    var config = ParseEnvFile(lines);
                    var apiKey = config.GetValueOrDefault("BREVO_API_KEY", "");
                    var smtpLogin = config.GetValueOrDefault("BREVO_SMTP_LOGIN", "");
                    var smtpKey = config.GetValueOrDefault("BREVO_SMTP_KEY", "");
                    var fromEmail = config.GetValueOrDefault("BREVO_FROM_EMAIL", "noreply@modmanger.com");
                    var fromName = config.GetValueOrDefault("BREVO_FROM_NAME", "爱酱工作室");

                    // 🔧 智能修正：当SMTP_LOGIN为"apikey"或为空时，使用FROM_EMAIL作为登录名
                    if (string.IsNullOrWhiteSpace(smtpLogin) || smtpLogin.Equals("apikey", StringComparison.OrdinalIgnoreCase))
                    {
                        // 使用Brevo分配的专用SMTP账户
                        smtpLogin = "984a39001@smtp-brevo.com"; // Brevo专用SMTP登录账户
                        Console.WriteLine($"[App] ⚠️ SMTP_LOGIN为占位符，使用Brevo专用SMTP账户: {smtpLogin}");
                    }

                    Console.WriteLine($"[App] 警告：使用明文 Brevo 配置（建议首启后已被加密迁移）: {plainCandidate} | API=" + (string.IsNullOrWhiteSpace(apiKey)? "无" : "有") + ", SMTP=" + ((!string.IsNullOrWhiteSpace(smtpLogin) && !string.IsNullOrWhiteSpace(smtpKey))? "有" : "无"));
                    return (apiKey, smtpLogin, smtpKey, fromEmail, fromName);
                }

                Console.WriteLine("[App] 警告：未找到 brevo 配置，使用占位符");
                return ("", "", "", "noreply@modmanger.com", "爱酱工作室");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] 读取 Brevo 配置失败：{ex.Message}");
                return ("", "", "", "noreply@modmanger.com", "爱酱工作室");
            }
        }

        /// <summary>
        /// 查找配置文件（当前目录 -> 向上4级）
        /// </summary>
        private static string? FindConfigFile(string fileName)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string> { Path.Combine(baseDir, fileName) };

            var dir = baseDir;
            for (int i = 0; i < 4; i++)
            {
                dir = Path.GetFullPath(Path.Combine(dir, ".."));
                candidates.Add(Path.Combine(dir, fileName));
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// 解析.env文件
        /// </summary>
        private static Dictionary<string, string> ParseEnvFile(string[] lines)
        {
            var result = new Dictionary<string, string>();
            bool isFirstLine = true;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // 🔒 自动删除UTF-8 BOM（如果存在）- 特别处理第一行
                if (isFirstLine && trimmed.Length > 0 && trimmed[0] == '\uFEFF')
                {
                    trimmed = trimmed.Substring(1);
                    Console.WriteLine("[App] 检测到BOM并移除");
                }
                isFirstLine = false;

                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    result[key] = value;
                    Console.WriteLine($"[App] ParseEnv: {key}={value.Substring(0, Math.Min(10, value.Length))}...");
                }
            }
            return result;
        }

        /// <summary>
        /// 从supabase.env文件加载Supabase密钥
        /// </summary>
        private static string LoadSupabasePassword()
        {
            try
            {
                string? foundPath = null;
                // 优先从当前目录查找
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new System.Collections.Generic.List<string>();
                candidates.Add(System.IO.Path.Combine(baseDir, "supabase.env"));
                // 向上查找最多4级（适配仓库根目录）
                var dir = baseDir;
                for (int i = 0; i < 4; i++)
                {
                    dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, ".."));
                    candidates.Add(System.IO.Path.Combine(dir, "supabase.env"));
                }
                foreach (var c in candidates)
                {
                    if (System.IO.File.Exists(c)) { foundPath = c; break; }
                }

                if (!string.IsNullOrEmpty(foundPath))
                {
                    var password = System.IO.File.ReadAllText(foundPath).Trim();
                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        Console.WriteLine($"[App] 成功从supabase.env加载Supabase密钥: {foundPath}");
                        return password;
                    }
                }

                Console.WriteLine("[App] 警告：未找到supabase.env文件或文件为空，使用默认密码");
                return "wosb68691018!";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] 读取supabase.env失败：{ex.Message}，使用默认密码");
                return "wosb68691018!";
            }
        }
} 





}



