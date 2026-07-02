using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class ImportConfirmDialog : Window
    {
        private readonly PackageImportService _importService;
        private readonly PackageRepository _packageRepo;
        private readonly ProfileService _profileService;
        private readonly ConflictAnalyzer _conflictAnalyzer;
        private readonly GameConfigService _gameConfig;
        private readonly List<FileEntry> _fileEntries = [];

        /// <summary>用户选择导入的文件路径。</summary>
        public List<string> FilePaths { get; set; } = [];

        /// <summary>导入完成后的结果列表。</summary>
        public List<PackageImportResult>? ImportResults { get; private set; }

        public ImportConfirmDialog(
            PackageImportService importService,
            PackageRepository packageRepo,
            ProfileService profileService,
            ConflictAnalyzer conflictAnalyzer,
            GameConfigService gameConfig)
        {
            InitializeComponent();
            _importService = importService;
            _packageRepo = packageRepo;
            _profileService = profileService;
            _conflictAnalyzer = conflictAnalyzer;
            _gameConfig = gameConfig;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadProfiles();
            AnalyzeFiles();
            UpdateDeployPaths();
        }

        private void LoadProfiles()
        {
            var profiles = _profileService.GetProfiles();
            ProfileSelector.Items.Clear();
            foreach (var p in profiles)
            {
                ProfileSelector.Items.Add(new ComboBoxItem
                {
                    Content = p.Name,
                    Tag = p.Id,
                    IsSelected = p.IsActive
                });
            }
            if (ProfileSelector.SelectedItem == null && ProfileSelector.Items.Count > 0)
                ProfileSelector.SelectedIndex = 0;
        }

        private void AnalyzeFiles()
        {
            _fileEntries.Clear();
            FileListPanel.Children.Clear();

            if (FilePaths.Count == 0) return;

            // 判断是否为压缩包
            if (FilePaths.Count == 1)
            {
                var ext = Path.GetExtension(FilePaths[0]).ToLowerInvariant();
                if (ext is ".zip" or ".rar" or ".7z")
                {
                    ArchiveInfoCard.Visibility = Visibility.Visible;
                    ArchiveFileName.Text = Path.GetFileName(FilePaths[0]);
                    var fi = new FileInfo(FilePaths[0]);
                    ArchiveFileInfo.Text = $"压缩包 · {UEModManager.Core.Utils.FileSizeFormatter.Format(fi.Length)}";
                }
                else
                {
                    ArchiveInfoCard.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                ArchiveInfoCard.Visibility = Visibility.Collapsed;
            }

            // 分析每个文件
            foreach (var path in FilePaths)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var kind = DetectKind(ext);
                var fi = new FileInfo(path);
                var entry = new FileEntry
                {
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Kind = kind,
                    Size = fi.Exists ? fi.Length : 0,
                    ShouldImport = true
                };
                _fileEntries.Add(entry);
                AddFileRow(entry);
            }

            UpdateTargetPathPanel();
            CheckConflicts();
        }

        private void UpdateTargetPathPanel()
        {
            var hasNonMod = _fileEntries.Any(f => f.Kind != PackageKind.Mod && f.ShouldImport);
            TargetPathPanel.Visibility = hasNonMod ? Visibility.Visible : Visibility.Collapsed;
            if (!hasNonMod || !string.IsNullOrWhiteSpace(TargetPathTextBox.Text))
                return;

            TargetPathTextBox.Text = _fileEntries.Any(f => f.Kind == PackageKind.Plugin)
                ? "Binaries/Win64"
                : "Saved/Config/WindowsNoEditor";
        }

        private void AddFileRow(FileEntry entry)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

            var border = new Border
            {
                Background = (Brush)FindResource("SurfaceBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Child = grid
            };

            // 文件名
            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            namePanel.Children.Add(new TextBlock
            {
                Text = "\xE8A5",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)FindResource("Text400Brush"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            namePanel.Children.Add(new TextBlock
            {
                Text = entry.FileName,
                FontSize = 13,
                Foreground = (Brush)FindResource("Text200Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(namePanel, 0);
            grid.Children.Add(namePanel);

            // 类型标签
            var kindColor = entry.Kind switch
            {
                PackageKind.Plugin => Color.FromRgb(0xa8, 0x55, 0xf7),
                PackageKind.Config => Color.FromRgb(0xf5, 0x9e, 0x0b),
                _ => Color.FromRgb(0x06, 0xb6, 0xd4)
            };
            var kindLabel = entry.Kind switch
            {
                PackageKind.Plugin => "插件",
                PackageKind.Config => "配置",
                _ => "MOD"
            };
            var kindBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, kindColor.R, kindColor.G, kindColor.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = kindLabel,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(kindColor)
                }
            };
            Grid.SetColumn(kindBadge, 1);
            grid.Children.Add(kindBadge);

            // 大小
            var sizeText = new TextBlock
            {
                Text = UEModManager.Core.Utils.FileSizeFormatter.Format(entry.Size),
                FontSize = 12,
                Foreground = (Brush)FindResource("Text400Brush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(sizeText, 2);
            grid.Children.Add(sizeText);

            // 勾选框
            var checkbox = new CheckBox
            {
                IsChecked = entry.ShouldImport,
                Style = (Style)FindResource("CyberCheckBox"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = entry
            };
            checkbox.Checked += (_, _) => { entry.ShouldImport = true; UpdateTargetPathPanel(); UpdateDeployPaths(); };
            checkbox.Unchecked += (_, _) => { entry.ShouldImport = false; UpdateTargetPathPanel(); UpdateDeployPaths(); };
            Grid.SetColumn(checkbox, 3);
            grid.Children.Add(checkbox);

            FileListPanel.Children.Add(border);
        }

        private void CheckConflicts()
        {
            // 简单检查：已有同名包
            var existingKeys = new HashSet<string>(
                _packageRepo.GetAllPackages().Select(p => p.PackageKey),
                StringComparer.OrdinalIgnoreCase);

            var conflicts = _fileEntries
                .Where(f => existingKeys.Contains(Path.GetFileNameWithoutExtension(f.FileName)))
                .ToList();

            if (conflicts.Count > 0)
            {
                ConflictWarningPanel.Visibility = Visibility.Visible;
                ConflictWarningText.Text = string.Join("\n",
                    conflicts.Select(c => $"{c.FileName} 与已有 MOD 存在文件路径冲突"));
            }
            else
            {
                ConflictWarningPanel.Visibility = Visibility.Collapsed;
            }
        }

        // ─── 事件处理 ───

        private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedEntries = _fileEntries.Where(f => f.ShouldImport).ToList();
            var toImport = selectedEntries.Select(f => f.FilePath).ToArray();
            if (toImport.Length == 0) return;

            var targetRootPath = NormalizeTargetPath(TargetPathTextBox.Text);
            if (selectedEntries.Any(f => f.Kind != PackageKind.Mod) && string.IsNullOrWhiteSpace(targetRootPath))
            {
                MessageBox.Show(this, "插件/配置文件需要指定安装目录。", "缺少目标目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                TargetPathTextBox.Focus();
                return;
            }

            ConfirmButton.IsEnabled = false;
            try
            {
                ImportResults = await _importService.ImportAsync(toImport, targetRootPath);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ConfirmButton.IsEnabled = true;
            }
            Close();
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();

        // ─── 部署路径检测 ───

        private void UpdateDeployPaths()
        {
            var modPath = _gameConfig.CurrentModPath;
            ModPathText.Text = string.IsNullOrEmpty(modPath) ? "未配置" : modPath;

            var gameName = _gameConfig.CurrentGameName;
            var nonModTargetPath = NormalizeTargetPath(TargetPathTextBox.Text);
            PluginPathText.Text = string.IsNullOrEmpty(nonModTargetPath) ? "未配置" : Path.Combine(_gameConfig.CurrentGamePath ?? "游戏根目录", nonModTargetPath);
            ConfigPathText.Text = string.IsNullOrEmpty(nonModTargetPath) ? "未配置" : Path.Combine(_gameConfig.CurrentGamePath ?? "游戏根目录", nonModTargetPath);

            bool hasMod = _fileEntries.Any(f => f.Kind == PackageKind.Mod && f.ShouldImport);
            bool hasPlugin = _fileEntries.Any(f => f.Kind == PackageKind.Plugin && f.ShouldImport);
            bool hasConfig = _fileEntries.Any(f => f.Kind == PackageKind.Config && f.ShouldImport);

            ModPathRow.Visibility = hasMod ? Visibility.Visible : Visibility.Collapsed;
            PluginPathRow.Visibility = hasPlugin ? Visibility.Visible : Visibility.Collapsed;
            ConfigPathRow.Visibility = hasConfig ? Visibility.Visible : Visibility.Collapsed;

            var warnings = new List<string>();
            if (hasMod && string.IsNullOrEmpty(modPath))
                warnings.Add("MOD");
            if ((hasPlugin || hasConfig) && string.IsNullOrEmpty(nonModTargetPath))
                warnings.Add("插件/配置");

            if (warnings.Count > 0)
            {
                PathWarningText.Text = $"{string.Join("、", warnings)}路径未配置，部署可能失败";
                PathWarningBorder.Visibility = Visibility.Visible;
            }
            else
            {
                PathWarningBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void EditPath_Click(object sender, MouseButtonEventArgs e)
        {
            OpenGamePathDialog();
        }

        private void ConfigurePath_Click(object sender, RoutedEventArgs e)
        {
            OpenGamePathDialog();
        }

        private void TargetPathPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetPathPresetBox.SelectedItem is not ComboBoxItem item) return;
            var path = item.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(path)) return;

            TargetPathTextBox.Text = path;
        }

        private void TargetPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDeployPaths();
        }

        private void OpenGamePathDialog()
        {
            var gameName = _gameConfig.CurrentGameName;
            if (string.IsNullOrEmpty(gameName)) return;

            var dialog = new GamePathDialog(gameName) { Owner = this };
            dialog.ShowDialog();
            UpdateDeployPaths();
        }

        // ─── 工具方法 ───

        private static string NormalizeTargetPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            return path.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        private static PackageKind DetectKind(string ext) => ext switch
        {
            ".pak" or ".utoc" or ".ucas" => PackageKind.Mod,
            ".dll" => PackageKind.Plugin,
            ".ini" or ".json" or ".cfg" or ".yaml" or ".xml" => PackageKind.Config,
            _ => PackageKind.Mod // 压缩包默认为 MOD
        };

        private class FileEntry
        {
            public string FilePath { get; init; } = "";
            public string FileName { get; init; } = "";
            public PackageKind Kind { get; init; }
            public long Size { get; init; }
            public bool ShouldImport { get; set; } = true;
        }
    }
}
