using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Services;
using UEModManager.Services.Migration;

namespace UEModManager.Views
{
    public partial class MigrationWizardWindow : Window
    {
        private readonly DataMigrationService _migrationService;
        private readonly string _gameName;
        private bool _isMigrating;

        // 任务 UI 引用
        private readonly Dictionary<string, (TextBlock statusIcon, TextBlock detailText)> _taskRows = [];

        private static readonly string[] TaskNames =
        [
            "扫描旧版 MOD 数据",
            "创建默认方案",
            "迁移文件到包仓库",
            "生成包清单 (Manifest)",
            "验证迁移完整性"
        ];

        public MigrationWizardWindow(DataMigrationService migrationService, string gameName)
        {
            InitializeComponent();
            _migrationService = migrationService;
            _gameName = gameName;
            Loaded += (_, _) => BuildTaskList();
        }

        private void BuildTaskList()
        {
            TaskListPanel.Children.Clear();
            _taskRows.Clear();

            for (int i = 0; i < TaskNames.Length; i++)
            {
                var name = TaskNames[i];
                var border = new Border { Padding = new Thickness(14, 8, 14, 8) };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 状态图标
                var statusIcon = new TextBlock
                {
                    Text = "○",
                    FontSize = 13,
                    Foreground = (Brush)FindResource("Text600Brush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(statusIcon, 0);
                grid.Children.Add(statusIcon);

                // 任务名称
                var nameText = new TextBlock
                {
                    Text = name,
                    FontSize = 13,
                    Foreground = (Brush)FindResource("Text300Brush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameText, 1);
                grid.Children.Add(nameText);

                // 详情文本
                var detailText = new TextBlock
                {
                    Text = "",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Text500Brush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(detailText, 2);
                grid.Children.Add(detailText);

                border.Child = grid;
                TaskListPanel.Children.Add(border);
                _taskRows[name] = (statusIcon, detailText);
            }
        }

        private void UpdateTaskStatus(int stepIndex, string status, string detail, bool isActive)
        {
            Dispatcher.Invoke(() =>
            {
                if (stepIndex < 0 || stepIndex >= TaskNames.Length) return;
                var name = TaskNames[stepIndex];
                if (!_taskRows.TryGetValue(name, out var row)) return;

                if (status == "done")
                {
                    row.statusIcon.Text = "✓";
                    row.statusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                }
                else if (isActive)
                {
                    row.statusIcon.Text = "◉";
                    row.statusIcon.Foreground = (Brush)FindResource("PrimaryBrush");
                }

                row.detailText.Text = detail;
                if (isActive)
                    row.detailText.Foreground = (Brush)FindResource("PrimaryBrush");
            });
        }

        // ─── 事件处理 ───

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isMigrating) return;
            _isMigrating = true;

            StartButton.IsEnabled = false;
            SkipButton.IsEnabled = false;

            _migrationService.ProgressChanged += OnMigrationProgress;

            try
            {
                var result = await _migrationService.MigrateAsync(_gameName);

                if (result.Success)
                {
                    MigrationProgress.Value = 100;
                    ProgressPercent.Text = "100%";
                    StepIndicator.Text = "完成";

                    await System.Threading.Tasks.Task.Delay(600);
                    DialogResult = true;
                }
                else
                {
                    CyberMessageBox.Show(this,
                        $"迁移失败:\n{string.Join("\n", result.Warnings)}",
                        "迁移失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"迁移出错: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _migrationService.ProgressChanged -= OnMigrationProgress;
                _isMigrating = false;
                StartButton.IsEnabled = true;
                SkipButton.IsEnabled = true;
            }
        }

        private void OnMigrationProgress(MigrationProgress progress)
        {
            Dispatcher.Invoke(() =>
            {
                var pct = (int)progress.Percentage;
                MigrationProgress.Value = pct;
                ProgressPercent.Text = $"{pct}%";
                StepIndicator.Text = $"步骤 {progress.CurrentStep}/{progress.TotalSteps}";

                // 更新已完成的步骤
                for (int i = 0; i < progress.CurrentStep - 1 && i < TaskNames.Length; i++)
                {
                    UpdateTaskStatus(i, "done", "", false);
                }

                // 当前步骤
                if (progress.CurrentStep > 0 && progress.CurrentStep <= TaskNames.Length)
                {
                    UpdateTaskStatus(progress.CurrentStep - 1, "active", progress.Detail ?? "", true);
                }
            });
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            if (_isMigrating)
            {
                var result = CyberMessageBox.Show(this,
                    "迁移正在进行中，关闭窗口可能导致数据不完整。\n确定要关闭吗？",
                    "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }
            Close();
        }
    }
}
