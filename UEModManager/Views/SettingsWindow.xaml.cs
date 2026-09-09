using UEModManager.Localization;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Paths;

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
            : this(GetServices().GetRequiredService<GameConfigService>(), GetServices().GetService<ObjectStore>(),
                GetServices().GetService<PackageRepository>(), GetServices().GetService<DiagnosticExportService>())
        {
        }

        private static IServiceProvider GetServices()
            => (Application.Current as App)?.ServiceProvider ?? throw new InvalidOperationException("ServiceProvider is not initialized.");

        internal SettingsWindow(GameConfigService gameConfig, ObjectStore? objectStore = null,
            PackageRepository? packageRepository = null, DiagnosticExportService? diagnosticExport = null,
            ResourceDictionary? resources = null)
        {
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            _objectStore = objectStore;
            _packageRepo = packageRepository;
            _diagnosticExport = diagnosticExport;
            if (resources != null) Resources = resources;
            InitializeComponent();

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

            // 检查更新与匿名统计（同一个开关，因为它们本来就是同一个网络请求）
            UpdateAndStatsToggle.IsChecked = UiPreferences.LoadTelemetryConsent().Enabled;

            // 语言
            LanguageManager.LanguageChanged += OnLanguageChanged;
            ApplyLocalization();
        }

        /// <summary>静态事件的具名 handler（必须具名，lambda 无法退订）。</summary>
        private void OnLanguageChanged(bool isEnglish) => Dispatcher.Invoke(ApplyLocalization);

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void SettingsWindow_Closed(object? sender, EventArgs e)
        {
            // 静态事件是 GC root，必须显式退订，否则每开一次设置窗就永久泄漏一个窗口
            LanguageManager.LanguageChanged -= OnLanguageChanged;

            // 如果不是保存关闭（DialogResult != true），恢复到原始背景设置
            if (DialogResult != true && _originalBgSettings != null)
            {
                BackgroundManager.RevertToSaved();
            }
        }

        /// <summary>
        /// 语言是"选中即生效"的即时设置，不经过保存按钮，所以失败处理必须写在这里
        /// （<c>Save_Click</c> 的 try/catch 兜不到它）。
        ///
        /// <para>
        /// 顺序是先落盘再改界面：反过来的话写盘失败时界面已经切成新语言，用户以为成功了，
        /// 重启后又变回去。失败时把下拉框拨回当前生效的语言——拨之前必须先摘掉本 handler，
        /// 否则 <c>SelectedIndex</c> 的赋值会再触发一次本方法（构造函数里初始化下拉框
        /// 用的是同一个手法）。
        /// </para>
        /// </summary>
        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isEnglish = LanguageComboBox.SelectedIndex == 1;

            try
            {
                LanguageManager.SaveAndSetEnglish(isEnglish);
            }
            catch (Exception ex)
            {
                LanguageComboBox.SelectionChanged -= LanguageComboBox_SelectionChanged;
                LanguageComboBox.SelectedIndex = LanguageManager.IsEnglish ? 1 : 0;
                LanguageComboBox.SelectionChanged += LanguageComboBox_SelectionChanged;

                CyberMessageBox.Show(this, UiText.Interpolate($"保存语言设置失败：{ex.Message}"), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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
                UpdateCurrentGameHint();
                return;
            }

            UpdateCurrentGameHint();
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
                Title = UiText.Get("选择背景图片"),
                Filter = UiText.Get("图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*")
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var bgDir = AppPaths.BackgroundsDirectory;
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
                    CyberMessageBox.Show(this, UiText.Interpolate($"选择图片失败: {ex.Message}"), UiText.Get("错误"),
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
                CyberMessageBox.Show(this, UiText.Get("\u8bf7\u5148\u5728\u4e3b\u754c\u9762\u9009\u62e9\u6e38\u620f\u540e\u518d\u8bbe\u7f6e\u56fe\u6807\u3002"), UiText.Get("\u63d0\u793a"));
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
                CyberMessageBox.Show(this, UiText.Interpolate($"\u8bbe\u7f6e\u56fe\u6807\u5931\u8d25: {ex.Message}"), UiText.Get("\u9519\u8bef"),
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
                Title = UiText.Get("选择游戏可执行文件"),
                Filter = UiText.Get("可执行文件 (*.exe)|*.exe")
            };
            if (dialog.ShowDialog() == true)
                GamePathTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
        }

        private void BrowseModPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = UiText.Get("选择 MOD 文件夹")
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ModPathTextBox.Text = dialog.SelectedPath;
        }

        private void BrowseBackupPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = UiText.Get("选择备份文件夹")
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                BackupPathTextBox.Text = dialog.SelectedPath;
        }

        // ─── 部署与仓库设置 ───

        private void LoadDeploySettings()
        {
            // 加载仓库路径。回退值走 AppPaths：此前这里自己拼了一份默认位置，
            // 用户把仓库设到别的盘时显示的仍是默认路径，与实际读写的目录对不上。
            RepoPathTextBox.Text = _objectStore?.RepositoryRoot ?? AppPaths.RepositoryRoot;

            // 加载仓库统计
            UpdateRepoStats();

            // 加载部署后端偏好
            _selectedBackend = UiPreferences.LoadDeployBackend();
            UpdateBackendUI();

            // 加载部署选项
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
                    RepoSizeText.Text = UiText.Interpolate($"已用空间: {sizeStr}");
                    RepoCountText.Text = UiText.Interpolate($"{count} 个包");
                }
                catch
                {
                    RepoSizeText.Text = UiText.Get("已用空间: 未知");
                    RepoCountText.Text = "";
                }
            }
        }

        private void UpdateBackendUI()
        {
            var primaryBrush = (SolidColorBrush)FindResource("PrimaryBrush");
            var borderBrush = (SolidColorBrush)FindResource("CyberBorderBrush");
        }

        private void BrowseRepoPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = UiText.Get("选择包仓库目录")
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
                    var repoWin = sp.GetRequiredService<ManagementCenterWindow>();
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
            LanguageComboBox.SelectionChanged -= LanguageComboBox_SelectionChanged;
            LanguageComboBox.SelectedIndex = LanguageManager.IsEnglish ? 1 : 0;
            LanguageComboBox.SelectionChanged += LanguageComboBox_SelectionChanged;
            UpdateCurrentGameHint();
            UpdateRepoStats();
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "2.1.0";
            VersionText.Text = UiText.Format("版本 {0}", version);
        }

        private void UpdateCurrentGameHint()
        {
            CurrentGameHint.Text = string.IsNullOrEmpty(_gameConfig.CurrentGameName)
                ? UiText.Get("当前游戏：未选择（请先在主界面选择游戏）")
                : UiText.Format("当前游戏：{0}", GameDisplayNames.For(_gameConfig.CurrentGameName));
        }

        // ─── 反馈与诊断 ───

        private async void ExportDiagnostic_Click(object sender, RoutedEventArgs e)
        {
            if (_diagnosticExport == null)
            {
                CyberMessageBox.Show(this,
                    UiText.Get("诊断包导出服务未初始化。请重启程序后再试。"),
                    UiText.Get("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = UiText.Get("导出诊断包"),
                FileName = $"UEModManager_diag_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                Filter = UiText.Get("诊断包 (*.zip)|*.zip"),
                DefaultExt = ".zip"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                ExportDiagButton.IsEnabled = false;

                var count = await _diagnosticExport.ExportToZipAsync(dialog.FileName);

                CyberMessageBox.Show(this,
                    UiText.Interpolate($"诊断包已导出（共 {count} 个条目）：\n{dialog.FileName}\n\n敏感信息已自动脱敏，可放心发给开发者。"),
                    UiText.Get("导出成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this,
                    UiText.Interpolate($"导出失败：{ex.Message}"),
                    UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            OpenUrl("mailto:mr.xzuo@foxmail.com?subject=" + Uri.EscapeDataString(UiText.Get("爱酱MOD管理器 - 反馈"))
                + "&body=" + Uri.EscapeDataString(UiText.Get("请在这里描述问题，并附上诊断包文件\n\n")));
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

                // 保存"检查更新与匿名统计"开关。
                // 走 SaveTelemetryEnabled 而不是直接写字段：它会顺带把"已问过"钉上——
                // 一个先在设置里关掉、却从没被弹窗问过的用户，下次启动本该被弹一次
                // "我们要开始统计了"，而他刚刚才明确表示过不要。
                UiPreferences.SaveTelemetryEnabled(UpdateAndStatsToggle.IsChecked == true);

                // 保存部署设置
                UiPreferences.SaveDeployBackend(_selectedBackend);
                UiPreferences.SaveAutoDeploy(AutoDeployToggle.IsChecked == true);

                // MOD 存放位置改在最后，而且走的是"先搬数据、搬成了才改位置"那条唯一的路。
                //
                // 此前这里是一行 _objectStore.SetRepositoryRoot(repoPath)——只改指针不搬数据。
                // 用户改完，已导入的包实体还躺在旧位置、新仓库是空的，界面上 MOD 全没了，
                // 而他既不知道发生了什么、也不会想到要自己去拷目录。那个方法现在已经整个删掉了。
                //
                // 放在最后是因为搬移会弹一个模态窗口、可能跑上十几分钟：排在前面的话，
                // 用户在搬移窗口里点"先不用"之后，前面那些设置到底存没存下来就说不清了。
                // 现在的语义很干净——走到这一步，别的设置已经全部落盘。
                if (!await TryRelocateRepositoryAsync()) return;

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                // 这个 catch 是承重的：上面每一个 UiPreferences.SaveXxx / BackgroundManager.Apply
                // 落盘失败都会抛到这里。抛之前偏好没被改动，此时窗口保持打开、DialogResult 仍是
                // null，用户看到失败原因后可以改路径重试。别把它收窄成某个具体异常类型，
                // 也别在中途 catch 掉——那等于把"设置没保存上"重新变成静默。
                CyberMessageBox.Show(this, UiText.Interpolate($"保存设置失败: {ex.Message}"), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 把用户在这里选的 MOD 存放位置真正落实：<b>先把数据搬过去，搬成了才改位置</b>。
        ///
        /// <para>
        /// 与部署降级提示里那个"帮我搬过去"走的是<b>同一个服务、同一个窗口</b>。
        /// 一个功能一条路径是硬要求：留一条只改指针的旁路，用户从设置里改照样丢 MOD，
        /// 而那正是这轮改动要根治的病。
        /// </para>
        /// </summary>
        /// <returns>
        /// 能不能关掉设置窗口。用户在搬移窗口里点了"先不用"或者搬移失败时返回 false
        /// ——此时设置窗口保持打开，路径框也退回原值，让他看得见"这次没改成"。
        /// 直接关窗口会让他以为已经换好了。
        /// </returns>
        private async Task<bool> TryRelocateRepositoryAsync()
        {
            var selected = RepoPathTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(selected)) return true;

            var relocation = ((App)Application.Current).ServiceProvider
                ?.GetService<RepositoryRelocationService>();
            if (relocation == null || _objectStore == null)
            {
                // 容器里没有这两个服务是不该发生的。此时唯一安全的做法是什么都不做
                // ——绝不能退回"直接改指针"，那正是会让 MOD 消失的那条路。
                Console.WriteLine("[Settings] 搬移服务不可用，本次不改 MOD 存放位置");
                return true;
            }

            // 落点换算与首次运行引导共用同一份判据：选中的目录里已经有别的东西时，
            // 落点自动退到它下面的 UEModManager\Repository，免得把用户自己的文件夹变成仓库根。
            var hasContent = DirectoryHasContent(selected);
            var resolved = RepositoryLocationValidator.ResolveRepositoryPath(selected, hasContent);

            var plan = relocation.Plan(resolved);

            // 没变化就别弹窗。用户改了别的设置顺手点保存，不该被一个"要不要搬 MOD"的框拦一次。
            if (plan.Action == RepositoryRelocationAction.None) return true;

            // 仓库是空的（新用户）：没什么可搬，秒改，不弹进度条。
            if (plan.Action == RepositoryRelocationAction.PointerOnly)
            {
                var quick = await relocation.ExecuteAsync(plan);
                if (quick.Switched)
                {
                    RepoPathTextBox.Text = _objectStore.RepositoryRoot;
                    return true;
                }

                CyberMessageBox.Show(this,
                    RepositoryRelocationMessages.FailureBody(plan, quick.FailureDetail ?? string.Empty, LanguageManager.IsEnglish),
                    UiText.Get("没能换位置"), MessageBoxButton.OK, MessageBoxImage.Warning, okText: UiText.Get("知道了"));
                RepoPathTextBox.Text = _objectStore.RepositoryRoot;
                return false;
            }

            var window = new RepositoryRelocationWindow(relocation, plan, AnyModDeployed())
            {
                Owner = this,
            };
            window.ShowDialog();

            // 无论搬成没搬成，路径框都退回"此刻真正生效的位置"：
            // 显示一个并没有生效的路径，正是让用户误以为 MOD 已经换过去的那一步。
            RepoPathTextBox.Text = _objectStore.RepositoryRoot;
            UpdateRepoStats();

            return window.Switched;
        }

        /// <summary>
        /// 目录里有没有东西。枚举失败按"有内容"处理，与
        /// <c>RepositorySetupService.HasContent</c> 取同一个保守方向：
        /// 错判成"有内容"只会让落点多一层专用子目录（安全且界面上写明了），
        /// 错判成"空"则可能把用户自己的文件夹直接变成仓库根。
        /// </summary>
        private static bool DirectoryHasContent(string path)
        {
            try
            {
                return Directory.Exists(path)
                    && Directory.EnumerateFileSystemEntries(path).Any();
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 当前有没有已经部署到游戏目录的 MOD。只影响结果文案要不要提"重新装一次"。
        /// 查不出来时按 <c>false</c>：一句多余的提示会让没部署过的人困惑，
        /// 而漏掉它的代价只是旧盘上那点空间晚一会儿才腾出来（游戏照常能玩）。
        /// </summary>
        private static bool AnyModDeployed()
        {
            try
            {
                var profiles = ((App)Application.Current).ServiceProvider
                    ?.GetService<ProfileService>();
                return profiles?.CurrentProfile?.Packages.Any(p => p.IsEnabled) == true;
            }
            catch
            {
                return false;
            }
        }
    }
}
