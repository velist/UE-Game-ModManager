using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.Logging;
using UEModManager.Services;
using UEModManager.ViewModels;

namespace UEModManager.Views
{
    public partial class LaunchCenterWindow : Window
    {
        private readonly LaunchViewModel _vm;

        public LaunchCenterWindow(
            LaunchOrchestrator launcher,
            ProfileService profileService,
            GameConfigService gameConfig,
            PackageRepository packageRepo,
            ConflictAnalyzer conflictAnalyzer,
            ILogger<LaunchCenterWindow> logger)
        {
            InitializeComponent();

            _vm = new LaunchViewModel(launcher, profileService, gameConfig, packageRepo, conflictAnalyzer, logger);
            _vm.PropertyChanged += Vm_PropertyChanged;
        }

        public void Initialize()
        {
            _vm.Initialize();
            UpdateUI();
            _ = _vm.RunConflictPreCheckAsync();
        }

        private void UpdateUI()
        {
            TitleText.Text = _vm.StatusTitle;
            SubtitleText.Text = _vm.StatusSubtitle;
            SidebarProfileText.Text = $"方案: {_vm.ProfileName}";
            SidebarGameInfo.Text = $"{_vm.GameName}";
            LastLaunchText.Text = _vm.LastLaunchInfo;
            ModCountText.Text = _vm.ModCountInfo;
            LastEventText.Text = $"上次事件: {_vm.LastEventInfo}";

            RebuildCheckList();
        }

        private void RebuildCheckList()
        {
            CheckListPanel.Children.Clear();

            for (int i = 0; i < _vm.Steps.Count; i++)
            {
                var step = _vm.Steps[i];
                if (i > 0)
                {
                    // 分隔线
                    CheckListPanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = FindResource("CyberBorderBrush") as Brush,
                        Margin = new Thickness(0, 8, 0, 8)
                    });
                }

                var row = CreateStepRow(step);
                CheckListPanel.Children.Add(row);
            }
        }

        private UIElement CreateStepRow(LaunchStepItem step)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Margin = new Thickness(0, 2, 0, 2);

            // 状态图标
            var icon = CreateStatusIcon(step.Status);
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            // 步骤名称
            var nameText = new TextBlock
            {
                Text = step.DisplayName,
                FontSize = 13,
                Foreground = (Brush)FindResource("Text200Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(nameText, 1);
            grid.Children.Add(nameText);

            // 状态标签
            var statusBadge = CreateStatusBadge(step.Status);
            Grid.SetColumn(statusBadge, 2);
            grid.Children.Add(statusBadge);

            return grid;
        }

        private UIElement CreateStatusIcon(StepItemStatus status)
        {
            var color = status switch
            {
                StepItemStatus.Passed => (Color)ColorConverter.ConvertFromString("#22c55e"),
                StepItemStatus.Warning => (Color)ColorConverter.ConvertFromString("#f97316"),
                StepItemStatus.Failed => (Color)ColorConverter.ConvertFromString("#ef4444"),
                StepItemStatus.Running => (Color)ColorConverter.ConvertFromString("#06b6d4"),
                _ => (Color)ColorConverter.ConvertFromString("#71717a")
            };

            if (status == StepItemStatus.Passed)
            {
                // 勾号图标
                var canvas = new Canvas { Width = 16, Height = 16 };
                var path = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M3 8.5L6.5 12L13 4"),
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                canvas.Children.Add(path);
                return new Viewbox { Width = 16, Height = 16, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
            }

            if (status == StepItemStatus.Warning)
            {
                // 警告三角
                var canvas = new Canvas { Width = 16, Height = 16 };
                var path = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M8 1L15 14H1L8 1Z M8 6V9 M8 11V12"),
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1.5,
                    Fill = Brushes.Transparent,
                    StrokeLineJoin = PenLineJoin.Round
                };
                canvas.Children.Add(path);
                return new Viewbox { Width = 16, Height = 16, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
            }

            if (status == StepItemStatus.Failed)
            {
                // X 图标
                var canvas = new Canvas { Width = 16, Height = 16 };
                var path = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M4 4L12 12M12 4L4 12"),
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                canvas.Children.Add(path);
                return new Viewbox { Width = 16, Height = 16, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
            }

            // Running / Pending: 圆点
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            return new Border { Width = 16, Height = 16, Child = dot, VerticalAlignment = VerticalAlignment.Center };
        }

        private UIElement CreateStatusBadge(StepItemStatus status)
        {
            var (text, bgColor, fgColor) = status switch
            {
                StepItemStatus.Passed => ("通过", "#1a22c55e", "#22c55e"),
                StepItemStatus.Warning => ("警告", "#1af97316", "#f97316"),
                StepItemStatus.Failed => ("失败", "#1aef4444", "#ef4444"),
                StepItemStatus.Running => ("运行中", "#1a06b6d4", "#06b6d4"),
                _ => ("等待", "#1a71717a", "#71717a")
            };

            return new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fgColor))
                }
            };
        }

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(LaunchViewModel.StatusTitle):
                        TitleText.Text = _vm.StatusTitle;
                        break;
                    case nameof(LaunchViewModel.StatusSubtitle):
                        SubtitleText.Text = _vm.StatusSubtitle;
                        break;
                    case nameof(LaunchViewModel.IsLaunching):
                        ProgressArea.Visibility = _vm.IsLaunching ? Visibility.Visible : Visibility.Collapsed;
                        LaunchButton.IsEnabled = !_vm.IsLaunching;
                        LaunchButton.Opacity = _vm.IsLaunching ? 0.5 : 1.0;
                        break;
                    case nameof(LaunchViewModel.CanLaunch):
                        LaunchButton.IsEnabled = _vm.CanLaunch;
                        LaunchButton.Opacity = _vm.CanLaunch ? 1.0 : 0.5;
                        break;
                    case nameof(LaunchViewModel.ConflictCount):
                        RebuildCheckList();
                        break;
                }
            });
        }

        private async void LaunchButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_vm.IsLaunching) return;

            ProgressText.Text = "正在启动...";
            var session = await _vm.LaunchGameAsync();

            if (session != null)
            {
                RebuildCheckList();
                UpdateUI();

                if (session.Success)
                {
                    // 启动成功后短暂显示状态，然后关闭窗口
                    await System.Threading.Tasks.Task.Delay(1500);
                    Close();
                }
            }
        }

        private void ShowHistory_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // 显示启动历史（简单消息框，后续可升级为独立面板）
            var history = _vm.Steps;
            var launcher = (LaunchOrchestrator)typeof(LaunchViewModel)
                .GetField("_launcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(_vm)!;

            if (launcher.SessionHistory.Count == 0)
            {
                MessageBox.Show(this, "暂无启动记录。", "启动历史", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var lines = new System.Text.StringBuilder();
            lines.AppendLine("最近启动记录：\n");
            foreach (var s in launcher.SessionHistory.Take(10))
            {
                var status = s.Success ? "成功" : "失败";
                lines.AppendLine($"  [{s.LaunchedAt:MM/dd HH:mm}] {s.GameName}/{s.ProfileName} — {status}");
                if (!s.Success && !string.IsNullOrEmpty(s.FailureReason))
                    lines.AppendLine($"    原因: {s.FailureReason}");
            }

            MessageBox.Show(this, lines.ToString(), "启动历史", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            Close();
        }

        private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
    }
}
