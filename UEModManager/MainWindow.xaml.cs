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
        private readonly HealthCheckService? _healthCheck;

        // ── UI 状态 ──
        private DispatcherTimer? _statsTimer = null;
        private DispatcherTimer? _searchDebounceTimer;
        private bool _isDragging;
        private Point _startPoint;
        private string _activeNavTag = "全部";


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
                RedirectConsoleOutput();
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
                _healthCheck = sp.GetService<HealthCheckService>();

                // 订阅认证事件
                if (_localAuthService != null)
                {
                    _localAuthService.AuthStateChanged += OnLocalAuthStateChanged;
                    UpdateUserStatusDisplay();
                }

                // 订阅 ViewModel 事件
                _vm.ModList.ModSelected += OnModSelected;
                _vm.ModDetail.ModStateChanged += async () => await RefreshAfterModChange();
                _vm.ModDetail.CloseRequested += () => _vm.IsDetailPanelOpen = false;

                // 订阅 Profile 变化事件
                _vm.ProfileService.ProfileChanged += OnProfileSelectorUpdate;
                _vm.ProfileService.ProfileListChanged += () => Dispatcher.Invoke(UpdateProfileSelector);

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
                    LanguageManager.LanguageChanged += _ => Dispatcher.Invoke(ApplyLocalization);
                    ApplyLocalization();
                }
                catch { }

                // 背景
                try
                {
                    BackgroundManager.Initialize();
                    BackgroundManager.BackgroundChanged += bg => Dispatcher.Invoke(() => ApplyBackground(bg));
                    ApplyBackground(BackgroundManager.Settings);
                }
                catch { }

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

                // 绑定数据源
                CategoryList.ItemsSource = _vm.Categories.Categories;
                ModsCardView.ItemsSource = _vm.ModList.Mods;
                ModsListView.ItemsSource = _vm.ModList.Mods;

                // 更新 UI
                UpdateNavCounts();
                UpdateModCountText();

                // Phase 11: 启动时检查未完成事务（崩溃恢复）
                await CheckForCrashesAsync();

                // Phase 11: 启动时健康检查（结果写入日志）
                await LogHealthReportAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化失败: {ex}");
                _logger?.LogError(ex, "MainWindow 初始化失败");
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

                var rollbackCount = candidates.Count(c => c.Action == UEModManager.Services.Recovery.RecoveryAction.RollbackRecommended);
                var cleanupCount = candidates.Count(c => c.Action == UEModManager.Services.Recovery.RecoveryAction.MarkFailedRecommended);

                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"检测到 {candidates.Count} 个未完成的部署事务（可能是上次崩溃留下的）：");
                summary.AppendLine();
                foreach (var c in candidates.Take(5))
                {
                    summary.AppendLine($"  • {c.CreatedAt:yyyy-MM-dd HH:mm}  [{c.Status}]  {c.Reason}");
                }
                if (candidates.Count > 5) summary.AppendLine($"  …还有 {candidates.Count - 5} 个");
                summary.AppendLine();
                summary.Append("是否回滚所有可回滚事务并清理失败记录？");

                var result = MessageBox.Show(this, summary.ToString(),
                    "崩溃恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;

                int succeeded = 0, failed = 0;
                foreach (var c in candidates)
                {
                    if (await _crashRecovery.ApplyRecoveryAsync(c.TransactionId, c.Action))
                        succeeded++;
                    else
                        failed++;
                }

                MessageBox.Show(this,
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
            _statsTimer?.Stop();
            _searchDebounceTimer?.Stop();
            DisposeTrayIcon();
        }

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
                catch { }
            }
            return IntPtr.Zero;
        }

        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e) { }

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
                            catch { }
                        };
                        menu.Items.Add(accountItem);

                        if (_localAuthService.CurrentUser?.IsAdmin == true)
                        {
                            var adminItem = new MenuItem { Header = LanguageManager.IsEnglish ? "Admin Panel" : "管理面板", Style = FindResource("CyberMenuItem") as Style };
                            adminItem.Click += (_, _) => { try { new AdminDashboardWindow { Owner = this }.ShowDialog(); } catch { } };
                            menu.Items.Add(adminItem);
                        }

                        menu.Items.Add(new Separator { Style = FindResource("CyberMenuSeparator") as Style });

                        var logoutItem = new MenuItem { Header = LanguageManager.IsEnglish ? "Log Out" : "退出登录", Style = FindResource("CyberMenuItemDanger") as Style };
                        logoutItem.Click += async (_, _) =>
                        {
                            try { await _localAuthService.LogoutAsync(); UpdateUserStatusDisplay(); }
                            catch { }
                        };
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
                addItem.Click += async (_, _) =>
                {
                    var dialog = new AddCustomGameDialog { Owner = this };
                    if (dialog.ShowDialog() == true)
                    {
                        // 保存自定义游戏的引擎类型
                        var engineType = dialog.IsAutoDetect
                            ? GameConfigService.AutoDetectEngine(dialog.GamePathTextBox.Text)
                            : dialog.SelectedEngineType;
                        if (engineType == Models.EngineType.Unknown)
                            engineType = Models.EngineType.UnrealEngine; // 无法识别时默认 UE
                        await _gameConfig.SetGameEngineAsync(dialog.GameName, engineType);

                        ShowGamePathDialog(dialog.GameName);
                    }
                };
                menu.Items.Add(addItem);

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

                // 关闭方案管理窗口后刷新
                UpdateProfileSelector();
                _ = _vm.RefreshFromRepositoryAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Profile] 打开方案管理失败: {ex.Message}");
            }
        }

        private void OnProfileSelectorUpdate(Models.InstanceProfile? profile)
        {
            Dispatcher.Invoke(UpdateProfileSelector);
        }

        private void UpdateProfileSelector()
        {
            var profile = _vm.ProfileService.CurrentProfile;
            if (profile != null)
            {
                ProfileSelectorName.Text = profile.Name;
                ProfileSelectorSummary.Text = $"{profile.EnabledCount}/{profile.TotalCount} 已启用";
            }
            else
            {
                ProfileSelectorName.Text = "未选择";
                ProfileSelectorSummary.Text = "";
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

        private void ShowGamePathDialog(string gameName)
        {
            SafeEvent.Run(this, async () =>
            {
            var dialog = new GamePathDialog(gameName) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                var backupPath = dialog.BackupPath;
                if (string.IsNullOrEmpty(backupPath) || backupPath.Contains("AppData") || backupPath.StartsWith("C:\\Users"))
                {
                    backupPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups", $"{gameName}_备份");
                    Directory.CreateDirectory(backupPath);
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
                    ModsCardView.ItemsSource = _vm.ModList.Mods;
                    ModsListView.ItemsSource = _vm.ModList.Mods;
                    CategoryList.ItemsSource = _vm.Categories.Categories;
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
            _searchDebounceTimer?.Stop();
            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer.Stop();
                _vm.ModList.SearchText = SearchBox.Text;
                UpdateModCountText();
            };
            _searchDebounceTimer.Start();
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

        private async void AddCategory_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var name = CyberInputDialog.Show(this, "新增分类", "请输入分类名称:");
            if (!string.IsNullOrWhiteSpace(name))
                await _vm.Categories.AddCategoryAsync(name.Trim());
        }

        private void CategoryContextMenu_Opened(object sender, RoutedEventArgs e) { }

        private async void RenameCategoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (CategoryList.SelectedItem is CategoryItem cat && !CategoryItem.SystemNames.Contains(cat.Name))
            {
                var newName = CyberInputDialog.Show(this, "重命名分类", "请输入新名称:", cat.DisplayText);
                if (!string.IsNullOrWhiteSpace(newName) && newName != cat.DisplayText)
                    await _vm.Categories.DoRenameCategoryAsync(cat, newName.Trim());
            }
        }

        private async void DeleteCategoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (CategoryList.SelectedItem is CategoryItem cat && !CategoryItem.SystemNames.Contains(cat.Name))
            {
                var r = CyberMessageBox.Show(this, $"确认删除分类 '{cat.DisplayText}'？", "确认", MessageBoxButton.YesNo);
                if (r == MessageBoxResult.Yes)
                    await _vm.Categories.DeleteCategoryAsync(cat);
            }
        }

        private void CategoryList_ContextMenuOpening(object sender, ContextMenuEventArgs e) { }

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
            if (target != null && target != draggedCat)
            {
                var oldIdx = items.IndexOf(draggedCat);
                var newIdx = items.IndexOf(target);
                if (oldIdx >= 0 && newIdx >= 0)
                    items.Move(oldIdx, newIdx);
            }
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

        private void OnModSelected(ModInfo? mod)
        {
            _vm.ModDetail.CurrentMod = mod;
            _vm.IsDetailPanelOpen = mod != null;
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

        private async void ModToggle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var mod = (sender as FrameworkElement)?.Tag as ModInfo;
            if (mod == null) return;

            await ToggleModFromUiAsync(mod, !mod.IsEnabled);
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
                        foreach (var package in importedPackages)
                            await _vm.DeployToggleAsync(package.PackageKey, true);
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

        private void ModContextMenu_Opened(object sender, RoutedEventArgs e) { }

        private async void EnableModMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mod = GetModFromContextMenu(sender);
            if (mod != null && !mod.IsEnabled)
                await ToggleModFromUiAsync(mod, true);
        }

        private async void DisableModMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mod = GetModFromContextMenu(sender);
            if (mod != null && mod.IsEnabled)
                await ToggleModFromUiAsync(mod, false);
        }

        private async void RenameModMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mod = GetModFromContextMenu(sender);
            if (mod != null)
                await RenameModFromUiAsync(mod);
        }

        private async void ChangePreviewMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mod = GetModFromContextMenu(sender);
            if (mod != null)
                await ChangePreviewFromUiAsync(mod);
        }

        private async void DeleteModMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mod = GetModFromContextMenu(sender);
            if (mod != null)
                await DeleteModFromUiAsync(mod);
        }

        private ModInfo? GetModFromContextMenu(object sender)
        {
            if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
                return fe.DataContext as ModInfo ?? fe.Tag as ModInfo;
            return _vm.ModList.SelectedMod;
        }

        private async Task<bool> ToggleModFromUiAsync(ModInfo mod, bool enable)
        {
            if (!await _vm.ToggleModAsync(mod, enable)) return false;

            UpdateNavCounts();
            UpdateModCountText();
            UpdateEmptyState();
            return true;
        }

        private async Task RenameModFromUiAsync(ModInfo mod)
        {
            var newName = CyberInputDialog.Show(this, "编辑MOD", "请输入MOD显示名称:", mod.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == mod.Name) return;

            await RenameModFromUiAsync(mod, newName);
        }

        private async Task<bool> RenameModFromUiAsync(ModInfo mod, string newName)
        {
            if (!await _vm.RenameModAsync(mod, newName)) return false;

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

            if (dialog.ShowDialog(this) != true) return false;
            if (!await _vm.ChangePreviewAsync(mod, dialog.FileName)) return false;

            UpdateNavCounts();
            UpdateModCountText();
            return true;
        }

        private async Task<bool> DeleteModFromUiAsync(ModInfo mod, bool confirm = true)
        {
            if (confirm)
            {
                var r = CyberMessageBox.Show(this, $"确认删除 '{mod.Name}'？\n此操作会从当前方案、包仓库和已部署文件中移除此 MOD。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return false;
            }

            if (!await _vm.DeletePackageModAsync(mod)) return false;

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

        private void MainContentArea_PreviewMouseDown(object sender, MouseButtonEventArgs e) { }

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

        private async void ChangePreviewDirect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var mod = (sender as FrameworkElement)?.Tag as ModInfo;
            if (mod != null)
                await ChangePreviewFromUiAsync(mod);
        }

        private async void DeleteModDirect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var mod = (sender as FrameworkElement)?.Tag as ModInfo;
            if (mod != null)
                await DeleteModFromUiAsync(mod);
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
        private async void OpenConflictPanel()
        {
            try
            {
                var result = await _vm.ConflictAnalysis.AnalyzeAsync();
                var win = new Views.ConflictResultWindow(_vm.ConflictAnalysis, result.Conflicts) { Owner = this };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Conflict] v2.0 冲突分析失败: {ex.Message}");
                // 回退到旧版冲突检测
                ConflictCheckButton_Click(null, new RoutedEventArgs());
            }
        }

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
            catch { }
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
            catch
            {
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
                ModsCardView.ItemsSource = null;
                ModsCardView.ItemsSource = _vm.ModList.Mods;
                ModsListView.ItemsSource = null;
                ModsListView.ItemsSource = _vm.ModList.Mods;
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
                        catch
                        {
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

        private void RedirectConsoleOutput()
        {
            try
            {
                var logPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "console.log");
                var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                var writer = new StreamWriter(fs) { AutoFlush = true };
                Console.SetOut(writer);
                Console.SetError(writer);
            }
            catch { }
        }

        // ═════════════════════════════════════════
        //  辅助方法
        // ═════════════════════════════════════════

        private static string NormalizeGameName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("（", "(").Replace("）", ")").Replace("·", "·").Trim();
        }

        // 窗口命令处理
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);
        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);
        private void ConflictCheckButton_Click(object sender, RoutedEventArgs e) { }
        private void DisposeTrayIcon() { }
    }
}
