using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class RepositoryManagerWindow : Window
    {
        private readonly PackageRepository _packageRepo;
        private readonly ProfileService _profileService;
        private readonly RepositoryReclaimService _reclaim;

        public RepositoryManagerWindow(
            PackageRepository packageRepo,
            ProfileService profileService,
            RepositoryReclaimService reclaim)
        {
            InitializeComponent();
            _packageRepo = packageRepo;
            _profileService = profileService;
            _reclaim = reclaim;
            Loaded += (_, _) => RefreshUI();
        }

        private void RefreshUI()
        {
            // 收集所有 Profile 引用的包 key
            var profiles = _profileService.GetProfiles();
            var referencedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var refCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in profiles)
            {
                foreach (var entry in p.Packages)
                {
                    referencedKeys.Add(entry.PackageKey);
                    refCounts[entry.PackageKey] = refCounts.GetValueOrDefault(entry.PackageKey) + 1;
                }
            }

            // 统计
            var allPackages = _packageRepo.GetAllPackages().ToList();
            var orphans = _packageRepo.GetOrphanPackages(referencedKeys);
            var duplicates = _packageRepo.GetDuplicateGroups();
            var totalSize = _packageRepo.GetTotalSize();

            TotalSizeText.Text = UEModManager.Core.Utils.FileSizeFormatter.Format(totalSize);
            TotalCountText.Text = allPackages.Count.ToString();
            OrphanCountText.Text = orphans.Count.ToString();
            DuplicateCountText.Text = duplicates.Count.ToString();

            var orphanSize = orphans.Sum(p => p.TotalSize);
            CleanupButtonText.Text = orphanSize > 0
                ? $"清理未使用 ({UEModManager.Core.Utils.FileSizeFormatter.Format(orphanSize)})"
                : "清理未使用";

            // 包列表
            PackageListPanel.Children.Clear();

            var orphanKeys = new HashSet<string>(orphans.Select(o => o.PackageKey), StringComparer.OrdinalIgnoreCase);
            var dupKeys = new HashSet<string>(duplicates.SelectMany(g => g.Skip(1).Select(p => p.PackageKey)), StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in allPackages.OrderBy(p => p.DisplayName))
            {
                var refs = refCounts.GetValueOrDefault(pkg.PackageKey);
                var isOrphan = orphanKeys.Contains(pkg.PackageKey);
                var isDuplicate = dupKeys.Contains(pkg.PackageKey);
                AddPackageRow(pkg, refs, isOrphan, isDuplicate);
            }
        }

        private void AddPackageRow(Package pkg, int refCount, bool isOrphan, bool isDuplicate)
        {
            var border = new Border
            {
                Padding = new Thickness(14, 8, 14, 8),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 0.5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            // 包名
            var name = new TextBlock
            {
                Text = pkg.DisplayName,
                FontSize = 13,
                Foreground = (Brush)FindResource("Text200Brush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            // 大小
            var size = new TextBlock
            {
                Text = UEModManager.Core.Utils.FileSizeFormatter.Format(pkg.TotalSize),
                FontSize = 12,
                Foreground = (Brush)FindResource("Text400Brush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(size, 1);
            grid.Children.Add(size);

            // 引用数
            var refText = refCount > 0
                ? $"{refCount} 个方案在用"
                : "未使用";
            var refColor = refCount > 0
                ? Color.FromRgb(0x06, 0xb6, 0xd4)
                : Color.FromRgb(0xf5, 0x9e, 0x0b);
            var refs = new TextBlock
            {
                Text = refText,
                FontSize = 11,
                Foreground = new SolidColorBrush(refColor),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(refs, 2);
            grid.Children.Add(refs);

            // 状态
            string statusLabel;
            Color statusColor;
            if (isDuplicate) { statusLabel = "重复"; statusColor = Color.FromRgb(0xef, 0x44, 0x44); }
            else if (isOrphan) { statusLabel = "未使用"; statusColor = Color.FromRgb(0xf5, 0x9e, 0x0b); }
            else { statusLabel = "正常"; statusColor = Color.FromRgb(0x22, 0xc5, 0x5e); }

            var statusBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, statusColor.R, statusColor.G, statusColor.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = statusLabel,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(statusColor)
                }
            };
            Grid.SetColumn(statusBadge, 3);
            grid.Children.Add(statusBadge);

            border.Child = grid;
            PackageListPanel.Children.Add(border);
        }

        // ─── 事件处理 ───

        private void CheckIntegrity_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                // 正向检查（索引 → 磁盘）与反向扫描（磁盘 → 索引）必须一起做：
                // 只查索引永远看不到"磁盘上有、索引里没有"的导入残留，那正是用户既看不见
                // 也删不掉、却实实在在占着几十 GB 的那部分。
                var issues = await _packageRepo.CheckIntegrityAsync();
                var plan = _reclaim.BuildPlan();
                var lines = issues.Select(i => $"[{i.packageKey}] {i.issue}")
                    .Concat(RepositoryReclaimPrompt.DescribeIssues(plan))
                    .ToList();

                var msg = lines.Count == 0
                    ? "所有 MOD 文件都能正常找到"
                    : $"发现 {lines.Count} 个问题:\n" + string.Join("\n", lines.Take(5));
                CyberMessageBox.Show(this, msg, "检查缺失文件",
                    MessageBoxButton.OK, lines.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

                if (RepositoryReclaimPrompt.ConfirmAndReclaim(this, _reclaim, plan))
                    RefreshUI();
            }, null, "检查仓库完整性");

        private async void MergeDuplicates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var groups = _packageRepo.GetDuplicateGroups();
                if (groups.Count == 0)
                {
                    CyberMessageBox.Show(this, "未发现相同文件", "合并相同文件", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var result = CyberMessageBox.Show(this,
                    $"发现 {groups.Count} 组相同文件，是否合并？\n每组保留最新导入的文件，并删除未被方案引用的旧副本。",
                    "合并相同文件", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                var merge = await _packageRepo.MergeDuplicateGroupsAsync(_profileService.GetProfiles());
                var message = $"合并完成，已删除 {merge.DeletedCount} 个旧副本。";
                if (merge.Skipped.Count > 0)
                {
                    message += $"\n跳过 {merge.Skipped.Count} 个：\n"
                        + string.Join("\n", merge.Skipped.Take(5));
                    if (merge.Skipped.Count > 5) message += "\n……";
                }

                CyberMessageBox.Show(this, message, "合并相同文件", MessageBoxButton.OK,
                    merge.Skipped.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                RefreshUI();
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"合并失败: {ex.Message}", "合并相同文件",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CleanupOrphans_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                // 收集引用 keys
                var referencedKeys = _profileService.GetProfiles()
                    .SelectMany(p => p.Packages.Select(e2 => e2.PackageKey));
                var orphans = _packageRepo.GetOrphanPackages(referencedKeys);
                if (orphans.Count == 0)
                {
                    CyberMessageBox.Show(this, "没有未使用的 MOD 文件", "清理", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var totalSize = orphans.Sum(p => p.TotalSize);
                var result = CyberMessageBox.Show(this,
                    $"将删除 {orphans.Count} 个未被任何方案使用的 MOD 文件（释放 {UEModManager.Core.Utils.FileSizeFormatter.Format(totalSize)}）。\n此操作不可撤销，确认继续？",
                    "清理未使用文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var allProfiles = _profileService.GetProfiles();
                    foreach (var pkg in orphans)
                    {
                        await _packageRepo.DeletePackageAsync(pkg.PackageKey, allProfiles, force: false);
                    }
                    RefreshUI();
                }
            }, null, "清理未使用的 MOD 文件");

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();
    }
}
