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
        private readonly DiagnosticExportService? _diagnosticExport;
        private string? _pendingGameIconPath;
        private BackgroundMode _selectedBgMode;
        private BackgroundSettings? _originalBgSettings;
        private DeploymentBackendType _selectedBackend = DeploymentBackendType.Copy;

        /// <summary>
        /// 背景图副本的实际路径（程序内部 AppData 下的拷贝），用于持久化与渲染。
        /// 文本框 BgImagePathBox 显示的是用户**原始**选择路径，仅作视觉反馈，不参与渲染。
        /// </summary>
        private string? _bgImageActualPath;

        public SettingsWindow()
        {
            InitializeComponent();

            var sp = (Application.Current as App)?.ServiceProvider;
            _gameConfig = sp!.GetRequiredService<GameConfigService>();
            _objectStore = sp.GetService<ObjectStore>();
            _packageRepo = sp.GetService<PackageRepository>();
            _diagnosticExport = sp.GetService<DiagnosticExportService>();

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
            FeedbackPanel.Visibility = Visibility.Collapsed;

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
            else if (sender == TabFeedback)
                FeedbackPanel.Visibility = Visibility.Visible;
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

            // 图片路径：bg.ImagePath 持久化的是副本路径
            // 显示在文本框里时尽量给"用户能看懂"的路径——若副本存在，仍显示副本路径（兼容旧设置）
            _bgImageActualPath = string.IsNullOrEmpty(bg.ImagePath) ? null : bg.ImagePath;
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

                    // 唯一文件名 — 避开 File.Copy 同名覆盖竞争 + WPF BitmapImage URI 缓存
                    var ext = Path.GetExtension(dlg.FileName);
                    var destPath = Path.Combine(bgDir, $"bg_{DateTime.Now.Ticks}{ext}");
                    File.Copy(dlg.FileName, destPath, true);

                    Console.WriteLine($"[Settings] 背景图已复制: {dlg.FileName} -> {destPath}");

                    // 清理旧副本，保留当前一份（含历史 custom_bg.* 命名）
                    CleanupOldBackgroundCopies(bgDir, destPath);

                    // 字段：实际副本路径（用于渲染 / 持久化）
                    _bgImageActualPath = destPath;

                    // 文本框：用户**原始**选择路径（视觉反馈，不参与渲染）
                    BgImagePathBox.Text = dlg.FileName;

                    PreviewBackground();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Settings] 背景图选择失败: {ex.Message}");
                    CyberMessageBox.Show(this, $"选择图片失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>清理 Backgrounds 目录下除当前副本外的所有 bg_* / custom_bg.* 旧文件，避免堆积。</summary>
        private static void CleanupOldBackgroundCopies(string bgDir, string keepPath)
        {
            try
            {
                var keep = Path.GetFullPath(keepPath);
                var files = System.Linq.Enumerable.Concat(
                    Directory.EnumerateFiles(bgDir, "bg_*"),
                    Directory.EnumerateFiles(bgDir, "custom_bg.*"));
                foreach (var f in files)
                {
                    if (string.Equals(Path.GetFullPath(f), keep, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(f); } catch { /* 被占用就跳过，下次再清 */ }
                }
            }
            catch { /* 清理是 best-effort，失败不影响主流程 */ }
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
                // 优先用副本路径（_bgImageActualPath），fallback 到文本框（兼容旧设置加载）
                ImagePath = _bgImageActualPath ?? BgImagePathBox?.Text?.Trim(),
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
                CyberMessageBox.Show(this, "\u8bf7\u5148\u5728\u4e3b\u754c\u9762\u9009\u62e9\u6e38\u620f\u540e\u518d\u8bbe\u7f6e\u56fe\u6807\u3002", "\u63d0\u793a");
                return;
            }

            try
            {
                var destPath = UEModManager.Infrastructure.GameIconPicker.BrowseAndCopy(this, _gameConfig.CurrentGameName);
                if (string.IsNullOrEmpty(destPath)) return;

                _pendingGameIconPath = destPath;
                ShowSettingsIconPreview(destPath);
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"\u8bbe\u7f6e\u56fe\u6807\u5931\u8d25: {ex.Message}", "\u9519\u8bef",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
                var bitmap = UEModManager.Infrastructure.ImageLoader.LoadFrozen(path, decodePixelWidth: 128);
                if (bitmap == null) return;

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
                TabFeedback.Content = "Feedback & Diagnostics";
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
                AboutDesc1.Text = "A mod manager for popular games, built for everyone";
                AboutDesc2.Text = "Supports multiple popular games";
                DonationText.Text = "If you find this helpful, buy me a coffee!";
                CreditsTitle.Text = "Credits";

                // 反馈与诊断
                FeedbackTitle.Text = "Feedback & Diagnostics";
                FeedbackSubtitle.Text = "Export a diagnostic bundle when reporting issues, or contact us via the channels below.";
                DiagBundleTitle.Text = "Export Diagnostic Bundle";
                DiagBundleHint.Text = "Bundle logs, current data snapshot, and recent transactions in one click";
                DiagBundleContentLabel.Text = "Bundle contents";
                DiagBundleRedactHint.Text = "Passwords, tokens and emails are automatically redacted — safe to send.";
                ExportDiagButton.Content = "Export Bundle";
                FeedbackChannelsTitle.Text = "Contact Channels";
                QqGroupLabel.Text = "QQ Group (recommended)";
                EmailLabel.Text = "Email";
                GithubLabel.Text = "GitHub Issue";
                JoinQqGroupBtn.Content = "Join";
                SendEmailBtn.Content = "Send";
                OpenGithubBtn.Content = "Open";
            }
            else
            {
                Title = "系统设置";
                TabGeneral.Content = "常规参数";
                TabPaths.Content = "游戏路径";
                TabAppearance.Content = "外观设置";
                TabAbout.Content = "关于软件";
                TabFeedback.Content = "反馈与诊断";
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
                AboutDesc1.Text = "面向普通玩家的游戏 MOD 管理器，已适配多款热门游戏";
                AboutDesc2.Text = "轻松管理导入、启用、备份与恢复";
                DonationText.Text = "如果你觉得有帮助，可以请我喝一杯咖啡！";
                CreditsTitle.Text = "鸣谢名单";

                // 反馈与诊断
                FeedbackTitle.Text = "反馈与诊断";
                FeedbackSubtitle.Text = "出问题时导出诊断包发给开发者，或通过下面的渠道直接联系";
                DiagBundleTitle.Text = "导出诊断包";
                DiagBundleHint.Text = "一键打包日志、当前数据快照与最近事务记录";
                DiagBundleContentLabel.Text = "包内容";
                DiagBundleRedactHint.Text = "包内的密码、token、邮箱地址会自动脱敏，可以放心发给开发者";
                ExportDiagButton.Content = "导出诊断包";
                FeedbackChannelsTitle.Text = "反馈渠道";
                QqGroupLabel.Text = "QQ 群（推荐）";
                EmailLabel.Text = "邮箱";
                GithubLabel.Text = "GitHub Issue";
                JoinQqGroupBtn.Content = "加入";
                SendEmailBtn.Content = "发送";
                OpenGithubBtn.Content = "打开";
            }
        }

        // ─── 反馈与诊断 ───

        private async void ExportDiagnostic_Click(object sender, RoutedEventArgs e)
        {
            if (_diagnosticExport == null)
            {
                CyberMessageBox.Show(this,
                    "诊断包导出服务未初始化。请重启程序后再试。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出诊断包",
                FileName = $"UEModManager_diag_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                Filter = "诊断包 (*.zip)|*.zip",
                DefaultExt = ".zip"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                ExportDiagButton.IsEnabled = false;

                var count = await _diagnosticExport.ExportToZipAsync(dialog.FileName);

                CyberMessageBox.Show(this,
                    $"诊断包已导出（共 {count} 个条目）：\n{dialog.FileName}\n\n敏感信息已自动脱敏，可放心发给开发者。",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this,
                    $"导出失败：{ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ExportDiagButton.IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        private void JoinQqGroup_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://qm.qq.com/q/CIi6LT94zK");
        }

        private void SendEmail_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("mailto:mr.xzuo@foxmail.com?subject=爱酱MOD管理器%20-%20反馈&body=请在这里描述问题，并附上诊断包文件%0A%0A");
        }

        private void OpenGithub_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/velist/UE-Game-ModManager");
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] 打开链接失败 {url}: {ex.Message}");
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
                    // 优先用副本路径，fallback 到文本框（兼容旧设置）
                    ImagePath = _bgImageActualPath ?? BgImagePathBox.Text?.Trim(),
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
                var repoPath = RepoPathTextBox.Text?.Trim();
                if (_objectStore != null && !string.IsNullOrWhiteSpace(repoPath))
                {
                    Directory.CreateDirectory(repoPath);
                    _objectStore.SetRepositoryRoot(repoPath);
                }

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
