using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UEModManager.Models;

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
        }

        private void LoadModInfo()
        {
            // 名称
            ModNameText.Text = _mod.Name;
            RealNameText.Text = _mod.RealName;

            UpdatePreviewImage();

            // 状态标签
            UpdateStatusBadge();

            // 信息
            CategoryText.Text = _mod.PrimaryCategory;
            FileSizeText.Text = _mod.FormattedSize;
            InstallDateText.Text = _mod.FormattedInstallDate;
            BackupStatusText.Text = _mod.BackupStatus;

            // 路径
            FolderPathText.Text = _mod.RealName;

            // 描述
            if (!string.IsNullOrWhiteSpace(_mod.Description))
            {
                DescriptionPanel.Visibility = Visibility.Visible;
                DescriptionText.Text = _mod.Description;
            }

            // 按钮文字
            UpdateToggleButton();
        }

        private void UpdatePreviewImage()
        {
            if (_mod.PreviewImage == null && !string.IsNullOrEmpty(_mod.PreviewImagePath) && File.Exists(_mod.PreviewImagePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_mod.PreviewImagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                bitmap.Freeze();
                _mod.PreviewImage = bitmap;
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
                StatusText.Text = "已启用";
                StatusText.Foreground = Brushes.White;
            }
            else
            {
                StatusBadge.Background = FindResource("SurfaceHoverBrush") as Brush;
                StatusText.Text = "已禁用";
                StatusText.Foreground = FindResource("Text500Brush") as Brush ?? Brushes.Gray;
            }
        }

        private void UpdateToggleButton()
        {
            ToggleBtn.Content = _mod.IsEnabled ? "禁用MOD" : "启用MOD";
        }

        private async void ToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            var changed = _onToggle == null || await _onToggle(_mod);
            if (!changed) return;

            ModChanged = true;
            UpdateStatusBadge();
            UpdateToggleButton();
        }

        private async void ChangePreviewBtn_Click(object sender, RoutedEventArgs e)
        {
            var changed = _onChangePreview == null || await _onChangePreview(_mod);
            if (!changed) return;

            ModChanged = true;
            UpdatePreviewImage();
        }

        private async void RenameBtn_Click(object sender, RoutedEventArgs e)
        {
            var newName = CyberInputDialog.Show(this, "编辑MOD", "请输入MOD显示名称:", _mod.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == _mod.Name) return;

            var changed = _onRename == null || await _onRename(_mod, newName);
            if (!changed) return;

            ModNameText.Text = _mod.Name;
            ModChanged = true;
        }

        private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            var r = CyberMessageBox.Show(this, $"确认删除 '{_mod.Name}'？\n此操作会从当前方案、包仓库和已部署文件中移除此 MOD。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            var changed = _onDelete == null || await _onDelete(_mod);
            if (!changed) return;

            ModChanged = true;
            DialogResult = true;
            Close();
        }

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
