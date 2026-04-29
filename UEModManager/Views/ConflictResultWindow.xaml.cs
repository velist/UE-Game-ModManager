using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class ConflictResultWindow : Window
    {
        private readonly ConflictAnalyzer? _conflictAnalyzer;
        private List<ConflictRecord> _conflicts = [];

        /// <summary>v1.8 兼容：接受旧版 ModConflictResult。</summary>
        public ConflictResultWindow(ModConflictResult result)
        {
            InitializeComponent();
            // 旧版数据转换为简单展示
            ConflictCountText.Text = $"{result.ConflictAssets} 个冲突";
            // 旧版不支持胜者/败者链，仅显示摘要
        }

        /// <summary>v2.0：接受 ConflictAnalyzer + 分析结果。</summary>
        public ConflictResultWindow(ConflictAnalyzer analyzer, List<ConflictRecord> conflicts)
        {
            InitializeComponent();
            _conflictAnalyzer = analyzer;
            _conflicts = conflicts;
            RefreshUI();
        }

        private void RefreshUI()
        {
            ConflictCountText.Text = $"{_conflicts.Count} 个冲突";
            ConflictListPanel.Children.Clear();

            if (_conflicts.Count == 0)
            {
                ConflictListPanel.Children.Add(new TextBlock
                {
                    Text = "未检测到冲突",
                    FontSize = 14,
                    Foreground = (Brush)FindResource("Text500Brush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                });
                ConflictCountBadge.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x22, 0xc5, 0x5e));
                ConflictCountText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                ConflictCountText.Text = "无冲突";
                return;
            }

            // 按目标路径分组
            foreach (var conflict in _conflicts)
            {
                AddConflictGroup(conflict);
            }
        }

        private void AddConflictGroup(ConflictRecord record)
        {
            var group = new Border
            {
                Background = (Brush)FindResource("SurfaceBrush"),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(0)
            };

            var stack = new StackPanel();

            // 冲突路径标题
            var typeIcon = record.Type switch
            {
                ConflictType.ConfigKey => "\xE90F",
                ConflictType.LoadOrder => "\xE8CB",
                _ => "\xE8A5"
            };
            var typeColor = record.Severity switch
            {
                ConflictSeverity.Error => Color.FromRgb(0xef, 0x44, 0x44),
                ConflictSeverity.Warning => Color.FromRgb(0xf5, 0x9e, 0x0b),
                _ => Color.FromRgb(0x06, 0xb6, 0xd4)
            };

            var headerBorder = new Border
            {
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock
            {
                Text = typeIcon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = new SolidColorBrush(typeColor),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = record.TargetPath,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("Text200Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = record.TargetPath
            });
            headerBorder.Child = headerPanel;
            stack.Children.Add(headerBorder);

            // 胜者行
            stack.Children.Add(CreateParticipantRow(
                isWinner: true,
                displayName: record.WinnerDisplayName,
                detail: record.Resolution == ResolutionMethod.UserOverride
                    ? "用户指定"
                    : $"优先级 #1"
            ));

            // 败者行
            foreach (var loser in record.Losers)
            {
                stack.Children.Add(CreateParticipantRow(
                    isWinner: false,
                    displayName: loser.DisplayName,
                    detail: $"优先级 #{loser.Priority}"
                ));
            }

            group.Child = stack;
            ConflictListPanel.Children.Add(group);
        }

        private static Border CreateParticipantRow(bool isWinner, string displayName, string detail)
        {
            var color = isWinner
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0xef, 0x44, 0x44);
            var bgColor = isWinner
                ? Color.FromArgb(0x18, 0x22, 0xc5, 0x5e)
                : Color.FromArgb(0x18, 0xef, 0x44, 0x44);
            var prefix = isWinner ? "✓  胜者" : "✕  败者";

            var border = new Border
            {
                Background = new SolidColorBrush(bgColor),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(8, 4, 8, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal };
            leftPanel.Children.Add(new TextBlock
            {
                Text = prefix,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            leftPanel.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xe4, 0xe4, 0xe7)),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            var detailText = new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7a)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(detailText, 1);
            grid.Children.Add(detailText);

            border.Child = grid;
            return border;
        }

        // ─── 事件处理 ───

        private async void AutoResolve_Click(object sender, RoutedEventArgs e)
        {
            // 按优先级自动解决（已经是默认行为）
            if (_conflictAnalyzer != null)
            {
                var result = await _conflictAnalyzer.AnalyzeAsync();
                _conflicts = result.Conflicts;
                RefreshUI();
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();
    }
}
