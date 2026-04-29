using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.ViewModels;

namespace UEModManager.Views
{
    public partial class ProfileManagerWindow : Window
    {
        private readonly ProfileViewModel _vm;
        private readonly ProfileService _profileService;
        private readonly ProfileLockService? _lockService;

        public ProfileManagerWindow(
            ProfileService profileService,
            Microsoft.Extensions.Logging.ILogger<ProfileManagerWindow> logger,
            ProfileLockService? lockService = null)
        {
            InitializeComponent();
            _profileService = profileService;
            _lockService = lockService;
            _vm = new ProfileViewModel(profileService, logger);
            _vm.PropertyChanged += Vm_PropertyChanged;
        }

        /// <summary>
        /// 初始化并加载方案列表。
        /// </summary>
        public void LoadForGame(string gameName)
        {
            _vm.LoadProfiles(gameName);
            RenderProfileCards();
            ShowSelectedProfile();
        }

        // ─── 事件处理 ───

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProfileViewModel.SelectedProfile))
                ShowSelectedProfile();
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            _ = _vm.CreateProfileCommand.ExecuteAsync(null);
            RenderProfileCards();
            ShowSelectedProfile();
        }

        private async void CloneProfile_Click(object sender, RoutedEventArgs e)
        {
            await _vm.CloneProfileCommand.ExecuteAsync(null);
            RenderProfileCards();
            ShowSelectedProfile();
        }

        private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedProfile == null) return;
            if (_vm.Profiles.Count <= 1)
            {
                MessageBox.Show("不能删除最后一个方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"确定删除方案「{_vm.SelectedProfile.Name}」吗？",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                await _vm.DeleteProfileCommand.ExecuteAsync(null);
                RenderProfileCards();
                ShowSelectedProfile();
            }
        }

        private void RenameProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedProfile == null) return;

            var newName = CyberInputDialog.Show(this, "重命名方案", "请输入新的方案名称：", _vm.SelectedProfile.Name);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                _ = _vm.RenameProfileAsync(newName);
                RenderProfileCards();
                ShowSelectedProfile();
            }
        }

        // ─── 渲染方法 ───

        /// <summary>
        /// 渲染左侧方案卡片列表。
        /// </summary>
        private void RenderProfileCards()
        {
            ProfileCardList.Children.Clear();
            ProfileCountText.Text = $"{_vm.Profiles.Count} 个方案";

            foreach (var profile in _vm.Profiles)
            {
                var card = CreateProfileCard(profile);
                ProfileCardList.Children.Add(card);
            }
        }

        /// <summary>
        /// 创建单个方案卡片（1:1 还原原型）。
        /// </summary>
        private Border CreateProfileCard(InstanceProfile profile)
        {
            bool isSelected = _vm.SelectedProfile?.Id == profile.Id;
            bool isActive = profile.IsActive;

            // 卡片容器
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0x1a, 0x06, 0xb6, 0xd4))
                    : new SolidColorBrush(Color.FromArgb(0xff, 0x0f, 0x0f, 0x11)),
                BorderThickness = new Thickness(1),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0xff, 0x06, 0xb6, 0xd4))
                    : new SolidColorBrush(Color.FromArgb(0xff, 0x27, 0x27, 0x2a))
            };

            var stack = new StackPanel();

            // 第一行：图标 + 名称 + 活跃标签
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

            // 方案图标
            var iconBorder = new Border
            {
                Width = 32, Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x1a, 0x06, 0xb6, 0xd4)),
                Margin = new Thickness(0, 0, 10, 0)
            };
            var iconText = new TextBlock
            {
                Text = "\uE72E", // Shield icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(0xff, 0x06, 0xb6, 0xd4)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = iconText;
            row1.Children.Add(iconBorder);

            // 名称
            var nameBlock = new TextBlock
            {
                Text = profile.Name,
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xff, 0xf4, 0xf4, 0xf5)),
                VerticalAlignment = VerticalAlignment.Center
            };
            row1.Children.Add(nameBlock);

            // 活跃标签
            if (isActive)
            {
                var activeBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x1a, 0x22, 0xc5, 0x5e)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                activeBadge.Child = new TextBlock
                {
                    Text = "活跃",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xff, 0x22, 0xc5, 0x5e))
                };
                row1.Children.Add(activeBadge);
            }

            stack.Children.Add(row1);

            // 第二行：描述
            if (!string.IsNullOrEmpty(profile.Description))
            {
                var descBlock = new TextBlock
                {
                    Text = profile.Description,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xff, 0x71, 0x71, 0x7a)),
                    Margin = new Thickness(0, 0, 0, 6),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                stack.Children.Add(descBlock);
            }

            // 第三行：统计信息
            var statsRow = new StackPanel { Orientation = Orientation.Horizontal };

            // MOD 数量
            AddStatBadge(statsRow, $"{profile.ModCount} MOD", "#06b6d4");
            // 插件数量
            if (profile.PluginCount > 0)
                AddStatBadge(statsRow, $"{profile.PluginCount} 插件", "#a855f7");
            // 配置数量
            if (profile.ConfigCount > 0)
                AddStatBadge(statsRow, $"{profile.ConfigCount} 配置", "#f59e0b");

            stack.Children.Add(statsRow);

            card.Child = stack;

            // 点击事件
            card.MouseLeftButtonDown += (s, e) =>
            {
                _vm.SelectedProfile = profile;
                RenderProfileCards();
                ShowSelectedProfile();
            };

            // 双击切换
            card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                    _ = _vm.SwitchToProfileCommand.ExecuteAsync(profile);
            };

            return card;
        }

        private static void AddStatBadge(StackPanel container, string text, string colorHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1a, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            badge.Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(color)
            };
            container.Children.Add(badge);
        }

        /// <summary>
        /// 显示右侧选中方案详情。
        /// </summary>
        private void ShowSelectedProfile()
        {
            var profile = _vm.SelectedProfile;
            if (profile == null)
            {
                DetailPanel.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            DetailPanel.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;

            DetailProfileName.Text = profile.Name;
            ActiveBadge.Visibility = profile.IsActive ? Visibility.Visible : Visibility.Collapsed;

            // 更新统计
            StatModCount.Text = profile.ModCount.ToString();
            StatPluginCount.Text = profile.PluginCount.ToString();
            StatConfigCount.Text = profile.ConfigCount.ToString();

            // 渲染 MOD 列表
            RenderModList(profile);
        }

        /// <summary>
        /// 渲染右侧 MOD 列表表格。
        /// </summary>
        private void RenderModList(InstanceProfile profile)
        {
            ModListPanel.Children.Clear();

            var sortedPackages = profile.Packages
                .OrderBy(p => p.Priority)
                .ToList();

            for (int i = 0; i < sortedPackages.Count; i++)
            {
                var entry = sortedPackages[i];
                var isEven = i % 2 == 0;

                var row = new Border
                {
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = isEven
                        ? new SolidColorBrush(Color.FromArgb(0x08, 0xff, 0xff, 0xff))
                        : Brushes.Transparent
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

                // 优先级
                var priorityText = new TextBlock
                {
                    Text = $"#{entry.Priority + 1}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xff, 0xa1, 0xa1, 0xaa)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(priorityText, 0);
                grid.Children.Add(priorityText);

                // 名称
                var nameText = new TextBlock
                {
                    Text = entry.PackageKey,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xff, 0xe4, 0xe4, 0xe7)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(nameText, 1);
                grid.Children.Add(nameText);

                // 类型标签
                var (typeText, typeColor) = entry.Kind switch
                {
                    PackageKind.Mod => ("MOD", "#06b6d4"),
                    PackageKind.Plugin => ("插件", "#a855f7"),
                    PackageKind.Config => ("配置", "#f59e0b"),
                    _ => ("MOD", "#06b6d4")
                };

                var typeBadge = CreateTypeBadge(typeText, typeColor);
                Grid.SetColumn(typeBadge, 2);
                grid.Children.Add(typeBadge);

                // 大小（暂时占位）
                var sizeText = new TextBlock
                {
                    Text = "-",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xff, 0x71, 0x71, 0x7a)),
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(sizeText, 3);
                grid.Children.Add(sizeText);

                // 开关 Toggle
                var toggleContainer = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                var toggle = CreateToggleSwitch(entry.IsEnabled);
                toggleContainer.Child = toggle;
                Grid.SetColumn(toggleContainer, 4);
                grid.Children.Add(toggleContainer);

                row.Child = grid;
                ModListPanel.Children.Add(row);
            }
        }

        private static Border CreateTypeBadge(string text, string colorHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(color)
            };
            return badge;
        }

        /// <summary>
        /// 创建开关组件（1:1 还原原型的 Toggle Switch）。
        /// </summary>
        private static Border CreateToggleSwitch(bool isOn)
        {
            var track = new Border
            {
                Width = 36, Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = isOn
                    ? new SolidColorBrush(Color.FromArgb(0xff, 0x06, 0xb6, 0xd4))
                    : new SolidColorBrush(Color.FromArgb(0xff, 0x3f, 0x3f, 0x46))
            };

            var thumb = new Border
            {
                Width = 16, Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.White,
                HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(2)
            };

            track.Child = thumb;
            return track;
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
                MessageBox.Show(this, "Lock 服务未初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var profile = _profileService.CurrentProfile;
            if (profile == null)
            {
                MessageBox.Show(this, "请先选择一个活跃方案", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出方案 lock",
                FileName = $"{profile.Name}.profile.lock.json",
                Filter = "Profile Lock (*.profile.lock.json)|*.profile.lock.json|JSON (*.json)|*.json",
                DefaultExt = ".profile.lock.json"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await _lockService.ExportAsync(dialog.FileName);
                MessageBox.Show(this,
                    $"已导出：\n{dialog.FileName}\n\n" +
                    "可分享给其他用户。对方导入时若缺少包，应用会提示。",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
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
                MessageBox.Show(this, "Lock 服务未初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入方案 lock",
                Filter = "Profile Lock (*.profile.lock.json;*.json)|*.profile.lock.json;*.json"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var (lockFile, diff) = await _lockService.PreviewImportAsync(dialog.FileName);

                Mouse.OverrideCursor = null;

                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"方案：{lockFile.Profile.Name}（{lockFile.Packages.Count} 个包）");
                summary.AppendLine();
                summary.AppendLine($"  ✓ 本地有匹配：{diff.MatchedCount}");
                summary.AppendLine($"  ✗ 本地缺失：{diff.MissingCount}");
                summary.AppendLine($"  ⚠ 哈希不一致：{diff.HashMismatchCount}");

                if (diff.MissingCount > 0)
                {
                    summary.AppendLine();
                    summary.AppendLine("缺失的包将不会加入新方案，导入后请先导入对应的 MOD。");
                }

                summary.AppendLine();
                summary.Append("是否继续导入并创建新方案？");

                var result = MessageBox.Show(this, summary.ToString(),
                    "导入预览",
                    MessageBoxButton.YesNo,
                    diff.CanImportFully ? MessageBoxImage.Question : MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;

                Mouse.OverrideCursor = Cursors.Wait;
                var newProfile = await _lockService.ApplyImportAsync(lockFile);

                _vm.LoadProfiles(_profileService.CurrentProfile?.HostGameName ?? newProfile.HostGameName);
                RenderProfileCards();
                ShowSelectedProfile();

                MessageBox.Show(this,
                    $"已创建新方案 \"{newProfile.Name}\"，包含 {newProfile.Packages.Count} 个包。",
                    "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, $"导入失败：{ex.Message}", "错误",
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
                MessageBox.Show(this, "Lock 服务未初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var profile = _profileService.CurrentProfile;
            if (profile == null)
            {
                MessageBox.Show(this, "请先选择一个活跃方案", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出整合包",
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

                MessageBox.Show(this,
                    $"整合包已导出：\n{dialog.FileName}\n\n" +
                    $"包含 {profile.Packages.Count} 个包条目及其物理文件。\n" +
                    "对方导入即可还原方案，无需单独导 MOD。",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ImportBundle_Click(object sender, RoutedEventArgs e)
        {
            if (_lockService == null)
            {
                MessageBox.Show(this, "Lock 服务未初始化", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入整合包",
                Filter = "Profile Bundle (*.zip)|*.zip"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var preview = await _lockService.PreviewBundleImportAsync(dialog.FileName);
                Mouse.OverrideCursor = null;

                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"方案：{preview.LockFile.Profile.Name}（{preview.LockFile.Packages.Count} 个包）");
                summary.AppendLine();
                summary.AppendLine($"  ✓ 本地已有：{preview.Diff.MatchedCount}");
                summary.AppendLine($"  ✗ 本地缺失：{preview.Diff.MissingCount}");
                summary.AppendLine($"  ⚠ 哈希不一致：{preview.Diff.HashMismatchCount}");
                summary.AppendLine();
                summary.AppendLine($"整合包内附带：{preview.PackageKeysInBundle.Count} 个包");

                var canFullyRestore = preview.Diff.MissingCount == 0
                    || preview.LockFile.Packages.All(p => preview.PackageKeysInBundle.Contains(p.PackageKey));

                if (!canFullyRestore)
                {
                    summary.AppendLine();
                    summary.AppendLine("⚠ 整合包不完整，部分包既不在本地也未附带，将被跳过。");
                }

                summary.AppendLine();
                summary.Append("是否继续导入并创建新方案？");

                var result = MessageBox.Show(this, summary.ToString(),
                    "整合包导入预览",
                    MessageBoxButton.YesNo,
                    canFullyRestore ? MessageBoxImage.Question : MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;

                Mouse.OverrideCursor = Cursors.Wait;
                var newProfile = await _lockService.ApplyBundleImportAsync(dialog.FileName, preview.LockFile);
                _vm.LoadProfiles(_profileService.CurrentProfile?.HostGameName ?? newProfile.HostGameName);
                RenderProfileCards();
                ShowSelectedProfile();

                MessageBox.Show(this,
                    $"已创建新方案 \"{newProfile.Name}\"，包含 {newProfile.Packages.Count} 个包。",
                    "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, $"导入失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}
