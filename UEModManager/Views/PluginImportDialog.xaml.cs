using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using UEModManager.Infrastructure;
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

        /// <summary>
        /// 刷新已选文件列表。目录大小是递归 IO（大插件目录可能几万个文件），
        /// 必须放到后台线程：这里是选择文件/文件夹后的同步 UI 路径，原来会直接冻结窗口。
        /// </summary>
        private async Task UpdateFileListAsync()
        {
            if (_selectedPaths.Count == 0)
            {
                FileListPanel.Visibility = Visibility.Collapsed;
                ImportBtn.IsEnabled = false;
                return;
            }

            var isEn = LanguageManager.IsEnglish;
            FileCountText.Text = isEn
                ? $"{_selectedPaths.Count} item(s) selected"
                : $"已选择 {_selectedPaths.Count} 个文件";
            FileListPanel.Visibility = Visibility.Visible;
            ImportBtn.IsEnabled = true;

            var paths = _selectedPaths.ToArray();
            var items = await Task.Run(() => paths.Select(BuildFileListItem).ToList());

            FileListItems.ItemsSource = items;
        }

        private static FileListItem BuildFileListItem(string path)
        {
            bool isDir = Directory.Exists(path) && !File.Exists(path);
            long size = 0;

            if (isDir)
            {
                // EnumerateFiles + FileInfo.Length：GetFiles(...).Sum(f => new FileInfo(f).Length)
                // 会先物化整个路径数组，再对每个文件多做一次 stat。
                try { size = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
                catch (Exception ex) { Console.WriteLine($"[PluginImportDialog] 统计目录大小失败 {path}: {ex.Message}"); }
            }
            else if (File.Exists(path))
            {
                try { size = new FileInfo(path).Length; }
                catch (Exception ex) { Console.WriteLine($"[PluginImportDialog] 读取文件大小失败 {path}: {ex.Message}"); }
            }

            return new FileListItem
            {
                Name = isDir ? $"[文件夹] {Path.GetFileName(path)}" : Path.GetFileName(path),
                SizeText = UEModManager.Core.Utils.FileSizeFormatter.Format(size)
            };
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
                SafeEvent.Run(this, UpdateFileListAsync, null, "刷新插件导入文件列表");
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
                SafeEvent.Run(this, UpdateFileListAsync, null, "刷新插件导入文件列表");
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

        public class FileListItem
        {
            public string Name { get; set; } = "";
            public string SizeText { get; set; } = "";
        }
    }
}
