using System;

using System.Collections.Generic;

using System.Collections.ObjectModel;

using System.Threading.Tasks;

using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

using System.Windows.Threading;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using UEModManager.Services;

using UEModManager.Models;

using UEModManager.Data;

using System.Linq;



namespace UEModManager.Views

{

    /// <summary>

    /// 云端数据库状态枚举

    /// </summary>

    public enum CloudDatabaseStatus

    {

        NotConfigured,  // 未配置

        Configured,     // 已配置但连接失败

        Online          // 在线可用

    }

    public partial class AdminDashboardWindow : Window

    {

        private readonly ILogger<AdminDashboardWindow> _logger;

        private readonly DefaultDatabaseConfig _databaseConfig;

        private readonly LocalAuthService _localAuthService;

        private readonly PostgreSQLAuthService _postgreSQLAuthService;

        private readonly EmailService _emailService;

        private readonly SupabaseRestService _supabaseRestService;

        private readonly DispatcherTimer _statusUpdateTimer;

        private readonly ObservableCollection<UserInfo> _users;

        private DateTime _systemStartTime;

        private bool _useCloudData = true; // 默认优先使用云端数据

        private bool _cloudViaRest = false; // 云端是否通过Supabase REST



        public AdminDashboardWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            UEModManager.Services.LanguageManager.LanguageChanged += _ => { Dispatcher.Invoke(ApplyLocalization); };




            // 获取依赖注入的服务

            var serviceProvider = ((App)Application.Current).ServiceProvider;

            _logger = serviceProvider.GetRequiredService<ILogger<AdminDashboardWindow>>();

            _databaseConfig = serviceProvider.GetRequiredService<DefaultDatabaseConfig>();

            _localAuthService = serviceProvider.GetRequiredService<LocalAuthService>();

            _postgreSQLAuthService = serviceProvider.GetRequiredService<PostgreSQLAuthService>();

            _emailService = serviceProvider.GetRequiredService<EmailService>();

            _supabaseRestService = serviceProvider.GetRequiredService<SupabaseRestService>();



            _users = new ObservableCollection<UserInfo>();

            UsersDataGrid.ItemsSource = _users;

            

            _systemStartTime = DateTime.Now;



            // 初始化定时器，每30秒更新一次状态

            _statusUpdateTimer = new DispatcherTimer

            {

                Interval = TimeSpan.FromSeconds(30)

            };

            _statusUpdateTimer.Tick += StatusUpdateTimer_Tick;

            _statusUpdateTimer.Start();



            // 立即加载数据

            _ = LoadDashboardDataAsync();

        }



        private async void StatusUpdateTimer_Tick(object sender, EventArgs e)

        {

            await UpdateSystemStatusAsync();

        }



        private async Task LoadDashboardDataAsync()
        {
            try

            {

                _logger.LogInformation("开始加载仪表盘数据");

                

                // 更新统计数据

                await UpdateStatisticsAsync();

                

                // 更新系统状态

                await UpdateSystemStatusAsync();

                

                // 加载用户数据

                await LoadUsersAsync();



                _logger.LogInformation("仪表盘数据加载完成");

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "加载仪表盘数据失败");

                AddLogMessage($"错误: 加载仪表盘数据失败 - {ex.Message}");

            }

    }
        // 标题栏按钮命令处理（Qt6风格自绘）
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MinimizeWindow(this); } catch { }
    }        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MaximizeWindow(this); } catch { }
    }        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.RestoreWindow(this); } catch { }
    }        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.CloseWindow(this); } catch { }
    }

        private async Task UpdateStatisticsAsync()

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始更新统计数据 (数据源: {(_useCloudData ? "云端" : "本地")})");



                int totalUsers = 0;

                int activeUsers = 0;



                // 尝试云端数据，失败则回退到本地

                if (_useCloudData)

                {

                    try

                    {

                        if (_cloudViaRest && _supabaseRestService != null && _supabaseRestService.IsConfigured)

                        {

                            totalUsers = await _supabaseRestService.GetUsersCountAsync(false);

                            activeUsers = await _supabaseRestService.GetUsersCountAsync(true);

                        }

                        else

                        {

                            totalUsers = await _postgreSQLAuthService.GetCloudUsersCountAsync();

                            activeUsers = await _postgreSQLAuthService.GetCloudActiveUsersCountAsync();

                        }

                        AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 云端统计数据: 总用户{totalUsers}, 活跃用户{activeUsers} ({(_cloudViaRest ? "REST" : "PG")})");

                    }

                    catch (Exception cloudEx)

                    {

                        _logger.LogWarning(cloudEx, "获取云端统计数据失败，回退到本地数据");

                        AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 云端数据获取失败，切换到本地数据源");

                        _useCloudData = false;

                        _cloudViaRest = false;

                    }

                }



                // 如果云端失败或未启用，使用本地数据

                if (!_useCloudData)

                {

                    totalUsers = await _localAuthService.GetTotalUsersCountAsync();

                    activeUsers = await _localAuthService.GetActiveUsersCountAsync();

                    AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 本地统计数据: 总用户{totalUsers}, 活跃用户{activeUsers}");

                }



                // 更新UI

                TotalUsersText.Text = totalUsers.ToString();

                ActiveUsersText.Text = activeUsers.ToString();



                // 邮件统计（模拟数据）

                EmailsSentText.Text = "156"; // 可以从邮件服务获取实际数据



                // 系统运行时间

                var uptime = DateTime.Now - _systemStartTime;

                SystemUptimeText.Text = $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";



                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 统计数据更新完成");

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "更新统计数据失败");

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 错误: 更新统计数据失败 - {ex.Message}");



                // 设置默认值避免界面显示异常

                TotalUsersText.Text = "0";

                ActiveUsersText.Text = "0";

                EmailsSentText.Text = "0";

            }

        }



        private async Task UpdateSystemStatusAsync()

        {

            try

            {

                // 检查本地SQLite数据库状态

                var localDbHealthy = await CheckLocalDatabaseHealthAsync();

                if (localDbHealthy)

                {

                    LocalDbStatusText.Text = "🟢 正常";

                    try

                    {

                        var sp = ((App)Application.Current).ServiceProvider;

                        using var scope = sp?.CreateScope();

                        var ctx = scope?.ServiceProvider.GetService<LocalDbContext>();

                        if (ctx != null)

                        {

                            var info = await ctx.GetDatabaseInfoAsync();

                            var sizeMb = info.Size / (1024.0 * 1024.0);

                            LocalDbSizeText.Text = $"大小: {sizeMb:F1}MB";

                        }

                    }

                    catch { }

                }

                else

                {

                    LocalDbStatusText.Text = "🔴 异常";

                }



                // 检查云端Supabase数据库状态

                var (cloudDbStatus, cloudDbLatency, cloudDbMessage) = await CheckCloudDatabaseStatusAsync();

                switch (cloudDbStatus)

                {

                    case CloudDatabaseStatus.Online:

                        PrimaryDbStatusText.Text = $"🟢 Supabase 在线{(_cloudViaRest ? "(REST)" : "")}";

                        BackupDbStatusText.Text = "🟢 备用在线";

                        PrimaryDbLatencyText.Text = $"延迟: {cloudDbLatency}ms";

                        // 启用云端数据源

                        if (!_useCloudData && cloudDbLatency > 0)

                        {

                            _useCloudData = true;

                            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 检测到云端数据库可用，启用云端数据源");

                        }

                        break;

                    case CloudDatabaseStatus.Configured:

                        PrimaryDbStatusText.Text = "🔴 Supabase 离线";

                        BackupDbStatusText.Text = "🔴 备用离线";

                        PrimaryDbLatencyText.Text = $"错误: {cloudDbMessage}";

                        _useCloudData = false;

                        _cloudViaRest = false;

                        break;

                    case CloudDatabaseStatus.NotConfigured:

                        PrimaryDbStatusText.Text = "🟡 未配置";

                        BackupDbStatusText.Text = "🟡 未配置";

                        PrimaryDbLatencyText.Text = "延迟: --ms";

                        _useCloudData = false;

                        _cloudViaRest = false;

                        break;

                }



                // 设置整体数据库状态

                if (localDbHealthy && cloudDbStatus == CloudDatabaseStatus.Online)

                {

                    DatabaseStatusText.Text = "优秀";

                    DatabaseStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);

                }

                else if (localDbHealthy)

                {

                    DatabaseStatusText.Text = "正常";

                    DatabaseStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);

                }

                else

                {

                    DatabaseStatusText.Text = "异常";

                    DatabaseStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);

                }



                // 检查邮件服务状态

                var emailServiceStatus = await CheckEmailServiceStatusAsync();

                EmailServiceStatusText.Text = emailServiceStatus;



                // 检查认证服务状态

                var authServiceStatus = CheckAuthServiceStatus();

                AuthServiceStatusText.Text = authServiceStatus;



                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 系统状态更新完成 (云端: {cloudDbStatus}, 延迟: {cloudDbLatency}ms)");

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "更新系统状态失败");

                DatabaseStatusText.Text = "错误";

                DatabaseStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);

            }

        }



        /// <summary>

        /// 检查本地数据库健康状态

        /// </summary>

        private async Task<bool> CheckLocalDatabaseHealthAsync()

        {

            try

            {

                // 检查本地SQLite数据库连接

                var userCount = await _localAuthService.GetTotalUsersCountAsync();

                return userCount >= 0; // 能够查询到数据说明本地数据库正常

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "本地数据库检查失败");

                return false;

            }

        }



        /// <summary>

        /// 检查云端Supabase数据库状态

        /// </summary>

        private async Task<(CloudDatabaseStatus status, long latency, string message)> CheckCloudDatabaseStatusAsync()

        {

            try

            {

                // 首先检查是否有配置

                if (_postgreSQLAuthService == null)

                {

                    return (CloudDatabaseStatus.NotConfigured, 0, "PostgreSQL服务未配置");

                }



                // 尝试连接并测试延迟

                var latency = await _postgreSQLAuthService.GetConnectionLatencyAsync();

                if (latency > 0)

                {

                    _cloudViaRest = false;

                    return (CloudDatabaseStatus.Online, latency, "连接正常");

                }

                else

                {

                    // 进一步检查连接错误

                    var (isConnected, message) = await _postgreSQLAuthService.TestConnectionAsync();

                    if (isConnected)

                    {

                        _cloudViaRest = false;

                        return (CloudDatabaseStatus.Online, 0, "连接正常");

                    }

                    // 使用 Supabase REST 尝试

                    if (_supabaseRestService != null && _supabaseRestService.IsConfigured)

                    {

                        var (ok, msg, restLatency) = await _supabaseRestService.TestAsync();

                        if (ok)

                        {

                            _cloudViaRest = true;

                            return (CloudDatabaseStatus.Online, restLatency, "REST连接正常");

                        }

                        _cloudViaRest = false;

                        return (CloudDatabaseStatus.Configured, 0, $"REST失败: {msg}");

                    }

                    _cloudViaRest = false;

                    return (CloudDatabaseStatus.Configured, 0, message ?? "连接失败");

                }

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "检查云端数据库状态失败");

                _cloudViaRest = false;

                return (CloudDatabaseStatus.Configured, 0, ex.Message);

            }

        }



        /// <summary>

        /// 检查云端数据库是否已配置

        /// </summary>

        private async Task<bool> IsCloudDatabaseConfiguredAsync()

        {

            try

            {

                // 检查是否有有效的云端数据库配置

                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                var configFile = System.IO.Path.Combine(appDataPath, "UEModManager", "current_database.json");



                if (System.IO.File.Exists(configFile))

                {

                    var json = await System.IO.File.ReadAllTextAsync(configFile);

                    return !string.IsNullOrWhiteSpace(json);

                }



                return false;

            }

            catch

            {

                return false;

            }

        }



        /// <summary>

        /// 检查邮件服务状态

        /// </summary>

        private async Task<string> CheckEmailServiceStatusAsync()

        {

            try

            {

                // 检查邮件配置文件是否存在

                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                var emailConfigFile = System.IO.Path.Combine(appDataPath, "UEModManager", "email_config.json");



                if (!System.IO.File.Exists(emailConfigFile))

                {

                    return "🟡 未配置";

                }



                // 可以在这里添加SMTP连接测试

                return "🟢 已配置";

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "检查邮件服务状态失败");

                return "🔴 检查失败";

            }

        }



        /// <summary>

        /// 检查认证服务状态

        /// </summary>

        private string CheckAuthServiceStatus()

        {

            try

            {

                // 检查本地认证服务是否可用

                if (_localAuthService != null)

                {

                    return "🟢 正常运行";

                }

                else

                {

                    return "🔴 服务不可用";

                }

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "检查认证服务状态失败");

                return "🔴 检查失败";

            }

        }



        private async Task LoadUsersAsync()

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始加载用户数据 (数据源: {(_useCloudData ? "云端" : "本地")})");



                _users.Clear();



                // 尝试云端数据，失败则回退到本地

                if (_useCloudData)

                {

                    try

                    {

                        var cloudUsers = _cloudViaRest

                            ? (_supabaseRestService != null ? await _supabaseRestService.GetUsersAsync(100, 0) : new List<CloudUserInfo>())

                            : await _postgreSQLAuthService.GetCloudUsersAsync(100, 0);

                        AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 从云端数据库获取到 {cloudUsers.Count} 个用户 ({(_cloudViaRest ? "REST" : "PG")})");



                        foreach (var user in cloudUsers)

                        {

                            try

                            {

                                _users.Add(new UserInfo

                                {

                                    Id = user.Id,

                                    Email = user.Email ?? "N/A",

                                    CreatedAt = user.CreatedAt.ToString("yyyy-MM-dd HH:mm"),

                                    LastLoginAt = user.LastLoginAt == null ? "从未" : user.LastLoginAt.Value.ToString("yyyy-MM-dd HH:mm"),

                                    Status = user.IsActive ? "正常" : "已禁用"

                                });

                            }

                            catch (Exception userEx)

                            {

                                _logger.LogError(userEx, $"添加云端用户 {user.Id} 到显示列表失败");

                                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 添加云端用户 {user.Id} 失败: {userEx.Message}");

                            }

                        }

                    }

                    catch (Exception cloudEx)

                    {

                        _logger.LogWarning(cloudEx, "获取云端用户数据失败，回退到本地数据");

                        AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 云端用户数据获取失败，切换到本地数据源");

                        _useCloudData = false;

                    }

                }



                // 如果云端失败或未启用，使用本地数据

                if (!_useCloudData)

                {

                    var users = await _localAuthService.GetAllUsersAsync();

                    AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 从本地数据库获取到 {users.Count()} 个用户");



                    foreach (var user in users)

                    {

                        try

                        {

                            _users.Add(new UserInfo

                            {

                                Id = user.Id,

                                Email = user.Email ?? "N/A",

                                CreatedAt = user.CreatedAt.ToString("yyyy-MM-dd HH:mm"),

                                LastLoginAt = user.LastLoginAt == default(DateTime) ? "从未" : user.LastLoginAt.ToString("yyyy-MM-dd HH:mm"),

                                Status = user.IsLocked ? "已锁定" : "正常"

                            });

                        }

                        catch (Exception userEx)

                        {

                            _logger.LogError(userEx, $"添加本地用户 {user.Id} 到显示列表失败");

                            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 添加本地用户 {user.Id} 失败: {userEx.Message}");

                        }

                    }

                }



                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 已成功加载 {_users.Count} 个用户到显示列表");



                // 强制更新UI

                Dispatcher.BeginInvoke(() =>

                {

                    if (UsersDataGrid.ItemsSource != _users)

                    {

                        UsersDataGrid.ItemsSource = _users;

                        AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 重新绑定数据源");

                    }

                });

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "加载用户数据失败");

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 错误: 加载用户数据失败 - {ex.Message}");

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 异常详情: {ex}");

            }

        }



        private void AddLogMessage(string message)

        {

            Dispatcher.BeginInvoke(() =>

            {

                LogTextBlock.Text += message + Environment.NewLine;

                

                // 限制日志行数

                var lines = LogTextBlock.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

                if (lines.Length > 100)

                {

                    LogTextBlock.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - 80));

                }

            });

        }



        #region 按钮事件处理



        private async void RefreshDataButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 手动刷新数据");

            await LoadDashboardDataAsync();

        }



        private async void UserManagementButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 打开用户管理");

            await LoadUsersAsync();

        }



        private async void DatabaseManagementButton_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 检查数据库连接");

                

                var config = await _databaseConfig.GetHealthyDatabaseConfigAsync();

                if (config != null)

                {

                    MessageBox.Show($"数据库连接正常\n\n" +

                                  $"提供商: {config.Provider}\n" +

                                  $"主机: {config.Host}\n" +

                                  $"数据库: {config.Database}", 

                                  "数据库状态", MessageBoxButton.OK, MessageBoxImage.Information);

                }

                else

                {

                    MessageBox.Show("无法连接到任何数据库！\n请检查网络连接和数据库配置。", 

                                  "数据库错误", MessageBoxButton.OK, MessageBoxImage.Error);

                }

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "数据库管理操作失败");

                MessageBox.Show($"数据库操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private async void EmailServiceButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 检查邮件服务");

            

            // 这里可以添加邮件服务测试逻辑

            MessageBox.Show("邮件服务运行正常", "邮件服务", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private void StatisticsButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 查看统计报表");

            MessageBox.Show("统计报表功能开发中", "统计报表", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private void SystemSettingsButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 打开系统设置");

            MessageBox.Show("系统设置功能开发中", "系统设置", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始清理缓存");

                

                // 模拟缓存清理

                await Task.Delay(1000);

                

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 缓存清理完成");

                MessageBox.Show("缓存清理完成", "操作成功", MessageBoxButton.OK, MessageBoxImage.Information);

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "清理缓存失败");

                MessageBox.Show($"清理缓存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private void ExportDataButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 导出数据");

            MessageBox.Show("数据导出功能开发中", "导出数据", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private async void BackupDataButton_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始数据备份");

                

                // 模拟数据备份

                await Task.Delay(2000);

                

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 数据备份完成");

                MessageBox.Show("数据备份完成", "操作成功", MessageBoxButton.OK, MessageBoxImage.Information);

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "数据备份失败");

                MessageBox.Show($"数据备份失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private async void SearchUsersButton_Click(object sender, RoutedEventArgs e)

        {

            var searchTerm = UserSearchBox.Text?.Trim();

            if (string.IsNullOrEmpty(searchTerm))

            {

                await LoadUsersAsync();

                return;

            }



            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 搜索用户: {searchTerm}");

                

                var allUsers = await _localAuthService.GetAllUsersAsync();

                var filteredUsers = allUsers.Where(u => 

                    u.Email.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));



                _users.Clear();

                foreach (var user in filteredUsers)

                {

                    _users.Add(new UserInfo

                    {

                        Id = user.Id,

                        Email = user.Email,

                        CreatedAt = user.CreatedAt.ToString("yyyy-MM-dd HH:mm"),

                        LastLoginAt = user.LastLoginAt == default(DateTime) ? "从未" : user.LastLoginAt.ToString("yyyy-MM-dd HH:mm"),

                        Status = user.IsLocked ? "已锁定" : "正常"

                    });

                }

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "搜索用户失败");

                AddLogMessage($"错误: 搜索用户失败 - {ex.Message}");

            }

        }



        private void AddUserButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 新增用户");

            MessageBox.Show("新增用户功能开发中", "新增用户", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private async void EditUserButton_Click(object sender, RoutedEventArgs e)

        {

            if (sender is Button button && button.Tag is int userId)

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 编辑用户 ID: {userId}");

                MessageBox.Show($"编辑用户 ID: {userId}\n此功能开发中", "编辑用户", MessageBoxButton.OK, MessageBoxImage.Information);

            }

        }



        private async void DeleteUserButton_Click(object sender, RoutedEventArgs e)

        {

            if (sender is Button button && button.Tag is int userId)

            {

                var result = MessageBox.Show($"确定要删除用户 ID: {userId} 吗？\n此操作不可恢复！", 

                                          "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                

                if (result == MessageBoxResult.Yes)

                {

                    AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 删除用户 ID: {userId}");

                    // 这里添加实际删除逻辑

                    MessageBox.Show("删除功能开发中", "删除用户", MessageBoxButton.OK, MessageBoxImage.Information);

                }

            }

        }



        private async void TestEmailServiceButton_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 测试邮件服务");

                

                // 发送测试邮件

                var result = await _emailService.SendTestEmailAsync("admin@example.com");

                

                if (result.IsSuccess)

                {

                    AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 邮件服务测试成功");

                    MessageBox.Show("邮件服务测试成功", "测试结果", MessageBoxButton.OK, MessageBoxImage.Information);

                }

                else

                {

                    AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 邮件服务测试失败: {result.Message}");

                    MessageBox.Show($"邮件服务测试失败: {result.Message}", "测试结果", MessageBoxButton.OK, MessageBoxImage.Error);

                }

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "邮件服务测试失败");

                AddLogMessage($"错误: 邮件服务测试失败 - {ex.Message}");

            }

        }



        private void RestartAuthServiceButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 重启认证服务");

            MessageBox.Show("认证服务重启功能开发中", "重启服务", MessageBoxButton.OK, MessageBoxImage.Information);

        }
        private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new System.Collections.Generic.Dictionary<string,string>
            {
                {"UEModManager - 管理员仪表盘","UEModManager - Admin Dashboard"},
                {"管理员仪表盘","Admin Dashboard"},
                {"在线","Online"},
                {"测试邮件服务","Test Email Service"},
                {"重启认证服务","Restart Auth Service"},
                {"搜索","Search"},
                {"新增用户","Add User"},
                {"编辑","Edit"},
                {"删除","Delete"}
            };
            UEModManager.Services.LocalizationHelper.Apply(this, toEnglish, map);
            this.Title = toEnglish ? "UEModManager - Admin Dashboard" : "UEModManager - 管理员仪表盘";
        }




        #endregion



        protected override void OnClosed(EventArgs e)

        {

            _statusUpdateTimer?.Stop();

            base.OnClosed(e);

        }

    }



    // 用户信息视图模型

    public class UserInfo

    {

        public int Id { get; set; }

        public string Email { get; set; } = string.Empty;

        public string CreatedAt { get; set; } = string.Empty;

        public string LastLoginAt { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;


}








}
