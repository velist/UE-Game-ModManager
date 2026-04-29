using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class PluginImportDialog : Window
    {
        private readonly string _gamePath;
        private readonly string _defaultPluginPath;
        private readonly List<string> _selectedPaths = new();

        /// <summary>
        /// 用户选择的文件/文件夹路径列表。
        /// </summary>
        public string[] SelectedFilePaths => _selectedPaths.ToArray();

        /// <summary>
        /// 用户指定的目标路径（相对于游戏根目录）。
        /// </summary>
        public string TargetPath => TargetPathBox.Text?.Trim() ?? string.Empty;

        public PluginImportDialog(string gamePath, string defaultPluginPath)
        {
            InitializeComponent();
            _gamePath = gamePath;
            _defaultPluginPath = defaultPluginPath;

            TargetPathBox.Text = defaultPluginPath;
            TargetPathBox.TextChanged += (_, _) => UpdateFullPathPreview();
            UpdateFullPathPreview();

            BackgroundManager.ApplyToDialog(DialogBgImage, DialogBgOverlay);
        }

        private void UpdateFullPathPreview()
        {
            var target = TargetPathBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(_gamePath) && !string.IsNullOrEmpty(target))
                FullPathPreview.Text = $"完整路径: {Path.Combine(_gamePath, target)}";
            else
                FullPathPreview.Text = "";
        }

        private void UpdateFileList()
        {
            if (_selectedPaths.Count == 0)
            {
                FileListPanel.Visibility = Visibility.Collapsed;
                ImportBtn.IsEnabled = false;
                return;
            }

            var items = _selectedPaths.Select(p =>
            {
                bool isDir = Directory.Exists(p) && !File.Exists(p);
                long size = 0;
                if (isDir)
                {
                    try { size = Directory.GetFiles(p, "*.*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
                    catch { }
                }
                else if (File.Exists(p))
                {
                    size = new FileInfo(p).Length;
                }
                return new FileListItem
                {
                    Name = isDir ? $"[文件夹] {Path.GetFileName(p)}" : Path.GetFileName(p),
                    SizeText = FormatSize(size)
                };
            }).ToList();

            FileListItems.ItemsSource = items;

            var isEn = LanguageManager.IsEnglish;
            FileCountText.Text = isEn
                ? $"{_selectedPaths.Count} item(s) selected"
                : $"已选择 {_selectedPaths.Count} 个文件";

            FileListPanel.Visibility = Visibility.Visible;
            ImportBtn.IsEnabled = true;
        }

        private void SelectFiles_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = LanguageManager.IsEnglish ? "Select Plugin Files" : "选择插件文件",
                Filter = "所有文件|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                _selectedPaths.Clear();
                _selectedPaths.AddRange(dlg.FileNames);
                UpdateFileList();
            }
        }

        private void SelectFolder_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = LanguageManager.IsEnglish ? "Select Plugin Folder" : "选择插件文件夹",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _selectedPaths.Clear();
                _selectedPaths.Add(dlg.SelectedPath);
                UpdateFileList();
            }
        }

        private void BrowseTarget_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = LanguageManager.IsEnglish ? "Select Target Directory" : "选择目标目录",
                UseDescriptionForTitle = true
            };

            // 如果有游戏路径，尝试从游戏路径开始浏览
            if (!string.IsNullOrEmpty(_gamePath) && Directory.Exists(_gamePath))
                dlg.InitialDirectory = _gamePath;

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var selectedPath = dlg.SelectedPath;
                // 如果选择了游戏路径下的子目录，转换为相对路径
                if (!string.IsNullOrEmpty(_gamePath) && selectedPath.StartsWith(_gamePath, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = Path.GetRelativePath(_gamePath, selectedPath);
                }
                TargetPathBox.Text = selectedPath;
            }
        }

        private void ImportBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPaths.Count == 0) return;
            if (string.IsNullOrWhiteSpace(TargetPathBox.Text))
            {
                CyberMessageBox.Show(this,
                    LanguageManager.IsEnglish ? "Please specify a target path." : "请指定目标路径。",
                    LanguageManager.IsEnglish ? "Notice" : "提示");
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            DialogResult = false;
            SystemCommands.CloseWindow(this);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            int i = 0;
            double size = bytes;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return $"{size:F1} {units[i]}";
        }

        public class FileListItem
        {
            public string Name { get; set; } = "";
            public string SizeText { get; set; } = "";
        }
    }
}
