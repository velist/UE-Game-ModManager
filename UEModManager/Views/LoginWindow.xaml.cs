using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;
using UEModManager.Localization;

namespace UEModManager.Views
{    public partial class LoginWindow : Window
    {
        private readonly LocalAuthService _localAuth;
        private readonly CustomOtpService _otpService;
        private readonly ILogger<LoginWindow> _logger;

        private bool _isProcessing = false;
        private int _countdown = 0;
        private DispatcherTimer? _countdownTimer;
        private string _loadingMessage = UiText.Get("正在处理...");

        public LoginWindow()
            : this(GetServices().GetRequiredService<LocalAuthService>(),
                GetServices().GetRequiredService<CustomOtpService>(),
                GetServices().GetService<ILogger<LoginWindow>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LoginWindow>.Instance)
        {
        }

        internal LoginWindow(LocalAuthService localAuth, CustomOtpService otpService, ILogger<LoginWindow> logger,
            ResourceDictionary? resources = null)
        {
            _localAuth = localAuth ?? throw new ArgumentNullException(nameof(localAuth));
            _otpService = otpService ?? throw new ArgumentNullException(nameof(otpService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (resources != null) Resources = resources;
            InitializeComponent();
            LanguageManager.LanguageChanged += OnLanguageChanged;
            UpdateLanguage();
        }

        private static IServiceProvider GetServices()
            => (Application.Current as App)?.ServiceProvider ?? throw new InvalidOperationException("ServiceProvider 未初始化");

        private void OnLanguageChanged(bool english) => Dispatcher.Invoke(UpdateLanguage);

        // 无标题栏窗口拖动支持
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
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
            {                CyberMessageBox.Show(this, UiText.Get("请输入有效的邮箱地址"), UiText.Get("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {                _isProcessing = true;
                ShowLoading(true, "正在发送验证码...");

                var result = await _otpService.SendOtpAsync(email);

                ShowLoading(false);

                if (result.Success)
                {                    var isMagicLink = result.Message != null && result.Message.Contains("登录链接");
                    if (isMagicLink)
                    {                        OtpInputPanel.Visibility = Visibility.Collapsed;
                        VerifyLoginButton.Visibility = Visibility.Collapsed;
                        CyberMessageBox.Show(this, result.Message, UiText.Get("成功"), MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {                        OtpInputPanel.Visibility = Visibility.Visible;
                        VerifyLoginButton.Visibility = Visibility.Visible;
                        StartCountdown(result.RetryAfterSeconds ?? 60);
                        CyberMessageBox.Show(this, UiText.Get("验证码已发送，请查收邮件"), UiText.Get("成功"), MessageBoxButton.OK, MessageBoxImage.Information);
                        OtpTextBox.Focus();
                    }
                }
                else
                {                    if (result.RetryAfterSeconds.HasValue && result.RetryAfterSeconds.Value > 0)
                    {

                    // 如果是频率限制，直接开始倒计时
                        StartCountdown(result.RetryAfterSeconds.Value);
                    }

                    CyberMessageBox.Show(this, UiText.Interpolate($"发送失败：{UiText.Get(result.Message)}"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {                _logger.LogError(ex, "发送验证码失败");
                CyberMessageBox.Show(this, UiText.Interpolate($"发送失败：{ex.Message}"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            {                CyberMessageBox.Show(this, UiText.Get("请输入6位验证码"), UiText.Get("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {                _isProcessing = true;
                ShowLoading(true, "正在验证登录...");

                // 1. 验证验证码
                var verifyResult = await _otpService.VerifyOtpAsync(email, otp);

                if (!verifyResult.Success)
                {                    ShowLoading(false);
                    CyberMessageBox.Show(this, UiText.Interpolate($"验证失败：{UiText.Get(verifyResult.Message)}"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                    ReportSignIn(email);
                    DialogResult = true;
                    Close();
                }
                else
                {                    CyberMessageBox.Show(this, UiText.Get("设置登录状态失败，请重试"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {                _logger.LogError(ex, "验证码登录失败");
                ShowLoading(false);
                CyberMessageBox.Show(this, UiText.Interpolate($"登录失败：{ex.Message}"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {                _isProcessing = false;
            }
        }

        /// <summary>
        /// 登录成功后上报一次账号（邮箱哈希，明文邮箱不出本机）。
        ///
        /// <para>
        /// <b>刻意不 await，也刻意不管失败。</b>登录已经成功了，用户在等窗口关掉；
        /// 为了一次统计让他多等哪怕 5 秒都不成立，更不用说让他看到一个错误。
        /// </para>
        ///
        /// <para>
        /// 首次运行时这一次上报会被<b>正确地丢弃</b>：登录窗口出现在主窗口之前，
        /// 那一刻还没告知过用户，<c>TelemetryConsent</c> 判定为不上报。兜底在心跳侧——
        /// 主窗口起来、用户做完选择之后，第一次心跳会发现"已登录但本次会话没报过账号"
        /// 并补上。那条兜底同时也覆盖靠"记住我"自动登录、根本不经过本窗口的老用户。
        /// </para>
        /// </summary>
        private void ReportSignIn(string email)
        {
            try
            {
                var telemetry = ((App)Application.Current).ServiceProvider?.GetService<TelemetryService>();
                if (telemetry == null) return;
                _ = telemetry.ReportSignInAsync(email);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Telemetry] 登录上报未能发起");
            }
        }

        // 倒计时功能
        private void StartCountdown(int seconds)
        {            _countdown = seconds;
            SendOtpButton.IsEnabled = false;
            UpdateLanguage();

            _countdownTimer?.Stop();
            _countdownTimer = new DispatcherTimer
            {                Interval = TimeSpan.FromSeconds(1)
            };

            _countdownTimer.Tick += (s, e) =>
            {                _countdown--;
                if (_countdown > 0)
                {                    UpdateLanguage();
                }
                else
                {                    _countdownTimer?.Stop();
                    UpdateLanguage();
                    SendOtpButton.IsEnabled = true;
                }
            };

            _countdownTimer.Start();
        }

        private void ShowLoading(bool show, string? text = null)
        {            if (LoadingOverlay == null) return;
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !string.IsNullOrWhiteSpace(text)) _loadingMessage = text;
            UpdateLanguage();
        }

        // 语言切换
        private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try { LanguageManager.SaveAndSetEnglish(!LanguageManager.IsEnglish); }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, UiText.Format("保存语言设置失败：{0}", ex.Message), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateLanguage()
        {
            SendOtpButton.Content = _countdown > 0
                ? UiText.Format("重新发送 ({0}s)", _countdown)
                : UiText.Get(OtpInputPanel.Visibility == Visibility.Visible ? "重新发送验证码" : "发送验证码");
            LoadingText.Text = UiText.Get(_loadingMessage);
        }

        protected override void OnClosed(EventArgs e)
        {            _countdownTimer?.Stop();
            LanguageManager.LanguageChanged -= OnLanguageChanged;
            base.OnClosed(e);
        }
    }
}









