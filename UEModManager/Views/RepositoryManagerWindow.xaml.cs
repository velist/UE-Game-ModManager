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
    public partial class RepositoryManagerWindow : Window
    {
        private readonly PackageRepository _packageRepo;
        private readonly ProfileService _profileService;

        public RepositoryManagerWindow(PackageRepository packageRepo, ProfileService profileService)
        {
            InitializeComponent();
            _packageRepo = packageRepo;
            _profileService = profileService;
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

            TotalSizeText.Text = FormatSize(totalSize);
            TotalCountText.Text = allPackages.Count.ToString();
            OrphanCountText.Text = orphans.Count.ToString();
            DuplicateCountText.Text = duplicates.Count.ToString();

            var orphanSize = orphans.Sum(p => p.TotalSize);
            CleanupButtonText.Text = orphanSize > 0
                ? $"清理未使用 ({FormatSize(orphanSize)})"
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
                Text = FormatSize(pkg.TotalSize),
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

        private async void CheckIntegrity_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var issues = await _packageRepo.CheckIntegrityAsync();
                var msg = issues.Count == 0
                    ? "所有 MOD 文件都能正常找到"
                    : $"发现 {issues.Count} 个文件问题:\n" + string.Join("\n", issues.Take(5).Select(i => $"[{i.packageKey}] {i.issue}"));
                CyberMessageBox.Show(this, msg, "检查缺失文件",
                    MessageBoxButton.OK, issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                CyberMessageBox.Show(this, $"检查失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MergeDuplicates_Click(object sender, RoutedEventArgs e)
        {
            var groups = _packageRepo.GetDuplicateGroups().ToList();
            if (groups.Count == 0)
            {
                CyberMessageBox.Show(this, "未发现相同文件", "合并相同文件", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = CyberMessageBox.Show(this,
                $"发现 {groups.Count} 组相同文件，是否合并？\n合并会保留最新导入的文件，并删除旧副本。",
                "合并相同文件", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // TODO: 实际合并逻辑
                RefreshUI();
            }
        }

        private async void CleanupOrphans_Click(object sender, RoutedEventArgs e)
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
                $"将删除 {orphans.Count} 个未被任何方案使用的 MOD 文件（释放 {FormatSize(totalSize)}）。\n此操作不可撤销，确认继续？",
                "清理未使用文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                foreach (var pkg in orphans)
                {
                    await _packageRepo.DeletePackageAsync(pkg.PackageKey);
                }
                RefreshUI();
            }
        }

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
