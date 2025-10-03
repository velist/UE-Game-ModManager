using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class ConflictResultWindow : Window
    {
        private ModConflictResult _result;
        private class ModPeerRow
        {
            public string ModName { get; set; } = string.Empty;
            public string ConflictsWith { get; set; } = string.Empty; // 用分号隔开
        }
        private class ConflictDetailRow
        {
            public string AssetPath { get; set; } = string.Empty;
            public string ModsJoined { get; set; } = string.Empty; // 分号分隔的涉及MOD
        }
        public ConflictResultWindow(ModConflictResult result)
        {
            InitializeComponent();
            _result = result;
            SummaryGrid.ItemsSource = result.Summaries;
            // 将明细的 Mods 列转为分号拼接后的字符串，提升可读性
            var detailRows = result.Conflicts
                .Select(c => new ConflictDetailRow
                {
                    AssetPath = c.AssetPath,
                    ModsJoined = string.Join("；", (c.Mods ?? new System.Collections.Generic.List<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                })
                .ToList();
            DetailGrid.ItemsSource = detailRows;
            SummaryText.Text = $"{result.ModeDescription}｜扫描MOD: {result.ScannedMods}｜资源: {result.TotalAssets}｜冲突资源: {result.ConflictAssets}｜耗时: {result.Elapsed.TotalSeconds:F1}s";

            // 计算每个MOD与哪些MOD冲突（去重，分号连接）
            try
            {
                var peersMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var conf in result.Conflicts)
                {
                    foreach (var mod in conf.Mods)
                    {
                        if (!peersMap.TryGetValue(mod, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            peersMap[mod] = set;
                        }
                        foreach (var other in conf.Mods)
                        {
                            if (!string.Equals(other, mod, StringComparison.OrdinalIgnoreCase))
                                set.Add(other);
                        }
                    }
                }

                var rows = peersMap
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new ModPeerRow
                    {
                        ModName = kv.Key,
                        ConflictsWith = string.Join("；", kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    })
                    .ToList();

                PeersGrid.ItemsSource = rows;
                // 启用排序与过滤视图
                try
                {
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(PeersGrid.ItemsSource);
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ModPeerRow.ModName), System.ComponentModel.ListSortDirection.Ascending));
                    view.Refresh();
                }
                catch { }
            }
            catch { }
        }

        private void ApplyPeersFilter()
        {
            try
            {
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(PeersGrid.ItemsSource);
                if (view == null) return;
                var keyword = (PeersFilterText.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(keyword))
                {
                    view.Filter = null;
                }
                else
                {
                    view.Filter = o =>
                    {
                        if (o is ModPeerRow row)
                        {
                            return (row.ModName?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                   (row.ConflictsWith?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        return true;
                    };
                }
                view.Refresh();
            }
            catch { }
        }

        private void PeersFilterText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyPeersFilter();
        }

        private void PeersFilterClear_Click(object sender, RoutedEventArgs e)
        {
            PeersFilterText.Text = string.Empty;
            ApplyPeersFilter();
        }

        private void ExportJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var json = JsonSerializer.Serialize(_result, new JsonSerializerOptions { WriteIndented = true });
                var file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"ConflictReport_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(file, json, Encoding.UTF8);
                MessageBox.Show($"已导出: {file}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // 标题栏按钮命令处理（Qt6风格自绘）
        private void OnMinimizeWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MinimizeWindow(this); } catch { }
        }
        private void OnMaximizeWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.MaximizeWindow(this); } catch { }
        }
        private void OnRestoreWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.RestoreWindow(this); } catch { }
        }
        private void OnCloseWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            try { SystemCommands.CloseWindow(this); } catch { }
        }
    }
}
