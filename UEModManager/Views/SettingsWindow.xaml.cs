using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly GameConfigService _gameConfig;
        private readonly ObjectStore? _objectStore;
        private readonly PackageRepository? _packageRepo;
        private string? _pendingGameIconPath;
        private BackgroundMode _selectedBgMode;
        private BackgroundSettings? _originalBgSettings;
        private DeploymentBackendType _selectedBackend = DeploymentBackendType.Copy;

        public SettingsWindow()
        {
            InitializeComponent();

            var sp = (Application.Current as App)?.ServiceProvider;
            _gameConfig = sp!.GetRequiredService<GameConfigService>();
            _objectStore = sp.GetService<ObjectStore>();
            _packageRepo = sp.GetService<PackageRepository>();

            // 初始化语言下拉框（避免触发 SelectionChanged）
            LanguageComboBox.SelectionChanged -= LanguageComboBox_SelectionChanged;
            LanguageComboBox.SelectedIndex = LanguageManager.IsEnglish ? 1 : 0;
            LanguageComboBox.SelectionChanged += LanguageComboBox_SelectionChanged;

            LoadCurrentGameSettings();
            LoadBackgroundSettings();
            LoadDeploySettings();
            BackgroundManager.ApplyToDialog(DialogBgImage, DialogBgOverlay);

            Closed += SettingsWindow_Closed;

            // 关闭行为
            CloseActionComboBox.SelectedIndex = (int)UiPreferences.LoadCloseAction();

            // 插件系统
            PluginSystemToggle.IsChecked = UiPreferences.LoadPluginEnabled();

            // 语言
            LanguageManager.LanguageChanged += _ => Dispatcher.Invoke(ApplyLocalization);
            ApplyLocalization();
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void SettingsWindow_Closed(object? sender, EventArgs e)
        {
            // 如果不是保存关闭（DialogResult != true），恢复到原始背景设置
            if (DialogResult != true && _originalBgSettings != null)
            {
                BackgroundManager.RevertToSaved();
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isEnglish = LanguageComboBox.SelectedIndex == 1;
            LanguageManager.SetEnglish(isEnglish);
            UiPreferences.SaveEnglish(isEnglish);
        }

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            GeneralPanel.Visibility = Visibility.Collapsed;
            PathsPanel.Visibility = Visibility.Collapsed;
            AppearancePanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            DeployPanel.Visibility = Visibility.Collapsed;

            if (sender == TabGeneral)
                GeneralPanel.Visibility = Visibility.Visible;
            else if (sender == TabPaths)
                PathsPanel.Visibility = Visibility.Visible;
            else if (sender == TabAppearance)
                AppearancePanel.Visibility = Visibility.Visible;
            else if (sender == TabAbout)
                AboutPanel.Visibility = Visibility.Visible;
            else if (sender == TabDeploy)
                DeployPanel.Visibility = Visibility.Visible;
        }

        // ─── 加载当前游戏配置 ───

        private void LoadCurrentGameSettings()
        {
            var gameName = _gameConfig.CurrentGameName;

            if (string.IsNullOrEmpty(gameName))
            {
                CurrentGameHint.Text = "当前游戏：未选择（请先在主界面选择游戏）";
                return;
            }

            CurrentGameHint.Text = $"当前游戏：{gameName}";
            GamePathTextBox.Text = _gameConfig.CurrentGamePath;
            ModPathTextBox.Text = _gameConfig.CurrentModPath;
            BackupPathTextBox.Text = _gameConfig.CurrentBackupPath;

            // 加载游戏图标
            var iconPath = _gameConfig.GetGameIconPath(gameName);
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                _pendingGameIconPath = iconPath;
                ShowSettingsIconPreview(iconPath);
            }
        }

        // ─── 外观设置 ───

        private void LoadBackgroundSettings()
        {
            var bg = BackgroundManager.Settings;
            _originalBgSettings = bg.Clone();
            _selectedBgMode = bg.Mode;
            UpdateBgModeUI();

            // 图片路径
            if (!string.IsNullOrEmpty(bg.ImagePath))
                BgImagePathBox.Text = bg.ImagePath;

            // 滑块（暂时解绑事件避免触发）
            BgOpacitySlider.ValueChanged -= BgOpacitySlider_ValueChanged;
            BgBlurSlider.ValueChanged -= BgBlurSlider_ValueChanged;

            BgOpacitySlider.Value = bg.Opacity * 100;
            BgBlurSlider.Value = bg.BlurRadius * 100;

            BgOpacitySlider.ValueChanged += BgOpacitySlider_ValueChanged;
            BgBlurSlider.ValueChanged += BgBlurSlider_ValueChanged;

            BgOpacityValue.Text = $"{(int)(bg.Opacity * 100)}%";
            BgBlurValue.Text = $"{(int)(bg.BlurRadius * 100)}%";

            ApplyToDialogsToggle.IsChecked = bg.ApplyToDialogs;
        }

        private void UpdateBgModeUI()
        {
            var primaryBrush = (SolidColorBrush)FindResource("PrimaryBrush");
            var borderBrush = (SolidColorBrush)FindResource("CyberBorderBrush");

            BgModeGradient.BorderBrush = _selectedBgMode == BackgroundMode.Gradient ? primaryBrush : borderBrush;
            BgModeImage.BorderBrush = _selectedBgMode == BackgroundMode.Image ? primaryBrush : borderBrush;
            BgModeSolid.BorderBrush = _selectedBgMode == BackgroundMode.SolidColor ? primaryBrush : borderBrush;

            ImagePickerPanel.Visibility = _selectedBgMode == BackgroundMode.Image
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BgModeGradient_Click(object sender, MouseButtonEventArgs e)
        {
            _selectedBgMode = BackgroundMode.Gradient;
            UpdateBgModeUI();
            PreviewBackground();
        }

        private void BgModeImage_Click(object sender, MouseButtonEventArgs e)
        {
            _selectedBgMode = BackgroundMode.Image;
            UpdateBgModeUI();
            PreviewBackground();
        }

        private void BgModeSolid_Click(object sender, MouseButtonEventArgs e)
        {
            _selectedBgMode = BackgroundMode.SolidColor;
            UpdateBgModeUI();
            PreviewBackground();
        }

        private void BrowseBgImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var bgDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "UEModManager", "Backgrounds");
                    Directory.CreateDirectory(bgDir);

                    var ext = Path.GetExtension(dlg.FileName);
                    var destPath = Path.Combine(bgDir, $"custom_bg{ext}");
                    File.Copy(dlg.FileName, destPath, true);

                    BgImagePathBox.Text = destPath;
                    PreviewBackground();
                }
                catch (Exception ex)
                {
                    CyberMessageBox.Show(this, $"选择图片失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BgOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgOpacityValue != null)
                BgOpacityValue.Text = $"{(int)BgOpacitySlider.Value}%";
            PreviewBackground();
        }

        private void BgBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgBlurValue != null)
                BgBlurValue.Text = $"{(int)BgBlurSlider.Value}%";
            PreviewBackground();
        }

        /// <summary>
        /// 构建当前 UI 状态的 BackgroundSettings 并实时预览到主窗口（不持久化）。
        /// </summary>
        private void PreviewBackground()
        {
            if (_originalBgSettings == null || BgOpacitySlider == null || BgBlurSlider == null)
                return;

            var preview = new BackgroundSettings
            {
                Mode = _selectedBgMode,
                ImagePath = BgImagePathBox?.Text?.Trim(),
                Opacity = BgOpacitySlider.Value / 100.0,
                BlurRadius = BgBlurSlider.Value / 100.0,
                ApplyToDialogs = ApplyToDialogsToggle?.IsChecked == true
            };
            BackgroundManager.Preview(preview);
        }

        // ─── 游戏图标操作 ───

        private void SettingsGameIcon_Click(object sender, MouseButtonEventArgs e)
        {
            BrowseSettingsGameIcon();
        }

        private void SettingsGameIcon_BrowseClick(object sender, RoutedEventArgs e)
        {
            BrowseSettingsGameIcon();
        }

        private void BrowseSettingsGameIcon()
        {
            if (string.IsNullOrEmpty(_gameConfig.CurrentGameName))
            {
                CyberMessageBox.Show(this, "请先在主界面选择游戏后再设置图标。", "提示");
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择游戏图标",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.webp|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var iconsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "GameIcons");
                    Directory.CreateDirectory(iconsDir);
                    var ext = Path.GetExtension(dlg.FileName);
                    var destName = $"{_gameConfig.CurrentGameName.Replace(" ", "_").Replace("/", "_")}{ext}";
                    var destPath = Path.Combine(iconsDir, destName);
                    File.Copy(dlg.FileName, destPath, true);

                    _pendingGameIconPath = destPath;
                    ShowSettingsIconPreview(destPath);
                }
                catch (Exception ex)
                {
                    CyberMessageBox.Show(this, $"设置图标失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SettingsGameIcon_ClearClick(object sender, RoutedEventArgs e)
        {
            _pendingGameIconPath = "";
            SettingsGameIconPreview.Source = null;
            SettingsGameIconPreview.Visibility = Visibility.Collapsed;
            SettingsGameIconPlaceholder.Visibility = Visibility.Visible;
            SettingsClearIconBtn.Visibility = Visibility.Collapsed;
        }

        private void ShowSettingsIconPreview(string path)
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 128;
                bitmap.EndInit();
                bitmap.Freeze();

                SettingsGameIconPreview.Source = bitmap;
                SettingsGameIconPreview.Visibility = Visibility.Visible;
                SettingsGameIconPlaceholder.Visibility = Visibility.Collapsed;
                SettingsClearIconBtn.Visibility = Visibility.Visible;
            }
            catch { }
        }

        // ─── 路径浏览 ───

        private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择游戏可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe"
            };
            if (dialog.ShowDialog() == true)
                GamePathTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
        }

        private void BrowseModPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 MOD 文件夹"
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ModPathTextBox.Text = dialog.SelectedPath;
        }

        private void BrowseBackupPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择备份文件夹"
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                BackupPathTextBox.Text = dialog.SelectedPath;
        }

        // ─── 部署与仓库设置 ───

        private void LoadDeploySettings()
        {
            // 加载仓库路径
            if (_objectStore != null)
                RepoPathTextBox.Text = _objectStore.RepositoryRoot;
            else
                RepoPathTextBox.Text = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "UEModManager", "Repository");

            // 加载仓库统计
            UpdateRepoStats();

            // 加载部署后端偏好
            _selectedBackend = UiPreferences.LoadDeployBackend();
            UpdateBackendUI();

            // 加载部署选项
            DeployConfirmToggle.IsChecked = UiPreferences.LoadDeployConfirm();
            AutoDeployToggle.IsChecked = UiPreferences.LoadAutoDeploy();
        }

        private void UpdateRepoStats()
        {
            if (_packageRepo != null)
            {
                try
                {
                    var count = _packageRepo.GetTotalCount();
                    var size = _packageRepo.GetTotalSize();
                    var sizeStr = size < 1024 * 1024
                        ? $"{size / 1024.0:F1} KB"
                        : size < 1024L * 1024 * 1024
                            ? $"{size / (1024.0 * 1024):F1} MB"
                            : $"{size / (1024.0 * 1024 * 1024):F2} GB";
                    RepoSizeText.Text = $"已用空间: {sizeStr}";
                    RepoCountText.Text = $"{count} 个包";
                }
                catch
                {
                    RepoSizeText.Text = "已用空间: 未知";
                    RepoCountText.Text = "";
                }
            }
        }

        private void UpdateBackendUI()
        {
            var primaryBrush = (SolidColorBrush)FindResource("PrimaryBrush");
            var borderBrush = (SolidColorBrush)FindResource("CyberBorderBrush");

            BackendCopy.BorderBrush = _selectedBackend == DeploymentBackendType.Copy ? primaryBrush : borderBrush;
            BackendHardLink.BorderBrush = _selectedBackend == DeploymentBackendType.HardLink ? primaryBrush : borderBrush;
            BackendSymlink.BorderBrush = _selectedBackend == DeploymentBackendType.Symlink ? primaryBrush : borderBrush;

            BackendCopyRadio.IsChecked = _selectedBackend == DeploymentBackendType.Copy;
            BackendHardLinkRadio.IsChecked = _selectedBackend == DeploymentBackendType.HardLink;
            BackendSymlinkRadio.IsChecked = _selectedBackend == DeploymentBackendType.Symlink;
        }

        private void BackendCopy_Click(object sender, MouseButtonEventArgs e)
        {
            _selectedBackend = DeploymentBackendType.Copy;
            UpdateBackendUI();
        }

        private void BackendHardLink_Click(object sender, MouseButtonEventArgs e)
        {
            _selectedBackend = DeploymentBackendType.HardLink;
            UpdateBackendUI();
        }

        private void BackendSymlink_Click(object sender, MouseButtonEventArgs e)
        {
            _selectedBackend = DeploymentBackendType.Symlink;
            UpdateBackendUI();
        }

        private void BrowseRepoPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择包仓库目录"
            };
            if (!string.IsNullOrEmpty(RepoPathTextBox.Text) && Directory.Exists(RepoPathTextBox.Text))
                dialog.SelectedPath = RepoPathTextBox.Text;
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                RepoPathTextBox.Text = dialog.SelectedPath;
        }

        private void ManageRepo_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // 打开仓库管理窗口
            try
            {
                var sp = ((App)Application.Current).ServiceProvider;
                if (sp != null)
                {
                    var repoWin = sp.GetRequiredService<RepositoryManagerWindow>();
                    repoWin.Owner = this;
                    repoWin.ShowDialog();
                    UpdateRepoStats();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] 打开仓库管理失败: {ex.Message}");
            }
        }

        // ─── 多语言 ───

        private void ApplyLocalization()
        {
            if (LanguageManager.IsEnglish)
            {
                Title = "Settings";
                TabGeneral.Content = "General";
                TabPaths.Content = "Game Paths";
                TabAppearance.Content = "Appearance";
                TabAbout.Content = "About";
                AppearanceTitle.Text = "Appearance";
                BgModeLabel.Text = "Background Mode";
                BgModeGradientText.Text = "Gradient";
                BgModeImageText.Text = "Custom Image";
                BgModeSolidText.Text = "Solid Color";
                BgOpacityLabel.Text = "Background Opacity";
                BgBlurLabel.Text = "Background Blur";
                ApplyToDialogsLabel.Text = "Apply to Dialogs";
                ApplyToDialogsDesc.Text = "Show background in dialogs";
                BrowseBgImageBtn.Content = "Browse";
                CloseActionLabel.Text = "Close Action";
                CloseActionDesc.Text = "Behavior when clicking close button";
                ((ComboBoxItem)CloseActionComboBox.Items[0]).Content = "Ask every time";
                ((ComboBoxItem)CloseActionComboBox.Items[1]).Content = "Exit directly";
                ((ComboBoxItem)CloseActionComboBox.Items[2]).Content = "Minimize to taskbar";
                AboutTitle.Text = "About";
                AboutDesc1.Text = "A MOD manager designed for Unreal Engine games";
                AboutDesc2.Text = "Supports Stellar Blade, Black Myth Wukong and more";
                DonationText.Text = "If you find this helpful, buy me a coffee!";
                CreditsTitle.Text = "Credits";
            }
            else
            {
                Title = "系统设置";
                TabGeneral.Content = "常规参数";
                TabPaths.Content = "游戏路径";
                TabAppearance.Content = "外观设置";
                TabAbout.Content = "关于软件";
                AppearanceTitle.Text = "外观设置";
                BgModeLabel.Text = "背景模式";
                BgModeGradientText.Text = "默认渐变";
                BgModeImageText.Text = "自定义图片";
                BgModeSolidText.Text = "纯色背景";
                BgOpacityLabel.Text = "背景透明度";
                BgBlurLabel.Text = "背景模糊度";
                ApplyToDialogsLabel.Text = "应用到弹窗";
                ApplyToDialogsDesc.Text = "弹窗也显示背景效果";
                BrowseBgImageBtn.Content = "选择图片";
                CloseActionLabel.Text = "关闭行为";
                CloseActionDesc.Text = "点击关闭按钮时的行为";
                ((ComboBoxItem)CloseActionComboBox.Items[0]).Content = "每次询问";
                ((ComboBoxItem)CloseActionComboBox.Items[1]).Content = "直接退出";
                ((ComboBoxItem)CloseActionComboBox.Items[2]).Content = "最小化到任务栏";
                AboutTitle.Text = "关于软件";
                AboutDesc1.Text = "专为虚幻引擎游戏设计的 MOD 管理器";
                AboutDesc2.Text = "支持剑星、黑神话悟空等多款游戏";
                DonationText.Text = "如果你觉得有帮助，可以请我喝一杯咖啡！";
                CreditsTitle.Text = "鸣谢名单";
            }
        }

        // ─── 保存 ───

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var gameName = _gameConfig.CurrentGameName;

                // 保存路径（如果有游戏选中且路径有变化）
                if (!string.IsNullOrEmpty(gameName))
                {
                    var gamePath = GamePathTextBox.Text?.Trim() ?? "";
                    var modPath = ModPathTextBox.Text?.Trim() ?? "";
                    var backupPath = BackupPathTextBox.Text?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(gamePath) || !string.IsNullOrEmpty(modPath))
                    {
                        await _gameConfig.SwitchGameAsync(gameName, gamePath, modPath, backupPath);
                    }

                    // 保存游戏图标
                    if (_pendingGameIconPath != null)
                    {
                        await _gameConfig.SetGameIconAsync(gameName,
                            string.IsNullOrEmpty(_pendingGameIconPath) ? null : _pendingGameIconPath);
                    }
                }

                // 保存背景设置
                var bgSettings = new BackgroundSettings
                {
                    Mode = _selectedBgMode,
                    ImagePath = BgImagePathBox.Text?.Trim(),
                    Opacity = BgOpacitySlider.Value / 100.0,
                    BlurRadius = BgBlurSlider.Value / 100.0,
                    ApplyToDialogs = ApplyToDialogsToggle.IsChecked == true
                };
                BackgroundManager.Apply(bgSettings);

                // 保存关闭行为
                UiPreferences.SaveCloseAction((UiPreferences.CloseAction)CloseActionComboBox.SelectedIndex);

                // 保存插件系统开关
                UiPreferences.SavePluginEnabled(PluginSystemToggle.IsChecked == true);

                // 保存部署设置
                UiPreferences.SaveDeployBackend(_selectedBackend);
                UiPreferences.SaveDeployConfirm(DeployConfirmToggle.IsChecked == true);
                UiPreferences.SaveAutoDeploy(AutoDeployToggle.IsChecked == true);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"保存设置失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
