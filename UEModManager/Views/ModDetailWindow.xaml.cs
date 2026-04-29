using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Models;

namespace UEModManager.Views
{
    public partial class ModDetailWindow : Window
    {
        private readonly ModInfo _mod;
        private readonly Action<ModInfo>? _onToggle;
        private readonly Action<ModInfo>? _onDelete;
        private readonly Action<ModInfo>? _onChangePreview;
        private readonly Action<ModInfo>? _onRename;

        public bool ModChanged { get; private set; }

        public ModDetailWindow(ModInfo mod,
            Action<ModInfo>? onToggle = null,
            Action<ModInfo>? onDelete = null,
            Action<ModInfo>? onChangePreview = null,
            Action<ModInfo>? onRename = null)
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

            // 预览图
            if (_mod.PreviewImage != null)
            {
                PreviewImage.Source = _mod.PreviewImage;
                NoPreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                NoPreviewPlaceholder.Visibility = Visibility.Visible;
            }

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

        private void ToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _onToggle?.Invoke(_mod);
            ModChanged = true;
            UpdateStatusBadge();
            UpdateToggleButton();
        }

        private void ChangePreviewBtn_Click(object sender, RoutedEventArgs e)
        {
            _onChangePreview?.Invoke(_mod);
            ModChanged = true;
            // 刷新预览图
            if (_mod.PreviewImage != null)
            {
                PreviewImage.Source = _mod.PreviewImage;
                PreviewImage.Visibility = Visibility.Visible;
                NoPreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
        }

        private void RenameBtn_Click(object sender, RoutedEventArgs e)
        {
            var newName = CyberInputDialog.Show(this, "编辑MOD", "请输入MOD显示名称:", _mod.Name);
            if (!string.IsNullOrWhiteSpace(newName) && newName != _mod.Name)
            {
                _onRename?.Invoke(_mod);
                _mod.Name = newName;
                ModNameText.Text = newName;
                ModChanged = true;
            }
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            var r = CyberMessageBox.Show(this, $"确认删除 '{_mod.Name}'？\n此操作不可恢复！",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                _onDelete?.Invoke(_mod);
                ModChanged = true;
                DialogResult = true;
                Close();
            }
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
