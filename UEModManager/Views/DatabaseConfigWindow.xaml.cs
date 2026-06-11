using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{
    /// <summary>
    /// 数据库配置窗口（已废弃 — 云端数据库配置已移除，当前使用本地 SQLite 数据库）
    /// 保留窗口壳以兼容 XAML，Phase 6 将完全重写
    /// </summary>
    public partial class DatabaseConfigWindow : Window
    {
        private readonly ILogger<DatabaseConfigWindow> _logger;

        public DatabaseConfigWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            UEModManager.Services.LanguageManager.LanguageChanged += _ => { Dispatcher.Invoke(ApplyLocalization); };

            var serviceProvider = ((App)Application.Current).ServiceProvider;
            _logger = serviceProvider.GetRequiredService<ILogger<DatabaseConfigWindow>>();

            _logger.LogInformation("数据库配置窗口已废弃，当前使用本地 SQLite 数据库");
        }

        private void LoadFreeProviders() { }
        private void LoadCurrentConfig()
        {
            ProviderComboBox.SelectedIndex = 0;
            HostTextBox.Text = "";
            DatabaseTextBox.Text = "";
            PortTextBox.Text = "5432";
            UsernameTextBox.Text = "";
            EnableSslCheckBox.IsChecked = false;
            ConnectionTimeoutTextBox.Text = "30";
            CommandTimeoutTextBox.Text = "60";
        }

        private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void UpdateProviderInfo(object provider) { }
        private void ApplyProviderDefaults(object provider) { }

        private void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            CyberMessageBox.Show(this, "此功能已废弃。\n云端数据库配置已移除，当前使用本地 SQLite 数据库。",
                          "功能已迁移", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void InitializeDatabase_Click(object sender, RoutedEventArgs e)
        {
            CyberMessageBox.Show(this, "此功能已废弃。\n云端数据库配置已移除，当前使用本地 SQLite 数据库。",
                          "功能已迁移", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MinimizeWindow(this); } catch { }
        }
        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MaximizeWindow(this); } catch { }
        }
        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.RestoreWindow(this); } catch { }
        }
        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.CloseWindow(this); } catch { }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"无法打开链接: {e.Uri}");
            }
        }

        private void ShowLoading(bool show, string message = "")
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message;
        }

        private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new Dictionary<string, string>
            {
                {"数据库配置","Database Configuration"},
                {"连接超时(秒):","Connection Timeout (s):"},
                {"命令超时(秒):","Command Timeout (s):"},
                {"🔗 测试连接","🔗 Test Connection"},
                {"🏗️ 初始化数据库","🏗️ Initialize Database"},
                {"免费PostgreSQL服务商推荐","Free PostgreSQL Providers"},
                {"保存配置","Save"},
                {"取消","Cancel"},
                {"正在连接...","Connecting..."}
            };
            UEModManager.Services.LocalizationHelper.Apply(this, toEnglish, map);
            this.Title = toEnglish ? "Database Configuration" : "数据库配置";
            if (LoadingText != null) LoadingText.Text = toEnglish ? "Connecting..." : "正在连接...";
        }
    }
}
