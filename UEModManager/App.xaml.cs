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
using UEModManager.Services.Backends;
using UEModManager.ViewModels;
using UEModManager.Views;
using UEModManager.Data;
using UEModManager.Infrastructure;

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

        /// <summary>
        /// 进程级致命异常：继续运行只会让状态更坏，一律不拦截，让 WPF 走默认崩溃并落转储。
        /// （StackOverflowException 无法被托管代码捕获，列出仅作说明。）
        /// </summary>
        private static bool IsUnrecoverable(Exception ex)
            => ex is OutOfMemoryException
                or StackOverflowException
                or System.Runtime.InteropServices.SEHException
                or AccessViolationException;

        /// <summary>同一时刻只允许一个致命错误对话框，避免异常风暴把用户淹没在弹窗里。</summary>
        private bool _isShowingFatalDialog;

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try { Console.WriteLine($"[FATAL] Dispatcher Unhandled: {e.Exception}"); } catch { }

            if (IsUnrecoverable(e.Exception))
            {
                // 不设 Handled：让进程崩溃，保留可分析的转储
                return;
            }

            // UI 线程的异常绝大多数来自 async void 事件处理器。此前这里无条件 Handled=true
            // 且只写日志，结果是"点了没反应，日志里也没有"——必须让用户看见失败。
            if (!_isShowingFatalDialog)
            {
                _isShowingFatalDialog = true;
                try
                {
                    MessageBox.Show(
                        $"操作失败：{e.Exception.Message}\n\n" +
                        $"详细信息已写入日志：\n{_logFilePath}",
                        "UEModManager 发生错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch { /* 弹窗本身失败时不能再抛，否则递归 */ }
                finally { _isShowingFatalDialog = false; }
            }

            e.Handled = true;
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
                // 日志随数据一起移出安装目录：装在 Program Files 下时那里没有写权限，
                // 且卸载/覆盖安装会连同日志一起抹掉——出问题时最需要的证据反而最先消失。
                var logDir = AppPaths.LogsDirectory;
                if (!AppPaths.TryEnsureDirectory(logDir))
                {
                    // 新位置不可用时退回安装目录，有日志总比没有强。
                    logDir = AppDomain.CurrentDomain.BaseDirectory;
                }

                _logFilePath = System.IO.Path.Combine(logDir, "console.log");
                // 轮转旧日志
                if (System.IO.File.Exists(_logFilePath))
                {
                    var bak = System.IO.Path.Combine(logDir, $"console_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    System.IO.File.Move(_logFilePath, bak, true);
                }
                PruneRotatedLogs(logDir);
                var sw = new StreamWriter(System.IO.File.Open(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                var structured = new UEModManager.Logging.StructuredLogWriter(sw);
                Console.SetOut(structured);
                Console.SetError(structured);
                Console.WriteLine($"[App] 文件日志重定向 -> {_logFilePath}");
            }
            catch { /* 如果失败，不阻断启动 */ }
        }

        /// <summary>
        /// 只保留最近 <see cref="MaxRotatedLogs"/> 份轮转日志。
        /// 此前每次启动都新增一份且从不清理，长期运行的机器上会攒出成百上千个文件。
        /// </summary>
        private static void PruneRotatedLogs(string logDir)
        {
            const int MaxRotatedLogs = 10;
            try
            {
                var stale = new DirectoryInfo(logDir)
                    .EnumerateFiles("console_*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(MaxRotatedLogs);

                foreach (var file in stale)
                {
                    try { file.Delete(); } catch { /* 被占用就留到下次 */ }
                }
            }
            catch { /* 清理是尽力而为，绝不能挡住日志初始化 */ }
        }

        /// <summary>
        /// 退出清理。必须是同步的：WPF 不会 await 派生的 OnExit，
        /// async void 版本会在第一个 await 处让出，随后 _host.Dispose() 与 base.OnExit()
        /// 能否执行取决于与进程终止的竞速——SQLite 上下文可能不被确定性释放、
        /// 日志 writer 可能来不及 flush。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                try { Console.WriteLine($"[App] 停止 Host 失败: {ex}"); } catch { }
            }
            finally
            {
                _host?.Dispose();
                try { Console.Out.Flush(); } catch { }
                base.OnExit(e);
            }
        }

        private IHostBuilder CreateHostBuilder()
        {
            AppPaths.TryEnsureDirectory(AppPaths.DataDirectory);

            return Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // 注册本地SQLite数据库
                    services.AddDbContext<LocalDbContext>(options =>
                    {
                        var dbPath = AppPaths.LocalDatabaseFile;
                        AppPaths.TryEnsureDirectory(Path.GetDirectoryName(dbPath)!);
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

                    // 注册本地缓存服务
                    services.AddScoped<LocalCacheService>();


                    // 注册邮件发送服务（Brevo API + SMTP 双通道）
                    RegisterEmailServices(services);

                    // 注册自定义OTP服务（使用MailerSend/Brevo发送验证码，内存存储）
                    services.AddSingleton<CustomOtpService>();

                    // 注册新服务层（Phase 1/3 产物）
                    services.AddSingleton<GameConfigService>();
                    services.AddSingleton<NewCategoryService>();
                    services.AddSingleton<ModDataService>();
                    services.AddSingleton<ProfileService>();

                    // v2.0 Phase 2: 包仓库服务
                    services.AddSingleton<ObjectStore>();
                    services.AddSingleton<PackageRepository>();
                    // 仓库孤儿对象的事后回收。PackageImportService 注入它，在每次压缩包导入前
                    // 回收上一次进程被强杀留下的解压临时目录。
                    services.AddSingleton<RepositoryReclaimService>();
                    services.AddSingleton<PackageImportService>();
                    services.AddSingleton<DataMigrationService>();

                    // v2.0 Phase 3: 部署服务
                    //
                    // 部署后端按接口注册，由 DI 汇集成 IEnumerable<IDeploymentBackend>
                    // 注入 DeploymentService。新增一种部署方式只需在此加一行，
                    // 不必再改 DeploymentService 的构造函数签名。
                    // 同一 DeploymentBackendType 若注册多次，后注册的覆盖先注册的，
                    // 因此自定义实现放在内置实现之后即可替换内置行为。
                    // 注意：后端仍需编译进本项目，没有插件式动态加载。
                    services.AddSingleton<IDeploymentBackend, CopyBackend>();
                    services.AddSingleton<IDeploymentBackend, HardLinkBackend>();
                    services.AddSingleton<DeploymentPlanner>();
                    services.AddSingleton<DeploymentService>();

                    // v2.0 Phase 4: 冲突治理
                    services.AddSingleton<ConflictAnalyzer>();

                    // v2.0 Phase 5: 生成物管理
                    services.AddSingleton<OverwriteStore>();

                    // v2.0 Phase 7: 配置合并
                    services.AddSingleton<Services.Config.ConfigMergeEngine>();

                    // v2.0 Phase 8: 最终视图
                    services.AddSingleton<ResolvedViewBuilder>();

                    // v2.0 Phase 9: 启动编排
                    services.AddSingleton<LaunchOrchestrator>();

                    // Phase 11: 诊断包导出
                    services.AddSingleton<DiagnosticExportService>();

                    // Phase 11: 崩溃恢复
                    services.AddSingleton<CrashRecoveryService>();

                    // Phase 11: 健康检查
                    services.AddSingleton<HealthCheckService>();

                    // Phase 12: Profile lock 导出/导入
                    services.AddSingleton<ProfileLockService>();

                    // 数据目录搬迁（安装目录 → %LOCALAPPDATA%）
                    services.AddSingleton<DataLocationMigrator>();

                    // 首次运行时引导用户挑一个包仓库位置（默认在系统盘，而仓库可能几十 GB）
                    services.AddSingleton<RepositorySetupService>();

                    // 换一个位置存 MOD：先搬数据、搬成了才改存放位置。
                    // 全项目唯一允许改仓库位置的地方（守卫测试钉住）。
                    //
                    // ObjectStore 用工厂而不是直接注入：启动期的断电恢复要解析本服务，
                    // 而解析时若把 ObjectStore 一并构造出来，它就会把"恢复之前"的存放位置
                    // 记进字段——恢复紧接着把位置改到新位置，用户这一整次会话却仍看着旧的空仓库。
                    services.AddSingleton<RepositoryRelocationService>(sp =>
                        new RepositoryRelocationService(
                            sp.GetRequiredService<ILogger<RepositoryRelocationService>>(),
                            sp.GetRequiredService<ObjectStore>));

                    services.AddTransient<ViewModels.MainViewModel>();
                    // 注册窗口
                    services.AddTransient<MainWindow>();
                    services.AddTransient<LoginWindow>();
                    services.AddTransient<Views.ProfileManagerWindow>();

                    // v2.0 Phase 2-4 UI 窗口
                    services.AddTransient<Views.ImportDialog>();
                    services.AddTransient<Views.ImportConfirmDialog>();
                    services.AddTransient<Views.DeployPreviewDialog>();
                    services.AddTransient<Views.RepositoryManagerWindow>();
                    services.AddTransient<Views.OverwriteManagerWindow>();
                    services.AddTransient<Views.LaunchCenterWindow>();
                    services.AddTransient<Views.ConfigManagerWindow>();
                    services.AddTransient<Views.ManagementCenterWindow>();
                    // MigrationWizardWindow 需要 gameName 参数，由调用方手动创建

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

                // 数据目录搬迁：必须在任何服务读写数据之前完成。
                // 迁移器自身不抛异常——失败时沿用旧位置继续，绝不阻断启动。
                //
                // 结果留在 DataLocationMigrator.LastOutcome 上供 UI 取用：迁移失败时数据
                // 分处新旧两地、每次启动都在重试，而用户界面上一点痕迹都没有，只能靠翻日志
                // 才发现——这正是 355589a 那轮修掉的"UI 静默失败通道"。
                //
                // TODO(UI)：主窗口初始化时读
                //     ServiceProvider.GetRequiredService<DataLocationMigrator>().LastOutcome
                // 若 outcome?.ShouldNotifyUser == true，用 outcome.UserMessage 弹一条
                // **非模态**提示（现有的 Snackbar/状态栏通道即可，绝不能用 MessageBox 拦人）。
                // 判据必须是 ShouldNotifyUser，不能是 Completed —— 搬移开关关着时每台老用户
                // 机器都有推迟项、Completed 恒为 false，照它提示等于给全体用户天天报一次警。
                Console.WriteLine("[Startup] DataLocationMigrator");
                try
                {
                    var migrator = ServiceProvider.GetRequiredService<DataLocationMigrator>();
                    var outcome = await migrator.RunAsync();
                    Console.WriteLine($"[Startup] 数据目录迁移: {outcome.Status}｜{outcome.Summary}");
                    if (outcome.ShouldNotifyUser)
                    {
                        Console.WriteLine($"[Startup] 数据目录迁移需提示用户: {outcome.UserMessage}");
                    }
                }
                catch (Exception migEx)
                {
                    // 只有解析服务本身失败才会走到这里（RunAsync 自己不抛）。
                    // 此时 LastOutcome 仍是 null，UI 侧按"没有可提示的状态"处理即可。
                    Console.WriteLine($"[Startup] 数据目录迁移异常（沿用旧位置继续）: {migEx}");
                }

                // 上次被中断（断电/强杀/崩溃）的仓库搬移，在这里自愈。
                // 位置的两头与下面那个首次运行引导完全相同，理由也相同：
                //   上界 —— 搬迁器的原地登记会写存放位置；
                //   下界 —— ObjectStore 构造时读一次存放位置就记进字段，晚一步的话
                //           恢复推过去的新位置这次会话根本不生效，用户看到的是一个空仓库。
                // 排在引导之前是必须的：引导判定"老用户"最主要的两条判据是
                // "配置里有没有仓库位置"和"当前仓库里有没有包"，而一次被中断的搬移
                // 恰好会让这两条都读成 false —— 于是一个 MOD 正躺在半搬完状态的老用户
                // 会被弹窗问"MOD 放哪个盘"。
                RecoverInterruptedRepositoryRelocation();

                // 首次运行的仓库位置引导。位置必须夹在这两件事之间，两头都是硬约束：
                //
                // 上界（搬迁器之后）：搬迁器的"原地登记"会把老用户的旧仓库位置写进配置，
                //   而"配置里已有仓库位置"正是 RepositorySetupPrompt 判定老用户最主要的一条判据。
                //   抢在它前面判，装满 MOD 的老用户会被读成"未配置"，于是被弹窗问一次
                //   ——他很可能会认真挑一个大盘，而引导只改指针不搬数据，他的 MOD 当场"消失"。
                //
                // 下界（ObjectStore 首次解析之前）：ObjectStore 是 DI 单例，在构造时读一次
                //   AppPaths.RepositoryRoot 并记进字段，之后本次会话不再回头看配置。
                //   引导只写偏好、刻意不去碰 ObjectStore —— 解析它就等于把它构造出来，
                //   反而会把"第一次读配置"的时机提前到引导内部。所以这里必须早于任何会拖出
                //   ObjectStore 的解析（下面第一处是 LocalDbContext，真正拖出它的是 MainWindow）。
                //
                // 两条约束都有源码守卫测试钉住（StartupSequenceGuardTests）。
                ShowRepositorySetupIfNeeded();

                // 初始化本地数据库
                Console.WriteLine("[Auth] Resolve LocalDbContext");
                var localDbContext = ServiceProvider.GetRequiredService<LocalDbContext>();
                Console.WriteLine("[Auth] EnsureDatabaseCreatedAsync");
                var databaseReady = await localDbContext.EnsureDatabaseCreatedAsync();
                if (!databaseReady)
                {
                    var choice = MessageBox.Show(
                        "本地数据库初始化失败。可以以离线只读方式继续，但账号登录、会话恢复和本地资料保存可能不可用。\n\n是否继续？",
                        "数据库初始化失败",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (choice == MessageBoxResult.Yes)
                    {
                        Console.WriteLine("[Auth] Database init failed, continue in offline read-only mode");
                        ShowMainWindow();
                    }
                    else
                    {
                        Console.WriteLine("[Auth] Database init failed, user chose exit");
                        Shutdown();
                    }
                    return;
                }

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
                try { Console.WriteLine("[Auth] SetAuthMode -> OfflineOnly"); await unifiedAuthService.SetAuthModeAsync(UEModManager.Services.UnifiedAuthService.AuthMode.OfflineOnly); } catch (Exception smEx) { Console.WriteLine($"[Auth] SetAuthMode failed: {smEx}"); }

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

        /// <summary>
        /// 收拾上次被中断的仓库搬移。
        ///
        /// <para>
        /// 判定与执行都在 <see cref="RepositoryRelocationService.RecoverInterrupted"/> 里，
        /// 它自己已经兜住全部异常；这里再包一层只为防住"解析服务本身失败"。
        /// 恢复失败绝不能阻断启动——什么都不做是安全的（两边的数据此刻至少有一份是完整的），
        /// 下次启动还会再判一次。
        /// </para>
        ///
        /// <para>
        /// 与引导一样<b>刻意不解析 ObjectStore</b>：解析它就等于把它构造出来，
        /// 正好把"第一次读存放位置"的时机提前到恢复内部，反手制造出这段代码要防的问题。
        /// </para>
        /// </summary>
        private void RecoverInterruptedRepositoryRelocation()
        {
            try
            {
                var service = ServiceProvider!.GetRequiredService<RepositoryRelocationService>();
                var plan = service.RecoverInterrupted();
                Console.WriteLine($"[Startup] 仓库搬移恢复: {plan}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] 仓库搬移恢复失败（沿用当前位置继续）: {ex}");
            }
        }

        /// <summary>
        /// 首次运行时问一句"MOD 存哪个盘"。判定与对话框都不允许影响启动：
        /// 判定本身不抛（<see cref="RepositorySetupService.Decide"/> 内部兜住），
        /// 这里再包一层是为了防住"弹窗构造/显示失败"这类 UI 侧异常——
        /// 一个可选的引导把用户挡在主界面之外是完全不成比例的代价。
        /// </summary>
        private void ShowRepositorySetupIfNeeded()
        {
            try
            {
                var setup = ServiceProvider!.GetRequiredService<RepositorySetupService>();
                var decision = setup.Decide();
                Console.WriteLine($"[Startup] 仓库位置引导: {decision}");
                if (!decision.ShouldPrompt) return;

                var logger = ServiceProvider.GetService<ILogger<Views.RepositorySetupWindow>>();
                new Views.RepositorySetupWindow(setup, logger).ShowDialog();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] 仓库位置引导失败（沿用默认位置继续）: {ex}");
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
        /// 注册邮件发送服务（Worker 代理为主通道，Brevo 直连为兜底）
        /// </summary>
        private static void RegisterEmailServices(IServiceCollection services)
        {
            // 加载Brevo配置（开发期本地放 brevo.env 仍可作为兜底；分发包不再内置 key）
            var brevoConfig = LoadBrevoConfig();

            // 主通道：通过 Cloudflare Worker (api.modmanger.com) 代理调 Brevo
            // 客户端不持有任何 API Key，所有 secrets 保留在 Worker 端
            services.AddSingleton<WorkerEmailService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<WorkerEmailService>>();
                return new WorkerEmailService(logger, "https://api.modmanger.com");
            });

            // 注册 Brevo API 服务（兜底通道，仅当 brevoConfig.ApiKey 非空时实际生效）
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

            // 注册 Brevo SMTP 服务（最后兜底）
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

            // 注册故障切换服务：Worker 优先 → Brevo API 兜底 → Brevo SMTP 最后兜底
            services.AddSingleton<FallbackEmailService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<FallbackEmailService>>();
                var senders = new List<IEmailSender>
                {
                    provider.GetRequiredService<WorkerEmailService>(),    // 主：Worker 代理
                    provider.GetRequiredService<BrevoApiEmailService>(),  // 兜底1：Brevo API 直连
                    provider.GetRequiredService<BrevoEmailService>()      // 兜底2：Brevo SMTP
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
                    var preview = IsSensitiveEnvKey(key)
                        ? "<redacted>"
                        : $"{value.Substring(0, Math.Min(10, value.Length))}...";
                    Console.WriteLine($"[App] ParseEnv: {key}={preview}");
                }
            }
            return result;
        }

        private static bool IsSensitiveEnvKey(string key)
        {
            return key.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase);
        }

} 





}



