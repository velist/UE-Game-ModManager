using UEModManager.Localization;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.ViewModels;

namespace UEModManager.Views
{
    /// <summary>
    /// 优先级列的显示转换：Priority 从 0 开始存储，界面按 #1 起算。
    /// 通用转换器集中在 ValueConverters.cs，那属于主题模块；本窗口专用的这一个就近放在这里。
    /// </summary>
    public sealed class PriorityDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int priority ? $"#{priority + 1}" : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public partial class ProfileManagerWindow : Window
    {
        private readonly ProfileViewModel _vm;
        private readonly ProfileService _profileService;
        private readonly ProfileLockService? _lockService;
        private readonly ILogger<ProfileManagerWindow> _logger;

        public ProfileManagerWindow(
            ProfileService profileService,
            ILogger<ProfileManagerWindow> logger,
            ProfileLockService? lockService = null)
        {
            InitializeComponent();
            _profileService = profileService;
            _lockService = lockService;
            _logger = logger;
            _vm = new ProfileViewModel(profileService);
            _vm.PropertyChanged += Vm_PropertyChanged;
            DataContext = _vm;
        }

        /// <summary>
        /// 初始化并加载方案列表。
        /// </summary>
        public void LoadForGame(string gameName)
        {
            _vm.LoadProfiles(gameName);
            RefreshProfileCards();
        }

        // ─── 事件处理 ───

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProfileViewModel.SelectedProfile))
                ApplyModListSort();
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            SafeEvent.Run(this, async () =>
            {
                await _vm.CreateProfileCommand.ExecuteAsync(null);
                RefreshProfileCards();
            }, _logger, "Create profile");
        }

        private void CloneProfile_Click(object sender, RoutedEventArgs e)
        {
            SafeEvent.Run(this, async () =>
            {
                await _vm.CloneProfileCommand.ExecuteAsync(null);
                RefreshProfileCards();
            }, _logger, "Clone profile");
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
            => SafeEvent.Run(this, async () =>
            {
                if (_vm.SelectedProfile == null) return;
                if (_vm.Profiles.Count <= 1)
                {
                    CyberMessageBox.Show(this, UiText.Get("不能删除最后一个方案"), UiText.Get("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = CyberMessageBox.Show(this, UiText.Interpolate($"确定删除方案「{_vm.SelectedProfile.Name}」吗？"),
                    UiText.Get("确认删除"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                    yesText: UiText.Get("删除"), noText: UiText.Get("取消"));
                if (result == MessageBoxResult.Yes)
                {
                    await _vm.DeleteProfileCommand.ExecuteAsync(null);
                    RefreshProfileCards();
                }
            }, _logger, UiText.Get("删除方案"));

        private void RenameProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedProfile == null) return;

            var newName = CyberInputDialog.Show(this, UiText.Get("重命名方案"), UiText.Get("请输入新的方案名称："), _vm.SelectedProfile.Name);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                _ = _vm.RenameProfileAsync(newName);
                RefreshProfileCards();
            }
        }

        /// <summary>
        /// 双击卡片切换为活跃方案。单击选中已由 ListBox 自身完成，
        /// 不再需要旧实现里"每次点击重建整棵卡片树"的做法。
        /// </summary>
        private void ProfileCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem { DataContext: InstanceProfile profile }) return;

            SafeEvent.Run(this, async () =>
            {
                await _vm.SwitchToProfileCommand.ExecuteAsync(profile);
                RefreshProfileCards();
            }, _logger, UiText.Get("切换方案"));
        }

        // ─── 视图刷新 ───

        /// <summary>
        /// 让卡片列表重新读取数据源。
        ///
        /// InstanceProfile 是 Core 里的纯模型、不实现 INotifyPropertyChanged，
        /// 所以"改名""切换活跃方案"这类只改模型字段、不动集合的操作不会自动反映到界面。
        /// 这里在旧实现调用 RenderProfileCards() 的同样位置刷新视图，刷新时机保持一致。
        /// </summary>
        private void RefreshProfileCards()
        {
            CollectionViewSource.GetDefaultView(ProfileCardList.ItemsSource)?.Refresh();
            ApplyModListSort();
        }

        /// <summary>
        /// MOD 清单按 Priority 升序，与旧实现的 OrderBy(p => p.Priority) 一致。
        /// ItemsControl 没有声明式排序，而放进资源字典的 CollectionViewSource 拿不到 DataContext，
        /// 故在数据源切换后重新挂一次排序。
        /// </summary>
        private void ApplyModListSort()
        {
            var items = ModListPanel.Items;
            if (items.SortDescriptions.Count == 1
                && items.SortDescriptions[0].PropertyName == nameof(ProfilePackageEntry.Priority))
            {
                return;
            }

            items.SortDescriptions.Clear();
            items.SortDescriptions.Add(
                new SortDescription(nameof(ProfilePackageEntry.Priority), ListSortDirection.Ascending));
        }

        // ─── 窗口控制 ───

        private void OnMinimizeWindow(object s, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void OnMaximizeWindow(object s, ExecutedRoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        private void OnRestoreWindow(object s, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
        private void OnCloseWindow(object s, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

        // ─── Phase 12：lock 文件导出/导入 ───

        private async void ExportLock_Click(object sender, RoutedEventArgs e)
        {
            if (_lockService == null)
            {
                CyberMessageBox.Show(this, UiText.Get("方案服务未初始化"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var profile = _profileService.CurrentProfile;
            if (profile == null)
            {
                CyberMessageBox.Show(this, UiText.Get("请先选择一个活跃方案"), UiText.Get("提示"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = UiText.Get("导出方案 lock"),
                FileName = $"{profile.Name}.profile.lock.json",
                Filter = UiText.Get("方案文件 (*.profile.lock.json)|*.profile.lock.json|JSON (*.json)|*.json"),
                DefaultExt = ".profile.lock.json"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await _lockService.ExportAsync(dialog.FileName);
                CyberMessageBox.Show(this,
                    UiText.Interpolate($"已导出：\n{dialog.FileName}\n\n") +
                    UiText.Get("可分享给其他用户。对方导入时若缺少包，应用会提示。"),
                    UiText.Get("导出成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                CyberMessageBox.Show(this, UiText.Interpolate($"导出失败：{ex.Message}"), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async void ImportLock_Click(object sender, RoutedEventArgs e)
        {
            if (_lockService == null)
            {
                CyberMessageBox.Show(this, UiText.Get("方案服务未初始化"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = UiText.Get("导入方案 lock"),
                Filter = UiText.Get("方案文件 (*.profile.lock.json;*.json)|*.profile.lock.json;*.json")
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var (lockFile, diff) = await _lockService.PreviewImportAsync(dialog.FileName);

                Mouse.OverrideCursor = null;

                var summary = new System.Text.StringBuilder();
                summary.AppendLine(UiText.Interpolate($"方案：{lockFile.Profile.Name}（{lockFile.Packages.Count} 个包）"));
                summary.AppendLine();
                summary.AppendLine(UiText.Interpolate($"  ✓ 本地有匹配：{diff.MatchedCount}"));
                summary.AppendLine(UiText.Interpolate($"  ✗ 本地缺失：{diff.MissingCount}"));
                summary.AppendLine(UiText.Interpolate($"  ⚠ 文件校验不一致：{diff.HashMismatchCount}"));

                if (diff.MissingCount > 0)
                {
                    summary.AppendLine();
                    summary.AppendLine(UiText.Get("缺失的包将不会加入新方案，导入后请先导入对应的 MOD。"));
                }

                summary.AppendLine();
                summary.Append(UiText.Get("是否继续导入并创建新方案？"));

                var result = CyberMessageBox.Show(this, summary.ToString(),
                    UiText.Get("导入预览"),
                    MessageBoxButton.YesNo,
                    diff.CanImportFully ? MessageBoxImage.Question : MessageBoxImage.Warning,
                    yesText: UiText.Get("导入"), noText: UiText.Get("取消"));

                if (result != MessageBoxResult.Yes) return;

                Mouse.OverrideCursor = Cursors.Wait;
                var newProfile = await _lockService.ApplyImportAsync(lockFile);

                _vm.LoadProfiles(_profileService.CurrentProfile?.HostGameName ?? newProfile.HostGameName);
                RefreshProfileCards();

                CyberMessageBox.Show(this,
                    UiText.Interpolate($"已创建新方案 \"{newProfile.Name}\"，包含 {newProfile.Packages.Count} 个包。"),
                    UiText.Get("导入成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                CyberMessageBox.Show(this, UiText.Interpolate($"导入失败：{ex.Message}"), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async void ExportBundle_Click(object sender, RoutedEventArgs e)
        {
            if (_lockService == null)
            {
                CyberMessageBox.Show(this, UiText.Get("方案服务未初始化"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var profile = _profileService.CurrentProfile;
            if (profile == null)
            {
                CyberMessageBox.Show(this, UiText.Get("请先选择一个活跃方案"), UiText.Get("提示"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = UiText.Get("导出整合包"),
                FileName = $"{profile.Name}.profile.bundle.zip",
                Filter = "Profile Bundle (*.zip)|*.zip",
                DefaultExt = ".zip"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await _lockService.ExportBundleAsync(dialog.FileName);
                Mouse.OverrideCursor = null;

                CyberMessageBox.Show(this,
                    UiText.Interpolate($"整合包已导出：\n{dialog.FileName}\n\n") +
                    UiText.Interpolate($"包含 {profile.Packages.Count} 个包条目及其物理文件。\n") +
                    UiText.Get("对方导入即可还原方案，无需单独导 MOD。"),
                    UiText.Get("导出成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                Mouse.OverrideCursor = null;
                CyberMessageBox.Show(this, UiText.Interpolate($"导出失败：{ex.Message}"), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ImportBundle_Click(object sender, RoutedEventArgs e)
        {
            if (_lockService == null)
            {
                CyberMessageBox.Show(this, UiText.Get("方案服务未初始化"), UiText.Get("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = UiText.Get("导入整合包"),
                Filter = "Profile Bundle (*.zip)|*.zip"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var preview = await _lockService.PreviewBundleImportAsync(dialog.FileName);
                Mouse.OverrideCursor = null;

                var summary = new System.Text.StringBuilder();
                summary.AppendLine(UiText.Interpolate($"方案：{preview.LockFile.Profile.Name}（{preview.LockFile.Packages.Count} 个包）"));
                summary.AppendLine();
                summary.AppendLine(UiText.Interpolate($"  ✓ 本地已有：{preview.Diff.MatchedCount}"));
                summary.AppendLine(UiText.Interpolate($"  ✗ 本地缺失：{preview.Diff.MissingCount}"));
                summary.AppendLine(UiText.Interpolate($"  ⚠ 文件校验不一致：{preview.Diff.HashMismatchCount}"));
                summary.AppendLine();
                summary.AppendLine(UiText.Interpolate($"整合包内附带：{preview.PackageKeysInBundle.Count} 个包"));

                var canFullyRestore = preview.Diff.MissingCount == 0
                    || preview.LockFile.Packages.All(p => preview.PackageKeysInBundle.Contains(p.PackageKey));

                if (!canFullyRestore)
                {
                    summary.AppendLine();
                    summary.AppendLine(UiText.Get("⚠ 整合包不完整，部分包既不在本地也未附带，将被跳过。"));
                }

                summary.AppendLine();
                summary.Append(UiText.Get("是否继续导入并创建新方案？"));

                var result = CyberMessageBox.Show(this, summary.ToString(),
                    UiText.Get("整合包导入预览"),
                    MessageBoxButton.YesNo,
                    canFullyRestore ? MessageBoxImage.Question : MessageBoxImage.Warning,
                    yesText: UiText.Get("导入"), noText: UiText.Get("取消"));

                if (result != MessageBoxResult.Yes) return;

                Mouse.OverrideCursor = Cursors.Wait;
                var newProfile = await _lockService.ApplyBundleImportAsync(dialog.FileName, preview.LockFile);
                _vm.LoadProfiles(_profileService.CurrentProfile?.HostGameName ?? newProfile.HostGameName);
                RefreshProfileCards();

                CyberMessageBox.Show(this,
                    UiText.Interpolate($"已创建新方案 \"{newProfile.Name}\"，包含 {newProfile.Packages.Count} 个包。"),
                    UiText.Get("导入成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                CyberMessageBox.Show(this, UiText.Interpolate($"导入失败：{ex.Message}"), UiText.Get("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}
