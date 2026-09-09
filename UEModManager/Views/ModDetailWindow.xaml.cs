using UEModManager.Localization;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class ModDetailWindow : Window
    {
        private readonly ModInfo _mod;
        private readonly Func<ModInfo, Task<bool>>? _onToggle;
        private readonly Func<ModInfo, Task<bool>>? _onDelete;
        private readonly Func<ModInfo, Task<bool>>? _onChangePreview;
        private readonly Func<ModInfo, string, Task<bool>>? _onRename;

        public bool ModChanged { get; private set; }

        public ModDetailWindow(ModInfo mod,
            Func<ModInfo, Task<bool>>? onToggle = null,
            Func<ModInfo, Task<bool>>? onDelete = null,
            Func<ModInfo, Task<bool>>? onChangePreview = null,
            Func<ModInfo, string, Task<bool>>? onRename = null)
        {
            InitializeComponent();
            _mod = mod;
            _onToggle = onToggle;
            _onDelete = onDelete;
            _onChangePreview = onChangePreview;
            _onRename = onRename;

            LoadModInfo();
            LanguageManager.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(bool _) => Dispatcher.Invoke(UpdateLocalizedInfo);

        protected override void OnClosed(EventArgs e)
        {
            LanguageManager.LanguageChanged -= OnLanguageChanged;
            base.OnClosed(e);
        }

        private void UpdateLocalizedInfo()
        {
            CategoryText.Text = CategoryDisplayNames.For(_mod.PrimaryCategory);
            BackupStatusText.Text = UiText.Get(_mod.BackupStatus);
            UpdateStatusBadge();
            UpdateToggleButton();
        }

        private void LoadModInfo()
        {
            // 名称
            ModNameText.Text = _mod.Name;
            RealNameText.Text = _mod.RealName;

            UpdatePreviewImage();

            // 状态标签
            UpdateLocalizedInfo();

            // 信息
            FileSizeText.Text = _mod.FormattedSize;
            InstallDateText.Text = _mod.FormattedInstallDate;

            // 路径
            FolderPathText.Text = _mod.RealName;

            // 描述
            if (!string.IsNullOrWhiteSpace(_mod.Description))
            {
                DescriptionPanel.Visibility = Visibility.Visible;
                DescriptionText.Text = _mod.Description;
            }

        }

        private void UpdatePreviewImage()
        {
            if (_mod.PreviewImage == null && !string.IsNullOrEmpty(_mod.PreviewImagePath) && File.Exists(_mod.PreviewImagePath))
            {
                var bitmap = UEModManager.Infrastructure.ImageLoader.LoadFrozen(_mod.PreviewImagePath, ignoreImageCache: true);
                if (bitmap != null)
                {
                    _mod.PreviewImage = bitmap;
                }
            }

            if (_mod.PreviewImage != null)
            {
                PreviewImage.Source = _mod.PreviewImage;
                PreviewImage.Visibility = Visibility.Visible;
                NoPreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                NoPreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private void UpdateStatusBadge()
        {
            if (_mod.IsEnabled)
            {
                StatusBadge.Background = FindResource("StatusGreenBrush") as Brush;
                StatusText.Text = UiText.Get("已启用");
                StatusText.Foreground = Brushes.White;
            }
            else
            {
                StatusBadge.Background = FindResource("SurfaceHoverBrush") as Brush;
                StatusText.Text = UiText.Get("已禁用");
                StatusText.Foreground = FindResource("Text500Brush") as Brush ?? Brushes.Gray;
            }
        }

        private void UpdateToggleButton()
        {
            ToggleBtn.Content = _mod.IsEnabled ? UiText.Get("禁用MOD") : UiText.Get("启用MOD");
        }

        private void ToggleBtn_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var changed = _onToggle == null || await _onToggle(_mod);
                if (!changed) return;

                ModChanged = true;
                UpdateStatusBadge();
                UpdateToggleButton();
            }, null, UiText.Get("切换 MOD 启用状态"));

        private void ChangePreviewBtn_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var changed = _onChangePreview == null || await _onChangePreview(_mod);
                if (!changed) return;

                ModChanged = true;
                UpdatePreviewImage();
            }, null, UiText.Get("更换 MOD 预览图"));

        private void RenameBtn_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var newName = CyberInputDialog.Show(this, UiText.Get("编辑MOD"), UiText.Get("请输入MOD显示名称:"), _mod.Name);
                if (string.IsNullOrWhiteSpace(newName) || newName == _mod.Name) return;

                var changed = _onRename == null || await _onRename(_mod, newName);
                if (!changed) return;

                ModNameText.Text = _mod.Name;
                ModChanged = true;
            }, null, UiText.Get("重命名 MOD"));

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                var r = CyberMessageBox.Show(this, UiText.Interpolate($"确认删除 '{_mod.Name}'？\n此操作会从当前方案、MOD 文件库和游戏目录中移除此 MOD。"),
                    UiText.Get("确认删除"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;

                var changed = _onDelete == null || await _onDelete(_mod);
                if (!changed) return;

                ModChanged = true;
                DialogResult = true;
                Close();
            }, null, UiText.Get("删除 MOD"));

        private void CloseBtn_Click(object sender, MouseButtonEventArgs e)
        {
            DialogResult = ModChanged;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = ModChanged;
                Close();
            }
        }
    }
}
