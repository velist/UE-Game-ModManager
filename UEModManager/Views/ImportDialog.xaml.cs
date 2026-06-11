using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UEModManager.Infrastructure;
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
                .Where(ImportWarningMessages.IsUnsupportedArchive)
                .ToList();
            if (unsupportedArchives.Count > 0)
            {
                MessageBox.Show(this,
                    ImportWarningMessages.UnsupportedArchiveMessage,
                    ImportWarningMessages.UnsupportedArchiveTitle,
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
