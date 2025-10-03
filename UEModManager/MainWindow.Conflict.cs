using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using UEModManager.Services;

namespace UEModManager
{
    public partial class MainWindow
    {
        // 冲突检测按钮（单独的partial，避免修改大文件主体）
        private async void ConflictCheckButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(currentModPath))
                {
                    ShowCustomMessageBox("请先在设置中配置MOD目录路径。", "提示");
                    return;
                }

                if (sender is Button btn) { btn.IsEnabled = false; btn.Content = "⏳ 检测中..."; }

                var modsForScan = allMods.Select(m => (DisplayName: m.Name, RealName: m.RealName, Status: m.Status)).ToList();
                var service = new ModConflictService();
                // 将基础游戏路径写入 config.json，供服务读取基础容器（IoStore名称映射）
                try
                {
                    var cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                    string gameBaseToWrite = currentGamePath ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(gameBaseToWrite))
                    {
                        string json;
                        if (System.IO.File.Exists(cfgPath))
                        {
                            var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(cfgPath));
                            using var ms = new System.IO.MemoryStream();
                            using var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true });
                            writer.WriteStartObject();
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (prop.NameEquals("GameBasePath")) continue;
                                prop.WriteTo(writer);
                            }
                            writer.WriteString("GameBasePath", gameBaseToWrite);
                            writer.WriteEndObject();
                            writer.Flush();
                            json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                        }
                        else
                        {
                            json = System.Text.Json.JsonSerializer.Serialize(new { GameBasePath = gameBaseToWrite }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        }
                        System.IO.File.WriteAllText(cfgPath, json, System.Text.Encoding.UTF8);
                    }
                }
                catch { }

                var result = await service.DetectConflictsAsync(currentModPath, currentBackupPath, modsForScan, enabledOnly: true);
                UEModManager.Services.ModConflictRegistry.SetCounts(result.Summaries);

                // 刷新主界面卡片视图以更新冲突徽章显示
                try { Dispatcher?.Invoke(() => RefreshModDisplay()); } catch { /* 忽略刷新异常 */ }

                

                var win = new UEModManager.Views.ConflictResultWindow(result);
                win.Owner = this;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 冲突检测失败: {ex}");
                MessageBox.Show($"冲突检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (sender is Button btn)
                {
                    btn.IsEnabled = true;
                    btn.Content = "🧩 冲突检测";
                }
            }
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
            try
            {
                // 拦截系统关闭命令，显示确认对话框，并根据用户选择决定是否关闭
                e.Handled = true; // 防止继续冒泡触发默认关闭

                var result = MessageBox.Show("确定要关闭 UEModManager 吗？", "确认关闭",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    SystemCommands.CloseWindow(this);
                }
                else
                {
                    // 用户选择“否”，不执行任何操作
                }
            }
            catch { }
        }
    }
}





