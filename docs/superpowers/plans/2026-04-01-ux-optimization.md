# UX 优化实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 简化 Header 操作按钮（5→2+启动），新建管理中心窗口整合仓库/配置/生成物管理，增强导入确认对话框显示部署目标路径和未配置警告。

**Architecture:** 从 MainWindow Header 移除仓库管理、配置、生成物三个按钮，替换为单一「管理中心」按钮，打开新建的 ManagementCenterWindow（4 Tab）。导入按钮文本简化为「导入」。ImportConfirmDialog 新增路径显示区域和未配置警告。DeploymentService 新增事务历史查询方法。

**Tech Stack:** .NET 8.0 WPF, C# 12, XAML, DI (Microsoft.Extensions.DependencyInjection)

**设计规范:** `docs/superpowers/specs/2026-04-01-ux-optimization-design.md`
**原型文件:** `updateplan.pen` (屏幕: `uGRyq` Header, `6oeSE` 管理中心, `QaUEg` 导入确认)

---

## 文件结构

### 新建文件
| 文件 | 职责 |
|------|------|
| `Views/ManagementCenterWindow.xaml` | 管理中心窗口 XAML — 4 Tab 布局 |
| `Views/ManagementCenterWindow.xaml.cs` | 管理中心 code-behind — 数据加载、Tab 切换、操作处理 |

### 修改文件
| 文件 | 变更概要 |
|------|---------|
| `Themes/CyberDarkTheme.xaml` | 新增 `ImportButtonGradient`、`LaunchButtonGradient` 渐变画刷 |
| `MainWindow.xaml` L525-625 | 删除仓库管理/配置/生成物三按钮，新增管理中心按钮，导入按钮文本改为「导入」 |
| `MainWindow.xaml.cs` L1150-1183 | 删除 `RepositoryManager_Click`/`ConfigManager_Click`/`OverwriteManager_Click`，新增 `ManagementCenter_Click` |
| `Views/ImportConfirmDialog.xaml` | 窗口高度 540→640，新增部署目标路径区域 + 路径未配置警告 |
| `Views/ImportConfirmDialog.xaml.cs` | 新增 `GameConfigService` 依赖注入，路径检测逻辑，配置按钮跳转 |
| `Services/DeploymentService.cs` | 新增 `GetTransactionHistoryAsync()` 方法 |
| `App.xaml.cs` L270 | 新增 `ManagementCenterWindow` DI 注册 |

---

## Task 1: 新增主题画刷

**Files:**
- Modify: `UEModManager/Themes/CyberDarkTheme.xaml:40-86` (颜色/画刷区域)

- [ ] **Step 1: 在 CyberDarkTheme.xaml 的 v2.0 类型色标画刷后面添加渐变画刷**

在 `LoserRedBgBrush` 之后、`<!-- ── 圆角 ── -->` 之前插入：

```xml
    <!-- v2.0 UX优化: 渐变按钮画刷 -->
    <LinearGradientBrush x:Key="ImportButtonGradient" StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#06b6d4" Offset="0"/>
        <GradientStop Color="#0891b2" Offset="1"/>
    </LinearGradientBrush>
    <LinearGradientBrush x:Key="LaunchButtonGradient" StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#10b981" Offset="0"/>
        <GradientStop Color="#059669" Offset="1"/>
    </LinearGradientBrush>
```

- [ ] **Step 2: 验证编译通过**

Run: `dotnet build UEModManager/UEModManager.csproj --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add UEModManager/Themes/CyberDarkTheme.xaml
git commit -m "feat: add gradient brushes for Import and Launch buttons"
```

---

## Task 2: 简化 MainWindow Header 按钮

**Files:**
- Modify: `UEModManager/MainWindow.xaml:525-625`
- Modify: `UEModManager/MainWindow.xaml.cs:1150-1183`

- [ ] **Step 1: 修改导入按钮文本和样式**

在 `MainWindow.xaml` 中找到 L538 的 `ImportModText`，将 `Text="导入 MOD"` 改为 `Text="导入"`。
同时将导入按钮的 `Background="{StaticResource SurfaceBrush}"` 改为 `Background="{StaticResource ImportButtonGradient}"`，
将图标 `Stroke` 和文本 `Foreground` 改为 `{StaticResource BgBaseBrush}`（白色图标/文字配渐变底色）。

- [ ] **Step 2: 删除仓库管理按钮**

删除 L543-559 整个 `PluginImportBorder` Border 元素（从 `<!-- 插件导入 → v2.0: 仓库管理 -->` 到对应的 `</Border>`）。

- [ ] **Step 3: 删除配置管理按钮**

删除 L561-577 整个配置管理 Border 元素（从 `<!-- 配置管理 -->` 到对应的 `</Border>`）。

- [ ] **Step 4: 删除生成物管理按钮**

删除 L579-595 整个生成物管理 Border 元素（从 `<!-- 生成物管理 -->` 到对应的 `</Border>`）。

- [ ] **Step 5: 在启动游戏按钮之前插入分隔符 + 管理中心按钮**

在 `<!-- 启动游戏 -->` 之前插入分隔符和管理中心按钮：

```xml
                            <!-- 分隔线（操作区 | 系统区） -->
                            <Border Width="1" Height="24" Background="{StaticResource CyberBorderBrush}" Margin="0,0,16,0"/>

                            <!-- 管理中心 -->
                            <Border CornerRadius="12" Background="{StaticResource SurfaceBrush}" Padding="16,10"
                                    Cursor="Hand" Margin="0,0,16,0"
                                    BorderBrush="{StaticResource BorderLightBrush}" BorderThickness="1"
                                    MouseLeftButtonDown="ManagementCenter_Click">
                                <StackPanel Orientation="Horizontal">
                                    <Viewbox Width="14" Height="14" VerticalAlignment="Center" Margin="0,0,8,0">
                                        <Canvas Width="24" Height="24">
                                            <!-- Lucide settings-2 -->
                                            <Path Data="M20 7H12M20 7V3M20 7L16.5 10.5M4 17H12M4 17V21M4 17L7.5 13.5M20 17H12M20 17V21M20 17L16.5 13.5M4 7H12M4 7V3M4 7L7.5 10.5"
                                                  Stroke="{StaticResource Text400Brush}" StrokeThickness="2"
                                                  StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                                        </Canvas>
                                    </Viewbox>
                                    <TextBlock Text="管理中心" Foreground="{StaticResource Text200Brush}"
                                               FontSize="13" FontWeight="Medium" VerticalAlignment="Center"/>
                                </StackPanel>
                            </Border>
```

- [ ] **Step 5b: 修改启动游戏按钮使用渐变画刷**

将启动游戏按钮的 `Background` 从 `{StaticResource PrimaryBrush}` 改为 `{StaticResource LaunchButtonGradient}`。
同时修改 hover DropShadowEffect 的 Color 为 `#10b98140`（绿色辉光）。

- [ ] **Step 6: 删除旧的 click handler 方法**

在 `MainWindow.xaml.cs` 中删除以下三个方法：
- `RepositoryManager_Click` (L1150-1156)
- `OverwriteManager_Click` (L1158-1164)
- `ConfigManager_Click` (L1166-1183)

- [ ] **Step 7: 添加管理中心 click handler**

在删除位置添加：

```csharp
        /// <summary>打开管理中心窗口。</summary>
        private void ManagementCenter_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                var sp = ((App)Application.Current).ServiceProvider;
                if (sp == null) return;

                var win = sp.GetRequiredService<Views.ManagementCenterWindow>();
                win.Owner = this;
                win.ShowDialog();

                // 管理中心关闭后刷新主界面数据
                _ = RefreshAfterManagementAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 打开管理中心失败: {ex.Message}");
            }
        }

        private async Task RefreshAfterManagementAsync()
        {
            try
            {
                await _vm.RefreshModsAsync();
                UpdateNavCounts();
                UpdateModCountText();
            }
            catch { }
        }
```

- [ ] **Step 8: 验证编译通过**

Run: `dotnet build UEModManager/UEModManager.csproj --no-restore -v q`
Expected: 如果 Task 4 尚未完成（ManagementCenterWindow 不存在），编译会报错。此时有两个选择：
1. 先完成 Task 4 再一起验证
2. 临时将 `ManagementCenter_Click` 中的 `GetRequiredService<Views.ManagementCenterWindow>()` 注释掉，验证 XAML 删除正确后再还原

- [ ] **Step 9: Commit**

```bash
git add UEModManager/MainWindow.xaml UEModManager/MainWindow.xaml.cs
git commit -m "feat: simplify header - replace 3 buttons with Management Center"
```

---

## Task 3: DeploymentService 新增事务历史查询

**Files:**
- Modify: `UEModManager/Services/DeploymentService.cs:275` (在内部方法区之前)

- [ ] **Step 1: 在 `CleanupOldBackups` 方法之后添加 `GetTransactionHistoryAsync`**

```csharp
        /// <summary>
        /// 扫描 Backups 目录，加载所有事务历史记录。
        /// </summary>
        public async Task<List<DeploymentTransaction>> GetTransactionHistoryAsync()
        {
            var result = new List<DeploymentTransaction>();

            if (!Directory.Exists(_backupRootPath))
                return result;

            foreach (var dir in Directory.GetDirectories(_backupRootPath))
            {
                var logPath = Path.Combine(dir, "transaction.json");
                if (!File.Exists(logPath)) continue;

                try
                {
                    var json = await File.ReadAllTextAsync(logPath);
                    var tx = JsonSerializer.Deserialize<DeploymentTransaction>(json, JsonOptions);
                    if (tx != null)
                        result.Add(tx);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取事务日志失败: {Path}", logPath);
                }
            }

            return result.OrderByDescending(t => t.CreatedAt).ToList();
        }
```

- [ ] **Step 2: 验证编译通过**

Run: `dotnet build UEModManager/UEModManager.csproj --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add UEModManager/Services/DeploymentService.cs
git commit -m "feat: add GetTransactionHistoryAsync to DeploymentService"
```

---

## Task 4: 创建 ManagementCenterWindow

**Files:**
- Create: `UEModManager/Views/ManagementCenterWindow.xaml`
- Create: `UEModManager/Views/ManagementCenterWindow.xaml.cs`
- Modify: `UEModManager/App.xaml.cs:279` (DI 注册)

这是最大的 Task，分为 XAML 和 code-behind 两部分。

### 4A: XAML 布局

- [ ] **Step 1: 创建 ManagementCenterWindow.xaml**

窗口规格：520×620，`CyberModalWindow` 样式，`ShowDialog` 模式。
4 个 Tab：MOD 库、部署记录、配置合并、生成文件。

```xml
<Window x:Class="UEModManager.Views.ManagementCenterWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="管理中心" Height="620" Width="520"
        WindowStartupLocation="CenterOwner"
        Style="{StaticResource CyberModalWindow}">

    <Window.CommandBindings>
        <CommandBinding Command="{x:Static SystemCommands.CloseWindowCommand}" Executed="OnCloseWindow"/>
    </Window.CommandBindings>

    <Grid>
        <Grid.RowDefinitions>
            <!-- 标题 -->
            <RowDefinition Height="Auto"/>
            <!-- Tab 栏 -->
            <RowDefinition Height="Auto"/>
            <!-- Tab 内容 -->
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- ═══ 标题 ═══ -->
        <Border Grid.Row="0" Padding="24,20,24,12">
            <StackPanel Orientation="Horizontal">
                <Viewbox Width="18" Height="18" VerticalAlignment="Center" Margin="0,0,10,0">
                    <Canvas Width="24" Height="24">
                        <Path Data="M20 7H12M20 7V3M20 7L16.5 10.5M4 17H12M4 17V21M4 17L7.5 13.5M20 17H12M20 17V21M20 17L16.5 13.5M4 7H12M4 7V3M4 7L7.5 10.5"
                              Stroke="{StaticResource PrimaryBrush}" StrokeThickness="2"
                              StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                    </Canvas>
                </Viewbox>
                <TextBlock Text="管理中心" FontSize="17" FontWeight="SemiBold"
                           Foreground="{StaticResource Text100Brush}" VerticalAlignment="Center"/>
            </StackPanel>
        </Border>

        <!-- ═══ Tab 栏 ═══ -->
        <Border Grid.Row="1" BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="0,0,0,1" Margin="24,0">
            <StackPanel x:Name="TabBar" Orientation="Horizontal">
                <Button x:Name="TabModLib" Content="MOD 库" Tag="0" Click="Tab_Click"
                        Style="{StaticResource ManagementTabStyle}"/>
                <Button x:Name="TabDeployHistory" Content="部署记录" Tag="1" Click="Tab_Click"
                        Style="{StaticResource ManagementTabStyle}" Margin="24,0,0,0"/>
                <Button x:Name="TabConfigMerge" Content="配置合并" Tag="2" Click="Tab_Click"
                        Style="{StaticResource ManagementTabStyle}" Margin="24,0,0,0"/>
                <Button x:Name="TabGenFiles" Content="生成文件" Tag="3" Click="Tab_Click"
                        Style="{StaticResource ManagementTabStyle}" Margin="24,0,0,0"/>
            </StackPanel>
        </Border>

        <!-- ═══ Tab 内容区 ═══ -->
        <Grid Grid.Row="2">
            <!-- Tab 0: MOD 库 -->
            <Grid x:Name="PanelModLib">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <!-- 统计卡片 -->
                <Grid Grid.Row="0" Margin="24,16,24,12">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>

                    <Border Grid.Column="0" Background="{StaticResource SurfaceBrush}"
                            BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                        <StackPanel>
                            <TextBlock x:Name="RepoTotalSize" Text="0 B" FontSize="16" FontWeight="Bold"
                                       Foreground="{StaticResource Text100Brush}"/>
                            <TextBlock Text="总占用" FontSize="10" Foreground="{StaticResource Text500Brush}" Margin="0,2,0,0"/>
                        </StackPanel>
                    </Border>
                    <Border Grid.Column="2" Background="{StaticResource SurfaceBrush}"
                            BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                        <StackPanel>
                            <TextBlock x:Name="RepoTotalCount" Text="0" FontSize="16" FontWeight="Bold"
                                       Foreground="{StaticResource Text100Brush}"/>
                            <TextBlock Text="总包数" FontSize="10" Foreground="{StaticResource Text500Brush}" Margin="0,2,0,0"/>
                        </StackPanel>
                    </Border>
                    <Border Grid.Column="4" Background="{StaticResource SurfaceBrush}"
                            BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                        <StackPanel>
                            <TextBlock x:Name="RepoUnrefCount" Text="0" FontSize="16" FontWeight="Bold"
                                       Foreground="{StaticResource StatusOrangeBrush}"/>
                            <TextBlock Text="未引用" FontSize="10" Foreground="{StaticResource Text500Brush}" Margin="0,2,0,0"/>
                        </StackPanel>
                    </Border>
                    <Border Grid.Column="6" Background="{StaticResource SurfaceBrush}"
                            BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                        <StackPanel>
                            <TextBlock x:Name="RepoDupCount" Text="0" FontSize="16" FontWeight="Bold"
                                       Foreground="{StaticResource StatusOrangeBrush}"/>
                            <TextBlock Text="重复包" FontSize="10" Foreground="{StaticResource Text500Brush}" Margin="0,2,0,0"/>
                        </StackPanel>
                    </Border>
                </Grid>

                <!-- 包列表 -->
                <Border Grid.Row="1" Margin="24,0" Background="{StaticResource SurfaceBrush}"
                        BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                        CornerRadius="{StaticResource RadiusSm}">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel x:Name="RepoPackageList"/>
                    </ScrollViewer>
                </Border>

                <!-- 底部操作 -->
                <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="24,12">
                    <Button Content="检查完整性" Click="RepoCheckIntegrity_Click"
                            Style="{StaticResource CyberSecondaryButton}" Margin="0,0,8,0"/>
                    <Button Content="合并重复" Click="RepoMergeDuplicates_Click"
                            Style="{StaticResource CyberSecondaryButton}" Margin="0,0,8,0"/>
                    <Button Content="清理未引用" Click="RepoCleanUnreferenced_Click"
                            Style="{StaticResource CyberSecondaryButton}"/>
                </StackPanel>
            </Grid>

            <!-- Tab 1: 部署记录 -->
            <Grid x:Name="PanelDeployHistory" Visibility="Collapsed">
                <Grid.RowDefinitions>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>

                <Border Margin="24,16" Background="{StaticResource SurfaceBrush}"
                        BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                        CornerRadius="{StaticResource RadiusSm}">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel x:Name="DeployHistoryList"/>
                    </ScrollViewer>
                </Border>
            </Grid>

            <!-- Tab 2: 配置合并 -->
            <Grid x:Name="PanelConfigMerge" Visibility="Collapsed">
                <Grid.RowDefinitions>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>

                <Border Margin="24,16" Background="{StaticResource SurfaceBrush}"
                        BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                        CornerRadius="{StaticResource RadiusSm}">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel x:Name="ConfigMergeList"/>
                    </ScrollViewer>
                </Border>
            </Grid>

            <!-- Tab 3: 生成文件 -->
            <Grid x:Name="PanelGenFiles" Visibility="Collapsed">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <!-- 统计 -->
                <Grid Grid.Row="0" Margin="24,16,24,12">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>

                    <Border Grid.Column="0" Background="{StaticResource SurfaceBrush}"
                            BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                        <StackPanel>
                            <TextBlock x:Name="GenActiveCount" Text="0" FontSize="16" FontWeight="Bold"
                                       Foreground="{StaticResource StatusGreenBrush}"/>
                            <TextBlock Text="活跃" FontSize="10" Foreground="{StaticResource Text500Brush}" Margin="0,2,0,0"/>
                        </StackPanel>
                    </Border>
                    <Border Grid.Column="2" Background="{StaticResource SurfaceBrush}"
                            BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                        <StackPanel>
                            <TextBlock x:Name="GenExpiredCount" Text="0" FontSize="16" FontWeight="Bold"
                                       Foreground="{StaticResource StatusOrangeBrush}"/>
                            <TextBlock Text="过期" FontSize="10" Foreground="{StaticResource Text500Brush}" Margin="0,2,0,0"/>
                        </StackPanel>
                    </Border>
                    <Border Grid.Column="4" Background="{StaticResource SurfaceBrush}"
                            BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                        <StackPanel>
                            <TextBlock x:Name="GenTotalSize" Text="0 B" FontSize="16" FontWeight="Bold"
                                       Foreground="{StaticResource Text100Brush}"/>
                            <TextBlock Text="总占用" FontSize="10" Foreground="{StaticResource Text500Brush}" Margin="0,2,0,0"/>
                        </StackPanel>
                    </Border>
                </Grid>

                <!-- 列表 -->
                <Border Grid.Row="1" Margin="24,0" Background="{StaticResource SurfaceBrush}"
                        BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                        CornerRadius="{StaticResource RadiusSm}">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel x:Name="GenFileList"/>
                    </ScrollViewer>
                </Border>

                <!-- 底部操作 -->
                <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="24,12">
                    <Button Content="清理过期" Click="GenCleanExpired_Click"
                            Style="{StaticResource CyberSecondaryButton}"/>
                </StackPanel>

                <!-- 注: 每行列表项自带"晋升"和"删除"按钮，见 code-behind AddGenFileRow 方法 -->
            </Grid>
        </Grid>
    </Grid>
</Window>
```

### 4B: Tab 样式

- [ ] **Step 2: 在 CyberDarkTheme.xaml 中添加 ManagementTabStyle**

在渐变画刷之后添加 Tab 按钮样式：

```xml
    <!-- v2.0 管理中心 Tab 样式 -->
    <Style x:Key="ManagementTabStyle" TargetType="{x:Type Button}">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0,0,0,2"/>
        <Setter Property="BorderBrush" Value="Transparent"/>
        <Setter Property="Foreground" Value="{StaticResource Text500Brush}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="FontWeight" Value="Medium"/>
        <Setter Property="Padding" Value="0,8,0,10"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type Button}">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Foreground" Value="{StaticResource Text200Brush}"/>
            </Trigger>
        </Style.Triggers>
    </Style>
```

### 4C: Code-Behind

- [ ] **Step 3: 创建 ManagementCenterWindow.xaml.cs**

```csharp
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

        private int _activeTab;
        private bool _hasDataChanged;

        public ManagementCenterWindow(
            PackageRepository packageRepo,
            ProfileService profileService,
            DeploymentService deploymentService,
            ConfigMergeEngine configMergeEngine,
            OverwriteStore overwriteStore)
        {
            InitializeComponent();
            _packageRepo = packageRepo;
            _profileService = profileService;
            _deploymentService = deploymentService;
            _configMergeEngine = configMergeEngine;
            _overwriteStore = overwriteStore;

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
        // 实际 API:
        //   _packageRepo.GetTotalSize() → long
        //   _packageRepo.GetTotalCount() → int
        //   _packageRepo.GetOrphanPackages(referencedKeys) → List<Package>
        //   _packageRepo.GetDuplicateGroups() → List<List<Package>>
        //   _packageRepo.CheckIntegrityAsync() → List<(string, string)>
        //   _profileService.CurrentProfile → InstanceProfile?

        private async Task LoadModLibAsync()
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
                Text = isReferenced ? "已引用" : "未引用",
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
                        Text = "暂无部署记录",
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
                _hasDataChanged = true;
                await LoadDeployHistoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"回滚失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─── Tab 2: 配置合并 ───

        private async Task LoadConfigMergeAsync()
        {
            try
            {
                ConfigMergeList.Children.Clear();
                var activeProfile = _profileService.CurrentProfile;
                if (activeProfile == null)
                {
                    ConfigMergeList.Children.Add(new TextBlock
                    {
                        Text = "无活跃方案",
                        Foreground = (Brush)FindResource("Text500Brush"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    });
                    return;
                }

                var enabledPackages = _packageRepo.GetAllPackages()
                    .Where(p => activeProfile.Packages.Any(pp => pp.PackageKey == p.PackageKey && pp.IsEnabled))
                    .ToList();

                var configFiles = enabledPackages
                    .SelectMany(p => p.Artifacts.Where(a => a.ArtifactType == ArtifactType.Config)
                        .Select(a => new { Package = p, Artifact = a }))
                    .GroupBy(x => x.Artifact.RelativeTargetPath)
                    .ToList();

                if (configFiles.Count == 0)
                {
                    ConfigMergeList.Children.Add(new TextBlock
                    {
                        Text = "当前方案无配置文件",
                        Foreground = (Brush)FindResource("Text500Brush"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    });
                    return;
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
                            Text = $"\u26A0 {group.Count()} 个包修改此文件",
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
        }

        // ─── Tab 3: 生成文件 ───
        // 实际 API:
        //   _overwriteStore.GetAll() → IReadOnlyList<GeneratedArtifact>
        //   _overwriteStore.ActiveCount → int
        //   _overwriteStore.TotalSize → long
        //   _overwriteStore.CleanupStaleAsync() → int
        //   _overwriteStore.PromoteToPackageAsync(id) → Package?
        //   _overwriteStore.DeleteAsync(id)
        //   GeneratedArtifact: RelativePath, DisplayName, Type, Status, SourceDescription, FileSize, FormattedSize

        private async Task LoadGenFilesAsync()
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
                        Text = "暂无生成文件",
                        Foreground = (Brush)FindResource("Text500Brush"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    });
                    return;
                }

                foreach (var a in artifacts)
                    AddGenFileRow(a);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagementCenter] 加载生成文件失败: {ex.Message}");
            }
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

            // 信息列
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

            // 状态
            bool isStale = artifact.Status == GeneratedArtifactStatus.Stale;
            var statusText = new TextBlock
            {
                Text = isStale ? "过期" : "活跃",
                Foreground = isStale
                    ? (Brush)FindResource("StatusOrangeBrush")
                    : (Brush)FindResource("StatusGreenBrush"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(statusText, 1);
            grid.Children.Add(statusText);

            // 晋升按钮
            var promoteBtn = new Button
            {
                Content = "晋升",
                Tag = artifact.Id,
                Style = (Style)FindResource("CyberSecondaryButton"),
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11
            };
            promoteBtn.Click += GenPromote_Click;
            Grid.SetColumn(promoteBtn, 2);
            grid.Children.Add(promoteBtn);

            // 删除按钮
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
                    issues.Count == 0 ? "仓库完整性检查通过" : $"发现 {issues.Count} 个问题",
                    "完整性检查", MessageBoxButton.OK,
                    issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                _hasDataChanged = true;
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
                    MessageBox.Show("没有发现重复包", "合并", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int merged = 0;
                foreach (var group in dupGroups)
                {
                    // 保留第一个，删除其余
                    foreach (var dup in group.Skip(1))
                    {
                        await _packageRepo.DeletePackageAsync(dup.PackageKey);
                        merged++;
                    }
                }

                MessageBox.Show($"合并了 {merged} 个重复包", "合并完成", MessageBoxButton.OK, MessageBoxImage.Information);
                _hasDataChanged = true;
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
                "确定要清理所有未被任何方案引用的包吗？此操作不可撤销。",
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

                MessageBox.Show($"清理了 {count} 个未引用包", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
                _hasDataChanged = true;
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
                    MessageBox.Show($"已晋升为正式包: {pkg.DisplayName}", "晋升成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    _hasDataChanged = true;
                    await LoadGenFilesAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"晋升失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GenDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Guid artifactId) return;

            var confirm = MessageBox.Show("确定要删除此生成文件吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await _overwriteStore.DeleteAsync(artifactId);
                _hasDataChanged = true;
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
                MessageBox.Show($"清理了 {count} 个过期文件", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
                _hasDataChanged = true;
                await LoadGenFilesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
```

- [ ] **Step 4: 在 App.xaml.cs 中注册 ManagementCenterWindow**

在 `services.AddTransient<Views.ConfigManagerWindow>();` 之后添加：

```csharp
                    services.AddTransient<Views.ManagementCenterWindow>();
```

- [ ] **Step 5: 验证编译通过**

Run: `dotnet build UEModManager/UEModManager.csproj --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add UEModManager/Views/ManagementCenterWindow.xaml UEModManager/Views/ManagementCenterWindow.xaml.cs UEModManager/Themes/CyberDarkTheme.xaml UEModManager/App.xaml.cs
git commit -m "feat: add ManagementCenterWindow with 4 tabs (MOD lib, deploy history, config merge, generated files)"
```

---

## Task 5: 增强 ImportConfirmDialog — 路径显示与警告

**Files:**
- Modify: `UEModManager/Views/ImportConfirmDialog.xaml` (Row 4 之前插入新行)
- Modify: `UEModManager/Views/ImportConfirmDialog.xaml.cs`

- [ ] **Step 1: 修改窗口高度**

`ImportConfirmDialog.xaml` 中将 `Height="540"` 改为 `Height="640"`。

- [ ] **Step 1b: 替换标题区 Segoe MDL2 图标为 Lucide download**

将标题区域的 `<TextBlock Text="&#xE896;" FontFamily="Segoe MDL2 Assets" .../>` 替换为 Lucide download Path 矢量图标（与 Header 导入按钮一致的 Viewbox+Canvas+Path）。

- [ ] **Step 2: 在文件列表之后、冲突警告之前插入路径显示区域**

在 XAML 的 Grid.RowDefinitions 中，在冲突警告行之前插入两个新行（部署路径 + 未配置警告）。原本 6 行变为 8 行：

```
Row 0: 标题 (Auto)
Row 1: 说明 + 压缩包信息 (Auto)
Row 2: 方案选择 (Auto)
Row 3: 文件列表 (*)
Row 4: 部署目标路径 (Auto)     ← 新增
Row 5: 路径未配置警告 (Auto)   ← 新增
Row 6: 冲突警告 (Auto)         ← 原 Row 4
Row 7: 底部按钮 (Auto)         ← 原 Row 5
```

在文件列表（Row 3）之后添加：

```xml
        <!-- ═══ 部署目标路径 ═══ -->
        <Border Grid.Row="4" Margin="24,12,24,0"
                Background="{StaticResource SurfaceBrush}"
                BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                CornerRadius="{StaticResource RadiusSm}" Padding="16,12">
            <StackPanel>
                <TextBlock Text="部署目标路径" FontSize="12" FontWeight="SemiBold"
                           Foreground="{StaticResource Text200Brush}" Margin="0,0,0,8"/>

                <!-- MOD 路径 -->
                <StackPanel x:Name="ModPathRow" Orientation="Horizontal" Margin="0,0,0,4">
                    <Ellipse Width="8" Height="8" Fill="{StaticResource PrimaryBrush}"
                             VerticalAlignment="Center" Margin="0,0,8,0"/>
                    <TextBlock Text="MOD →" FontSize="11" Foreground="{StaticResource Text400Brush}"
                               VerticalAlignment="Center" Margin="0,0,6,0"/>
                    <TextBlock x:Name="ModPathText" Text="未配置" FontSize="11"
                               Foreground="{StaticResource Text300Brush}"
                               VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                </StackPanel>

                <!-- 插件路径 -->
                <StackPanel x:Name="PluginPathRow" Orientation="Horizontal" Margin="0,0,0,4">
                    <Ellipse Width="8" Height="8" Fill="{StaticResource PluginPurpleBrush}"
                             VerticalAlignment="Center" Margin="0,0,8,0"/>
                    <TextBlock Text="插件 →" FontSize="11" Foreground="{StaticResource Text400Brush}"
                               VerticalAlignment="Center" Margin="0,0,6,0"/>
                    <TextBlock x:Name="PluginPathText" Text="未配置" FontSize="11"
                               Foreground="{StaticResource Text300Brush}"
                               VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                </StackPanel>

                <!-- 配置路径 -->
                <StackPanel x:Name="ConfigPathRow" Orientation="Horizontal" Margin="0,0,0,4">
                    <Ellipse Width="8" Height="8" Fill="{StaticResource ConfigAmberBrush}"
                             VerticalAlignment="Center" Margin="0,0,8,0"/>
                    <TextBlock Text="配置 →" FontSize="11" Foreground="{StaticResource Text400Brush}"
                               VerticalAlignment="Center" Margin="0,0,6,0"/>
                    <TextBlock x:Name="ConfigPathText" Text="未配置" FontSize="11"
                               Foreground="{StaticResource Text300Brush}"
                               VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                </StackPanel>

                <!-- 修改路径按钮 -->
                <TextBlock x:Name="EditPathLink" Text="修改路径" FontSize="11"
                           Foreground="{StaticResource PrimaryBrush}" Cursor="Hand"
                           Margin="0,4,0,0" MouseLeftButtonDown="EditPath_Click">
                    <TextBlock.TextDecorations>
                        <TextDecoration Location="Underline"/>
                    </TextBlock.TextDecorations>
                </TextBlock>
            </StackPanel>
        </Border>

        <!-- ═══ 路径未配置警告 ═══ -->
        <Border x:Name="PathWarningBorder" Grid.Row="5" Margin="24,8,24,0"
                Visibility="Collapsed"
                Background="#1Af59e0b"
                BorderBrush="{StaticResource ConfigAmberBrush}" BorderThickness="1"
                CornerRadius="{StaticResource RadiusSm}" Padding="16,10">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <StackPanel Grid.Column="0" Orientation="Horizontal">
                    <TextBlock Text="⚠" FontSize="14" VerticalAlignment="Center" Margin="0,0,8,0"
                               Foreground="{StaticResource ConfigAmberBrush}"/>
                    <TextBlock x:Name="PathWarningText" Text="路径未配置"
                               FontSize="12" Foreground="{StaticResource ConfigAmberBrush}"
                               VerticalAlignment="Center"/>
                </StackPanel>

                <Button Grid.Column="1" Content="配置" Click="ConfigurePath_Click"
                        Style="{StaticResource CyberSecondaryButton}" Padding="12,4"/>
            </Grid>
        </Border>
```

- [ ] **Step 3: 更新冲突警告和底部按钮的 Grid.Row**

原冲突警告从 `Grid.Row="4"` 改为 `Grid.Row="6"`。
原底部按钮从 `Grid.Row="5"` 改为 `Grid.Row="7"`。

- [ ] **Step 4: 在 code-behind 添加 GameConfigService 依赖和路径检测**

在构造函数中增加 `GameConfigService` 参数：

```csharp
        private readonly GameConfigService _gameConfig;

        public ImportConfirmDialog(
            PackageImportService importService,
            PackageRepository packageRepo,
            ProfileService profileService,
            ConflictAnalyzer conflictAnalyzer,
            GameConfigService gameConfig)
        {
            InitializeComponent();
            _importService = importService;
            _packageRepo = packageRepo;
            _profileService = profileService;
            _conflictAnalyzer = conflictAnalyzer;
            _gameConfig = gameConfig;
            Loaded += OnLoaded;
        }
```

- [ ] **Step 5: 在 OnLoaded 末尾调用路径检测**

```csharp
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadProfiles();
            AnalyzeFiles();
            UpdateDeployPaths();
        }
```

- [ ] **Step 6: 添加路径检测和警告逻辑方法**

```csharp
        private void UpdateDeployPaths()
        {
            // 实际 API:
            //   _gameConfig.CurrentModPath → string (MOD 部署路径)
            //   _gameConfig.GetPluginPath(gameName) → string (插件路径)
            //   _gameConfig.CurrentGameName → string

            var modPath = _gameConfig.CurrentModPath;
            ModPathText.Text = string.IsNullOrEmpty(modPath) ? "未配置" : modPath;

            var gameName = _gameConfig.CurrentGameName;
            var pluginPath = !string.IsNullOrEmpty(gameName) ? _gameConfig.GetPluginPath(gameName) : "";
            PluginPathText.Text = string.IsNullOrEmpty(pluginPath) ? "未配置" : pluginPath;

            // 配置路径: 当前系统无独立配置路径，复用 MOD 路径
            ConfigPathText.Text = string.IsNullOrEmpty(modPath) ? "未配置" : modPath;

            // 仅显示当前导入文件涉及的路径行
            bool hasMod = _fileEntries.Any(f => f.Kind == PackageKind.Mod);
            bool hasPlugin = _fileEntries.Any(f => f.Kind == PackageKind.Plugin);
            bool hasConfig = _fileEntries.Any(f => f.Kind == PackageKind.Config);

            ModPathRow.Visibility = hasMod ? Visibility.Visible : Visibility.Collapsed;
            PluginPathRow.Visibility = hasPlugin ? Visibility.Visible : Visibility.Collapsed;
            ConfigPathRow.Visibility = hasConfig ? Visibility.Visible : Visibility.Collapsed;

            // 路径未配置警告
            var warnings = new List<string>();
            if (hasMod && string.IsNullOrEmpty(modPath))
                warnings.Add("MOD");
            if (hasPlugin && string.IsNullOrEmpty(pluginPath))
                warnings.Add("插件");

            if (warnings.Count > 0)
            {
                PathWarningText.Text = $"{string.Join("、", warnings)}路径未配置，部署可能失败";
                PathWarningBorder.Visibility = Visibility.Visible;
            }
            else
            {
                PathWarningBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void EditPath_Click(object sender, MouseButtonEventArgs e)
        {
            OpenGamePathDialog();
        }

        private void ConfigurePath_Click(object sender, RoutedEventArgs e)
        {
            OpenGamePathDialog();
        }

        private void OpenGamePathDialog()
        {
            var gameName = _gameConfig.CurrentGameName;
            if (string.IsNullOrEmpty(gameName)) return;

            var dialog = new GamePathDialog(gameName) { Owner = this };
            dialog.ShowDialog();
            // 无论结果如何都刷新路径显示（GamePathDialog 内部已保存配置）
            UpdateDeployPaths();
        }
```

- [ ] **Step 7: 验证编译通过**

Run: `dotnet build UEModManager/UEModManager.csproj --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 8: Commit**

```bash
git add UEModManager/Views/ImportConfirmDialog.xaml UEModManager/Views/ImportConfirmDialog.xaml.cs
git commit -m "feat: add deploy path display and unconfigured path warning to import dialog"
```

---

## Task 6: 集成验证

- [ ] **Step 1: 全量编译**

Run: `dotnet build UEModManager.sln --configuration Debug`
Expected: Build succeeded, 0 errors

- [ ] **Step 2: 启动应用验证**

手动验证清单：
1. Header 只有 4 个元素：冲突检测、导入、管理中心、启动游戏
2. 「导入」按钮文本已从"导入 MOD"改为"导入"
3. 点击「管理中心」打开 4 Tab 窗口
4. MOD 库 Tab 显示统计卡片和包列表
5. 部署记录 Tab 显示事务历史
6. 配置合并 Tab 显示配置文件来源追踪
7. 生成文件 Tab 显示生成物列表
8. 导入确认对话框显示部署目标路径
9. 路径未配置时显示黄色警告
10. 管理中心关闭后主界面数据自动刷新

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "chore: integration verification for UX optimization"
```

---

## 依赖关系

```
Task 1 (画刷+Tab样式) ──→ Task 2 (Header，使用渐变画刷)
                     └──→ Task 4 (管理中心，使用 Tab 样式)
Task 3 (DeploymentService) → Task 4 (管理中心，部署记录 Tab)
Task 5 (导入确认增强) ─── 独立，无依赖
所有 Task ──────────────→ Task 6 (集成验证)
```

- Task 1 和 Task 3 和 Task 5 无依赖，可并行
- Task 2 依赖 Task 1（导入/启动按钮使用渐变画刷）
- Task 4 依赖 Task 1（ManagementTabStyle）+ Task 3（GetTransactionHistoryAsync）
- Task 6 依赖所有前置 Task

**推荐执行顺序:** Task 1 → Task 2 + Task 3 (并行) → Task 4 → Task 5 → Task 6

---

## 旧窗口处置说明

以下窗口 **保留但不再从 MainWindow Header 直接打开**，DI 注册也保留（其他入口可能仍需要）：
- `Views/RepositoryManagerWindow.xaml/.cs`
- `Views/OverwriteManagerWindow.xaml/.cs`
- `Views/ConfigManagerWindow.xaml/.cs`
