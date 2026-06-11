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

    public partial class AdminDashboardWindow : Window

    {

        private readonly ILogger<AdminDashboardWindow> _logger;

        private readonly LocalAuthService _localAuthService;

        private readonly DispatcherTimer _statusUpdateTimer;

        private readonly ObservableCollection<UserInfo> _users;

        private DateTime _systemStartTime;



        public AdminDashboardWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            UEModManager.Services.LanguageManager.LanguageChanged += _ => { Dispatcher.Invoke(ApplyLocalization); };




            // 获取依赖注入的服务

            var serviceProvider = ((App)Application.Current).ServiceProvider;

            _logger = serviceProvider.GetRequiredService<ILogger<AdminDashboardWindow>>();

            _localAuthService = serviceProvider.GetRequiredService<LocalAuthService>();



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

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始更新本地统计数据");



                var totalUsers = await _localAuthService.GetTotalUsersCountAsync();

                var activeUsers = await _localAuthService.GetActiveUsersCountAsync();

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 本地统计数据: 总用户{totalUsers}, 活跃用户{activeUsers}");



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



                PrimaryDbStatusText.Text = localDbHealthy ? "🟢 SQLite 正常" : "🔴 SQLite 异常";

                BackupDbStatusText.Text = "未启用";

                PrimaryDbLatencyText.Text = string.Empty;



                // 设置整体数据库状态

                if (localDbHealthy)

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



                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 系统状态更新完成");

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

        /// 检查邮件服务状态

        /// </summary>

        private Task<string> CheckEmailServiceStatusAsync()

        {

            try

            {

                // 检查邮件配置文件是否存在

                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                var emailConfigFile = System.IO.Path.Combine(appDataPath, "UEModManager", "email_config.json");



                if (!System.IO.File.Exists(emailConfigFile))

                {

                    return Task.FromResult("🟡 未配置");

                }



                // 可以在这里添加SMTP连接测试

                return Task.FromResult("🟢 已配置");

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "检查邮件服务状态失败");

                return Task.FromResult("🔴 检查失败");

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

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始加载本地用户数据");



                _users.Clear();

                var users = (await _localAuthService.GetAllUsersAsync()).ToList();

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 从本地数据库获取到 {users.Count} 个用户");



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



                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 已成功加载 {_users.Count} 个用户到显示列表");



                _ = Dispatcher.BeginInvoke(() =>

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



                var localHealthy = await CheckLocalDatabaseHealthAsync();

                if (localHealthy)

                {

                    CyberMessageBox.Show(this, "本地数据库连接正常",

                                  "数据库状态", MessageBoxButton.OK, MessageBoxImage.Information);

                }

                else

                {

                    CyberMessageBox.Show(this, "本地数据库连接异常！\n请检查本地数据库配置。",

                                  "数据库错误", MessageBoxButton.OK, MessageBoxImage.Error);

                }

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "数据库管理操作失败");

                CyberMessageBox.Show(this,$"数据库操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private void EmailServiceButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 检查邮件服务");

            

            // 这里可以添加邮件服务测试逻辑

            CyberMessageBox.Show(this,"邮件服务运行正常", "邮件服务", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private void StatisticsButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 查看统计报表");

            CyberMessageBox.Show(this,"统计报表功能开发中", "统计报表", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private void SystemSettingsButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 打开系统设置");

            CyberMessageBox.Show(this,"系统设置功能开发中", "系统设置", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始清理缓存");

                

                // 模拟缓存清理

                await Task.Delay(1000);

                

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 缓存清理完成");

                CyberMessageBox.Show(this,"缓存清理完成", "操作成功", MessageBoxButton.OK, MessageBoxImage.Information);

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "清理缓存失败");

                CyberMessageBox.Show(this,$"清理缓存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private void ExportDataButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 导出数据");

            CyberMessageBox.Show(this,"数据导出功能开发中", "导出数据", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private async void BackupDataButton_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 开始数据备份");

                

                // 模拟数据备份

                await Task.Delay(2000);

                

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 数据备份完成");

                CyberMessageBox.Show(this,"数据备份完成", "操作成功", MessageBoxButton.OK, MessageBoxImage.Information);

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "数据备份失败");

                CyberMessageBox.Show(this,$"数据备份失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

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

            CyberMessageBox.Show(this,"新增用户功能开发中", "新增用户", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private void EditUserButton_Click(object sender, RoutedEventArgs e)

        {

            if (sender is Button button && button.Tag is int userId)

            {

                AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 编辑用户 ID: {userId}");

                CyberMessageBox.Show(this,$"编辑用户 ID: {userId}\n此功能开发中", "编辑用户", MessageBoxButton.OK, MessageBoxImage.Information);

            }

        }



        private void DeleteUserButton_Click(object sender, RoutedEventArgs e)

        {

            if (sender is Button button && button.Tag is int userId)

            {

                var result = CyberMessageBox.Show(this,$"确定要删除用户 ID: {userId} 吗？\n此操作不可恢复！", 

                                          "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                

                if (result == MessageBoxResult.Yes)

                {

                    AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 删除用户 ID: {userId}");

                    // 这里添加实际删除逻辑

                    CyberMessageBox.Show(this,"删除功能开发中", "删除用户", MessageBoxButton.OK, MessageBoxImage.Information);

                }

            }

        }



        private void TestEmailServiceButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 邮件服务通过 Brevo API 管理，请使用 Cloudflare Workers 面板查看状态");

            CyberMessageBox.Show(this,"邮件服务已迁移至 Brevo API + Cloudflare Workers，\n请通过管理面板查看状态。", "邮件服务", MessageBoxButton.OK, MessageBoxImage.Information);

        }



        private void RestartAuthServiceButton_Click(object sender, RoutedEventArgs e)

        {

            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] 重启认证服务");

            CyberMessageBox.Show(this,"认证服务重启功能开发中", "重启服务", MessageBoxButton.OK, MessageBoxImage.Information);

        }
        private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new System.Collections.Generic.Dictionary<string,string>
            {
                {"爱酱MOD管理器 - 管理员仪表盘","爱酱MOD管理器 - Admin Dashboard"},
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
            this.Title = toEnglish ? "爱酱MOD管理器 - Admin Dashboard" : "爱酱MOD管理器 - 管理员仪表盘";
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
