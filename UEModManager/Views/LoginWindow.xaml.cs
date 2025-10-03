using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class LoginWindow : Window
    {
        private readonly UnifiedAuthService _unifiedAuth;
        private readonly LocalAuthService _localAuth;
        private readonly AuthenticationService _authService;
        private readonly HybridOtpService _otpService;
        private readonly ILogger<LoginWindow> _logger;

        private bool _isPasswordVisible = false;
        private bool _isProcessing = false;
        private string _currentLang = UEModManager.Services.LanguageManager.IsEnglish ? "en-US" : "zh-CN"; // 默认中文

        public LoginWindow()
        {
            InitializeComponent();

            // 解析依赖
            var sp = (Application.Current as App)?.ServiceProvider ?? throw new InvalidOperationException("ServiceProvider 未初始化");
            _unifiedAuth = sp.GetRequiredService<UnifiedAuthService>();
            _localAuth = sp.GetRequiredService<LocalAuthService>();
            _authService = sp.GetRequiredService<AuthenticationService>();
            _otpService = sp.GetRequiredService<HybridOtpService>();
            _logger = sp.GetService<ILogger<LoginWindow>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LoginWindow>.Instance;

            // 应用默认语言
            ApplyLocalization(_currentLang);
        }

        // 顶部窗口控制
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);
        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

        // 邮箱输入变化 -> 简易提示
        private void EmailTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SmartDetectionText == null) return;
            var email = (sender as TextBox)?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email))
            {
                SmartDetectionText.Visibility = Visibility.Collapsed;
                return;
            }
            SmartDetectionText.Text = email.Contains("@") ? LoginWindowLocalization.GetString(_currentLang, "SmartDetect_LoginOrRegister") : string.Empty;
            SmartDetectionText.Visibility = string.IsNullOrEmpty(SmartDetectionText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // 密码可见切换
        private void ShowPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            _isPasswordVisible = !_isPasswordVisible;
            if (_isPasswordVisible)
            {
                PasswordTextBox.Text = PasswordBox.Password;
                PasswordTextBox.Visibility = Visibility.Visible;
                PasswordBox.Visibility = Visibility.Collapsed;
                (sender as Button)!.Content = "🙈";
            }
            else
            {
                PasswordBox.Password = PasswordTextBox.Text;
                PasswordTextBox.Visibility = Visibility.Collapsed;
                PasswordBox.Visibility = Visibility.Visible;
                (sender as Button)!.Content = "👁";
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isPasswordVisible)
            {
                PasswordTextBox.Text = PasswordBox.Password;
            }
            UpdatePasswordStrength(PasswordBox.Password);
        }

        private void PasswordTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isPasswordVisible)
            {
                PasswordBox.Password = PasswordTextBox.Text;
            }
            UpdatePasswordStrength(_isPasswordVisible ? PasswordTextBox.Text : PasswordBox.Password);
        }

        private void UpdatePasswordStrength(string pwd)
        {
            if (PasswordStrengthText == null) return;
            if (string.IsNullOrEmpty(pwd))
            {
                PasswordStrengthText.Visibility = Visibility.Collapsed;
                return;
            }

            var score = 0;
            if (pwd.Length >= 8) score++;
            if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[A-Z]")) score++;
            if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[a-z]")) score++;
            if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[0-9]")) score++;
            if (System.Text.RegularExpressions.Regex.IsMatch(pwd, @"[^a-zA-Z0-9]")) score++;

            string text;
            string color;
            if (score <= 2) { text = LoginWindowLocalization.GetString(_currentLang, "PwdWeak"); color = "#F87171"; }
            else if (score == 3) { text = LoginWindowLocalization.GetString(_currentLang, "PwdMedium"); color = "#FBBF24"; }
            else { text = LoginWindowLocalization.GetString(_currentLang, "PwdStrong"); color = "#4ADE80"; }

            PasswordStrengthText.Text = text;
            PasswordStrengthText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            PasswordStrengthText.Visibility = Visibility.Visible;
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
            var email = EmailTextBox.Text?.Trim() ?? string.Empty;
            var password = _isPasswordVisible ? PasswordTextBox.Text : PasswordBox.Password;
            var remember = RememberMeCheckBox.IsChecked == true;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrEmailPwdEmpty"), LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _isProcessing = true;
                ShowLoading(true, LoginWindowLocalization.GetString(_currentLang, "LoadingLoggingIn"));
                var result = await _unifiedAuth.LoginAsync(email, password, remember);
                if (result.IsSuccess)
                {
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(result.Message, LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "登录失败");
                MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrLoginFailed") + ": " + ex.Message, LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isProcessing = false;
                ShowLoading(false);
            }
        }

        private async void OtpLoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
            var email = EmailTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email))
            {
                MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrEmailEmpty"), LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _isProcessing = true;
                ShowLoading(true, LoginWindowLocalization.GetString(_currentLang, "LoadingSendOtp"));
                var send = await _otpService.SendOtpAsync(email);
                if (!send.Success)
                {
                    MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrSendOtpFailed") + ": " + send.Message, LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                ShowLoading(false);

                // 简易输入框获取验证码
                string token = Microsoft.VisualBasic.Interaction.InputBox(LoginWindowLocalization.GetString(_currentLang, "InputOtpPrompt"), LoginWindowLocalization.GetString(_currentLang, "OtpTitle"), "");
                if (string.IsNullOrWhiteSpace(token)) return;

                ShowLoading(true, LoginWindowLocalization.GetString(_currentLang, "LoadingVerifyOtp"));
                var verify = await _otpService.VerifyOtpAsync(email, token);
                if (!verify.Success)
                {
                    MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrVerifyOtpFailed") + ": " + verify.Message, LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 验证成功 -> 强制设置本地登录状态
                var forced = await _localAuth.ForceSetAuthStateAsync(email);
                if (forced)
                {
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrSetSessionFailed"), LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证码登录失败");
                MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrOtpLoginFailed") + ": " + ex.Message, LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isProcessing = false;
                ShowLoading(false);
            }
        }

        private async void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            var email = EmailTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email))
            {
                MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrEmailEmpty"), LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                ShowLoading(true, LoginWindowLocalization.GetString(_currentLang, "LoadingResetPwd"));
                var result = await _authService.ResetPasswordAsync(email);
                ShowLoading(false);

                if (result.IsSuccess)
                {
                    MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ResetPwdSent"), LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrResetPwdFailed") + ": " + result.Message, LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置密码请求失败");
                MessageBox.Show(LoginWindowLocalization.GetString(_currentLang, "ErrResetPwdFailed") + ": " + ex.Message, LoginWindowLocalization.GetString(_currentLang, "Tip"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ShowLoading(false);
            }
        }

        private void OfflineModeButton_Click(object sender, RoutedEventArgs e)
        {
            // 离线直接进入
            DialogResult = false; // 约定：false 表示离线进入
            Close();
        }

        private void ShowLoading(bool show, string? text = null)
        {
            if (LoadingOverlay == null) return;
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !string.IsNullOrWhiteSpace(text) && LoadingText != null)
            {
                LoadingText.Text = text!;
            }
        }

        private void LangToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _currentLang = _currentLang == "zh-CN" ? "en-US" : "zh-CN";
            ApplyLocalization(_currentLang);
            try { UEModManager.Services.LanguageManager.SetEnglish(_currentLang == "en-US"); } catch { }
            try { UEModManager.Services.UiPreferences.SaveEnglish(UEModManager.Services.LanguageManager.IsEnglish); } catch { }
            if (sender is Button b)
            {
                b.Content = _currentLang == "zh-CN" ? "EN" : "中";
            }
        }

        private void ApplyLocalization(string lang)
        {
            // Window 标题
            this.Title = LoginWindowLocalization.GetString(lang, "WindowTitle");
            // 顶部副标题
            TitleText.Text = LoginWindowLocalization.GetString(lang, "Subtitle");
            // 智能提示
            SmartAuthHint.Text = LoginWindowLocalization.GetString(lang, "SmartHint");
            // 邮箱标签
            EmailLabel.Text = LoginWindowLocalization.GetString(lang, "Email");
            // 忘记密码按钮
            if (ForgotPwdBtn != null) ForgotPwdBtn.Content = LoginWindowLocalization.GetString(lang, "ForgotPwd");
            // 密码标签
            PasswordLabel.Text = LoginWindowLocalization.GetString(lang, "Password");
            // 新用户提示
            NewUserHint.Text = LoginWindowLocalization.GetString(lang, "NewUserHint");
            // 记住我/忘记密码
            RememberMeCheckBox.Content = LoginWindowLocalization.GetString(lang, "RememberMe");
            // 登录按钮
            LoginButton.Content = LoginWindowLocalization.GetString(lang, "LoginBtn");
            // 下方两个按钮
            OtpLoginButton.Content = LoginWindowLocalization.GetString(lang, "OtpLoginBtn");
            OfflineModeBtn.Content = LoginWindowLocalization.GetString(lang, "OfflineBtn");
            // Loading 文案
            LoadingText.Text = LoginWindowLocalization.GetString(lang, "LoadingLoggingIn");
        }
    }
}








