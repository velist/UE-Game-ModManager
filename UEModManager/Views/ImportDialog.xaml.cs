using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class ImportDialog : Window
    {
        private readonly PackageImportService? _importService;

        /// <summary>用户选择的文件路径列表（对话框结果）。</summary>
        public List<string> SelectedFiles { get; } = [];

        public ImportDialog()
        {
            InitializeComponent();
        }

        public ImportDialog(PackageImportService importService) : this()
        {
            _importService = importService;
        }

        // ─── 窗口命令 ───

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();

        // ─── 拖拽 ───

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                NormalDropState.Visibility = Visibility.Collapsed;
                DragOverState.Visibility = Visibility.Visible;
                DropZoneBorder.BorderBrush = (Brush)FindResource("PrimaryBrush");
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            base.OnDragLeave(e);
            NormalDropState.Visibility = Visibility.Visible;
            DragOverState.Visibility = Visibility.Collapsed;
            DropZoneBorder.BorderBrush = (Brush)FindResource("CyberBorderBrush");
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            NormalDropState.Visibility = Visibility.Visible;
            DragOverState.Visibility = Visibility.Collapsed;
            DropZoneBorder.BorderBrush = (Brush)FindResource("CyberBorderBrush");

            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            ProcessFiles(files);
        }

        // ─── 浏览按钮 ───

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要导入的文件",
                Filter = "支持的文件|*.zip;*.rar;*.7z;*.pak;*.utoc;*.ucas;*.dll;*.ini;*.json;*.cfg|所有文件|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                ProcessFiles(dlg.FileNames);
            }
        }

        // ─── 处理文件 ───

        private void ProcessFiles(string[] filePaths)
        {
            var valid = filePaths.Where(f => File.Exists(f)).ToList();
            if (valid.Count == 0) return;

            var unsupportedArchives = valid
                .Where(f => string.Equals(Path.GetExtension(f), ".rar", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(f), ".7z", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (unsupportedArchives.Count > 0)
            {
                MessageBox.Show(this,
                    "检测到 RAR/7z 压缩包。\n\n当前版本仅保证 ZIP、PAK/UCAS/UTOC 等文件稳定导入。RAR/7z 受用户电脑解压环境影响，可能出现解压失败或中文文件名乱码。\n\n请先用 WinRAR/7-Zip 手动解压，再把解压后的文件夹或其中的 MOD 文件重新导入。",
                    "请先手动解压 RAR/7z",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SelectedFiles.Clear();
            SelectedFiles.AddRange(valid);
            DialogResult = true;
            Close();
        }
    }
}
