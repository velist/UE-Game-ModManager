using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Config;

namespace UEModManager.Views
{
    public partial class ManagementCenterWindow : Window
    {
        private readonly PackageRepository _packageRepo;
        private readonly ProfileService _profileService;
        private readonly DeploymentService _deploymentService;
        private readonly ConfigMergeEngine _configMergeEngine;
        private readonly OverwriteStore _overwriteStore;
        private readonly DiagnosticExportService _diagnosticExport;

        private int _activeTab;

        public ManagementCenterWindow(
            PackageRepository packageRepo,
            ProfileService profileService,
            DeploymentService deploymentService,
            ConfigMergeEngine configMergeEngine,
            OverwriteStore overwriteStore,
            DiagnosticExportService diagnosticExport)
        {
            InitializeComponent();
            _packageRepo = packageRepo;
            _profileService = profileService;
            _deploymentService = deploymentService;
            _configMergeEngine = configMergeEngine;
            _overwriteStore = overwriteStore;
            _diagnosticExport = diagnosticExport;

            Loaded += OnLoaded;
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            Close();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            SwitchTab(0);
            await LoadModLibAsync();
        }

        // ─── Tab 切换 ───

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var idx))
                SwitchTab(idx);
        }

        private void SwitchTab(int index)
        {
            _activeTab = index;

            PanelModLib.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelDeployHistory.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelConfigMerge.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelGenFiles.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

            var tabs = new[] { TabModLib, TabDeployHistory, TabConfigMerge, TabGenFiles };
            for (int i = 0; i < tabs.Length; i++)
            {
                var isActive = i == index;
                tabs[i].BorderBrush = isActive
                    ? (Brush)FindResource("PrimaryBrush")
                    : Brushes.Transparent;
                tabs[i].Foreground = isActive
                    ? (Brush)FindResource("PrimaryBrush")
                    : (Brush)FindResource("Text500Brush");
            }

            switch (index)
            {
                case 1: _ = LoadDeployHistoryAsync(); break;
                case 2: _ = LoadConfigMergeAsync(); break;
                case 3: _ = LoadGenFilesAsync(); break;
            }
        }

        // ─── Tab 0: MOD 库 ───

        private Task LoadModLibAsync()
        {
            try
            {
                var packages = _packageRepo.GetAllPackages();
                var activeProfile = _profileService.CurrentProfile;

                var referencedKeys = activeProfile?.Packages
                    .Select(p => p.PackageKey).ToHashSet() ?? new HashSet<string>();

                var orphans = _packageRepo.GetOrphanPackages(referencedKeys);
                var dupGroups = _packageRepo.GetDuplicateGroups();

                RepoTotalSize.Text = FormatSize(_packageRepo.GetTotalSize());
                RepoTotalCount.Text = _packageRepo.GetTotalCount().ToString();
                RepoUnrefCount.Text = orphans.Count.ToString();
                RepoDupCount.Text = dupGroups.Count.ToString();

                RepoPackageList.Children.Clear();
                foreach (var pkg in packages)
                {
                    var isRef = referencedKeys.Contains(pkg.PackageKey);
                    AddRepoPackageRow(pkg, isRef);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载 MOD 库失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private void AddRepoPackageRow(Package pkg, bool isReferenced)
        {
            var row = new Border
            {
                Padding = new Thickness(16, 10, 16, 10),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = pkg.DisplayName,
                Foreground = (Brush)FindResource("Text200Brush"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var status = new TextBlock
            {
                Text = isReferenced ? "方案在用" : "未使用",
                Foreground = isReferenced
                    ? (Brush)FindResource("StatusGreenBrush")
                    : (Brush)FindResource("StatusOrangeBrush"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);

            row.Child = grid;
            RepoPackageList.Children.Add(row);
        }

        // ─── Tab 1: 部署记录 ───

        private async Task LoadDeployHistoryAsync()
        {
            try
            {
                DeployHistoryList.Children.Clear();
                var history = await _deploymentService.GetTransactionHistoryAsync();

                if (history.Count == 0)
                {
                    DeployHistoryList.Children.Add(new TextBlock
                    {
                        Text = "暂无安装记录",
                        Foreground = (Brush)FindResource("Text500Brush"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    });
                    return;
                }

                foreach (var tx in history)
                    AddDeployHistoryRow(tx);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载部署记录失败: {ex.Message}");
            }
        }

        private void AddDeployHistoryRow(DeploymentTransaction tx)
        {
            var row = new Border
            {
                Padding = new Thickness(16, 12, 16, 12),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = tx.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                Foreground = (Brush)FindResource("Text200Brush"),
                FontSize = 12
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{tx.TotalOperations} 个操作 · {tx.BackendType}",
                Foreground = (Brush)FindResource("Text500Brush"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var statusColor = tx.Status switch
            {
                DeploymentStatus.Committed => "StatusGreenBrush",
                DeploymentStatus.Failed => "StatusRedBrush",
                DeploymentStatus.RolledBack => "StatusOrangeBrush",
                _ => "Text500Brush"
            };
            var statusText = new TextBlock
            {
                Text = tx.Status.ToString(),
                Foreground = (Brush)FindResource(statusColor),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };
            Grid.SetColumn(statusText, 1);
            grid.Children.Add(statusText);

            if (tx.CanRollback)
            {
                var rollbackBtn = new Button
                {
                    Content = "回滚",
                    Tag = tx,
                    Style = (Style)FindResource("CyberSecondaryButton")
                };
                rollbackBtn.Click += RollbackTransaction_Click;
                Grid.SetColumn(rollbackBtn, 2);
                grid.Children.Add(rollbackBtn);
            }

            row.Child = grid;
            DeployHistoryList.Children.Add(row);
        }

        private async void RollbackTransaction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DeploymentTransaction tx) return;

            var result = MessageBox.Show(
                $"确定要回滚 {tx.CreatedAt:yyyy-MM-dd HH:mm} 的部署事务吗？",
                "确认回滚", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                await _deploymentService.RollbackAsync(tx);
                await LoadDeployHistoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"回滚失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─── Tab 2: 配置合并 ───

        private Task LoadConfigMergeAsync()
        {
            try
            {
                ConfigMergeList.Children.Clear();
                var activeProfile = _profileService.CurrentProfile;
                if (activeProfile == null)
                {
                    ConfigMergeList.Children.Add(new TextBlock
                    {
                        Text = "当前没有可用的 MOD 方案",
                        Foreground = (Brush)FindResource("Text500Brush"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    });
                    return Task.CompletedTask;
                }

                var enabledPackages = _packageRepo.GetAllPackages()
                    .Where(p => activeProfile.Packages.Any(pp => pp.PackageKey == p.PackageKey && pp.IsEnabled))
                    .ToList();

                var configFiles = enabledPackages
                    .SelectMany(p => p.Artifacts.Where(a => a.ArtifactType == ArtifactType.ConfigFile)
                        .Select(a => new { Package = p, Artifact = a }))
                    .GroupBy(x => x.Artifact.RelativeTargetPath)
                    .ToList();

                if (configFiles.Count == 0)
                {
                    ConfigMergeList.Children.Add(new TextBlock
                    {
                        Text = "当前 MOD 方案没有配置文件",
                        Foreground = (Brush)FindResource("Text500Brush"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    });
                    return Task.CompletedTask;
                }

                foreach (var group in configFiles)
                {
                    var row = new Border
                    {
                        Padding = new Thickness(16, 10, 16, 10),
                        BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                        BorderThickness = new Thickness(0, 0, 0, 1)
                    };

                    var stack = new StackPanel();
                    stack.Children.Add(new TextBlock
                    {
                        Text = group.Key,
                        Foreground = (Brush)FindResource("Text200Brush"),
                        FontSize = 12,
                        FontWeight = FontWeights.Medium
                    });

                    foreach (var item in group)
                    {
                        stack.Children.Add(new TextBlock
                        {
                            Text = $"  \u2190 {item.Package.DisplayName}",
                            Foreground = (Brush)FindResource("Text400Brush"),
                            FontSize = 11,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }

                    if (group.Count() > 1)
                    {
                        stack.Children.Add(new TextBlock
                        {
                            Text = $"\u26A0 {group.Count()} 个 MOD 会修改此配置文件",
                            Foreground = (Brush)FindResource("StatusOrangeBrush"),
                            FontSize = 11,
                            Margin = new Thickness(0, 4, 0, 0)
                        });
                    }

                    row.Child = stack;
                    ConfigMergeList.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载配置合并失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        // ─── Tab 3: 生成文件 ───

        private Task LoadGenFilesAsync()
        {
            try
            {
                GenFileList.Children.Clear();
                var artifacts = _overwriteStore.GetAll();

                int active = _overwriteStore.ActiveCount;
                int stale = artifacts.Count(a => a.Status == GeneratedArtifactStatus.Stale);

                GenActiveCount.Text = active.ToString();
                GenExpiredCount.Text = stale.ToString();
                GenTotalSize.Text = FormatSize(_overwriteStore.TotalSize);

                if (artifacts.Count == 0)
                {
                    GenFileList.Children.Add(new TextBlock
                    {
                        Text = "暂无临时文件",
                        Foreground = (Brush)FindResource("Text500Brush"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    });
                    return Task.CompletedTask;
                }

                foreach (var a in artifacts)
                    AddGenFileRow(a);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载生成文件失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private void AddGenFileRow(GeneratedArtifact artifact)
        {
            var row = new Border
            {
                Padding = new Thickness(16, 10, 16, 10),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = artifact.DisplayName,
                Foreground = (Brush)FindResource("Text200Brush"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{artifact.Type} \u00B7 {artifact.SourceSummary}",
                Foreground = (Brush)FindResource("Text500Brush"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            bool isStale = artifact.Status == GeneratedArtifactStatus.Stale;
            var statusText = new TextBlock
            {
                Text = isStale ? "可清理" : "使用中",
                Foreground = isStale
                    ? (Brush)FindResource("StatusOrangeBrush")
                    : (Brush)FindResource("StatusGreenBrush"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(statusText, 1);
            grid.Children.Add(statusText);

            var promoteBtn = new Button
            {
                Content = "转为MOD",
                Tag = artifact.Id,
                Style = (Style)FindResource("CyberSecondaryButton"),
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11
            };
            promoteBtn.Click += GenPromote_Click;
            Grid.SetColumn(promoteBtn, 2);
            grid.Children.Add(promoteBtn);

            var deleteBtn = new Button
            {
                Content = "删除",
                Tag = artifact.Id,
                Style = (Style)FindResource("CyberSecondaryButton"),
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11
            };
            deleteBtn.Click += GenDelete_Click;
            Grid.SetColumn(deleteBtn, 3);
            grid.Children.Add(deleteBtn);

            row.Child = grid;
            GenFileList.Children.Add(row);
        }

        // ─── MOD 库操作 ───

        private async void RepoCheckIntegrity_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var issues = await _packageRepo.CheckIntegrityAsync();
                MessageBox.Show(
                    issues.Count == 0 ? "所有 MOD 文件都能正常找到" : $"发现 {issues.Count} 个文件问题",
                    "检查缺失文件", MessageBoxButton.OK,
                    issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadModLibAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RepoMergeDuplicates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dupGroups = _packageRepo.GetDuplicateGroups();
                if (dupGroups.Count == 0)
                {
                    MessageBox.Show("没有发现相同文件", "合并相同文件", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int merged = 0;
                foreach (var group in dupGroups)
                {
                    foreach (var dup in group.Skip(1))
                    {
                        await _packageRepo.DeletePackageAsync(dup.PackageKey);
                        merged++;
                    }
                }

                MessageBox.Show($"合并了 {merged} 个相同文件", "合并完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadModLibAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"合并失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RepoCleanUnreferenced_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "确定要清理所有未被任何 MOD 方案使用的文件吗？此操作不可撤销。",
                "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var profiles = _profileService.GetProfiles();
                var allRefKeys = profiles
                    .SelectMany(p => p.Packages.Select(pp => pp.PackageKey))
                    .ToHashSet();
                var orphans = _packageRepo.GetOrphanPackages(allRefKeys);

                int count = 0;
                foreach (var pkg in orphans)
                {
                    if (await _packageRepo.DeletePackageAsync(pkg.PackageKey))
                        count++;
                }

                MessageBox.Show($"清理了 {count} 个未使用文件", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadModLibAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─── 生成文件操作 ───

        private async void GenPromote_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Guid artifactId) return;

            try
            {
                var pkg = await _overwriteStore.PromoteToPackageAsync(artifactId);
                if (pkg != null)
                {
                    MessageBox.Show($"已转为正式 MOD: {pkg.DisplayName}", "转换成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        await LoadGenFilesAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"转换失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GenDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Guid artifactId) return;

            var confirm = MessageBox.Show("确定要删除此临时文件吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await _overwriteStore.DeleteAsync(artifactId);
                await LoadGenFilesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GenCleanExpired_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var count = await _overwriteStore.CleanupStaleAsync();
                MessageBox.Show($"清理了 {count} 个可删除文件", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadGenFilesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─── Phase 11 诊断导出 ───

        private async void ExportDiagnostic_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出诊断包",
                FileName = $"UEModManager_diag_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                Filter = "诊断包 (*.zip)|*.zip",
                DefaultExt = ".zip"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var count = await _diagnosticExport.ExportToZipAsync(dialog.FileName);
                MessageBox.Show(this,
                    $"诊断包已导出（{count} 个条目）：\n{dialog.FileName}",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"导出失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        // ─── 工具方法 ───

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:F1} {units[unitIndex]}";
        }
    }
}
