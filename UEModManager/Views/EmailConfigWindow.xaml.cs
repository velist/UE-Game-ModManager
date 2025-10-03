using System.Collections.Generic;
using UEModManager.Services;using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

using System.Linq;
namespace UEModManager.Views
{
    public partial class EmailConfigWindow : Window
    {
        private string _lang = UEModManager.Services.LanguageManager.IsEnglish ? "en-US" : "zh-CN";
        private readonly EmailService _emailService;
        private readonly ILogger<EmailConfigWindow> _logger;
        
        private readonly System.Collections.Generic.Dictionary<EmailProvider, string> _providerHints = new()
        {
            [EmailProvider.Gmail] = "需要使用应用专用密码，而非Gmail登录密码。请在Google账户设置中生成应用专用密码。",
            [EmailProvider.Outlook] = "可以使用Outlook/Hotmail账户密码，或生成应用专用密码。",
            [EmailProvider.QQ] = "需要开启SMTP服务并使用授权码，而非QQ密码。请在QQ邮箱设置中获取授权码。",
            [EmailProvider.NetEase163] = "需要开启SMTP服务并使用授权码，而非登录密码。",
            [EmailProvider.Sina] = "可以使用新浪邮箱登录密码。",
            [EmailProvider.Custom] = "请根据您的邮件服务商要求配置SMTP参数。"
        };

                private void ApplyLocalization()
        {
            var toEnglish = UEModManager.Services.LanguageManager.IsEnglish;
            var map = new System.Collections.Generic.Dictionary<string,string>
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
        }public EmailConfigWindow()
        {
            InitializeComponent();
            var sp = ((App)Application.Current).ServiceProvider;
            _emailService = sp.GetRequiredService<EmailService>();
            _logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EmailConfigWindow>>();
            ApplyLocalization();
            LanguageManager.LanguageChanged += en => { Dispatcher.Invoke(ApplyLocalization); };
            LoadCurrentConfig();
        }

        // 标题栏按钮命令处理（Qt6风格自绘）
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

        /// <summary>
        /// 加载当前配置
        /// </summary>
        private void LoadCurrentConfig()
        {
            try
            {
                // 这里可以从EmailService获取当前配置
                // 暂时设置默认值
                ProviderComboBox.SelectedIndex = 0; // 默认选择Gmail
                SenderNameTextBox.Text = "UEModManager";
                
                _logger.LogInformation("邮件配置界面初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载邮件配置失败");
            }
        }

        /// <summary>
        /// 邮件提供商选择变化
        /// </summary>
        private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProviderComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var providerTag = selectedItem.Tag?.ToString();
                if (Enum.TryParse<EmailProvider>(providerTag, out var provider))
                {
                    UpdateUIForProvider(provider);
                }
            }
        }

        /// <summary>
        /// 根据邮件提供商更新界面
        /// </summary>
        private void UpdateUIForProvider(EmailProvider provider)
        {
            // 显示/隐藏自定义配置面板
            CustomConfigPanel.Visibility = provider == EmailProvider.Custom 
                ? Visibility.Visible 
                : Visibility.Collapsed;

            // 更新密码提示信息
            if (_providerHints.TryGetValue(provider, out var hint))
            {
                PasswordHintTextBlock.Text = hint;
                PasswordHintTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                PasswordHintTextBlock.Visibility = Visibility.Collapsed;
            }

            // 根据提供商预填充SMTP配置
            if (provider != EmailProvider.Custom)
            {
                var presetConfigs = new System.Collections.Generic.Dictionary<EmailProvider, (string server, int port, bool ssl)>
                {
                    [EmailProvider.Gmail] = ("smtp.gmail.com", 587, true),
                    [EmailProvider.Outlook] = ("smtp-mail.outlook.com", 587, true),
                    [EmailProvider.QQ] = ("smtp.qq.com", 587, true),
                    [EmailProvider.NetEase163] = ("smtp.163.com", 25, false),
                    [EmailProvider.Sina] = ("smtp.sina.com", 587, true)
                };

                if (presetConfigs.TryGetValue(provider, out var config))
                {
                    SmtpServerTextBox.Text = config.server;
                    SmtpPortTextBox.Text = config.port.ToString();
                    EnableSslCheckBox.IsChecked = config.ssl;
                }
            }
        }

        /// <summary>
        /// 测试配置
        /// </summary>
        private async void TestConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs())
                return;

            try
            {
                ShowLoading(true, "正在测试邮件配置...");
                TestConfigButton.IsEnabled = false;
                TestResultTextBlock.Text = "";

                var config = CreateEmailConfig();
                var result = await _emailService.TestEmailConfigAsync(config);

                if (result.IsSuccess)
                {
                    TestResultTextBlock.Text = "✅ 邮件配置测试成功！已发送测试邮件到您的邮箱。";
                    TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Green);
                }
                else
                {
                    TestResultTextBlock.Text = $"❌ 邮件配置测试失败: {result.Message}";
                    TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试邮件配置异常");
                TestResultTextBlock.Text = $"❌ 测试过程中发生异常: {ex.Message}";
                TestResultTextBlock.Foreground = new SolidColorBrush(Colors.Red);
            }
            finally
            {
                ShowLoading(false);
                TestConfigButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs())
                return;

            try
            {
                ShowLoading(true, "正在保存配置...");

                var config = CreateEmailConfig();
                var success = await _emailService.SaveEmailConfigAsync(config);

                if (success)
                {
                    MessageBox.Show("邮件配置保存成功！", "保存成功", 
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("保存邮件配置失败，请重试。", "保存失败", 
                                   MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存邮件配置异常");
                MessageBox.Show($"保存过程中发生异常: {ex.Message}", "保存失败", 
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ShowLoading(false);
            }
        }

        /// <summary>
        /// 取消
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 验证输入
        /// </summary>
        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(SenderEmailTextBox.Text))
            {
                MessageBox.Show("请输入发送邮箱地址。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                SenderEmailTextBox.Focus();
                return false;
            }

            if (!IsValidEmail(SenderEmailTextBox.Text))
            {
                MessageBox.Show("请输入有效的邮箱地址。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                SenderEmailTextBox.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(SenderPasswordBox.Password))
            {
                MessageBox.Show("请输入邮箱密码或授权码。", "输入错误", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                SenderPasswordBox.Focus();
                return false;
            }

            // 如果是自定义配置，验证SMTP设置
            if (CustomConfigPanel.Visibility == Visibility.Visible)
            {
                if (string.IsNullOrWhiteSpace(SmtpServerTextBox.Text))
                {
                    MessageBox.Show("请输入SMTP服务器地址。", "输入错误", 
                                   MessageBoxButton.OK, MessageBoxImage.Warning);
                    SmtpServerTextBox.Focus();
                    return false;
                }

                if (!int.TryParse(SmtpPortTextBox.Text, out var port) || port <= 0 || port > 65535)
                {
                    MessageBox.Show("请输入有效的SMTP端口号 (1-65535)。", "输入错误", 
                                   MessageBoxButton.OK, MessageBoxImage.Warning);
                    SmtpPortTextBox.Focus();
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 创建邮件配置对象
        /// </summary>
        private EmailConfig CreateEmailConfig()
        {
            var selectedItem = (ComboBoxItem)ProviderComboBox.SelectedItem;
            var providerTag = selectedItem.Tag?.ToString();
            Enum.TryParse<EmailProvider>(providerTag, out var provider);

            var config = new EmailConfig
            {
                SenderEmail = SenderEmailTextBox.Text.Trim(),
                SenderPassword = SenderPasswordBox.Password,
                SenderName = SenderNameTextBox.Text.Trim(),
                Provider = provider
            };

            if (provider == EmailProvider.Custom)
            {
                config.SmtpServer = SmtpServerTextBox.Text.Trim();
                config.SmtpPort = int.Parse(SmtpPortTextBox.Text);
                config.EnableSsl = EnableSslCheckBox.IsChecked == true;
            }
            else
            {
                // 应用预设配置
                config = _emailService.ApplyPresetConfig(provider, config.SenderEmail, config.SenderPassword);
                config.SenderName = SenderNameTextBox.Text.Trim();
            }

            return config;
        }

        /// <summary>
        /// 验证邮箱格式
        /// </summary>
        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 显示加载状态
        /// </summary>
        private void ShowLoading(bool show, string message = "")
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message;
        }
    }
}



