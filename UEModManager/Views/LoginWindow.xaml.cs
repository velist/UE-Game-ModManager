using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{    public partial class LoginWindow : Window
    {        private readonly UnifiedAuthService _unifiedAuth;
        private readonly LocalAuthService _localAuth;
        private readonly CustomOtpService _otpService;
        private readonly ILogger<LoginWindow> _logger;

        private bool _isProcessing = false;
        private int _countdown = 0;
        private DispatcherTimer? _countdownTimer;
        private bool _isEnglish = false;

        public LoginWindow()
        {            InitializeComponent();

            // 解析依赖
            var sp = (Application.Current as App)?.ServiceProvider ?? throw new InvalidOperationException("ServiceProvider 未初始化");
            _unifiedAuth = sp.GetRequiredService<UnifiedAuthService>();
            _localAuth = sp.GetRequiredService<LocalAuthService>();
            _otpService = sp.GetRequiredService<CustomOtpService>();
            _logger = sp.GetService<ILogger<LoginWindow>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LoginWindow>.Instance;
        }

        // 顶部窗口控制
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);
        private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

        // 邮箱输入变化 -> 启用/禁用发送按钮
        private void EmailTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {            var email = EmailTextBox.Text?.Trim() ?? string.Empty;
            SendOtpButton.IsEnabled = !string.IsNullOrWhiteSpace(email) && email.Contains("@") && _countdown == 0;
        }

        // 验证码输入变化 -> 自动启用验证按钮
        private void OtpTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {            var otp = OtpTextBox.Text?.Trim() ?? string.Empty;
            VerifyLoginButton.IsEnabled = otp.Length == 6 && !_isProcessing;
        }

        // 发送验证码
        private async void SendOtpButton_Click(object sender, RoutedEventArgs e)
        {            if (_isProcessing || _countdown > 0) return;

            var email = EmailTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
            {                MessageBox.Show("请输入有效的邮箱地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {                _isProcessing = true;
                ShowLoading(true, _isEnglish ? "Sending code..." : "正在发送验证码...");

                var result = await _otpService.SendOtpAsync(email);

                ShowLoading(false);

                if (result.Success)
                {                    var isMagicLink = result.Message != null && result.Message.Contains("登录链接");
                    if (isMagicLink)
                    {                        OtpInputPanel.Visibility = Visibility.Collapsed;
                        VerifyLoginButton.Visibility = Visibility.Collapsed;
                        MessageBox.Show(result.Message, "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {                        OtpInputPanel.Visibility = Visibility.Visible;
                        VerifyLoginButton.Visibility = Visibility.Visible;
                        StartCountdown(result.RetryAfterSeconds ?? 60);
                        MessageBox.Show("验证码已发送，请查收邮件", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        OtpTextBox.Focus();
                    }
                }
                else
                {                    if (result.RetryAfterSeconds.HasValue && result.RetryAfterSeconds.Value > 0)
                    {

                    // 如果是频率限制，直接开始倒计时
                        StartCountdown(result.RetryAfterSeconds.Value);
                    }

                    MessageBox.Show($"发送失败：{result.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {                _logger.LogError(ex, "发送验证码失败");
                MessageBox.Show($"发送失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {                _isProcessing = false;
            }
        }

        // 验证登录
        private async void VerifyLoginButton_Click(object sender, RoutedEventArgs e)
        {            if (_isProcessing) return;

            var email = EmailTextBox.Text?.Trim() ?? string.Empty;
            var otp = OtpTextBox.Text?.Trim() ?? string.Empty;

            if (otp.Length != 6)
            {                MessageBox.Show("请输入6位验证码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {                _isProcessing = true;
                ShowLoading(true, _isEnglish ? "Verifying..." : "正在验证登录...");

                // 1. 验证验证码
                var verifyResult = _otpService.VerifyOtp(email, otp);

                if (!verifyResult.Success)
                {                    ShowLoading(false);
                    MessageBox.Show($"验证失败：{verifyResult.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 2. 设置本地认证状态
                var loginSuccess = await _localAuth.ForceSetAuthStateAsync(email);

                ShowLoading(false);

                if (loginSuccess)
                {
                    // 持久化“记住我”令牌，确保二次重启自动登录
                    await _localAuth.SaveRememberMeTokenAsync(email, true);
                    _logger.LogInformation($"验证码登录成功: {email}");
                    DialogResult = true;
                    Close();
                }
                else
                {                    MessageBox.Show("设置登录状态失败，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {                _logger.LogError(ex, "验证码登录失败");
                ShowLoading(false);
                MessageBox.Show($"登录失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {                _isProcessing = false;
            }
        }

        // 倒计时功能
        private void StartCountdown(int seconds)
        {            _countdown = seconds;
            SendOtpButton.IsEnabled = false;
            SendOtpButton.Content = _isEnglish ? $"Resend ({_countdown}s)" : $"重新发送 ({_countdown}s)";

            _countdownTimer?.Stop();
            _countdownTimer = new DispatcherTimer
            {                Interval = TimeSpan.FromSeconds(1)
            };

            _countdownTimer.Tick += (s, e) =>
            {                _countdown--;
                if (_countdown > 0)
                {                    SendOtpButton.Content = _isEnglish ? $"Resend ({_countdown}s)" : $"重新发送 ({_countdown}s)";
                }
                else
                {                    _countdownTimer?.Stop();
                    SendOtpButton.Content = _isEnglish ? "Resend Code" : "重新发送验证码";
                    SendOtpButton.IsEnabled = true;
                }
            };

            _countdownTimer.Start();
        }

        private void ShowLoading(bool show, string? text = null)
        {            if (LoadingOverlay == null) return;
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !string.IsNullOrWhiteSpace(text) && LoadingText != null)
            {                LoadingText.Text = text!;
            }
        }

        // 语言切换
        private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
        {            _isEnglish = !_isEnglish;
            UpdateLanguage();
        }

        private void UpdateLanguage()
        {            if (_isEnglish)
            {

                    // 英文
                LanguageToggleButton.Content = "中文";
                TitleText.Text = "OTP Login";
                SmartAuthHint.Text = "💡 New users will be automatically registered";
                EmailLabel.Text = "Email Address";

                if (_countdown > 0)
                {                    SendOtpButton.Content = $"Resend ({_countdown}s)";
                }
                else if (OtpInputPanel.Visibility == Visibility.Visible)
                {                    SendOtpButton.Content = "Resend Code";
                }
                else
                {                    SendOtpButton.Content = "Send Code";
                }

                if (OtpInputPanel.Visibility == Visibility.Visible)
                {                    OtpTextBox.Text = OtpTextBox.Text; // Keep user input
                    OtpHintText.Text = "Code sent to your email, valid for 10 minutes";
                }

                VerifyLoginButton.Content = "Verify Login";
                LoadingText.Text = "Processing...";
            }
            else
            {

                    // 中文
                LanguageToggleButton.Content = "EN";
                TitleText.Text = "验证码登录";
                SmartAuthHint.Text = "💡 新用户首次登录将自动注册账号";
                EmailLabel.Text = "邮箱地址";

                if (_countdown > 0)
                {                    SendOtpButton.Content = $"重新发送 ({_countdown}s)";
                }
                else if (OtpInputPanel.Visibility == Visibility.Visible)
                {                    SendOtpButton.Content = "重新发送验证码";
                }
                else
                {                    SendOtpButton.Content = "发送验证码";
                }

                if (OtpInputPanel.Visibility == Visibility.Visible)
                {                    OtpTextBox.Text = OtpTextBox.Text; // Keep user input
                    OtpHintText.Text = "验证码已发送到您的邮箱，10分钟内有效";
                }

                VerifyLoginButton.Content = "验证登录";
                LoadingText.Text = "正在处理...";
            }
        }

        protected override void OnClosed(EventArgs e)
        {            _countdownTimer?.Stop();
            base.OnClosed(e);
        }
    }
}









