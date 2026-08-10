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
using UEModManager.Infrastructure;
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
        private readonly RepositoryReclaimService _reclaim;

        private int _activeTab;

        public ManagementCenterWindow(
            PackageRepository packageRepo,
            ProfileService profileService,
            DeploymentService deploymentService,
            ConfigMergeEngine configMergeEngine,
            OverwriteStore overwriteStore,
            DiagnosticExportService diagnosticExport,
            RepositoryReclaimService reclaim)
        {
            InitializeComponent();
            _packageRepo = packageRepo;
            _profileService = profileService;
            _deploymentService = deploymentService;
            _configMergeEngine = configMergeEngine;
            _overwriteStore = overwriteStore;
            _diagnosticExport = diagnosticExport;
            _reclaim = reclaim;

            Loaded += OnLoaded;
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            Close();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                SwitchTab(0);
                await LoadModLibAsync();
            }, null, "加载管理中心");

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

                RepoTotalSize.Text = UEModManager.Core.Utils.FileSizeFormatter.Format(_packageRepo.GetTotalSize());
                RepoTotalCount.Text = _packageRepo.GetTotalCount().ToString();
                RepoUnrefCount.Text = orphans.Count.ToString();
                RepoDupCount.Text = dupGroups.Count.ToString();

                RepoPackageList.ItemsSource = packages
                    .Select(pkg => RepoPackageRow.Create(
                        pkg, referencedKeys.Contains(pkg.PackageKey), ResolveBrush))
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载 MOD 库失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 按资源键取画刷。沿用 <see cref="FrameworkElement.FindResource"/> 的语义（键不存在即抛），
        /// 与改造前逐行 new 控件时一致：主题令牌被改名时应当立刻暴露，而不是静默变成无前景色。
        /// </summary>
        private Brush ResolveBrush(string key) => (Brush)FindResource(key);

        // ─── Tab 1: 部署记录 ───

        private async Task LoadDeployHistoryAsync()
        {
            try
            {
                // 先清空再取数：与改造前 Children.Clear() 的位置一致。
                // 取数失败时呈现空列表而不是上一次的旧内容——旧内容不带任何"已过期"提示，
                // 比空列表更容易误导。
                //
                // 注意这与 3d8f4e0「列表刷新不再拔插 ItemsSource」不矛盾：那条针对的是
                // 绑定到实例恒定的 ObservableCollection、靠 INotifyCollectionChanged 自动
                // 刷新的列表，拔插纯属多余且会毁掉滚动位置与选中项。此处每次加载都产出
                // 一个全新的 List，没有集合变更通知，重新赋值是唯一的刷新手段；
                // ItemsControl 也不存在选中项，清空只影响滚动位置，而这几个列表本就
                // 只在切换 Tab / 显式刷新时重载，归零是预期行为。
                DeployHistoryList.ItemsSource = null;
                DeployHistoryEmptyText.Visibility = Visibility.Collapsed;

                var history = await _deploymentService.GetTransactionHistoryAsync();

                DeployHistoryList.ItemsSource = history
                    .Select(tx => DeployHistoryRow.Create(tx, ResolveBrush))
                    .ToList();

                DeployHistoryEmptyText.Visibility =
                    history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载部署记录失败: {ex.Message}");
            }
        }

        private async void RollbackTransaction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DeploymentTransaction tx) return;

            var result = CyberMessageBox.Show(this,
                $"确定要回滚 {tx.CreatedAt:yyyy-MM-dd HH:mm} 的部署事务吗？",
                "确认回滚", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                yesText: "回滚", noText: "取消");
            if (result != MessageBoxResult.Yes) return;

            try
            {
                await _deploymentService.RollbackAsync(tx);
                await LoadDeployHistoryAsync();
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"回滚失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                // 同 LoadDeployHistoryAsync：仅为让取数失败时呈现空列表而非旧内容。
                GenFileList.ItemsSource = null;
                GenFileEmptyText.Visibility = Visibility.Collapsed;

                var artifacts = _overwriteStore.GetAll();

                int active = _overwriteStore.ActiveCount;
                int stale = artifacts.Count(a => a.Status == GeneratedArtifactStatus.Stale);

                GenActiveCount.Text = active.ToString();
                GenExpiredCount.Text = stale.ToString();
                GenTotalSize.Text = UEModManager.Core.Utils.FileSizeFormatter.Format(_overwriteStore.TotalSize);

                GenFileList.ItemsSource = artifacts
                    .Select(a => GenFileRow.Create(a, ResolveBrush))
                    .ToList();

                GenFileEmptyText.Visibility =
                    artifacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载生成文件失败: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        // ─── MOD 库操作 ───

        private void RepoCheckIntegrity_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                // 正向检查（索引 → 磁盘）之外还要反向扫描（磁盘 → 索引）：
                // "磁盘上有、索引里没有"的导入残留在界面上完全不可见，只有这里能发现它。
                var issues = await _packageRepo.CheckIntegrityAsync();
                var plan = _reclaim.BuildPlan();
                var problemCount = issues.Count + plan.Reclaimable.Count + plan.Unregistered.Count;

                CyberMessageBox.Show(this,
                    problemCount == 0 ? "所有 MOD 文件都能正常找到" : $"发现 {problemCount} 个文件问题",
                    "检查缺失文件", MessageBoxButton.OK,
                    problemCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

                RepositoryReclaimPrompt.ConfirmAndReclaim(this, _reclaim, plan);
                await LoadModLibAsync();
            }, null, "检查仓库完整性");

        private async void RepoMergeDuplicates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dupGroups = _packageRepo.GetDuplicateGroups();
                if (dupGroups.Count == 0)
                {
                    CyberMessageBox.Show(this, "没有发现相同文件", "合并相同文件", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var merge = await _packageRepo.MergeDuplicateGroupsAsync(_profileService.GetProfiles());

                var message = $"合并完成，已删除 {merge.DeletedCount} 个旧副本。";
                if (merge.Skipped.Count > 0)
                {
                    message += $"\n跳过 {merge.Skipped.Count} 个：\n"
                        + string.Join("\n", merge.Skipped.Take(5));
                    if (merge.Skipped.Count > 5) message += "\n……";
                }

                CyberMessageBox.Show(this, message, "合并完成", MessageBoxButton.OK,
                    merge.Skipped.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadModLibAsync();
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"合并失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RepoCleanUnreferenced_Click(object sender, RoutedEventArgs e)
        {
            var confirm = CyberMessageBox.Show(this,
                "确定要清理所有未被任何 MOD 方案使用的文件吗？此操作不可撤销。",
                "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                yesText: "清理", noText: "取消");
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
                    // 孤儿包 = 不在任何 Profile.Packages 中 → 引用计数必为 0 → 安全删除
                    var (success, _) = await _packageRepo.DeletePackageAsync(
                        pkg.PackageKey, profiles, force: false);
                    if (success) count++;
                }

                CyberMessageBox.Show(this, $"清理了 {count} 个未使用文件", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);

                // 用户点"清理未使用文件"要的是腾空间，而占空间最狠的往往不是这些登记在册的包，
                // 而是索引里根本没有记录的导入残留 —— 顺带问一句，否则那部分永远没人清。
                RepositoryReclaimPrompt.ConfirmAndReclaim(this, _reclaim, _reclaim.BuildPlan());
                await LoadModLibAsync();
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"清理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    CyberMessageBox.Show(this, $"已转为正式 MOD: {pkg.DisplayName}", "转换成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        await LoadGenFilesAsync();
                }
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"转换失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GenDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Guid artifactId) return;

            var confirm = CyberMessageBox.Show(this, "确定要删除此临时文件吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                yesText: "删除", noText: "取消");
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await _overwriteStore.DeleteAsync(artifactId);
                await LoadGenFilesAsync();
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GenCleanExpired_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var count = await _overwriteStore.CleanupStaleAsync();
                CyberMessageBox.Show(this, $"清理了 {count} 个可删除文件", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadGenFilesAsync();
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"清理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                CyberMessageBox.Show(this,
                    $"诊断包已导出（{count} 个条目）：\n{dialog.FileName}",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this,
                    $"导出失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        // ─── 工具方法 ───
    }

    // ═══════════════════════════════════════════════════════════════════
    //  行展示模型
    //
    //  三个列表原本各有一个几十行的 AddXxxRow 方法，用 C# 手工 new 出
    //  Border/Grid/TextBlock 再逐个 Grid.SetColumn —— 布局藏在代码里，
    //  改一处样式要读一遍控件树，也没法在设计器里看。现在结构交给 XAML 的
    //  DataTemplate，这里只负责把模型翻译成"该显示什么文字、什么颜色"。
    //
    //  画刷用 Brush 属性而不是在模板里写 DataTrigger：改造前每行是在构建时
    //  FindResource 取一次画刷，这样写与之保持逐像素一致，也让状态→颜色的
    //  映射留在可单测的 C# 里。
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>MOD 库列表的一行。</summary>
    public sealed class RepoPackageRow
    {
        private RepoPackageRow(string displayName, string statusText, string statusBrushKey, Brush? statusBrush)
        {
            DisplayName = displayName;
            StatusText = statusText;
            StatusBrushKey = statusBrushKey;
            StatusBrush = statusBrush;
        }

        public string DisplayName { get; }
        public string StatusText { get; }
        public string StatusBrushKey { get; }
        public Brush? StatusBrush { get; }

        /// <summary>被方案引用时显示绿色的"方案在用"，否则橙色的"未使用"。</summary>
        public static string StatusTextFor(bool isReferenced) => isReferenced ? "方案在用" : "未使用";

        public static string StatusBrushKeyFor(bool isReferenced)
            => isReferenced ? "StatusGreenBrush" : "StatusOrangeBrush";

        public static RepoPackageRow Create(Package package, bool isReferenced, Func<string, Brush?> resolveBrush)
        {
            var key = StatusBrushKeyFor(isReferenced);
            return new RepoPackageRow(
                package.DisplayName, StatusTextFor(isReferenced), key, resolveBrush(key));
        }
    }

    /// <summary>部署记录列表的一行。</summary>
    public sealed class DeployHistoryRow
    {
        private DeployHistoryRow(
            string timeText, string summaryText, string statusText,
            string statusBrushKey, Brush? statusBrush,
            Visibility rollbackVisibility, DeploymentTransaction transaction)
        {
            TimeText = timeText;
            SummaryText = summaryText;
            StatusText = statusText;
            StatusBrushKey = statusBrushKey;
            StatusBrush = statusBrush;
            RollbackVisibility = rollbackVisibility;
            Transaction = transaction;
        }

        public string TimeText { get; }
        public string SummaryText { get; }
        public string StatusText { get; }
        public string StatusBrushKey { get; }
        public Brush? StatusBrush { get; }

        /// <summary>
        /// 不可回滚时把按钮 Collapsed 而不是不生成它：DataTemplate 是固定结构，
        /// 折叠不占位，与改造前"不 Add 这个按钮"的布局结果一致。
        /// </summary>
        public Visibility RollbackVisibility { get; }

        /// <summary>回滚按钮的 Tag，交给既有的 RollbackTransaction_Click 取用。</summary>
        public DeploymentTransaction Transaction { get; }

        public static string StatusBrushKeyFor(DeploymentStatus status) => status switch
        {
            DeploymentStatus.Committed => "StatusGreenBrush",
            DeploymentStatus.Failed => "StatusRedBrush",
            DeploymentStatus.RolledBack => "StatusOrangeBrush",
            _ => "Text500Brush"
        };

        public static string SummaryTextFor(DeploymentTransaction tx)
            => $"{tx.TotalOperations} 个操作 · {DisplayNameMapper.DeploymentBackend(tx.BackendType)}";

        public static DeployHistoryRow Create(DeploymentTransaction tx, Func<string, Brush?> resolveBrush)
        {
            var key = StatusBrushKeyFor(tx.Status);
            return new DeployHistoryRow(
                tx.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                SummaryTextFor(tx),
                DisplayNameMapper.DeploymentStatus(tx.Status),
                key,
                resolveBrush(key),
                tx.CanRollback ? Visibility.Visible : Visibility.Collapsed,
                tx);
        }
    }

    /// <summary>生成文件列表的一行。</summary>
    public sealed class GenFileRow
    {
        private GenFileRow(
            string displayName, string summaryText, string statusText,
            string statusBrushKey, Brush? statusBrush, Guid artifactId)
        {
            DisplayName = displayName;
            SummaryText = summaryText;
            StatusText = statusText;
            StatusBrushKey = statusBrushKey;
            StatusBrush = statusBrush;
            ArtifactId = artifactId;
        }

        public string DisplayName { get; }
        public string SummaryText { get; }
        public string StatusText { get; }
        public string StatusBrushKey { get; }
        public Brush? StatusBrush { get; }

        /// <summary>两个按钮的 Tag，交给既有的 GenPromote_Click / GenDelete_Click 取用。</summary>
        public Guid ArtifactId { get; }

        public static string StatusTextFor(GeneratedArtifactStatus status)
            => status == GeneratedArtifactStatus.Stale ? "可清理" : "使用中";

        public static string StatusBrushKeyFor(GeneratedArtifactStatus status)
            => status == GeneratedArtifactStatus.Stale ? "StatusOrangeBrush" : "StatusGreenBrush";

        public static string SummaryTextFor(GeneratedArtifact artifact)
            => $"{artifact.Type} · {artifact.SourceSummary}";

        public static GenFileRow Create(GeneratedArtifact artifact, Func<string, Brush?> resolveBrush)
        {
            var key = StatusBrushKeyFor(artifact.Status);
            return new GenFileRow(
                artifact.DisplayName,
                SummaryTextFor(artifact),
                StatusTextFor(artifact.Status),
                key,
                resolveBrush(key),
                artifact.Id);
        }
    }
}
