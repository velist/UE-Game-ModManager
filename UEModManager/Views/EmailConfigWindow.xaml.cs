using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{
    /// <summary>
    /// 邮件服务配置窗口（已废弃 — 邮件服务现通过 Brevo API + Cloudflare Workers 管理）
    /// 保留窗口壳以兼容 XAML，Phase 6 将完全重写
    /// </summary>
    public partial class EmailConfigWindow : Window
    {
        private readonly ILogger<EmailConfigWindow> _logger;

        private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new Dictionary<string, string>
            {
                {"邮件服务配置","Email Service Configuration"},
                {"邮件提供商:","Provider:"},
                {"发送邮箱:","Sender Email:"},
                {"邮箱密码/授权码:","Password/App Key:"},
                {"发送者姓名:","Sender Name:"},
                {"SMTP服务器:","SMTP Server:"},
                {"SMTP端口:","SMTP Port:"},
                {"启用SSL/TLS加密","Enable SSL/TLS"},
                {"🧪 测试配置","🧪 Test"},
                {"保存","Save"},
                {"保存配置","Save"},
                {"取消","Cancel"},
                {"正在测试...","Testing..."},
                {"Gmail (推荐)","Gmail (Recommended)"},
                {"Outlook/Hotmail","Outlook/Hotmail"},
                {"QQ邮箱","QQ Mail"},
                {"网易163邮箱","NetEase 163"},
                {"新浪邮箱","Sina Mail"},
                {"自定义配置","Custom"}
            };
            UEModManager.Services.LocalizationHelper.Apply(this, toEnglish, map);
            this.Title = toEnglish ? "Email Settings" : "邮件服务配置";
            if (LoadingText != null) LoadingText.Text = toEnglish ? "Testing..." : "正在测试...";
        }

        public EmailConfigWindow()
        {
            InitializeComponent();
            var sp = ((App)Application.Current).ServiceProvider;
            _logger = sp.GetRequiredService<ILogger<EmailConfigWindow>>();
            ApplyLocalization();
            LanguageManager.LanguageChanged += en => { Dispatcher.Invoke(ApplyLocalization); };
            LoadCurrentConfig();
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

        private void LoadCurrentConfig()
        {
            try
            {
                ProviderComboBox.SelectedIndex = 0;
                SenderNameTextBox.Text = "UEModManager";
                _logger.LogInformation("邮件配置界面初始化完成（已废弃，邮件通过 Brevo API 管理）");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载邮件配置失败");
            }
        }

        private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void TestConfig_Click(object sender, RoutedEventArgs e)
        {
            CyberMessageBox.Show(this, "此功能已废弃。\n邮件服务现通过 Brevo API + Cloudflare Workers 管理。",
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

        private void ShowLoading(bool show, string message = "")
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message;
        }
    }
}
