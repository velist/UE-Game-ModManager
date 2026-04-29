using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Config;
using UEModManager.ViewModels;

namespace UEModManager.Views
{
    public partial class ConfigManagerWindow : Window
    {
        private readonly ConfigManagerViewModel _vm;

        public ConfigManagerWindow(
            ConfigMergeEngine mergeEngine,
            PackageRepository packageRepo,
            ObjectStore objectStore,
            ProfileService profileService,
            GameConfigService gameConfig,
            ILogger<ConfigManagerWindow> logger)
        {
            InitializeComponent();

            _vm = new ConfigManagerViewModel(mergeEngine, packageRepo, objectStore, profileService, gameConfig, logger);
            _vm.PropertyChanged += Vm_PropertyChanged;
            _vm.Entries.CollectionChanged += (_, __) => RebuildEntryTable();
        }

        public void Initialize()
        {
            _vm.Initialize();
            RebuildFileList();
            UpdateHeader();

            FileCountText.Text = $"{_vm.ConfigFiles.Count} 个文件";
        }

        // ─── 左侧文件列表 ───

        private void RebuildFileList()
        {
            FileListPanel.Children.Clear();

            foreach (var file in _vm.ConfigFiles)
            {
                var isSelected = file == _vm.SelectedFile;
                var item = CreateFileItem(file, isSelected);
                FileListPanel.Children.Add(item);
            }
        }

        private UIElement CreateFileItem(ConfigFileItem file, bool isSelected)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0x1a, 0x06, 0xb6, 0xd4))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = isSelected
                    ? (Brush)FindResource("PrimaryBrush")
                    : Brushes.Transparent,
                Tag = file
            };

            var stack = new StackPanel();

            // 文件名
            var nameText = new TextBlock
            {
                Text = file.FileName,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = isSelected
                    ? (Brush)FindResource("PrimaryLightBrush")
                    : (Brush)FindResource("Text200Brush")
            };
            stack.Children.Add(nameText);

            // 来源数
            if (file.SourceCount > 1)
            {
                var sourceText = new TextBlock
                {
                    Text = $"{file.SourceCount} 个包修改",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("Text500Brush"),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(sourceText);
            }

            border.Child = stack;
            border.MouseLeftButtonDown += async (s, e) =>
            {
                e.Handled = true;
                await _vm.SelectFileAsync(file);
                RebuildFileList();
                UpdateHeader();
            };

            // 悬停效果
            border.MouseEnter += (s, e) =>
            {
                if (file != _vm.SelectedFile)
                    border.Background = (Brush)FindResource("SurfaceHoverBrush");
            };
            border.MouseLeave += (s, e) =>
            {
                if (file != _vm.SelectedFile)
                    border.Background = Brushes.Transparent;
            };

            return border;
        }

        // ─── 右侧头部 ───

        private void UpdateHeader()
        {
            if (_vm.SelectedFile != null)
            {
                FileNameText.Text = _vm.SelectedFileName;
                FilePathText.Text = _vm.SelectedFilePath;

                FormatBadge.Visibility = Visibility.Visible;
                FormatText.Text = _vm.SelectedFile.Format.ToString().ToUpperInvariant();

                StatsText.Text = $"{_vm.TotalKeys} 个键" +
                    (_vm.ConflictKeys > 0 ? $"，{_vm.ConflictKeys} 个冲突" : "");
            }
            else
            {
                FileNameText.Text = "未选择文件";
                FilePathText.Text = "";
                FormatBadge.Visibility = Visibility.Collapsed;
                StatsText.Text = "";
            }
        }

        // ─── 键值表格 ───

        private void RebuildEntryTable()
        {
            Dispatcher.Invoke(() =>
            {
                EntryTablePanel.Children.Clear();

                for (int i = 0; i < _vm.Entries.Count; i++)
                {
                    var entry = _vm.Entries[i];
                    var row = CreateEntryRow(entry, i % 2 == 1);
                    EntryTablePanel.Children.Add(row);
                }

                UpdateHeader();
            });
        }

        private UIElement CreateEntryRow(ConfigEntryRow entry, bool isOddRow)
        {
            var border = new Border
            {
                Padding = new Thickness(24, 8, 24, 8),
                Background = isOddRow
                    ? new SolidColorBrush(Color.FromArgb(0x08, 0xff, 0xff, 0xff))
                    : Brushes.Transparent
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

            // Section
            var sectionText = new TextBlock
            {
                Text = entry.Section,
                FontSize = 12,
                Foreground = (Brush)FindResource("Text400Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(sectionText, 0);
            grid.Children.Add(sectionText);

            // Key
            var keyText = new TextBlock
            {
                Text = entry.Key,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("Text200Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(keyText, 1);
            grid.Children.Add(keyText);

            // Value
            var valueText = new TextBlock
            {
                Text = entry.Value,
                FontSize = 12,
                Foreground = entry.IsConflictResolution
                    ? (Brush)FindResource("StatusOrangeBrush")
                    : (Brush)FindResource("Text300Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            // Source badge
            var sourceBadge = CreateSourceBadge(entry.SourceDisplayName, entry.IsConflictResolution);
            Grid.SetColumn(sourceBadge, 3);
            grid.Children.Add(sourceBadge);

            border.Child = grid;
            return border;
        }

        private UIElement CreateSourceBadge(string sourceName, bool isConflict)
        {
            var bgColor = isConflict ? "#1af97316" : "#1a06b6d4";
            var fgColor = isConflict ? "#f97316" : "#06b6d4";

            return new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = sourceName,
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fgColor)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 120
                }
            };
        }

        // ─── 事件处理 ───

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ConfigManagerViewModel.IsLoading):
                        LoadingOverlay.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    case nameof(ConfigManagerViewModel.TotalKeys):
                    case nameof(ConfigManagerViewModel.ConflictKeys):
                        UpdateHeader();
                        break;
                }
            });
        }

        private async void PreviewMerge_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_vm.SelectedFile != null)
                await _vm.LoadFileEntriesAsync(_vm.SelectedFile);
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();
        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e) => WindowState = WindowState.Minimized;
    }
}
