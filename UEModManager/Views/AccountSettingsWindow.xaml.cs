using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class AccountSettingsWindow : Window
    {
        private readonly LocalAuthService _localAuth;
        private readonly ILogger<AccountSettingsWindow>? _logger;
        private string? _selectedAvatarTemp;

        public AccountSettingsWindow()
        {
            InitializeComponent();
            var sp = (Application.Current as App)?.ServiceProvider ?? throw new InvalidOperationException("ServiceProvider 未初始化");
            _localAuth = sp.GetRequiredService<LocalAuthService>();
            _logger = sp.GetService<ILogger<AccountSettingsWindow>>();

            var user = _localAuth.CurrentUser;
            UsernameTextBox.Text = user?.Username ?? string.Empty;
            DisplayNameTextBox.Text = user?.DisplayName ?? user?.Username ?? user?.Email ?? string.Empty;
            _ = LoadSignatureAsync();
            try
            {
                if (!string.IsNullOrEmpty(user?.Avatar) && System.IO.File.Exists(user.Avatar))
                {
                    AvatarPreview.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(user.Avatar, UriKind.Absolute));
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task LoadSignatureAsync()
        {
            try
            {
                var sig = string.Empty; // 暂不从本地读取签名（方法未提供）
                SignatureTextBox.Text = sig ?? string.Empty;
            }
            catch { }
        }

        private void ChangeAvatar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择头像图片",
                    Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                    CheckFileExists = true,
                    CheckPathExists = true
                };
                if (ofd.ShowDialog() == true)
                {
                    _selectedAvatarTemp = ofd.FileName;
                    try { AvatarPreview.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(ofd.FileName, UriKind.Absolute)); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "选择头像失败");
                MessageBox.Show($"选择头像失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = (DisplayNameTextBox.Text ?? string.Empty).Trim();
                var uname = (UsernameTextBox.Text ?? string.Empty).Trim();
                var sig = (SignatureTextBox.Text ?? string.Empty).Trim();
                if (_localAuth.CurrentUser == null)
                {
                    MessageBox.Show("请先登录后再修改", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string? avatarPath = _localAuth.CurrentUser.Avatar;
                if (!string.IsNullOrEmpty(_selectedAvatarTemp))
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var avatarsDir = System.IO.Path.Combine(baseDir, "UserData", "Avatars");
                    try
                    {
                        System.IO.Directory.CreateDirectory(avatarsDir);
                        var ext = System.IO.Path.GetExtension(_selectedAvatarTemp);
                        var fileName = $"{_localAuth.CurrentUser.Id}_{DateTime.Now:yyyyMMddHHmmss}{ext}";
                        var dest = System.IO.Path.Combine(avatarsDir, fileName);
                        System.IO.File.Copy(_selectedAvatarTemp, dest, true);
                        avatarPath = dest;
                    }
                    catch
                    {
                        var appDataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UEModManager", "Avatars");
                        System.IO.Directory.CreateDirectory(appDataDir);
                        var ext = System.IO.Path.GetExtension(_selectedAvatarTemp);
                        var fileName = $"{_localAuth.CurrentUser.Id}_{DateTime.Now:yyyyMMddHHmmss}{ext}";
                        var dest = System.IO.Path.Combine(appDataDir, fileName);
                        System.IO.File.Copy(_selectedAvatarTemp, dest, true);
                        avatarPath = dest;
                    }
                }

                if (!string.IsNullOrEmpty(name)) _localAuth.CurrentUser.DisplayName = name;
                if (!string.IsNullOrEmpty(uname)) _localAuth.CurrentUser.Username = uname;
                if (!string.IsNullOrEmpty(avatarPath)) _localAuth.CurrentUser.Avatar = avatarPath;

                var okUser = await _localAuth.UpdateUserAsync(_localAuth.CurrentUser);
                var okSig = await _localAuth.SetUserSignatureAsync(sig); // ✅ 调用签名保存方法

                if (okUser && okSig) // ✅ 检查两个保存结果
                {
                    _logger?.LogInformation("[AccountSettings] 用户本地设置已保存");
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("保存失败，请稍后重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "保存账户设置失败");
                MessageBox.Show($"保存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
