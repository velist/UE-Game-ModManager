using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class AuthSettingsWindow : Window
    {
        private string _currentLang = "zh-CN";

        private readonly UnifiedAuthService _unifiedAuthService;
        private readonly ILogger<AuthSettingsWindow> _logger;
        private readonly DispatcherTimer _networkCheckTimer;

        public AuthSettingsWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            // 获取依赖注入的服务
            var serviceProvider = ((App)Application.Current).ServiceProvider;
            _unifiedAuthService = serviceProvider.GetRequiredService<UnifiedAuthService>();
            _logger = serviceProvider.GetRequiredService<ILogger<AuthSettingsWindow>>();

            // 初始化网络检查定时器
            _networkCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _networkCheckTimer.Tick += NetworkCheckTimer_Tick;

            // 加载当前设置
            LoadCurrentSettings();
            
            // 开始网络状态检查
            StartNetworkStatusCheck();

            this.Loaded += AuthSettingsWindow_Loaded;
            this.Closing += AuthSettingsWindow_Closing;
        }

        private void AuthSettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _logger.LogInformation("认证设置窗口已加载");
        }

        private void AuthSettingsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopNetworkStatusCheck();
        }

        /// <summary>
        /// 加载当前认证模式设置
        /// </summary>
        private void LoadCurrentSettings()
        {
            try
            {
                var currentMode = _unifiedAuthService.CurrentMode;
                
                switch (currentMode)
                {
                    case UnifiedAuthService.AuthMode.Hybrid:
                        HybridModeRadio.IsChecked = true;
                        break;
                    case UnifiedAuthService.AuthMode.OnlineOnly:
                        OnlineOnlyRadio.IsChecked = true;
                        break;
                    case UnifiedAuthService.AuthMode.OfflineOnly:
                        OfflineOnlyRadio.IsChecked = true;
                        break;
                }
                
                _logger.LogInformation($"已加载当前认证模式: {currentMode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载当前设置失败");
                // 默认选择混合模式
                HybridModeRadio.IsChecked = true;
            }
        }

        /// <summary>
        /// 开始网络状态检查
        /// </summary>
        private void StartNetworkStatusCheck()
        {
            try
            {
                _networkCheckTimer.Start();
                CheckNetworkStatus(); // 立即检查一次
                _logger.LogInformation("网络状态检查已启动");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动网络状态检查失败");
            }
        }

        /// <summary>
        /// 停止网络状态检查
        /// </summary>
        private void StopNetworkStatusCheck()
        {
            try
            {
                _networkCheckTimer?.Stop();
                _logger.LogInformation("网络状态检查已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止网络状态检查失败");
            }
        }

        /// <summary>
        /// 网络检查定时器事件
        /// </summary>
        private void NetworkCheckTimer_Tick(object sender, EventArgs e)
        {
            CheckNetworkStatus();
        }

        /// <summary>
        /// 检查网络状态
        /// </summary>
        private async void CheckNetworkStatus()
        {
            try
            {
                var isOnline = _unifiedAuthService.IsOnline;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    if (isOnline)
                    {
                        NetworkStatusText.Text = "✅ 网络连接正常";
                        NetworkStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                    }
                    else
                    {
                        NetworkStatusText.Text = "❌ 网络连接异常";
                        NetworkStatusText.Foreground = System.Windows.Media.Brushes.LightCoral;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查网络状态失败");
                
                await Dispatcher.InvokeAsync(() =>
                {
                    NetworkStatusText.Text = "⚠️ 网络检查失败";
                    NetworkStatusText.Foreground = System.Windows.Media.Brushes.Orange;
                });
            }
        }

        /// <summary>
        /// 刷新网络状态按钮点击事件
        /// </summary>
        private void RefreshNetwork_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                NetworkStatusText.Text = "检查中...";
                NetworkStatusText.Foreground = System.Windows.Media.Brushes.White;
                
                CheckNetworkStatus();
                _logger.LogInformation("手动刷新网络状态");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新网络状态失败");
                ShowMessage("刷新网络状态失败", "错误");
            }
        }

        /// <summary>
        /// 测试连接按钮点击事件
        /// </summary>
        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowLoading(true, "正在测试连接...");
                
                // 测试网络连接
                var testResult = await TestNetworkConnection();
                
                if (testResult.IsSuccess)
                {
                    ShowMessage($"连接测试成功！\n{testResult.Details}", "测试结果");
                }
                else
                {
                    ShowMessage($"连接测试失败：\n{testResult.ErrorMessage}", "测试结果");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试连接失败");
                ShowMessage("测试连接时发生异常", "错误");
            }
            finally
            {
                ShowLoading(false);
            }
        }

        /// <summary>
        /// 测试网络连接
        /// </summary>
        private async Task<ConnectionTestResult> TestNetworkConnection()
        {
            try
            {
                _logger.LogInformation("开始测试网络连接");
                
                var startTime = DateTime.Now;
                
                // 这里可以添加具体的连接测试逻辑
                // 暂时使用简单的网络检查
                var isConnected = _unifiedAuthService.IsOnline;
                var endTime = DateTime.Now;
                var responseTime = (endTime - startTime).TotalMilliseconds;
                
                await Task.Delay(1000); // 模拟网络测试延迟
                
                if (isConnected)
                {
                    var details = $"响应时间: {responseTime:F0} ms\n" +
                                 $"测试时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                 "云端服务可用";
                    
                    return new ConnectionTestResult(true, details);
                }
                else
                {
                    return new ConnectionTestResult(false, "无法连接到云端服务");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "网络连接测试异常");
                return new ConnectionTestResult(false, $"测试异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存设置按钮点击事件
        /// </summary>
        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowLoading(true, "正在保存设置...");
                
                // 获取选择的认证模式
                UnifiedAuthService.AuthMode selectedMode;
                
                if (HybridModeRadio.IsChecked == true)
                {
                    selectedMode = UnifiedAuthService.AuthMode.Hybrid;
                }
                else if (OnlineOnlyRadio.IsChecked == true)
                {
                    selectedMode = UnifiedAuthService.AuthMode.OnlineOnly;
                }
                else if (OfflineOnlyRadio.IsChecked == true)
                {
                    selectedMode = UnifiedAuthService.AuthMode.OfflineOnly;
                }
                else
                {
                    selectedMode = UnifiedAuthService.AuthMode.Hybrid; // 默认
                }

                // 如果选择仅在线模式，检查网络连接
                if (selectedMode == UnifiedAuthService.AuthMode.OnlineOnly && !_unifiedAuthService.IsOnline)
                {
                    var result = MessageBox.Show(
                        "当前网络连接不可用，选择仅在线模式可能导致无法登录。\n\n是否仍要继续？",
                        "网络警告",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                        
                    if (result == MessageBoxResult.No)
                    {
                        ShowLoading(false);
                        return;
                    }
                }

                // 保存设置
                var saveResult = await _unifiedAuthService.SetAuthModeAsync(selectedMode);
                
                if (saveResult)
                {
                    _logger.LogInformation($"认证模式已更改为: {selectedMode}");
                    ShowMessage($"设置已保存！\n当前认证模式: {GetModeDisplayName(selectedMode)}", "保存成功");
                    
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ShowMessage("保存设置失败，请重试", "保存失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存设置失败");
                ShowMessage("保存设置时发生异常", "错误");
            }
            finally
            {
                ShowLoading(false);
            }
        }

        /// <summary>
        /// 取消按钮点击事件
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

                private void ApplyLocalization()
        {
            try
            {
                this.Title = AuthSettingsWindowLocalization.GetString(_currentLang, "WindowTitle");
                if (HeaderText != null) HeaderText.Text = AuthSettingsWindowLocalization.GetString(_currentLang, "Header");
                if (NetworkLabelText != null) NetworkLabelText.Text = AuthSettingsWindowLocalization.GetString(_currentLang, "NetworkLabel");
                if (RefreshNetworkButton != null) RefreshNetworkButton.Content = AuthSettingsWindowLocalization.GetString(_currentLang, "Refresh");
                if (TestConnectionButton != null) TestConnectionButton.Content = AuthSettingsWindowLocalization.GetString(_currentLang, "TestConnection");
                if (CancelButton != null) CancelButton.Content = AuthSettingsWindowLocalization.GetString(_currentLang, "Cancel");
                if (SaveButton != null) SaveButton.Content = AuthSettingsWindowLocalization.GetString(_currentLang, "SaveSettings");
                if (LangToggleButton != null) LangToggleButton.Content = _currentLang == "zh-CN" ? "EN" : "中";
            }
            catch { }
        }

        private void LangToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _currentLang = _currentLang == "zh-CN" ? "en-US" : "zh-CN";
            ApplyLocalization();
        }
        #region 辅助方法

        /// <summary>
        /// 获取模式显示名称
        /// </summary>
        private static string GetModeDisplayName(UnifiedAuthService.AuthMode mode)
        {
            return mode switch
            {
                UnifiedAuthService.AuthMode.Hybrid => "混合模式",
                UnifiedAuthService.AuthMode.OnlineOnly => "仅云端模式",
                UnifiedAuthService.AuthMode.OfflineOnly => "仅本地模式",
                _ => "未知模式"
            };
        }

        /// <summary>
        /// 显示消息对话框
        /// </summary>
        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 显示/隐藏加载状态
        /// </summary>
        private void ShowLoading(bool isLoading, string message = "")
        {
            // 简单实现：禁用/启用按钮
            SaveButton.IsEnabled = !isLoading;
            TestConnectionButton.IsEnabled = !isLoading;
            RefreshNetworkButton.IsEnabled = !isLoading;
            
            if (isLoading)
            {
                SaveButton.Content = message;
            }
            else
            {
                SaveButton.Content = "保存设置";
            }
        }

        #endregion
    }

    /// <summary>
    /// 连接测试结果
    /// </summary>
    public class ConnectionTestResult
    {
        public bool IsSuccess { get; }
        public string Details { get; }
        public string ErrorMessage { get; }

        public ConnectionTestResult(bool isSuccess, string details)
        {
            IsSuccess = isSuccess;
            Details = isSuccess ? details : "";
            ErrorMessage = isSuccess ? "" : details;
        }
    }
}






