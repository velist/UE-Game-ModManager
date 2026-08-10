using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.ViewModels;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Paths;
using UEModManager.Services.Recovery;
using UEModManager.Views;
using UEModManager.Infrastructure;

using IOPath = System.IO.Path;

namespace UEModManager
{
    public partial class MainWindow : Window
    {
        // ── CardWidth 自适应卡片宽度 ──
        public static readonly DependencyProperty CardWidthProperty =
            DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(MainWindow),
                new PropertyMetadata(265.0));

        public double CardWidth
        {
            get => (double)GetValue(CardWidthProperty);
            set => SetValue(CardWidthProperty, value);
        }

        // ── Win32 互操作 ──
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        private const int WM_GETMINMAXINFO = 0x0024;

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        // ── 服务引用 ──
        private readonly MainViewModel _vm;
        private readonly LocalAuthService? _localAuthService;
        private readonly ILogger<MainWindow>? _logger;
        private readonly GameConfigService _gameConfig;
        private readonly CrashRecoveryService? _crashRecovery;
        private readonly DataLocationMigrator? _dataMigrator;
        private readonly HealthCheckService? _healthCheck;
        private readonly DeploymentService? _deployService;

        // ── UI 状态 ──
        private DispatcherTimer? _searchDebounceTimer;
        private bool _isDragging;
        private Point _startPoint;
        private string _activeNavTag = "全部";

        /// <summary>降级提示正在显示。批量部署会连着抛 N 次告知事件，这个标志挡住后面几次。</summary>
        private bool _degradationNoticeShowing;


        // ═════════════════════════════════════════
        //  构造函数 & 初始化
        // ═════════════════════════════════════════

        public MainWindow()
        {
#if DEBUG
            AllocConsole();
#endif
            try
            {
                InitializeComponent();
                Console.WriteLine("MainWindow: InitializeComponent 完成");

                var sp = ((App)Application.Current).ServiceProvider;

                // 获取 ViewModel
                _vm = sp.GetRequiredService<MainViewModel>();
                DataContext = _vm;

                // 获取服务
                _gameConfig = sp.GetRequiredService<GameConfigService>();
                _localAuthService = sp.GetService<LocalAuthService>();
                _logger = sp.GetService<ILogger<MainWindow>>();
                _crashRecovery = sp.GetService<CrashRecoveryService>();
                _dataMigrator = sp.GetService<DataLocationMigrator>();
                _healthCheck = sp.GetService<HealthCheckService>();
                _deployService = sp.GetService<DeploymentService>();

                // 订阅认证事件
                if (_localAuthService != null)
                {
                    _localAuthService.AuthStateChanged += OnLocalAuthStateChanged;
                    UpdateUserStatusDisplay();
                }

                // 订阅部署降级告知。挂在主窗口而不是各个部署入口上：单个开关、批量开关、
                // 导入后自动部署、启动前部署走的是同一个 DeploymentService 单例，订一次就全覆盖；
                // 分散到入口上则必然漏掉某一条路径——"某条路径上用户什么都看不到"正是这次要修的病。
                if (_deployService != null)
                {
                    _deployService.DegradationDetected += OnDeploymentDegraded;
                }

                // 订阅 ViewModel 事件。
                // ModList.ModSelected 只由 MainViewModel 订阅：这里曾有第二个 handler，
                // 与 VM 那份对"取消选中"的处理相反，谁最后订阅谁说了算。见 MainViewModel。
                _vm.ModDetail.ModStateChanged += async () => await RefreshAfterModChange();
                _vm.ModDetail.CloseRequested += () => _vm.IsDetailPanelOpen = false;

                // 方案选择器的名称/摘要走 XAML 绑定（{Binding CurrentProfileName/CurrentProfileSummary}），
                // ProfileChanged / ProfileListChanged 由 MainViewModel 独家订阅，
                // 这里不再挂第二份直接写 TextBlock.Text 的实现。

                // 拖拽
                AllowDrop = true;
                DragEnter += MainWindow_DragEnter;
                DragOver += MainWindow_DragOver;
                Drop += MainWindow_Drop;

                // 加载配置
                Loaded += async (_, _) => await InitializeAsync();
                Closing += (_, _) => Cleanup();

                // 语言
                try
                {
                    LanguageManager.LanguageChanged += OnLanguageChanged;
                    ApplyLocalization();
                }
                catch (Exception ex)
                {
                    // 不上抛：本地化失败不该拦住主窗口启动。但必须留痕——
                    // ApplyLocalization 里任一具名控件被重命名就会 NullReferenceException，
                    // 原先被吞掉后界面文案静默停在 XAML 默认值，没人知道发生过什么。
                    // 此时 _logger 已就位，但日志系统本身可能还没配好，故两条通道都写。
                    _logger?.LogError(ex, "[UI] 初始化本地化失败，界面文案将停留在默认值");
                    Console.WriteLine($"[MainWindow] 初始化本地化失败: {ex}");
                }

                // 背景
                try
                {
                    BackgroundManager.Initialize();
                    BackgroundManager.BackgroundChanged += OnBackgroundChanged;
                    ApplyBackground(BackgroundManager.Settings);
                }
                catch (Exception ex)
                {
                    // 同上：背景加载失败不拦启动，但不能静默——
                    // 用户设过的背景图突然不见了，日志里得能查到原因
                    _logger?.LogError(ex, "[UI] 初始化背景失败，将使用默认背景");
                    Console.WriteLine($"[MainWindow] 初始化背景失败: {ex}");
                }

                Console.WriteLine("MainWindow 初始化完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MainWindow 构造函数异常: {ex}");
                CyberMessageBox.Show(IsVisible ? this : null, $"初始化失败: {ex.Message}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                // 加载配置
                await _gameConfig.LoadConfigAsync();

                // 恢复游戏选择
                if (!string.IsNullOrEmpty(_gameConfig.CurrentGameName))
                {
                    CurrentGameName.Text = _gameConfig.CurrentGameName;
                    UpdateGameIcon(_gameConfig.CurrentGameName);
                }

                // 初始化 MOD 和分类
                await _vm.InitializeAsync();

                // 数据源在 XAML 中绑定（ItemsSource="{Binding ModList.Mods}" 等），
                // 集合是 ObservableCollection 且实例从不替换，这里无需再手工接线

                // 更新 UI
                UpdateNavCounts();
                UpdateModCountText();

                // Phase 11: 启动时检查未完成事务（崩溃恢复）
                await CheckForCrashesAsync();

                // 数据目录搬迁没做完时告知用户（软件仍在用旧位置，功能不受影响）
                NotifyDataMigrationIfNeeded();

                // Phase 11: 启动时健康检查（结果写入日志）
                await LogHealthReportAsync();

                // 匿名统计：先告知（只在从没问过时弹一次），再开始心跳。
                // 排在最后一位是有意的——崩溃恢复和数据搬迁是用户真的需要处理的事，
                // 统计是我们的需求，不该抢在它们前面占用户的注意力。
                StartTelemetry();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化失败: {ex}");
                _logger?.LogError(ex, "MainWindow 初始化失败");
            }
        }

        /// <summary>
        /// 匿名统计的告知与启动。
        ///
        /// <para><b>为什么告知框在主窗口之后、而不是在启动序列里</b></para>
        /// 全新安装的用户在见到主界面之前已经可能被拦两次（仓库位置引导、登录窗口）。
        /// 再插一个"能不能统计你"进去，是把一个我们自己的需求排到用户还没看到软件长什么样
        /// 的前面。放在主窗口起来之后问，用户至少已经知道这是个什么东西。
        ///
        /// <para><b>关掉窗口（点 X 而不选按钮）视为"不参与"，并且记为已问过</b></para>
        /// 两条理由：没有明确同意就绝不上报，这是硬底线；而不记"已问过"就意味着每次启动
        /// 再弹一遍，对一个习惯性关弹窗的用户等于永久骚扰。代价是有一部分只是随手关掉的人
        /// 被算成了退出——这个方向的误差是可接受的那一侧，他们随时能在设置里打开。
        ///
        /// <para><b>整段不允许影响启动</b></para>
        /// 判定、弹窗、心跳启动全部包在 try 里；心跳本身跑在线程池上，这里一行都不等它。
        /// </summary>
        private void StartTelemetry()
        {
            try
            {
                var telemetry = ((App)Application.Current).ServiceProvider?.GetService<TelemetryService>();
                if (telemetry == null) return;

                var decision = TelemetryService.CurrentConsent();
                Console.WriteLine($"[Telemetry] {decision}");

                if (decision.ShouldAsk)
                {
                    var choice = CyberMessageBox.Show(this,
                        "UEModManager 会在启动时看一眼有没有新版本，顺手带上一个随机编号和版本号，" +
                        "好让我们知道有多少人在用。登录过的话还会带上邮箱算出来的一串乱码" +
                        "（还原不回邮箱），用来去掉重复的人。\n\n" +
                        "不会发送：你的邮箱、电脑名、文件路径、装了哪些 MOD。\n\n" +
                        "随时可以在「设置 → 常规参数」里关掉。",
                        "想知道有多少人在用",
                        MessageBoxButton.YesNo, MessageBoxImage.Information,
                        yesText: "可以", noText: "不用了");

                    UiPreferences.SaveTelemetryConsent(choice == MessageBoxResult.Yes);
                }

                telemetry.StartHeartbeat();
            }
            catch (Exception ex)
            {
                // 统计是我们的需求，不是用户的。它出任何问题都不该在界面上留下一个字。
                _logger?.LogDebug(ex, "[Telemetry] 启动失败");
            }
        }

        /// <summary>
        /// 数据目录搬迁未彻底完成时告知用户一次。
        ///
        /// <para>
        /// 判据必须是 <c>ShouldNotifyUser</c> 而不是 <c>Completed</c>：后者的语义是
        /// "可以打数据布局版本标记了"，有推迟项时也是 false。搬迁总开关关着的当下，
        /// 每台老用户机器都有推迟项——照 <c>Completed</c> 提示等于给全体用户天天报警，
        /// 报的还是一件根本没开始做的事。
        /// </para>
        ///
        /// <para>
        /// 迁移方案要求这里是"非模态提示"，本实现用的是一次性对话框，是有意偏离：
        /// 项目没有非模态提示基础设施，而主界面有 1:1 原型约束，新增常驻提示条要动布局。
        /// 对话框在主窗口显示之后弹出，点掉即继续，并不阻断任何功能——方案那句话的实质
        /// 诉求（迁移失败绝不拦人）仍然满足。真要做成提示条时，改这一处即可。
        /// </para>
        /// </summary>
        private void NotifyDataMigrationIfNeeded()
        {
            try
            {
                var outcome = _dataMigrator?.LastOutcome;
                if (outcome?.ShouldNotifyUser != true) return;

                CyberMessageBox.Show(this, outcome.UserMessage, "数据目录",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                // 提示失败不能反过来影响启动——数据本身还在旧位置好好待着
                _logger?.LogError(ex, "[DataMigration] 提示用户失败");
            }
        }

        /// <summary>
        /// 启动时扫描未完成事务，发现崩溃则提示用户回滚或清理。
        /// 不阻塞 UI；失败时静默记录日志，不影响主流程。
        /// </summary>
        private async Task CheckForCrashesAsync()
        {
            if (_crashRecovery == null) return;

            try
            {
                var candidates = await _crashRecovery.ScanForCrashesAsync();
                if (candidates.Count == 0) return;

                var rollbackCount = candidates.Count(c => c.Action == RecoveryAction.RollbackRecommended);
                var cleanupCount = candidates.Count(c => c.Action == RecoveryAction.MarkFailedRecommended);
                var manualReviewCount = candidates.Count(c => c.Action == RecoveryAction.ManualReviewRequired);
                var verifyCount = candidates.Count(c => c.Action == RecoveryAction.VerifyAndResubmit);

                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"检测到 {candidates.Count} 个未完成的部署事务（可能是上次崩溃留下的）：");
                summary.AppendLine();
                foreach (var c in candidates.Take(5))
                {
                    summary.AppendLine($"  • {c.CreatedAt:yyyy-MM-dd HH:mm}  [{DisplayNameMapper.DeploymentStatus(c.Status)}]  {c.Reason}");
                }
                if (candidates.Count > 5) summary.AppendLine($"  …还有 {candidates.Count - 5} 个");
                summary.AppendLine();
                summary.AppendLine($"建议处理：回滚 {rollbackCount} 个，标记失败 {cleanupCount} 个，人工核查 {manualReviewCount} 个，日志核查 {verifyCount} 个。");
                summary.AppendLine();
                summary.Append("请选择要执行的操作。\"本次跳过\"下次启动仍会提醒；\"不再提醒\"会把这些事务标记为已忽略。");

                var result = CyberMessageBox.Show(this, summary.ToString(),
                    "崩溃恢复", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning,
                    yesText: "恢复", noText: "本次跳过", cancelText: "不再提醒");

                if (result == MessageBoxResult.Cancel)
                {
                    int dismissed = 0, dismissFailed = 0;
                    foreach (var c in candidates)
                    {
                        if (await _crashRecovery.DismissTransactionAsync(c.TransactionId, "用户在启动崩溃恢复弹窗选择不再提醒"))
                            dismissed++;
                        else
                            dismissFailed++;
                    }

                    CyberMessageBox.Show(this,
                        $"已忽略：成功 {dismissed}，失败 {dismissFailed}。",
                        "崩溃恢复", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (result != MessageBoxResult.Yes) return;

                int succeeded = 0, failed = 0;
                foreach (var c in candidates)
                {
                    if (await _crashRecovery.ApplyRecoveryAsync(c.TransactionId, c.Action))
                        succeeded++;
                    else
                        failed++;
                }

                CyberMessageBox.Show(this,
                    $"恢复完成：成功 {succeeded}，失败 {failed}。",
                    "崩溃恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Recovery] 崩溃恢复检查失败");
            }
        }

        /// <summary>
        /// 启动时跑一次健康检查，把结果作为 INFO 日志写入 console.log。
        /// 失败时静默记录错误，不影响主流程。
        /// </summary>
        private async Task LogHealthReportAsync()
        {
            if (_healthCheck == null) return;

            try
            {
                var report = await _healthCheck.CheckAsync();
                Console.WriteLine($"[Health] === Startup Report (Overall: {report.OverallStatus}) ===");
                foreach (var line in report.ToText().Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        Console.WriteLine($"[Health] {line.TrimEnd()}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Health] 健康检查失败");
            }
        }

        private void Cleanup()
        {
            _searchDebounceTimer?.Stop();

            // 静态事件是 GC root，必须显式退订，否则窗口连同整棵视觉树永远无法回收
            LanguageManager.LanguageChanged -= OnLanguageChanged;
            BackgroundManager.BackgroundChanged -= OnBackgroundChanged;

            // DeploymentService 是单例，同理必须退订
            if (_deployService != null)
            {
                _deployService.DegradationDetected -= OnDeploymentDegraded;
            }

            // ProfileService 是单例，退订后 ViewModel 才能被回收。
            // 本窗口自己已不再订阅它的事件，退订由 MainViewModel.Dispose 完成。
            _vm.Dispose();
        }

        // ═════════════════════════════════════════
        //  部署降级告知
        // ═════════════════════════════════════════

        /// <summary>
        /// 部署成功但"没能按用户选的方式做"时告知一次。
        ///
        /// <para>
        /// 典型场景：用户在设置里选了「硬链接」，而包仓库默认在系统盘、游戏装在别的盘。
        /// 硬链接建不了跨盘，后端逐个文件降级为复制——部署成功、MOD 能用、空间一点没省，
        /// 而此前整个过程在界面上不留一个字，只有日志里一行 LogWarning。
        /// </para>
        ///
        /// <para>
        /// 事件可能在部署线程上抛出，且此刻还在 <c>ExecuteAsync</c> 的 finally 里：
        /// 直接弹框会把部署的收尾卡在一个等用户点确定的模态窗口上。
        /// 丢回 UI 队列末尾再弹，让部署流程和列表刷新先跑完。
        /// </para>
        /// </summary>
        private void OnDeploymentDegraded(DeploymentTransaction transaction)
        {
            var summaries = transaction.Degradations.ToList();
            if (summaries.Count == 0) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => ShowDeploymentDegradationNotice(summaries)));
        }

        /// <summary>
        /// 真正弹这条提示。
        ///
        /// <para>
        /// <b>不走 <c>SafeEvent.Run</c></b>：它的失败呈现是一个"操作失败"的错误框，
        /// 而这里整体只是一条提示——为一条没弹出来的提示再弹一个错误框是荒唐的。
        /// 本方法自带 try/catch，失败只留痕，事务日志里那条降级记录仍然在。
        /// </para>
        /// </summary>
        private void ShowDeploymentDegradationNotice(IReadOnlyList<DeploymentDegradationSummary> summaries)
        {
            // 批量启用会连着部署 N 次、抛 N 次告知。第一条弹出来的时候后面几条已经排在队列里了
            // （签名要等这一条弹完才存得下去），靠这个标志挡住，否则用户要连点 N 个一模一样的框。
            if (_degradationNoticeShowing) return;

            try
            {
                // "什么时候值得说一次"的判定在 Core：同一种情形（原因 + 哪两个盘）只说一次，
                // 用户换了存放位置或换了别的盘上的游戏才会再说。
                var decision = DeploymentDegradationNotice.Decide(
                    summaries, UiPreferences.LoadDeployDegradationNotice());

                _logger?.LogInformation("[Deploy] 降级告知 shouldNotify={Should}：{Reason}",
                    decision.ShouldNotify, decision.Reason);
                if (!decision.ShouldNotify) return;

                var content = DeploymentDegradationNotice.BuildContent(summaries);

                _degradationNoticeShowing = true;
                MessageBoxResult choice;
                try
                {
                    // 能一键修时给两个按钮，否则退回单个"知道了"。
                    //
                    // 只告知不给动作，对相当一部分玩家等于没告知——他们会关掉弹窗然后放弃；
                    // 而照着"你自己去设置里改"做的那些人，此前还会撞上"改位置只改指针不搬数据"，
                    // MOD 当场从界面上消失。所以发现问题的这个位置就得给出解决。
                    choice = content.CanFixInPlace
                        ? CyberMessageBox.Show(this, content.Message, content.Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Information,
                            yesText: content.FixButtonText, noText: "先这样")
                        : CyberMessageBox.Show(this, content.Message, content.Title,
                            MessageBoxButton.OK, MessageBoxImage.Information, okText: "知道了");
                }
                finally
                {
                    _degradationNoticeShowing = false;
                }

                // 先记账再看用户选了什么：无论他点哪个，这套盘的组合都已经告知过了。
                // 反过来（只在"先这样"时记账）会让点了"帮我搬"却中途取消的用户下次部署
                // 再被弹一次，而他刚刚才明确表达过"现在不想搬"。
                UiPreferences.SaveDeployDegradationNotice(decision.Signature);

                if (choice == MessageBoxResult.Yes && content.CanFixInPlace)
                {
                    StartOneKeyRelocation(content.FixTargetVolumeRoot!);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[UI] 显示部署降级提示失败");
            }
        }

        /// <summary>
        /// 一键换盘：把 MOD 存放位置搬到游戏所在的盘。
        ///
        /// <para>
        /// 落点是 <c>&lt;游戏所在盘&gt;\UEModManager\Repository</c>——盘根几乎必然非空，
        /// 于是 <see cref="RepositoryLocationValidator.ResolveRepositoryPath"/> 会自动
        /// 退到这个专用子目录，正是想要的结果（绝不能把盘根本身当成仓库根）。
        /// </para>
        ///
        /// <para>
        /// 走的是<b>与设置界面完全相同</b>的服务与窗口。一个功能一条路径：
        /// 留一条只改指针的旁路，用户从别处改照样丢 MOD。
        /// </para>
        /// </summary>
        private void StartOneKeyRelocation(string gameVolumeRoot)
        {
            SafeEvent.Run(this, () =>
            {
                var relocation = ((App)Application.Current).ServiceProvider
                    ?.GetService<RepositoryRelocationService>();
                if (relocation == null)
                {
                    _logger?.LogWarning("[UI] 搬移服务不可用，一键换盘无法进行");
                    return Task.CompletedTask;
                }

                var resolved = RepositoryLocationValidator.ResolveRepositoryPath(
                    gameVolumeRoot, directoryHasContent: true);
                var plan = relocation.Plan(resolved);

                // 这条入口的前提就是"刚刚部署过"，所以结果页一定要提醒重新装一次：
                // 已装进游戏目录的文件是指向旧仓库的硬链接或副本，搬完游戏照常能玩，
                // 但旧盘上那份空间要重新部署一次才腾得出来。
                var window = new Views.RepositoryRelocationWindow(
                    relocation, plan, anyDeployed: true, _logger)
                {
                    Owner = this,
                };
                window.ShowDialog();

                if (window.Switched)
                {
                    // 存放位置变了，列表要按新仓库重建一遍；同时把降级告知的记账清掉——
                    // 盘的组合已经变了，下次若仍有降级，那是一个新情况，值得再说一次。
                    UiPreferences.SaveDeployDegradationNotice(null);
                    return _vm.RefreshFromRepositoryAsync();
                }

                return Task.CompletedTask;
            }, _logger, "一键搬移 MOD 存放位置");
        }

        /// <summary>静态事件的具名 handler（必须具名，lambda 无法退订）。</summary>
        private void OnLanguageChanged(bool isEnglish) => Dispatcher.Invoke(ApplyLocalization);

        /// <summary>静态事件的具名 handler（必须具名，lambda 无法退订）。</summary>
        private void OnBackgroundChanged(BackgroundSettings bg) => Dispatcher.Invoke(() => ApplyBackground(bg));

        // ═════════════════════════════════════════
        //  窗口控制 (Chrome + WM_GETMINMAXINFO)
        //  OnMinimizeWindow / OnMaximizeWindow / OnRestoreWindow / OnCloseWindow
        //  定义在 MainWindow.Conflict.cs partial
        // ═════════════════════════════════════════

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
                    var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
                    var wa = screen.WorkingArea;
                    var sb = screen.Bounds;
                    mmi.ptMaxPosition.x = Math.Abs(wa.Left - sb.Left);
                    mmi.ptMaxPosition.y = Math.Abs(wa.Top - sb.Top);
                    mmi.ptMaxSize.x = wa.Width;
                    mmi.ptMaxSize.y = wa.Height;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
                catch (Exception ex)
                {
                    // 结构上就该吞：这是 Win32 消息回调，异常不能穿过非托管边界，
                    // 且失败只意味着最大化尺寸回落到系统默认，不影响功能。
                    // 但留一条调试痕迹，免得排查窗口尺寸异常时完全无从下手。
                    Debug.WriteLine($"[MainWindow] WM_GETMINMAXINFO 处理失败: {ex.Message}");
                }
            }
            return IntPtr.Zero;
        }


        // ═════════════════════════════════════════
        //  认证 & 用户状态
        // ═════════════════════════════════════════

        private void OnLocalAuthStateChanged(object? sender, LocalAuthEventArgs e)
        {
            Dispatcher.Invoke(UpdateUserStatusDisplay);
        }

        private void UpdateUserStatusDisplay()
        {
            bool en = LanguageManager.IsEnglish;
            if (_localAuthService?.IsLoggedIn == true)
            {
                var user = _localAuthService.CurrentUser;
                UserNameText.Text = user?.DisplayName ?? user?.Email ?? (en ? "Logged in" : "已登录");
                UserStatusText.Text = en ? "Cloud Online" : "云端在线";
                UserStatusText.Foreground = FindResource("StatusGreenBrush") as Brush ?? Brushes.Green;
            }
            else
            {
                UserNameText.Text = en ? "Not Logged In" : "未登录";
                UserStatusText.Text = en ? "Click to login" : "点击登录账号";
                UserStatusText.Foreground = FindResource("Text600Brush") as Brush ?? Brushes.Gray;
            }
        }

        private void UserArea_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var target = sender as UIElement;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_localAuthService?.IsLoggedIn == true)
                    {
                        var menu = new ContextMenu { Style = FindResource("CyberContextMenu") as Style };
                        var accountItem = new MenuItem { Header = LanguageManager.IsEnglish ? "Account Settings" : "账户设置", Style = FindResource("CyberMenuItem") as Style };
                        accountItem.Click += (_, _) =>
                        {
                            try
                            {
                                new AccountSettingsWindow { Owner = this }.ShowDialog();
                                UpdateUserStatusDisplay();
                            }
                            catch (Exception ex)
                            {
                                // 窗口构造 / XAML 解析 / DI 解析失败原先全部静默，
                                // 用户点了"账户设置"什么都不发生且日志里毫无痕迹
                                _logger?.LogError(ex, "[UI] 打开账户设置窗口失败");
                                CyberMessageBox.Show(this, $"打开账户设置失败：{ex.Message}",
                                    "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        };
                        menu.Items.Add(accountItem);

                        if (_localAuthService.CurrentUser?.IsAdmin == true)
                        {
                            var adminItem = new MenuItem { Header = LanguageManager.IsEnglish ? "Admin Panel" : "管理面板", Style = FindResource("CyberMenuItem") as Style };
                            adminItem.Click += (_, _) =>
                            {
                                try
                                {
                                    new AdminDashboardWindow { Owner = this }.ShowDialog();
                                }
                                catch (Exception ex)
                                {
                                    // 管理员入口原先静默失败，"后台打不开"没有任何线索可查
                                    _logger?.LogError(ex, "[UI] 打开管理面板失败");
                                    CyberMessageBox.Show(this, $"打开管理面板失败：{ex.Message}",
                                        "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
                                }
                            };
                            menu.Items.Add(adminItem);
                        }

                        menu.Items.Add(new Separator { Style = FindResource("CyberMenuSeparator") as Style });

                        var logoutItem = new MenuItem { Header = LanguageManager.IsEnglish ? "Log Out" : "退出登录", Style = FindResource("CyberMenuItemDanger") as Style };
                        logoutItem.Click += (_, _) => SafeEvent.Run(this, async () =>
                        {
                            // 原先是 try { ... } catch { } 的裸 async void：退出登录失败
                            // 既不弹窗也不留日志，界面还停在已登录状态，用户只会觉得"点了没反应"。
                            await _localAuthService.LogoutAsync();
                            UpdateUserStatusDisplay();
                        }, _logger, "退出登录");
                        menu.Items.Add(logoutItem);

                        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                        menu.PlacementTarget = target;
                        menu.IsOpen = true;
                    }
                    else
                    {
                        var loginWin = new LoginWindow { Owner = this };
                        if (loginWin.ShowDialog() == true)
                            UpdateUserStatusDisplay();
                    }
                }
                catch (Exception ex) { _logger?.LogError(ex, "打开用户窗口失败"); }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void SettingsIcon_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                new SettingsWindow { Owner = this }.ShowDialog();
            }
            catch (Exception ex) { _logger?.LogError(ex, "打开设置失败"); }
        }

        // ═════════════════════════════════════════
        //  捐赠支持
        // ═════════════════════════════════════════

        private void DonateBtn_MouseEnter(object sender, MouseEventArgs e)
        {
            DonatePopup.IsOpen = true;
        }

        private void DonateBtn_MouseLeave(object sender, MouseEventArgs e)
        {
            // 如果鼠标移到了 Popup 上，则不关闭
            if (!DonatePopup.IsMouseOver)
                DonatePopup.IsOpen = false;
        }

        private void DonateBtn_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            DonatePopup.IsOpen = !DonatePopup.IsOpen;
        }

        // 使用说明书 — 打开 WPS 云文档
        private const string HelpDocUrl = "https://www.kdocs.cn/l/chqhf7cWy7K8";

        private void HelpDocBtn_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = HelpDocUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "打开使用说明书失败");
                Views.CyberMessageBox.Show(this,
                    $"无法打开默认浏览器，请手动复制此链接到浏览器访问：\n\n{HelpDocUrl}",
                    "打开使用说明书", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ═════════════════════════════════════════
        //  游戏选择
        // ═════════════════════════════════════════

        private void GameSwitcher_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var target = sender as UIElement;

            // 延迟打开菜单，避免鼠标按下事件导致焦点丢失
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var menu = new ContextMenu { Style = FindResource("CyberContextMenu") as Style };

                var games = _gameConfig.GetAvailableGames();

                foreach (var game in games)
                {
                    var item = new MenuItem { Header = game, Style = FindResource("CyberMenuItem") as Style, Tag = game };
                    if (game == _gameConfig.CurrentGameName)
                        item.FontWeight = FontWeights.Bold;

                    // 加载游戏图标
                    var iconPath = _gameConfig.GetGameIconPath(game);
                    var bitmap = ImageLoader.LoadFrozen(iconPath, decodePixelWidth: 32);
                    if (bitmap != null)
                    {
                        item.Icon = new Image { Source = bitmap, Width = 20, Height = 20, Stretch = Stretch.UniformToFill };
                    }

                    item.Click += GameMenuItem_Click;
                    menu.Items.Add(item);
                }

                menu.Items.Add(new Separator { Style = FindResource("CyberMenuSeparator") as Style });

                var addItem = new MenuItem { Header = LanguageManager.IsEnglish ? "Add New Game..." : "添加新游戏...", Style = FindResource("CyberMenuItem") as Style,
                                             Foreground = FindResource("PrimaryBrush") as Brush };
                // 裸 async void lambda 抛出去只能落到全局 DispatcherUnhandledException——
                // 能弹窗，但不带"添加新游戏"这个上下文，日志里也看不出是哪一步失败的。
                addItem.Click += (_, _) => SafeEvent.Run(this, async () =>
                {
                    var dialog = new AddCustomGameDialog { Owner = this };
                    if (dialog.ShowDialog() == true)
                    {
                        // 先把游戏名写入 CustomGames。此前这里只保存引擎类型，
                        // 导致新游戏本次能配置、重启后却从游戏列表消失。
                        var gameName = await _gameConfig.AddCustomGameAsync(dialog.GameName);

                        // 保存自定义游戏的引擎类型
                        var engineType = dialog.IsAutoDetect
                            ? GameConfigService.AutoDetectEngine(dialog.GamePathTextBox.Text)
                            : dialog.SelectedEngineType;
                        if (engineType == Models.EngineType.Unknown)
                            engineType = Models.EngineType.UnrealEngine; // 无法识别时默认 UE
                        await _gameConfig.SetGameEngineAsync(gameName, engineType);

                        ShowGamePathDialog(gameName);
                    }
                }, _logger, "添加新游戏");
                menu.Items.Add(addItem);

                var customGames = _gameConfig.GetCustomGames();
                if (customGames.Count > 0)
                {
                    var removeMenu = new MenuItem
                    {
                        Header = LanguageManager.IsEnglish ? "Remove Custom Game" : "删除自定义游戏",
                        Style = FindResource("CyberMenuItemSubmenuHeader") as Style
                    };
                    foreach (var customGame in customGames)
                    {
                        var removeItem = new MenuItem
                        {
                            Header = customGame,
                            Tag = customGame,
                            Style = FindResource("CyberMenuItem") as Style
                        };
                        removeItem.Click += RemoveCustomGameMenuItem_Click;
                        removeMenu.Items.Add(removeItem);
                    }
                    menu.Items.Add(removeMenu);
                }

                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.PlacementTarget = target;
                menu.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // ═════════════════════════════════════════
        //  游戏方案选择器
        // ═════════════════════════════════════════

        private void ProfileSelector_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                var sp = ((App)Application.Current).ServiceProvider;
                if (sp == null) return;

                var profileWindow = sp.GetRequiredService<Views.ProfileManagerWindow>();
                profileWindow.Owner = this;
                profileWindow.LoadForGame(_gameConfig.CurrentGameName);
                profileWindow.ShowDialog();

                // 关闭方案管理窗口后刷新（方案可能被改名/切换/删除）
                _vm.RefreshProfileDisplay();
                _ = _vm.RefreshFromRepositoryAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Profile] 打开方案管理失败: {ex.Message}");
            }
        }

        private void GameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string gameName)
            {
                if (gameName == _gameConfig.CurrentGameName) return;

                if (!string.IsNullOrEmpty(_gameConfig.CurrentGameName))
                {
                    var result = CyberMessageBox.Show(this,
                        LanguageManager.IsEnglish
                            ? $"Switch from '{_gameConfig.CurrentGameName}' to '{gameName}'?\nCurrent MOD states will be saved."
                            : $"确认从 '{_gameConfig.CurrentGameName}' 切换到 '{gameName}'？\n当前MOD状态会被保存。",
                        LanguageManager.IsEnglish ? "Switch Game" : "切换游戏", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.No) return;
                }

                ShowGamePathDialog(gameName);
            }
        }

        private void RemoveCustomGameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string gameName }) return;

            SafeEvent.Run(this, async () =>
            {
                var result = CyberMessageBox.Show(this,
                    $"确定从游戏列表中移除“{gameName}”吗？\n\n不会删除游戏文件、MOD、方案或仓库数据。",
                    "移除自定义游戏", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    yesText: "移除", noText: "取消");
                if (result != MessageBoxResult.Yes) return;

                await _gameConfig.RemoveCustomGameAsync(gameName);
            }, _logger, "移除自定义游戏");
        }

        private void ShowGamePathDialog(string gameName)
        {
            SafeEvent.Run(this, async () =>
            {
            var dialog = new GamePathDialog(gameName) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                var backupPath = dialog.BackupPath;
                if (string.IsNullOrEmpty(backupPath))
                {
                    // 兜底值跟随 MOD 备份根。此前是 {安装目录}\Backups，与服务层实际使用的
                    // 备份根是两个互不相干的目录，装在 Program Files 下时还根本建不出来。
                    //
                    // 同时删掉了原来"路径含 AppData 或位于 C:\Users 下就判为非法"的两个条件：
                    // 备份根现在正是 %LOCALAPPDATA%\UEModManager\Backups\Mods，判据整个反了过来，
                    // 留着只会把用户在个人目录下亲手选的备份位置无声改掉。
                    backupPath = IOPath.Combine(AppPaths.ModBackupsDirectory, $"{gameName}_备份");
                    AppPaths.TryEnsureDirectory(backupPath);
                }

                await _gameConfig.SwitchGameAsync(gameName, dialog.GamePath, dialog.ModPath, backupPath);

                // 保存游戏图标
                if (!string.IsNullOrEmpty(dialog.GameIconPath))
                    await _gameConfig.SetGameIconAsync(gameName, dialog.GameIconPath);

                CurrentGameName.Text = gameName;
                UpdateGameIcon(gameName);

                // 重新初始化
                IsEnabled = false;
                Cursor = Cursors.Wait;
                try
                {
                    await _vm.InitializeAsync();
                    UpdateNavCounts();
                    UpdateModCountText();

                    CyberMessageBox.Show(this, $"游戏 '{gameName}' 配置完成！\n\n" +
                        $"MOD路径: {_gameConfig.CurrentModPath}\n已扫描到 {_vm.ModList.Mods.Count} 个MOD",
                        "配置成功");
                }
                finally { IsEnabled = true; Cursor = Cursors.Arrow; }
            }
            }, _logger, "Configure game path");
        }

        // ═════════════════════════════════════════
        //  侧边栏导航
        // ═════════════════════════════════════════

        private void NavItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                _activeNavTag = tag;
                UpdateNavHighlight();

                // 应用筛选
                var cat = _vm.Categories.Categories.FirstOrDefault(c => c.Name == tag);
                if (cat != null)
                {
                    CategoryList.SelectedItem = null; // 取消分类选中
                    _vm.Categories.SelectedCategory = cat;
                    AnimateContentTransition(() =>
                    {
                        _vm.ModList.ApplyFilter(cat, _vm.ModList.SearchText);
                        UpdateModCountText();
                    });
                }
            }
        }

        private void UpdateNavHighlight()
        {
            // 全部
            NavAllMods.Background = _activeNavTag == "全部"
                ? FindResource("SurfaceHoverBrush") as Brush : Brushes.Transparent;
            NavAllMods.BorderThickness = _activeNavTag == "全部" ? new Thickness(1) : new Thickness(0);

            // 已启用
            NavEnabled.Background = _activeNavTag == "已启用"
                ? FindResource("SurfaceHoverBrush") as Brush : Brushes.Transparent;

            // 已禁用
            NavDisabled.Background = _activeNavTag == "已禁用"
                ? FindResource("SurfaceHoverBrush") as Brush : Brushes.Transparent;
        }

        private void UpdateNavCounts()
        {
            var mods = _vm.ModList.Mods;
            NavAllCount.Text = mods.Count.ToString();
            NavEnabledCount.Text = mods.Count(m => m.IsEnabled).ToString();
            NavDisabledCount.Text = mods.Count(m => !m.IsEnabled).ToString();
        }

        // ═════════════════════════════════════════
        //  搜索框
        // ═════════════════════════════════════════

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchPlaceholder != null) SearchPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && SearchPlaceholder != null && string.IsNullOrWhiteSpace(tb.Text))
                SearchPlaceholder.Visibility = Visibility.Visible;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 只重置计时，不再每次击键 new 一个 timer。
            // 原实现的 Tick 闭包读的是字段 _searchDebounceTimer 而不是它自己那个实例，
            // 于是某个旧 timer 若抢先触发，它 Stop 掉的是**新** timer ——
            // 表现为"搜索偶发不生效"。Cleanup 里也只 Stop 得到最后一个实例，其余全泄漏。
            EnsureSearchDebounceTimer();
            _searchDebounceTimer!.Stop();
            _searchDebounceTimer.Start();
        }

        /// <summary>
        /// 惰性创建唯一的搜索防抖计时器，Tick 只订阅一次。
        /// </summary>
        private void EnsureSearchDebounceTimer()
        {
            if (_searchDebounceTimer != null) return;

            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchDebounceTimer.Tick += OnSearchDebounceTick;
        }

        private void OnSearchDebounceTick(object? sender, EventArgs e)
        {
            _searchDebounceTimer?.Stop();
            _vm.ModList.SearchText = SearchBox.Text;
            UpdateModCountText();
        }

        // ═════════════════════════════════════════
        //  分类操作
        // ═════════════════════════════════════════

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CategoryList.SelectedItem is CategoryItem cat)
            {
                _activeNavTag = ""; // 清除库导航高亮
                UpdateNavHighlight();
                _vm.Categories.SelectedCategory = cat;
                AnimateContentTransition(() =>
                {
                    _vm.ModList.ApplyFilter(cat, _vm.ModList.SearchText);
                    UpdateModCountText();
                });
            }
        }

        private void AddCategory_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeEvent.Run(this, async () =>
            {
                var name = CyberInputDialog.Show(this, "新增分类", "请输入分类名称:");
                if (!string.IsNullOrWhiteSpace(name))
                    await _vm.Categories.AddCategoryAsync(name.Trim());
            }, _logger, "新增分类");
        }


        private void RenameCategoryMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                if (CategoryList.SelectedItem is CategoryItem cat && !CategoryItem.SystemNames.Contains(cat.Name))
                {
                    var newName = CyberInputDialog.Show(this, "重命名分类", "请输入新名称:", cat.DisplayText);
                    if (!string.IsNullOrWhiteSpace(newName) && newName != cat.DisplayText)
                        await _vm.Categories.DoRenameCategoryAsync(cat, newName.Trim());
                }
            }, _logger, "重命名分类");

        private void DeleteCategoryMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                if (CategoryList.SelectedItem is CategoryItem cat && !CategoryItem.SystemNames.Contains(cat.Name))
                {
                    var r = CyberMessageBox.Show(this, $"确认删除分类 '{cat.DisplayText}'？", "确认", MessageBoxButton.YesNo);
                    if (r == MessageBoxResult.Yes)
                        await _vm.Categories.DeleteCategoryAsync(cat);
                }
            }, _logger, "删除分类");


        // ── 分类拖拽 ──

        private void CategoryList_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(CategoryList);
            if (Math.Abs(pos.X - _startPoint.X) > 4 || Math.Abs(pos.Y - _startPoint.Y) > 4)
            {
                if (CategoryList.SelectedItem is CategoryItem cat && !CategoryItem.SystemNames.Contains(cat.Name))
                {
                    DragDrop.DoDragDrop(CategoryList, cat, DragDropEffects.Move);
                    _isDragging = false;
                }
            }
        }

        private void CategoryList_MouseUp(object sender, MouseButtonEventArgs e) => _isDragging = false;
        private void CategoryList_DragEnter(object sender, DragEventArgs e) => e.Effects = DragDropEffects.Move;

        private void CategoryList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(CategoryItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void CategoryList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(CategoryItem))) return;
            var draggedCat = (CategoryItem)e.Data.GetData(typeof(CategoryItem));
            var items = _vm.Categories.Categories;
            var target = GetCategoryItemAtPosition(e.GetPosition(CategoryList));
            if (target == null || target == draggedCat) return;

            var newIdx = items.IndexOf(target);
            if (newIdx < 0 || items.IndexOf(draggedCat) < 0) return;

            // 走服务而不是直接 items.Move：直接移动只改内存，用户排好的顺序重启就没了。
            // 服务那边落盘失败会把顺序移回原位并上抛，这里用 SafeEvent.Run 接住弹窗——
            // 否则失败的表现是"拖完看着好好的，下次启动又乱了"。
            SafeEvent.Run(this, () => _vm.Categories.ReorderCategoryAsync(draggedCat, newIdx),
                _logger, "调整分类顺序");
        }

        private CategoryItem? GetCategoryItemAtPosition(Point pos)
        {
            var element = CategoryList.InputHitTest(pos) as DependencyObject;
            while (element != null && element != CategoryList)
            {
                if (element is ListBoxItem lbi && lbi.Content is CategoryItem cat)
                    return cat;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        // ═════════════════════════════════════════
        //  MOD 操作
        // ═════════════════════════════════════════

        private void ModCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            ModInfo? mod = null;
            if (sender is FrameworkElement fe)
                mod = fe.DataContext as ModInfo ?? fe.Tag as ModInfo;
            if (mod != null)
            {
                _vm.ModList.SelectedMod = mod;

                // 双击打开详情窗口
                if (e.ClickCount == 2)
                {
                    OpenModDetailWindow(mod);
                }

                e.Handled = true;
            }
        }

        private void OpenModDetailWindow(ModInfo mod)
        {
            var detailWin = new ModDetailWindow(
                mod,
                onToggle: async m => await ToggleModFromUiAsync(m, !m.IsEnabled),
                onDelete: async m => await DeleteModFromUiAsync(m, confirm: false),
                onChangePreview: async m => await ChangePreviewFromUiAsync(m),
                onRename: async (m, newName) => await RenameModFromUiAsync(m, newName)
            );
            detailWin.Owner = this;
            detailWin.ShowDialog();

            if (detailWin.ModChanged)
            {
                _ = RefreshAfterModChange();
            }
        }

        private void ModToggle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var mod = (sender as FrameworkElement)?.Tag as ModInfo;
            if (mod == null) return;

            SafeEvent.Run(this, () => ToggleModFromUiAsync(mod, !mod.IsEnabled), _logger, "切换 MOD 启用状态");
        }

        // ── MOD 导入 (v2.0: ImportDialog → ImportConfirmDialog) ──

        private void ImportMod_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeEvent.Run(this, () => OpenImportWizardAsync(), _logger, "Import MOD");
        }

        private void ImportPlugin_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeEvent.Run(this, () => OpenImportWizardAsync(), _logger, "Import plugin");
        }

        /// <summary>v2.0 统一导入流程：ImportDialog → ImportConfirmDialog → 刷新。</summary>
        private async Task OpenImportWizardAsync(string[]? preSelectedFiles = null)
        {
            string[] filesToImport;

            if (preSelectedFiles != null && preSelectedFiles.Length > 0)
            {
                filesToImport = preSelectedFiles;
            }
            else
            {
                // Step 1: 打开导入向导
                var importDlg = new Views.ImportDialog(_vm.PackageImport) { Owner = this };
                if (importDlg.ShowDialog() != true || importDlg.SelectedFiles.Count == 0)
                    return;
                filesToImport = importDlg.SelectedFiles.ToArray();
            }

            var unsupportedArchives = filesToImport
                .Where(ImportWarningMessages.IsUnsupportedArchive)
                .ToList();
            if (unsupportedArchives.Count > 0)
            {
                CyberMessageBox.Show(this,
                    ImportWarningMessages.UnsupportedArchiveMessage,
                    ImportWarningMessages.UnsupportedArchiveTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Step 2: 打开确认对话框
            var confirmDlg = new Views.ImportConfirmDialog(
                _vm.PackageImport, _vm.PackageRepo, _vm.ProfileService, _vm.ConflictAnalysis, _gameConfig)
            { Owner = this, FilePaths = filesToImport.ToList() };

            if (confirmDlg.ShowDialog() == true && confirmDlg.ImportResults != null)
            {
                var successCount = confirmDlg.ImportResults.Count(r => r.Success);
                if (successCount > 0)
                {
                    var importedPackages = confirmDlg.ImportResults
                        .Where(r => r.Success && r.Package != null)
                        .Select(r => r.Package!)
                        .ToList();

                    await _vm.ProfileService.AddPackagesToCurrentProfileAsync(importedPackages);

                    if (UiPreferences.LoadAutoDeploy())
                    {
                        // 自动部署失败过去被整个丢掉：包导进了仓库，文件没进游戏目录，
                        // 用户看到 MOD 显示为"已启用"却不生效。汇总后一次性告知。
                        var deployResults = new List<OperationResult>();
                        foreach (var package in importedPackages)
                            deployResults.Add(await _vm.DeployToggleAsync(package.PackageKey, true));

                        var deployResult = OperationResult.Aggregate(deployResults);
                        if (!deployResult.Success)
                            ShowOperationFailure(deployResult, "导入后自动部署失败");
                    }

                    await _vm.RefreshFromRepositoryAsync();
                    UpdateNavCounts();
                    UpdateModCountText();
                    UpdateEmptyState();

                    Console.WriteLine($"[Import] v2.0 导入完成: {successCount} 个包成功");
                }
            }
        }

        // ── MOD 窗口拖拽导入 ──

        private void MainWindow_DragEnter(object sender, DragEventArgs e) => e.Effects = DragDropEffects.Copy;
        private void MainWindow_DragOver(object sender, DragEventArgs e) => e.Effects = DragDropEffects.Copy;

        private void MainWindow_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files?.Length > 0)
                {
                    SafeEvent.Run(this, () => OpenImportWizardAsync(files), _logger, "Drag import MOD");
                }
            }
        }

        // ── 右键菜单事件 ──


        private void EnableModMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var mod = GetModFromContextMenu(sender);
                if (mod != null && !mod.IsEnabled)
                    await ToggleModFromUiAsync(mod, true);
            }, _logger, "启用 MOD");

        private void DisableModMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var mod = GetModFromContextMenu(sender);
                if (mod != null && mod.IsEnabled)
                    await ToggleModFromUiAsync(mod, false);
            }, _logger, "禁用 MOD");

        private void RenameModMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var mod = GetModFromContextMenu(sender);
                if (mod != null)
                    await RenameModFromUiAsync(mod);
            }, _logger, "重命名 MOD");

        private void ChangePreviewMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var mod = GetModFromContextMenu(sender);
                if (mod != null)
                    await ChangePreviewFromUiAsync(mod);
            }, _logger, "更换 MOD 预览图");

        private void DeleteModMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var mod = GetModFromContextMenu(sender);
                if (mod != null)
                    await DeleteModFromUiAsync(mod);
            }, _logger, "删除 MOD");

        private void MoveToCategoryMenuItem_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                // 子项由 ItemsSource 生成，每一项的 DataContext 就是它代表的分类。
                if (sender is not MenuItem { DataContext: CategoryItem category }) return;

                var mod = GetModFromContextMenu(sender);
                if (mod == null) return;

                var result = await _vm.MoveModToCategoryAsync(mod, category.Name);
                if (!result.Success)
                {
                    ShowOperationFailure(result, "移动到分类失败");
                    return;
                }

                UpdateNavCounts();
                UpdateModCountText();
            }, _logger, "移动 MOD 到分类");

        private ModInfo? GetModFromContextMenu(object sender)
        {
            // 一路往上找 ContextMenu，而不是只看一层 Parent：子菜单项（"移动到分类"下面的
            // 每个分类）的 Parent 是它的父 MenuItem，只看一层就永远找不到 ContextMenu。
            var current = sender as DependencyObject;
            while (current != null)
            {
                if (current is ContextMenu cm)
                {
                    // 卡片模式：PlacementTarget 是卡片 Border，DataContext 就是这个 MOD。
                    if (cm.PlacementTarget is FrameworkElement fe
                        && (fe.DataContext as ModInfo ?? fe.Tag as ModInfo) is { } fromTarget)
                        return fromTarget;

                    // 列表模式：菜单挂在 ListView 上，PlacementTarget 给不出具体某一行，
                    // 只能回落到选中项（右键会先选中该行）。此前这里直接返回 null，
                    // 列表模式下整个右键菜单点了都没反应。
                    break;
                }

                current = current is FrameworkElement f && f.Parent != null
                    ? f.Parent
                    : LogicalTreeHelper.GetParent(current);
            }

            return _vm.ModList.SelectedMod;
        }

        private async Task<bool> ToggleModFromUiAsync(ModInfo mod, bool enable)
        {
            var result = await _vm.ToggleModAsync(mod, enable);
            if (!result.Success)
            {
                ShowOperationFailure(result, enable ? "启用 MOD 失败" : "禁用 MOD 失败");
                return false;
            }

            UpdateNavCounts();
            UpdateModCountText();
            UpdateEmptyState();
            return true;
        }

        /// <summary>
        /// 把操作失败的原因呈现给用户。
        /// 这些操作过去只返回 bool，失败原因（部署事务的 ErrorMessage、异常消息）
        /// 只进日志就被丢掉，用户看到的是"开关弹回原位，什么都没说"。
        /// 用户主动取消不是失败，不弹框。
        /// </summary>
        private void ShowOperationFailure(OperationResult result, string title)
        {
            if (result.IsCancelled) return;

            CyberMessageBox.Show(this,
                result.Error ?? OperationResult.DefaultError,
                title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private async Task RenameModFromUiAsync(ModInfo mod)
        {
            var newName = CyberInputDialog.Show(this, "编辑MOD", "请输入MOD显示名称:", mod.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == mod.Name) return;

            await RenameModFromUiAsync(mod, newName);
        }

        private async Task<bool> RenameModFromUiAsync(ModInfo mod, string newName)
        {
            var result = await _vm.RenameModAsync(mod, newName);
            if (!result.Success)
            {
                ShowOperationFailure(result, "重命名 MOD 失败");
                return false;
            }

            UpdateNavCounts();
            UpdateModCountText();
            return true;
        }

        private async Task<bool> ChangePreviewFromUiAsync(ModInfo mod)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择预览图",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*"
            };

            // 用户在文件对话框里点了取消——不是失败，直接退出，不能弹错误框
            if (dialog.ShowDialog(this) != true) return false;

            var result = await _vm.ChangePreviewAsync(mod, dialog.FileName);
            if (!result.Success)
            {
                ShowOperationFailure(result, "更换预览图失败");
                return false;
            }

            UpdateNavCounts();
            UpdateModCountText();
            return true;
        }

        private async Task<bool> DeleteModFromUiAsync(ModInfo mod, bool confirm = true)
        {
            if (confirm)
            {
                var r = CyberMessageBox.Show(this, $"确认删除 '{mod.Name}'？\n此操作会从当前方案、包仓库和已部署文件中移除此 MOD。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                // 用户选择"否"——不是失败，直接退出，不能弹错误框
                if (r != MessageBoxResult.Yes) return false;
            }

            var result = await _vm.DeletePackageModAsync(mod);
            if (!result.Success)
            {
                ShowOperationFailure(result, "删除 MOD 失败");
                return false;
            }

            _vm.ModList.SelectedMod = null;
            UpdateNavCounts();
            UpdateModCountText();
            UpdateEmptyState();
            return true;
        }

        // ═════════════════════════════════════════
        //  视图切换 & 排序
        // ═════════════════════════════════════════

        private void CardViewBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ModsCardView.Visibility = Visibility.Visible;
            ModsListViewContainer.Visibility = Visibility.Collapsed;
            _vm.ModList.IsGridView = true;
            GridViewBtn.Background = FindResource("CyberBorderBrush") as Brush;
            ListViewBtnBorder.Background = Brushes.Transparent;
            UpdateEmptyState();
        }

        private void ListViewBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ModsCardView.Visibility = Visibility.Collapsed;
            ModsListViewContainer.Visibility = Visibility.Visible;
            _vm.ModList.IsGridView = false;
            GridViewBtn.Background = Brushes.Transparent;
            ListViewBtnBorder.Background = FindResource("CyberBorderBrush") as Brush;
            UpdateEmptyState();
        }

        private void SelectAll_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _vm.ModList.SelectAll();
        }

        private void BatchEnable_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeEvent.Run(this, async () =>
            {
                await _vm.ModList.EnableSelectedAsync();
                UpdateNavCounts();
                UpdateModCountText();
                UpdateEmptyState();
            }, _logger, "Batch enable MOD");
        }

        private void BatchDisable_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeEvent.Run(this, async () =>
            {
                await _vm.ModList.DisableSelectedAsync();
                UpdateNavCounts();
                UpdateModCountText();
                UpdateEmptyState();
            }, _logger, "Batch disable MOD");
        }

        private void BatchDelete_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeEvent.Run(this, async () =>
            {
                var count = _vm.ModList.SelectedCount;
                if (count <= 0) return;

                var r = CyberMessageBox.Show(this, $"\u786e\u8ba4\u5378\u8f7d\u9009\u4e2d\u7684 {count} \u4e2a MOD\uff1f\n\u6b64\u64cd\u4f5c\u4f1a\u4ece\u5f53\u524d\u65b9\u6848\u3001\u5305\u4ed3\u5e93\u548c\u5df2\u90e8\u7f72\u6587\u4ef6\u4e2d\u79fb\u9664\u8fd9\u4e9b MOD\u3002", "\u6279\u91cf\u5378\u8f7d", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;

                await _vm.ModList.DeleteSelectedAsync();
                _vm.ModList.SelectedMod = null;
                UpdateNavCounts();
                UpdateModCountText();
                UpdateEmptyState();
            }, _logger, "Batch delete MOD");
        }

        private void SortButton_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.IsOpen = true;
            }
        }

        private void SortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string sortMode)
            {
                _vm.ModList.SortMode = sortMode;
                var label = LanguageManager.IsEnglish ? "Sort" : "排序";
                SortText.Text = $"{label}: {mi.Header}";
            }
        }

        private void ModsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModsListView.SelectedItem is ModInfo mod)
                _vm.ModList.SelectedMod = mod;
        }

        private void ModsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ModsListView.SelectedItem is ModInfo mod)
                OpenModDetailWindow(mod);
        }


        // ── 卡片悬停遮罩动画 ──

        private void ModsCardView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            const double minCardWidth = 180;
            const double maxCardWidth = 260;
            const double gap = 12; // Margin="6" on each side = 12px gap between cards

            double availableWidth = e.NewSize.Width;
            if (availableWidth <= 0) return;

            int columns = Math.Max(1, (int)Math.Floor((availableWidth + gap) / (minCardWidth + gap)));
            double cardWidth = (availableWidth - gap * (columns - 1)) / columns;

            // Clamp to max
            if (cardWidth > maxCardWidth)
            {
                columns = Math.Max(1, (int)Math.Floor((availableWidth + gap) / (maxCardWidth + gap)));
                cardWidth = (availableWidth - gap * (columns - 1)) / columns;
            }

            CardWidth = Math.Max(minCardWidth, Math.Floor(cardWidth));
        }

        private void ModCard_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border card)
            {
                var overlay = FindChildByName<Border>(card, "HoverOverlay");
                if (overlay != null)
                {
                    overlay.IsHitTestVisible = true;
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(150));
                    overlay.BeginAnimation(OpacityProperty, anim);
                }
            }
        }

        private void ModCard_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border card)
            {
                var overlay = FindChildByName<Border>(card, "HoverOverlay");
                if (overlay != null)
                {
                    overlay.IsHitTestVisible = false;
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
                    overlay.BeginAnimation(OpacityProperty, anim);
                }
            }
        }

        private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && fe.Name == name) return fe;
                var found = FindChildByName<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ── 悬停遮罩上的直接操作按钮 ──

        private void ChangePreviewDirect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var mod = (sender as FrameworkElement)?.Tag as ModInfo;
            if (mod != null)
                SafeEvent.Run(this, () => ChangePreviewFromUiAsync(mod), _logger, "更换 MOD 预览图");
        }

        private void DeleteModDirect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var mod = (sender as FrameworkElement)?.Tag as ModInfo;
            if (mod != null)
                SafeEvent.Run(this, () => DeleteModFromUiAsync(mod), _logger, "删除 MOD");
        }

        private void ModMore_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var border = sender as FrameworkElement;
            if (border == null) return;

            // 向上找到 CardRoot (带 ContextMenu 的卡片根元素)
            DependencyObject current = border;
            while (current != null)
            {
                if (current is Border b && b.ContextMenu != null && b.Name != "HoverOverlay")
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        b.ContextMenu.PlacementTarget = b;
                        b.ContextMenu.IsOpen = true;
                    }), System.Windows.Threading.DispatcherPriority.Input);
                    return;
                }
                current = VisualTreeHelper.GetParent(current);
            }
        }

        // ═════════════════════════════════════════
        //  冲突检测 & 启动游戏
        // ═════════════════════════════════════════

        private void ConflictCheck_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // v2.0: 使用 ConflictAnalyzer 结果打开新冲突面板
            OpenConflictPanel();
        }

        /// <summary>v2.0 冲突面板：使用 ConflictAnalyzer 分析结果。</summary>
        private void OpenConflictPanel()
            => SafeEvent.Run(this, async () =>
            {
                // 此前这里 catch 后"回退到旧版冲突检测"，而回退目标已在重构中被掏空成
                // 空方法 —— 分析失败时用户得不到任何反馈（那个空方法已随死代码清理删除）。
                // 现改由 SafeEvent 统一记日志 + 弹窗。
                var result = await _vm.ConflictAnalysis.AnalyzeAsync();
                var win = new Views.ConflictResultWindow(_vm.ConflictAnalysis, result.Conflicts) { Owner = this };
                win.ShowDialog();
            }, _logger, "冲突检测");

        /// <summary>打开管理中心窗口。</summary>
        private void ManagementCenter_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                var sp = ((App)Application.Current).ServiceProvider;
                if (sp == null) return;

                var win = sp.GetRequiredService<Views.ManagementCenterWindow>();
                win.Owner = this;
                win.ShowDialog();

                // 管理中心关闭后刷新主界面数据
                _ = RefreshAfterManagementAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 打开管理中心失败: {ex.Message}");
            }
        }

        private async Task RefreshAfterManagementAsync()
        {
            try
            {
                await _vm.RefreshFromRepositoryAsync();
                UpdateNavCounts();
                UpdateModCountText();
            }
            catch (Exception ex)
            {
                // 刷新失败若继续静默，列表会停在旧状态且用户毫不知情，
                // 之后的操作全都基于已失效的 ModInfo 引用 —— 这正是"幽灵 MOD"类
                // 难复现故障的来源。必须让用户知道界面已经不可信。
                _logger?.LogError(ex, "[UI] 管理中心操作后刷新列表失败");
                CyberMessageBox.Show(this,
                    $"MOD 列表刷新失败，当前显示的内容可能已过期，建议重新打开本窗口。\n{ex.Message}",
                    "刷新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LaunchGame_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                var sp = ((App)Application.Current).ServiceProvider;
                if (sp == null) return;

                var launchWindow = sp.GetRequiredService<Views.LaunchCenterWindow>();
                launchWindow.Owner = this;
                launchWindow.Initialize();
                launchWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Launch] 打开启动中心失败: {ex.Message}");
                // 降级为直接启动
                if (!_gameConfig.LaunchGame())
                    CyberMessageBox.Show(this, "启动游戏失败，请检查游戏路径和可执行文件设置。", "启动失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ═════════════════════════════════════════
        //  UI 更新辅助
        // ═════════════════════════════════════════

        private void UpdateGameIcon(string gameName)
        {
            try
            {
                var iconPath = _gameConfig.GetGameIconPath(gameName);
                var bitmap = ImageLoader.LoadFrozen(iconPath, decodePixelWidth: 64);
                if (bitmap != null)
                {
                    CurrentGameIcon.Source = bitmap;
                    CurrentGameIcon.Visibility = Visibility.Visible;
                    CurrentGameIconPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CurrentGameIcon.Source = null;
                    CurrentGameIcon.Visibility = Visibility.Collapsed;
                    CurrentGameIconPlaceholder.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                // 有明确回退（隐藏图标、显示占位符），结构不动；留一条调试痕迹
                Debug.WriteLine($"[MainWindow] 加载游戏图标失败: {ex.Message}");
                CurrentGameIcon.Visibility = Visibility.Collapsed;
                CurrentGameIconPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private void UpdateModCountText()
        {
            var cat = _vm.Categories.SelectedCategory;
            var name = cat?.DisplayText;
            if (string.IsNullOrEmpty(name))
                name = LanguageManager.IsEnglish ? "All MODs" : "全部 MOD";
            ModCountText.Text = name;
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            var visibleMods = _vm.ModList.Mods;
            var hasSearchText = !string.IsNullOrWhiteSpace(_vm.ModList.SearchText);
            var isEmpty = visibleMods == null || visibleMods.Count == 0;

            if (isEmpty)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                if (_vm.ModList.IsGridView)
                {
                    ModsCardView.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ModsListViewContainer.Visibility = Visibility.Collapsed;
                }

                if (hasSearchText)
                {
                    EmptyStateIcon.Text = "🔍";
                    EmptyStateTitle.Text = LanguageManager.IsEnglish ? "No matching MODs found" : "未找到匹配的 MOD";
                    EmptyStateSubtitle.Text = LanguageManager.IsEnglish ? "Try different keywords or switch category" : "尝试修改搜索关键词或切换分类";
                }
                else
                {
                    EmptyStateIcon.Text = "📦";
                    EmptyStateTitle.Text = LanguageManager.IsEnglish ? "No MODs yet" : "还没有 MOD";
                    EmptyStateSubtitle.Text = LanguageManager.IsEnglish
                        ? "Click \"Import MOD\" or drag files here"
                        : "点击「导入 MOD」按钮或拖拽文件到此处";
                }
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                if (_vm.ModList.IsGridView)
                    ModsCardView.Visibility = Visibility.Visible;
                else
                    ModsListViewContainer.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 分类切换时的内容淡入淡出动画。
        /// </summary>
        private void AnimateContentTransition(Action updateAction)
        {
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fadeOut.Completed += (_, _) =>
            {
                updateAction();
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                ModContentArea.BeginAnimation(OpacityProperty, fadeIn);
            };
            ModContentArea.BeginAnimation(OpacityProperty, fadeOut);
        }

        private Task RefreshAfterModChange()
        {
            try
            {
                // 这里曾经是 ItemsSource = null 再重新赋值的"拔插"。
                // ModList.Mods 是 ObservableCollection 且实例从不替换，增删改本就会
                // 经 INotifyCollectionChanged 自动反映到界面，拔插不增加任何正确性，
                // 却会强制重建全部容器 —— 滚动位置归零、选中项丢失、悬停动画被打断。
                // 现在数据源在 XAML 绑定，这里只负责刷新那些不参与绑定的统计文字。
                UpdateNavCounts();
                UpdateModCountText();
                UpdateEmptyState();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "刷新MOD显示失败");
            }

            return Task.CompletedTask;
        }

        // ═════════════════════════════════════════
        //  国际化
        // ═════════════════════════════════════════

        private void ApplyLocalization()
        {
            if (LanguageManager.IsEnglish)
            {
                Title = "AiJiang MOD Manager";
                SearchPlaceholder.Text = "Search MOD name...";
                SidebarLogoText.Text = "AiJiang MOD Manager";
                CurrentGameLabel.Text = "Current Game";
                NavLibraryHeader.Text = "Library";
                NavAllModsText.Text = "All MODs";
                NavEnabledText.Text = "Enabled";
                NavDisabledText.Text = "Disabled";
                NavCategoriesHeader.Text = "Categories";
                ConflictCheckText.Text = "Conflicts";
                ImportModText.Text = "Import";
                LaunchGameText.Text = "Launch";
                SelectAllText.Text = "Select All";
                BatchEnableText.Text = "Enable";
                BatchDisableText.Text = "Disable";
                BatchDeleteText.Text = "Uninstall";
                LoadingText.Text = "Loading...";
                CtxMenuRename.Header = "Rename";
                CtxMenuDelete.Header = "Delete";
            }
            else
            {
                Title = "爱酱MOD管理器";
                SearchPlaceholder.Text = "搜索 MOD 名称...";
                SidebarLogoText.Text = "爱酱MOD管理器";
                CurrentGameLabel.Text = "当前游戏";
                NavLibraryHeader.Text = "库";
                NavAllModsText.Text = "全部 MOD";
                NavEnabledText.Text = "已启用";
                NavDisabledText.Text = "已禁用";
                NavCategoriesHeader.Text = "分\u2009类\u2009目\u2009录";
                ConflictCheckText.Text = "冲突检测";
                ImportModText.Text = "导入";
                LaunchGameText.Text = "启动游戏";
                SelectAllText.Text = "全选";
                BatchEnableText.Text = "启用";
                BatchDisableText.Text = "禁用";
                BatchDeleteText.Text = "卸载";
                LoadingText.Text = "加载中...";
                CtxMenuRename.Header = "重命名";
                CtxMenuDelete.Header = "删除";
            }

            // 更新当前导航标题
            UpdateModCountText();
            // 更新空状态文本
            UpdateEmptyState();
            // 更新用户状态文本
            UpdateUserStatusDisplay();
            // 更新排序文本
            var sortLabel = LanguageManager.IsEnglish ? "Sort: Name" : "排序: 名称";
            if (SortText != null) SortText.Text = sortLabel;
        }

        // ═════════════════════════════════════════
        //  背景自定义
        // ═════════════════════════════════════════

        private void ApplyBackground(BackgroundSettings bg)
        {
            try
            {
                // 重置所有背景层
                BgImage.Visibility = Visibility.Collapsed;
                BgImage.Source = null;
                BgImage.Effect = null;
                BgSolidLayer.Visibility = Visibility.Collapsed;
                BgOverlay.Visibility = Visibility.Collapsed;

                switch (bg.Mode)
                {
                    case BackgroundMode.Image:
                        if (!string.IsNullOrEmpty(bg.ImagePath) && File.Exists(bg.ImagePath))
                        {
                            var bitmap = ImageLoader.LoadFrozen(bg.ImagePath, ignoreImageCache: true);
                            if (bitmap == null)
                            {
                                break;
                            }

                            BgImage.Source = bitmap;
                            BgImage.Opacity = bg.Opacity;
                            BgImage.Visibility = Visibility.Visible;

                            if (bg.BlurRadius > 0)
                            {
                                BgImage.Effect = new BlurEffect { Radius = bg.BlurRadius * 30 };
                            }

                            BgOverlay.Visibility = Visibility.Visible;
                            Console.WriteLine($"[MainWindow] 背景图已应用: {bg.ImagePath} (Opacity={bg.Opacity:F2}, Blur={bg.BlurRadius:F2})");
                        }
                        else if (!string.IsNullOrEmpty(bg.ImagePath))
                        {
                            Console.WriteLine($"[MainWindow] 背景图路径不存在，跳过: {bg.ImagePath}");
                        }
                        break;

                    case BackgroundMode.SolidColor:
                        try
                        {
                            var color = (Color)ColorConverter.ConvertFromString(bg.SolidColor ?? "#030303");
                            BgSolidLayer.Background = new SolidColorBrush(color);
                            BgSolidLayer.Opacity = bg.Opacity;
                            BgSolidLayer.Visibility = Visibility.Visible;
                        }
                        catch (Exception ex)
                        {
                            // 有明确回退（回落到默认底色），结构不动；留一条调试痕迹
                            Debug.WriteLine($"[MainWindow] 背景纯色解析失败，回落默认: {ex.Message}");
                            BgSolidLayer.Background = new SolidColorBrush(Color.FromRgb(3, 3, 3));
                            BgSolidLayer.Visibility = Visibility.Visible;
                        }
                        break;

                    case BackgroundMode.Gradient:
                    default:
                        // 默认渐变模式 - 不显示额外背景层，使用原始 BgBaseBrush
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ApplyBackground 异常: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════
        //  控制台输出重定向
        // ═════════════════════════════════════════


        // ═════════════════════════════════════════
        //  辅助方法
        // ═════════════════════════════════════════

        // 窗口命令处理
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);
        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);
    }
}
