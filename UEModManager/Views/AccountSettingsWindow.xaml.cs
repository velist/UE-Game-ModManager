using UEModManager.Localization;
using System;
using System.Windows;
using System.Windows.Input;
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

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

        public AccountSettingsWindow()
        {
            InitializeComponent();
            var sp = (Application.Current as App)?.ServiceProvider ?? throw new InvalidOperationException("ServiceProvider 未初始化");
            _localAuth = sp.GetRequiredService<LocalAuthService>();
            _logger = sp.GetService<ILogger<AccountSettingsWindow>>();

            var user = _localAuth.CurrentUser;
            DisplayNameTextBox.Text = user?.DisplayName ?? user?.Username ?? user?.Email ?? string.Empty;
            ShowAvatar(user?.Avatar);

            Infrastructure.SafeEvent.Run(this, LoadSignatureAsync, _logger, UiText.Get("读取个性签名"));
        }

        /// <summary>
        /// 显示头像预览，返回是否真的画上了。传 null / 文件不存在 / 解码失败都退回 👤 占位图标。
        /// 用 Background=ImageBrush 而不是 Image 子元素，这样才能被 CornerRadius 裁成圆形。
        /// </summary>
        private bool ShowAvatar(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    // 80pt 控件按 2x 解码；OnLoad 避免持有文件句柄导致用户删不掉原图。
                    var bmp = MainWindow.LoadAvatarBitmap(path!, 160);
                    AvatarPreviewBorder.Background = new System.Windows.Media.ImageBrush(bmp)
                    {
                        Stretch = System.Windows.Media.Stretch.UniformToFill
                    };
                    AvatarPlaceholder.Visibility = Visibility.Collapsed;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[AccountSettings] 加载头像失败，回退占位图标: {Path}", path);
            }

            AvatarPreviewBorder.Background =
                FindResource("SurfaceBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Transparent;
            AvatarPlaceholder.Visibility = Visibility.Visible;
            return false;
        }

        /// <summary>
        /// 读回已保存的个性签名。签名存在 <c>AppConfiguration</c> 表，键 <c>UserSignature.{UserId}</c>。
        /// （此处原先是个空壳，注释写"方法未提供"，实际 <c>LocalAuthService</c> 一直有这个方法，
        /// 结果签名存得进去、读不回来，每次打开都是空白。）
        /// </summary>
        private async System.Threading.Tasks.Task LoadSignatureAsync()
        {
            SignatureTextBox.Text = await _localAuth.GetUserSignatureAsync() ?? string.Empty;
        }

        /// <summary>点击头像本身也能换头像，与"更换头像"按钮同一入口。</summary>
        private void AvatarArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            PickAvatar();
        }

        private void ChangeAvatar_Click(object sender, RoutedEventArgs e) => PickAvatar();

        private void PickAvatar()
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = UiText.Get("选择头像图片"),
                    Filter = UiText.Get("图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*"),
                    CheckFileExists = true,
                    CheckPathExists = true
                };
                if (ofd.ShowDialog() == true)
                {
                    // 先验证能不能解码，再认这张图：选到损坏文件或改了扩展名的非图片时，
                    // 要在这里就告诉用户，而不是等保存完、下次启动才发现头像是空的。
                    if (!ShowAvatar(ofd.FileName))
                    {
                        CyberMessageBox.Show(this, UiText.Get("这个文件无法作为图片打开，请换一张。"),
                            UiText.Get("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    _selectedAvatarTemp = ofd.FileName;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "选择头像失败");
                CyberMessageBox.Show(this, UiText.Interpolate($"选择头像失败：{ex.Message}"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                var sig = (SignatureTextBox.Text ?? string.Empty).Trim();
                if (_localAuth.CurrentUser == null)
                {
                    CyberMessageBox.Show(this, UiText.Get("请先登录后再修改"), UiText.Get("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string? avatarPath = _localAuth.CurrentUser.Avatar;
                if (!string.IsNullOrEmpty(_selectedAvatarTemp))
                {
                    // 头像仍写在安装目录下（AppPaths.Legacy.AvatarsDirectory），**这是有意的**。
                    // 绝对路径存在 SQLite 的 Users.Avatar 列里，光改写入目录会让老用户
                    // 数据库里的指针指向旧文件、新旧两处各存一份且谁也不知道哪份算数；
                    // 要正确迁移必须同时补一个改写 Users.Avatar 的数据库侧改写器。
                    // 这件事归属未定（认证体系是否保留待拍板），详见
                    // AppPaths.Legacy.AvatarsDirectory 的注释。
                    // 这里只做两件不越界的事：路径经 AppPaths 表达（将来只需改一个符号），
                    // 以及去掉原先静默回退到 %APPDATA% 的分支——那会造出第三个位置。
                    var avatarsDir = Infrastructure.AppPaths.AvatarsDirectory;
                    System.IO.Directory.CreateDirectory(avatarsDir);
                    var ext = System.IO.Path.GetExtension(_selectedAvatarTemp);
                    var fileName = $"{_localAuth.CurrentUser.Id}_{DateTime.Now:yyyyMMddHHmmss}{ext}";
                    var dest = System.IO.Path.Combine(avatarsDir, fileName);
                    System.IO.File.Copy(_selectedAvatarTemp, dest, true);
                    avatarPath = dest;
                }

                if (!string.IsNullOrEmpty(name)) _localAuth.CurrentUser.DisplayName = name;
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
                    CyberMessageBox.Show(this, UiText.Get("保存失败，请稍后重试"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "保存账户设置失败");
                CyberMessageBox.Show(this, UiText.Interpolate($"保存失败：{ex.Message}"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
