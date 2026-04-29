using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class DeployPreviewDialog : Window
    {
        private readonly DeploymentService _deployService;
        private DeploymentPlan? _plan;

        /// <summary>部署是否成功执行。</summary>
        public bool DeploySucceeded { get; private set; }

        public DeployPreviewDialog(DeploymentService deployService)
        {
            InitializeComponent();
            _deployService = deployService;
        }

        /// <summary>设置部署计划并刷新 UI。</summary>
        public void SetPlan(DeploymentPlan plan)
        {
            _plan = plan;
            RefreshUI();
        }

        private void RefreshUI()
        {
            if (_plan == null) return;

            // 统计卡片
            AddCountText.Text = $"{_plan.AddCount} 文件";
            ReplaceCountText.Text = $"{_plan.ReplaceCount} 文件";
            RemoveCountText.Text = $"{_plan.RemoveCount} 文件";

            // 操作列表
            OperationListPanel.Children.Clear();
            foreach (var op in _plan.Operations.OrderBy(o => o.Type))
            {
                AddOperationRow(op);
            }

            DeployButton.IsEnabled = _plan.HasChanges;
        }

        private void AddOperationRow(DeploymentOperation op)
        {
            var (labelText, labelColor) = op.Type switch
            {
                DeploymentOperationType.Add => ("新增", Color.FromRgb(0x22, 0xc5, 0x5e)),
                DeploymentOperationType.Replace => ("修改", Color.FromRgb(0xf5, 0x9e, 0x0b)),
                DeploymentOperationType.Remove => ("移除", Color.FromRgb(0xef, 0x44, 0x44)),
                _ => ("新增", Color.FromRgb(0x22, 0xc5, 0x5e))
            };

            var row = new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 0.5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 操作标签
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, labelColor.R, labelColor.G, labelColor.B)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = labelText,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(labelColor)
                }
            };
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            // 文件路径
            var pathText = new TextBlock
            {
                Text = op.RelativeTargetPath,
                FontSize = 12,
                Foreground = (Brush)FindResource("Text300Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = op.RelativeTargetPath
            };
            Grid.SetColumn(pathText, 1);
            grid.Children.Add(pathText);

            row.Child = grid;
            OperationListPanel.Children.Add(row);
        }

        // ─── 事件处理 ───

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void DeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (_plan == null) return;

            DeployButton.IsEnabled = false;
            DeployProgressOverlay.Visibility = Visibility.Visible;
            DeployProgress.IsIndeterminate = true;

            _deployService.ProgressChanged += OnDeployProgress;

            try
            {
                var transaction = await _deployService.ExecuteAsync(_plan);

                if (transaction.Status == DeploymentStatus.Committed)
                {
                    DeploySucceeded = true;
                    DeployStatusText.Text = "MOD 已应用";
                    DeployDetailText.Text = $"已处理 {transaction.CompletedOperations}/{transaction.TotalOperations} 个操作";
                    await System.Threading.Tasks.Task.Delay(800);
                    DialogResult = true;
                }
                else
                {
                    DeployStatusText.Text = "应用失败";
                    DeployDetailText.Text = "已自动回滚到之前的状态";
                    DeployStatusText.Foreground = (Brush)FindResource("StatusRedBrush");
                }
            }
            catch (Exception ex)
            {
                DeployStatusText.Text = "应用出错";
                DeployDetailText.Text = ex.Message;
                DeployStatusText.Foreground = (Brush)FindResource("StatusRedBrush");
            }
            finally
            {
                _deployService.ProgressChanged -= OnDeployProgress;
                DeployButton.IsEnabled = true;
                DeployProgress.IsIndeterminate = false;
            }
        }

        private void OnDeployProgress(DeploymentTransaction tx)
        {
            Dispatcher.Invoke(() =>
            {
                DeployProgress.IsIndeterminate = false;
                DeployProgress.Value = tx.Progress;
                DeployStatusText.Text = $"正在应用 MOD... ({tx.CompletedOperations}/{tx.TotalOperations})";
            });
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();
    }
}
