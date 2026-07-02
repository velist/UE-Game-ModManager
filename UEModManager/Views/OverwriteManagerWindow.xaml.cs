using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class OverwriteManagerWindow : Window
    {
        private readonly OverwriteStore _overwriteStore;
        private readonly PackageRepository _packageRepo;

        public OverwriteManagerWindow(OverwriteStore overwriteStore, PackageRepository packageRepo)
        {
            InitializeComponent();
            _overwriteStore = overwriteStore;
            _packageRepo = packageRepo;
            Loaded += (_, _) => RefreshUI();
        }

        private void RefreshUI()
        {
            var all = _overwriteStore.GetAll();
            var active = all.Count(a => a.Status == GeneratedArtifactStatus.Active);
            var stale = all.Count(a => a.Status == GeneratedArtifactStatus.Stale);

            ActiveCountText.Text = active.ToString();
            StaleCountText.Text = stale.ToString();
            TotalSizeText.Text = UEModManager.Core.Utils.FileSizeFormatter.Format(_overwriteStore.TotalSize);

            var staleSize = _overwriteStore.StaleSize;
            CleanupButtonText.Text = staleSize > 0
                ? $"清理可删除文件 ({UEModManager.Core.Utils.FileSizeFormatter.Format(staleSize)})"
                : "清理可删除文件";

            ArtifactListPanel.Children.Clear();
            EmptyState.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var artifact in all.OrderByDescending(a => a.CreatedAt))
            {
                AddArtifactRow(artifact);
            }
        }

        private void AddArtifactRow(GeneratedArtifact artifact)
        {
            var border = new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 0.5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            // 名称 + 来源
            var nameStack = new StackPanel();
            nameStack.Children.Add(new TextBlock
            {
                Text = artifact.DisplayName,
                FontSize = 13,
                Foreground = (Brush)FindResource("Text200Brush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            nameStack.Children.Add(new TextBlock
            {
                Text = artifact.SourceSummary,
                FontSize = 10,
                Foreground = (Brush)FindResource("Text500Brush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(nameStack, 0);
            grid.Children.Add(nameStack);

            // 类型标签
            var typeLabel = artifact.Type switch
            {
                GeneratedArtifactType.DeploymentSnapshot => "快照",
                GeneratedArtifactType.MergedConfig => "合并",
                GeneratedArtifactType.ToolOutput => "工具",
                GeneratedArtifactType.Cache => "缓存",
                GeneratedArtifactType.UserFix => "修复",
                _ => "其他"
            };
            var typeColor = artifact.Type switch
            {
                GeneratedArtifactType.DeploymentSnapshot => Color.FromRgb(0x06, 0xb6, 0xd4),
                GeneratedArtifactType.MergedConfig => Color.FromRgb(0xf5, 0x9e, 0x0b),
                GeneratedArtifactType.UserFix => Color.FromRgb(0x22, 0xc5, 0x5e),
                GeneratedArtifactType.ToolOutput => Color.FromRgb(0xa8, 0x55, 0xf7),
                _ => Color.FromRgb(0x71, 0x71, 0x7a)
            };
            var typeBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, typeColor.R, typeColor.G, typeColor.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = typeLabel,
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(typeColor)
                }
            };
            Grid.SetColumn(typeBadge, 1);
            grid.Children.Add(typeBadge);

            // 大小
            var size = new TextBlock
            {
                Text = artifact.FormattedSize,
                FontSize = 11,
                Foreground = (Brush)FindResource("Text400Brush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            // 状态
            var (statusLabel, statusColor) = artifact.Status switch
            {
                GeneratedArtifactStatus.Active => ("使用中", Color.FromRgb(0x22, 0xc5, 0x5e)),
                GeneratedArtifactStatus.Stale => ("可清理", Color.FromRgb(0xf5, 0x9e, 0x0b)),
                GeneratedArtifactStatus.Promoted => ("已转为MOD", Color.FromRgb(0x06, 0xb6, 0xd4)),
                _ => ("未知", Color.FromRgb(0x71, 0x71, 0x7a))
            };
            var statusBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, statusColor.R, statusColor.G, statusColor.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = statusLabel,
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(statusColor)
                }
            };
            Grid.SetColumn(statusBadge, 3);
            grid.Children.Add(statusBadge);

            // 操作按钮
            var actionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (artifact.Status == GeneratedArtifactStatus.Active)
            {
                var promoteBtn = new Button
                {
                    Content = "↑",
                    ToolTip = "转为正式 MOD",
                    Style = (Style)FindResource("CyberGhostButton"),
                    Width = 24, Height = 24,
                    FontSize = 12,
                    Tag = artifact.Id
                };
                promoteBtn.Click += PromoteArtifact_Click;
                actionPanel.Children.Add(promoteBtn);
            }

            var deleteBtn = new Button
            {
                Content = "✕",
                ToolTip = "删除",
                Style = (Style)FindResource("CyberGhostButton"),
                Width = 24, Height = 24,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)),
                Tag = artifact.Id
            };
            deleteBtn.Click += DeleteArtifact_Click;
            actionPanel.Children.Add(deleteBtn);

            Grid.SetColumn(actionPanel, 4);
            grid.Children.Add(actionPanel);

            border.Child = grid;
            ArtifactListPanel.Children.Add(border);
        }

        // ─── 事件处理 ───

        private async void PromoteArtifact_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Guid id }) return;
            var artifact = _overwriteStore.GetAll().FirstOrDefault(a => a.Id == id);
            if (artifact == null) return;

            var result = CyberMessageBox.Show(this,
                $"将「{artifact.DisplayName}」转为正式 MOD？\n转换后会保存到 MOD 文件库，并可加入任意方案。",
                "转为正式 MOD", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var pkg = await _overwriteStore.PromoteToPackageAsync(id, artifact.DisplayName);
                if (pkg != null)
                    CyberMessageBox.Show(this, $"已转为正式 MOD「{pkg.DisplayName}」", "成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    CyberMessageBox.Show(this, "转换失败，请检查文件是否完整", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshUI();
            }
        }

        private async void DeleteArtifact_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Guid id }) return;
            await _overwriteStore.DeleteAsync(id);
            RefreshUI();
        }

        private async void CleanupStale_Click(object sender, RoutedEventArgs e)
        {
            var staleSize = _overwriteStore.StaleSize;
            if (staleSize == 0)
            {
                CyberMessageBox.Show(this, "没有可清理的临时文件", "清理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = CyberMessageBox.Show(this,
                $"将清理所有不再使用的临时文件，释放 {UEModManager.Core.Utils.FileSizeFormatter.Format(staleSize)}。\n此操作不可撤销，确认继续？",
                "清理可删除文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var count = await _overwriteStore.CleanupStaleAsync();
                CyberMessageBox.Show(this, $"已清理 {count} 个临时文件", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshUI();
            }
        }

        private async void AddUserFix_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择自定义修复文件",
                Filter = "所有文件|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var file in dlg.FileNames)
            {
                var name = System.IO.Path.GetFileName(file);
                await _overwriteStore.RegisterAsync(
                    file,
                    GeneratedArtifactType.UserFix,
                    name,
                    sourceDescription: "用户手动添加");
            }
            RefreshUI();
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();
    }
}
