using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class RegisterWindow : Window
    {
        private string _currentLang = "zh-CN";

        private readonly UnifiedAuthService _unifiedAuthService;
        private readonly LocalAuthService _localAuthService;
        private readonly EnhancedAuthService _enhancedAuthService;
        private readonly ILogger<RegisterWindow> _logger;

        public string? RegisteredEmail { get; private set; }

        public RegisterWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            // 获取依赖注入的服务
            var serviceProvider = ((App)Application.Current).ServiceProvider;
            _unifiedAuthService = serviceProvider.GetRequiredService<UnifiedAuthService>();
            _localAuthService = serviceProvider.GetRequiredService<LocalAuthService>();
            _enhancedAuthService = serviceProvider.GetRequiredService<EnhancedAuthService>();
            _logger = serviceProvider.GetRequiredService<ILogger<RegisterWindow>>();

            // 设置事件处理
            this.KeyDown += RegisterWindow_KeyDown;
            PasswordBox.PasswordChanged += PasswordBox_PasswordChanged;
            this.Closing += RegisterWindow_Closing;
            
            // 自动聚焦到用户名输入框
            this.Loaded += (s, e) => UsernameTextBox.Focus();
        }

        private void RegisterWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 如果没有设置DialogResult，默认为false
            if (DialogResult == null)
            {
                DialogResult = false;
            }
        }

        private void RegisterWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                RegisterButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                BackToLoginButton_Click(sender, e);
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UpdatePasswordStrength();
        }

        private void UpdatePasswordStrength()
        {
            var password = PasswordBox.Password;
            var strength = CalculatePasswordStrength(password);
            
            switch (strength)
            {
                case PasswordStrength.VeryWeak:
                    PasswordStrengthText.Text = "密码强度：很弱 - 至少需要8位字符";
                    PasswordStrengthText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                    break;
                case PasswordStrength.Weak:
                    PasswordStrengthText.Text = "密码强度：较弱 - 建议添加数字和特殊字符";
                    PasswordStrengthText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                    break;
                case PasswordStrength.Medium:
                    PasswordStrengthText.Text = "密码强度：中等 - 还不错，可以更强";
                    PasswordStrengthText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Yellow);
                    break;
                case PasswordStrength.Strong:
                    PasswordStrengthText.Text = "密码强度：强 - 很好的密码";
                    PasswordStrengthText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);
                    break;
                case PasswordStrength.VeryStrong:
                    PasswordStrengthText.Text = "密码强度：很强 - 非常安全";
                    PasswordStrengthText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                    break;
                default:
                    PasswordStrengthText.Text = "请输入密码";
                    PasswordStrengthText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
                    break;
            }
        }

        private PasswordStrength CalculatePasswordStrength(string password)
        {
            if (string.IsNullOrEmpty(password)) return PasswordStrength.VeryWeak;
            if (password.Length < 6) return PasswordStrength.VeryWeak;
            if (password.Length < 8) return PasswordStrength.Weak;

            int score = 0;
            if (Regex.IsMatch(password, @"[a-z]")) score++; // 小写字母
            if (Regex.IsMatch(password, @"[A-Z]")) score++; // 大写字母
            if (Regex.IsMatch(password, @"[0-9]")) score++; // 数字
            if (Regex.IsMatch(password, @"[^a-zA-Z0-9]")) score++; // 特殊字符
            if (password.Length >= 12) score++; // 长度加分

            return score switch
            {
                0 or 1 => PasswordStrength.Weak,
                2 => PasswordStrength.Medium,
                3 => PasswordStrength.Strong,
                _ => PasswordStrength.VeryStrong
            };
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            // 检查服务是否可用
            if (_localAuthService == null)
            {
                ShowMessage("认证服务未初始化，请重新打开窗口", "服务错误");
                return;
            }

            var username = UsernameTextBox.Text?.Trim();
            var email = EmailTextBox.Text?.Trim();
            var password = PasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;

            // 验证输入
            if (!ValidateInput(username, email, password, confirmPassword))
                return;

            await PerformRegistrationAsync(email, password, username);
        }

        private bool ValidateInput(string? username, string? email, string password, string confirmPassword)
        {
            if (string.IsNullOrEmpty(email))
            {
                ShowMessage("请输入邮箱地址", "输入错误");
                EmailTextBox.Focus();
                return false;
            }

            if (!IsValidEmail(email))
            {
                ShowMessage("请输入有效的邮箱地址", "输入错误");
                EmailTextBox.Focus();
                return false;
            }

            if (string.IsNullOrEmpty(password))
            {
                ShowMessage("请输入密码", "输入错误");
                PasswordBox.Focus();
                return false;
            }

            if (password.Length < 8)
            {
                ShowMessage("密码至少需要8位字符", "密码过短");
                PasswordBox.Focus();
                return false;
            }

            if (password != confirmPassword)
            {
                ShowMessage("两次输入的密码不一致", "密码确认错误");
                ConfirmPasswordBox.Focus();
                return false;
            }

            if (!string.IsNullOrEmpty(username) && (username.Length < 2 || username.Length > 20))
            {
                ShowMessage("用户名长度应在2-20个字符之间", "用户名格式错误");
                UsernameTextBox.Focus();
                return false;
            }

            if (AgreeCheckBox.IsChecked != true)
            {
                ShowMessage("请同意用户协议和隐私政策", "协议确认");
                AgreeCheckBox.Focus();
                return false;
            }

            return true;
        }

        private async System.Threading.Tasks.Task PerformRegistrationAsync(string email, string password, string? username)
        {
            try
            {
                ShowLoading(true, "正在进行增强注册验证...");
                
                // 使用增强认证服务进行注册（包含密码强度和邮箱验证）
                var enhancedResult = await _enhancedAuthService.RegisterAsync(email, password, true);
                
                if (enhancedResult.IsSuccess)
                {
                    var securityLevel = enhancedResult.SecurityChecks.Count > 0 ? "高安全" : "标准";
                    
                    _logger?.LogInformation($"用户增强注册成功 ({securityLevel}): {email}");
                    RegisteredEmail = email;
                    
                    // 显示增强注册的详细信息
                    var successMessage = $"🎉 账户创建成功！({securityLevel})\n";
                    if (enhancedResult.SecurityChecks.Count > 0)
                    {
                        successMessage += $"✓ 通过 {enhancedResult.SecurityChecks.Count} 项安全验证\n";
                        
                        // 显示密码强度验证结果
                        if (enhancedResult.SecurityChecks.ContainsKey("password_strength"))
                        {
                            var passwordStrength = (dynamic)enhancedResult.SecurityChecks["password_strength"];
                            successMessage += $"✓ 密码强度: {passwordStrength.Score}/100\n";
                        }
                        
                        // 显示邮箱验证结果
                        if (enhancedResult.SecurityChecks.ContainsKey("email_validation"))
                        {
                            successMessage += "✓ 邮箱格式验证通过\n";
                        }
                        
                        // 显示欢迎邮件发送状态
                        if (enhancedResult.SecurityChecks.ContainsKey("welcome_email"))
                        {
                            successMessage += "✓ 欢迎邮件已发送到您的邮箱\n";
                        }
                    }
                    
                    // 显示警告信息（如欢迎邮件发送失败）
                    if (enhancedResult.Warnings.Count > 0)
                    {
                        successMessage += "\n⚠️ 提醒：\n";
                        foreach (var warning in enhancedResult.Warnings)
                        {
                            successMessage += $"• {warning}\n";
                        }
                    }
                    
                    successMessage += "\n🎮 欢迎加入爱酱游戏！\n您已自动登录，可以开始使用应用程序了。";
                    
                    ShowMessage(successMessage, "增强注册成功");
                    
                    DialogResult = true;
                    Close();
                }
                else
                {
                    var errorMessage = enhancedResult.Message;
                    if (enhancedResult.Errors.Count > 0)
                    {
                        errorMessage += "\n详细错误:\n" + string.Join("\n", enhancedResult.Errors);
                    }
                    if (enhancedResult.Warnings.Count > 0)
                    {
                        errorMessage += "\n改进建议:\n" + string.Join("\n", enhancedResult.Warnings);
                    }
                    
                    ShowMessage(errorMessage, "注册失败");
                    
                    // 根据错误类型重新聚焦
                    if (enhancedResult.Message.Contains("邮箱") || enhancedResult.Errors.Any(e => e.Contains("邮箱")))
                    {
                        EmailTextBox.Focus();
                    }
                    else if (enhancedResult.Message.Contains("密码") || enhancedResult.Warnings.Any(w => w.Contains("密码")))
                    {
                        PasswordBox.Focus();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"增强注册异常: {email}");
                
                // 如果增强注册失败，尝试回退到基础注册
                try
                {
                    ShowLoading(true, "回退到基础注册模式...");
                    var basicResult = await _unifiedAuthService.RegisterAsync(email, password, username);
                    
                    if (basicResult.IsSuccess)
                    {
                        _logger?.LogInformation($"基础注册成功: {email}");
                        RegisteredEmail = email;
                        ShowMessage("账户创建成功！(基础模式)\n您已自动登录，可以开始使用应用程序了。", "注册成功");
                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        ShowMessage("注册失败: " + basicResult.Message, "注册错误");
                    }
                }
                catch (Exception fallbackEx)
                {
                    _logger?.LogError(fallbackEx, $"基础注册也失败: {email}");
                    ShowMessage("注册过程中发生错误，请重试", "注册错误");
                }
            }
            finally
            {
                ShowLoading(false);
            }
        }

        private void BackToLoginButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void UserAgreementLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://your-website.com/user-agreement",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "无法打开用户协议链接");
                ShowMessage("无法打开链接，请手动访问官网查看用户协议", "链接错误");
            }
        }

        private void PrivacyPolicyLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://your-website.com/privacy-policy",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "无法打开隐私政策链接");
                ShowMessage("无法打开链接，请手动访问官网查看隐私政策", "链接错误");
            }
        }

        private void ShowLoading(bool show, string message = "")
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message;
            
            // 禁用/启用控件
            UsernameTextBox.IsEnabled = !show;
            EmailTextBox.IsEnabled = !show;
            PasswordBox.IsEnabled = !show;
            ConfirmPasswordBox.IsEnabled = !show;
            AgreeCheckBox.IsEnabled = !show;
            RegisterButton.IsEnabled = !show;
            BackToLoginButton.IsEnabled = !show;
        }

        private void ShowMessage(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

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
        private void ApplyLocalization()
        {
            try
            {
                this.Title = RegisterWindowLocalization.GetString(_currentLang, "WindowTitle");
                if (SubtitleText != null) SubtitleText.Text = RegisterWindowLocalization.GetString(_currentLang, "Subtitle");
                if (UsernameLabel != null) UsernameLabel.Text = RegisterWindowLocalization.GetString(_currentLang, "UsernameLabel");
                if (EmailLabel != null) EmailLabel.Text = RegisterWindowLocalization.GetString(_currentLang, "EmailLabel");
                if (PasswordLabel != null) PasswordLabel.Text = RegisterWindowLocalization.GetString(_currentLang, "PasswordLabel");
                if (ConfirmPasswordLabel != null) ConfirmPasswordLabel.Text = RegisterWindowLocalization.GetString(_currentLang, "ConfirmPasswordLabel");
                if (RegisterButton != null) RegisterButton.Content = RegisterWindowLocalization.GetString(_currentLang, "RegisterBtn");
                if (BackToLoginButton != null) BackToLoginButton.Content = RegisterWindowLocalization.GetString(_currentLang, "BackToLoginBtn");
                if (LoadingText != null) LoadingText.Text = RegisterWindowLocalization.GetString(_currentLang, "LoadingCreating");
                if (LangToggleButton != null) LangToggleButton.Content = _currentLang == "zh-CN" ? "EN" : "中";
            }
            catch { }
        }

        private void LangToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _currentLang = _currentLang == "zh-CN" ? "en-US" : "zh-CN";
            ApplyLocalization();
        }
    }

    public enum PasswordStrength
    {
        VeryWeak,
        Weak,
        Medium,
        Strong,
        VeryStrong
    }
}










