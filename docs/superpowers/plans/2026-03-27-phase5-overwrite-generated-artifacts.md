# Phase 5: Overwrite / Generated Artifacts 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 纳管所有运行时生成的"非原始输入"（合并配置、部署快照、临时修复产物），使其可查看、可清理、可晋升为正式 Package。

**Architecture:** 新增 `GeneratedArtifact` 模型追踪每个生成物的来源、类型和状态。新增 `OverwriteStore` 服务管理生成物的存储目录（`%APPDATA%/UEModManager/Overwrites/{gameName}/`），独立于包仓库。生成物可通过 `PromoteToPackageAsync` 晋升为正式 Package。UI 新增 `OverwriteManagerWindow`（仓库管理面板的生成物变体）。

**Tech Stack:** .NET 8.0 / WPF / C# 12 / CommunityToolkit.Mvvm / Newtonsoft.Json

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `Models/GeneratedArtifact.cs` | 生成物数据模型 + GeneratedArtifactType 枚举 |
| `Services/OverwriteStore.cs` | 生成物存储、索引、清理、晋升 |
| `Views/OverwriteManagerWindow.xaml` | 生成物管理面板 UI（原型风格同 Screen 11） |
| `Views/OverwriteManagerWindow.xaml.cs` | 代码隐藏：列表渲染、清理、晋升交互 |

### Modified Files

| File | Change |
|------|--------|
| `Models/PackageKind.cs` | 添加 `GeneratedArtifactType` 枚举 |
| `Converters/ValueConverters.cs` | 添加 `GeneratedTypeToBrushConverter` |
| `Services/DeploymentService.cs` | 部署后记录生成物到 OverwriteStore |
| `ViewModels/MainViewModel.cs` | 注入 OverwriteStore、公开属性 |
| `App.xaml.cs` | DI 注册 OverwriteStore + OverwriteManagerWindow |
| `MainWindow.xaml` | 添加"生成物管理"按钮（header 区域） |
| `MainWindow.xaml.cs` | 添加按钮点击事件打开 OverwriteManagerWindow |

---

## Task 1: GeneratedArtifact 模型 + 枚举

**Files:**
- Create: `UEModManager/Models/GeneratedArtifact.cs`
- Modify: `UEModManager/Models/PackageKind.cs`

- [ ] **Step 1: 在 PackageKind.cs 末尾添加 GeneratedArtifactType 枚举**

在 `ConflictType` 枚举之后添加：

```csharp
/// <summary>生成物类型。</summary>
public enum GeneratedArtifactType
{
    /// <summary>部署快照 — 部署事务执行后的状态记录。</summary>
    DeploymentSnapshot,
    /// <summary>合并配置 — 多个配置文件合并后的产物（Phase 7 扩展）。</summary>
    MergedConfig,
    /// <summary>工具输出 — 外部工具或脚本生成的文件。</summary>
    ToolOutput,
    /// <summary>缓存 — 运行时缓存文件（如缩略图缓存）。</summary>
    Cache,
    /// <summary>用户修复 — 用户手动放入的临时修复文件。</summary>
    UserFix,
    /// <summary>其他 — 未分类的生成物。</summary>
    Other
}

/// <summary>生成物状态。</summary>
public enum GeneratedArtifactStatus
{
    /// <summary>活跃 — 当前部署中生效。</summary>
    Active,
    /// <summary>过期 — 已被更新的部署覆盖。</summary>
    Stale,
    /// <summary>已晋升 — 已转为正式 Package。</summary>
    Promoted
}
```

- [ ] **Step 2: 创建 GeneratedArtifact.cs**

```csharp
using System;
using System.Collections.Generic;

namespace UEModManager.Models
{
    /// <summary>
    /// 运行时生成物记录。追踪部署过程或工具产生的非原始输入文件。
    /// </summary>
    public class GeneratedArtifact
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>生成物在 Overwrite 目录中的相对路径。</summary>
        public string RelativePath { get; init; } = "";

        /// <summary>部署到游戏目录时的相对目标路径。</summary>
        public string? RelativeTargetPath { get; init; }

        /// <summary>显示名称。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>生成物类型。</summary>
        public GeneratedArtifactType Type { get; init; } = GeneratedArtifactType.Other;

        /// <summary>生成物状态。</summary>
        public GeneratedArtifactStatus Status { get; set; } = GeneratedArtifactStatus.Active;

        /// <summary>来源包的 PackageKey（如果有）。</summary>
        public string? SourcePackageKey { get; init; }

        /// <summary>来源 Profile ID（如果有）。</summary>
        public Guid? SourceProfileId { get; init; }

        /// <summary>来源部署事务 ID（如果有）。</summary>
        public Guid? SourceTransactionId { get; init; }

        /// <summary>来源描述（人类可读）。</summary>
        public string? SourceDescription { get; init; }

        /// <summary>文件大小（字节）。</summary>
        public long FileSize { get; init; }

        /// <summary>文件哈希（SHA-256 前 16 字符）。</summary>
        public string? FileHash { get; init; }

        /// <summary>创建时间。</summary>
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>最后修改时间。</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>所属游戏名称。</summary>
        public string HostGameName { get; init; } = "";

        /// <summary>格式化文件大小。</summary>
        public string FormattedSize => FormatFileSize(FileSize);

        /// <summary>来源摘要文本（UI 用）。</summary>
        public string SourceSummary =>
            SourceDescription
            ?? (SourcePackageKey != null ? $"来自 {SourcePackageKey}" : "手动添加");

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build UEModManager.sln --configuration Debug`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add UEModManager/Models/GeneratedArtifact.cs UEModManager/Models/PackageKind.cs
git commit -m "feat(phase5): add GeneratedArtifact model and enums"
```

---

## Task 2: OverwriteStore 服务

**Files:**
- Create: `UEModManager/Services/OverwriteStore.cs`

- [ ] **Step 1: 创建 OverwriteStore.cs**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 生成物仓库。管理部署过程和工具产生的非原始输入文件。
    /// 存储路径：%APPDATA%/UEModManager/Overwrites/{gameName}/
    /// 索引文件：Data/{gameName}_overwrites.json
    /// </summary>
    public class OverwriteStore
    {
        private readonly ILogger<OverwriteStore> _logger;
        private readonly PackageRepository _packageRepo;
        private readonly PackageImportService _packageImport;
        private readonly string _dataDirectory;
        private string _overwriteRoot;
        private string _currentGame = "";
        private List<GeneratedArtifact> _artifacts = [];

        /// <summary>生成物列表发生变化时触发。</summary>
        public event Action? ArtifactsChanged;

        public OverwriteStore(
            ILogger<OverwriteStore> logger,
            PackageRepository packageRepo,
            PackageImportService packageImport)
        {
            _logger = logger;
            _packageRepo = packageRepo;
            _packageImport = packageImport;

            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            Directory.CreateDirectory(_dataDirectory);

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _overwriteRoot = Path.Combine(appData, "UEModManager", "Overwrites");
            Directory.CreateDirectory(_overwriteRoot);
        }

        /// <summary>Overwrite 存储根目录。</summary>
        public string OverwriteRoot => _overwriteRoot;

        // ─── 初始化 ───

        /// <summary>切换当前游戏并加载生成物索引。</summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGame = gameName;
            var gameDir = Path.Combine(_overwriteRoot, gameName);
            Directory.CreateDirectory(gameDir);
            await LoadIndexAsync();
        }

        // ─── 查询 ───

        /// <summary>获取所有生成物。</summary>
        public IReadOnlyList<GeneratedArtifact> GetAll() => _artifacts.AsReadOnly();

        /// <summary>按类型筛选。</summary>
        public IReadOnlyList<GeneratedArtifact> GetByType(GeneratedArtifactType type)
            => _artifacts.Where(a => a.Type == type).ToList();

        /// <summary>按状态筛选。</summary>
        public IReadOnlyList<GeneratedArtifact> GetByStatus(GeneratedArtifactStatus status)
            => _artifacts.Where(a => a.Status == status).ToList();

        /// <summary>按来源包筛选。</summary>
        public IReadOnlyList<GeneratedArtifact> GetBySourcePackage(string packageKey)
            => _artifacts.Where(a => a.SourcePackageKey == packageKey).ToList();

        /// <summary>获取活跃生成物数量。</summary>
        public int ActiveCount => _artifacts.Count(a => a.Status == GeneratedArtifactStatus.Active);

        /// <summary>获取总占用空间。</summary>
        public long TotalSize => _artifacts.Sum(a => a.FileSize);

        /// <summary>获取过期生成物占用空间。</summary>
        public long StaleSize => _artifacts
            .Where(a => a.Status == GeneratedArtifactStatus.Stale)
            .Sum(a => a.FileSize);

        // ─── 注册生成物 ───

        /// <summary>
        /// 注册一个新的生成物。文件必须已存在于 Overwrite 目录中。
        /// </summary>
        public async Task<GeneratedArtifact> RegisterAsync(
            string filePath,
            GeneratedArtifactType type,
            string displayName,
            string? sourcePackageKey = null,
            Guid? sourceProfileId = null,
            Guid? sourceTransactionId = null,
            string? sourceDescription = null,
            string? relativeTargetPath = null)
        {
            var gameDir = Path.Combine(_overwriteRoot, _currentGame);

            // 如果文件不在 Overwrite 目录中，复制进来
            string relativePath;
            if (!filePath.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(filePath);
                var typeFolder = type.ToString().ToLowerInvariant();
                var destDir = Path.Combine(gameDir, typeFolder);
                Directory.CreateDirectory(destDir);
                var destPath = Path.Combine(destDir, fileName);

                // 避免重名
                var counter = 1;
                while (File.Exists(destPath))
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
                    var ext = Path.GetExtension(fileName);
                    destPath = Path.Combine(destDir, $"{nameNoExt}_{counter++}{ext}");
                }

                File.Copy(filePath, destPath);
                relativePath = Path.GetRelativePath(gameDir, destPath);
            }
            else
            {
                relativePath = Path.GetRelativePath(gameDir, filePath);
            }

            var fullPath = Path.Combine(gameDir, relativePath);
            var fi = new FileInfo(fullPath);

            var artifact = new GeneratedArtifact
            {
                RelativePath = relativePath,
                RelativeTargetPath = relativeTargetPath,
                DisplayName = displayName,
                Type = type,
                Status = GeneratedArtifactStatus.Active,
                SourcePackageKey = sourcePackageKey,
                SourceProfileId = sourceProfileId,
                SourceTransactionId = sourceTransactionId,
                SourceDescription = sourceDescription,
                FileSize = fi.Exists ? fi.Length : 0,
                FileHash = fi.Exists ? await ComputeHashAsync(fullPath) : null,
                HostGameName = _currentGame
            };

            _artifacts.Add(artifact);
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();

            _logger.LogInformation("Registered generated artifact: {Name} ({Type})", displayName, type);
            return artifact;
        }

        // ─── 删除 ───

        /// <summary>删除单个生成物（文件 + 索引）。</summary>
        public async Task DeleteAsync(Guid artifactId)
        {
            var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
            if (artifact == null) return;

            var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogInformation("Deleted overwrite file: {Path}", fullPath);
            }

            _artifacts.Remove(artifact);
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();
        }

        /// <summary>清理所有过期生成物。</summary>
        public async Task<int> CleanupStaleAsync()
        {
            var stale = _artifacts.Where(a => a.Status == GeneratedArtifactStatus.Stale).ToList();
            foreach (var artifact in stale)
            {
                var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                _artifacts.Remove(artifact);
            }

            if (stale.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
                _logger.LogInformation("Cleaned up {Count} stale artifacts", stale.Count);
            }
            return stale.Count;
        }

        /// <summary>清理某个包的所有生成物。</summary>
        public async Task CleanupByPackageAsync(string packageKey)
        {
            var toRemove = _artifacts.Where(a => a.SourcePackageKey == packageKey).ToList();
            foreach (var artifact in toRemove)
            {
                var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                _artifacts.Remove(artifact);
            }

            if (toRemove.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
            }
        }

        /// <summary>清理某个 Profile 的所有生成物。</summary>
        public async Task CleanupByProfileAsync(Guid profileId)
        {
            var toRemove = _artifacts.Where(a => a.SourceProfileId == profileId).ToList();
            foreach (var artifact in toRemove)
            {
                var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                _artifacts.Remove(artifact);
            }

            if (toRemove.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
            }
        }

        // ─── 晋升为正式 Package ───

        /// <summary>
        /// 将生成物晋升为正式 Package（复制到仓库 + 注册）。
        /// </summary>
        public async Task<Package?> PromoteToPackageAsync(Guid artifactId, string? packageDisplayName = null)
        {
            var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
            if (artifact == null) return null;

            var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("Cannot promote artifact {Id}: file not found at {Path}", artifactId, fullPath);
                return null;
            }

            // 通过 PackageImportService 导入
            var results = await _packageImport.ImportAsync([fullPath]);
            if (results.Count == 0 || !results[0].Success || results[0].Package == null)
            {
                _logger.LogWarning("Cannot promote artifact {Id}: import failed", artifactId);
                return null;
            }

            // 更新显示名
            var pkg = results[0].Package;
            if (packageDisplayName != null)
            {
                pkg.DisplayName = packageDisplayName;
                await _packageRepo.UpdatePackageAsync(pkg);
            }

            // 标记为已晋升
            artifact.Status = GeneratedArtifactStatus.Promoted;
            artifact.LastModified = DateTime.Now;
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();

            _logger.LogInformation("Promoted artifact {Name} to package {Key}", artifact.DisplayName, results[0].PackageKey);
            return pkg;
        }

        // ─── 状态管理 ───

        /// <summary>将生成物标记为过期。</summary>
        public async Task MarkStaleAsync(Guid artifactId)
        {
            var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
            if (artifact == null) return;
            artifact.Status = GeneratedArtifactStatus.Stale;
            artifact.LastModified = DateTime.Now;
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();
        }

        /// <summary>将某个部署事务的所有生成物标记为过期。</summary>
        public async Task MarkTransactionStaleAsync(Guid transactionId)
        {
            var affected = _artifacts
                .Where(a => a.SourceTransactionId == transactionId && a.Status == GeneratedArtifactStatus.Active)
                .ToList();
            foreach (var a in affected)
            {
                a.Status = GeneratedArtifactStatus.Stale;
                a.LastModified = DateTime.Now;
            }
            if (affected.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
            }
        }

        // ─── 持久化 ───

        private string GetIndexPath() => Path.Combine(_dataDirectory, $"{_currentGame}_overwrites.json");

        private async Task LoadIndexAsync()
        {
            var path = GetIndexPath();
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                _artifacts = JsonConvert.DeserializeObject<List<GeneratedArtifact>>(json) ?? [];
                _logger.LogInformation("Loaded {Count} overwrite artifacts for {Game}", _artifacts.Count, _currentGame);
            }
            else
            {
                _artifacts = [];
            }
        }

        private async Task SaveIndexAsync()
        {
            var path = GetIndexPath();
            var json = JsonConvert.SerializeObject(_artifacts, Formatting.Indented);
            await File.WriteAllTextAsync(path, json);
        }

        // ─── 哈希 ───

        private static async Task<string> ComputeHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            var hash = await SHA256.HashDataAsync(stream);
            return Convert.ToHexString(hash)[..16];
        }
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build UEModManager.sln --configuration Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add UEModManager/Services/OverwriteStore.cs
git commit -m "feat(phase5): add OverwriteStore service with CRUD, cleanup, and promote-to-package"
```

---

## Task 3: 转换器 + DI 注册 + MainViewModel 集成

**Files:**
- Modify: `UEModManager/Converters/ValueConverters.cs`
- Modify: `UEModManager/App.xaml.cs`
- Modify: `UEModManager/ViewModels/MainViewModel.cs`

- [ ] **Step 1: 在 ValueConverters.cs 末尾添加 GeneratedTypeToBrushConverter**

在 `BoolToEnabledColorConverter` 之后、`MultiBooleanOrConverter` 之前添加：

```csharp
/// <summary>GeneratedArtifactType → 前景色画刷。</summary>
public class GeneratedTypeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GeneratedArtifactType type) return Brushes.Gray;
        return type switch
        {
            GeneratedArtifactType.DeploymentSnapshot => new SolidColorBrush(Color.FromRgb(0x06, 0xb6, 0xd4)),
            GeneratedArtifactType.MergedConfig => new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)),
            GeneratedArtifactType.ToolOutput => new SolidColorBrush(Color.FromRgb(0xa8, 0x55, 0xf7)),
            GeneratedArtifactType.Cache => new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7a)),
            GeneratedArtifactType.UserFix => new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            _ => Brushes.Gray
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>GeneratedArtifactStatus → 状态色画刷。</summary>
public class GeneratedStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GeneratedArtifactStatus status) return Brushes.Gray;
        return status switch
        {
            GeneratedArtifactStatus.Active => new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            GeneratedArtifactStatus.Stale => new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)),
            GeneratedArtifactStatus.Promoted => new SolidColorBrush(Color.FromRgb(0x06, 0xb6, 0xd4)),
            _ => Brushes.Gray
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

添加必要的 using：`using UEModManager.Models;`（如尚未存在）。

- [ ] **Step 2: 在 App.xaml.cs 的 DI 注册中添加 OverwriteStore**

在 `services.AddSingleton<ConflictAnalyzer>();` 之后添加：

```csharp
services.AddSingleton<OverwriteStore>();
```

在窗口注册区域添加：

```csharp
services.AddTransient<Views.OverwriteManagerWindow>();
```

- [ ] **Step 3: 在 MainViewModel 中注入 OverwriteStore**

构造函数参数添加 `OverwriteStore overwriteStore`，字段 `private readonly OverwriteStore _overwriteStore;`，公开属性：

```csharp
public OverwriteStore OverwriteStore => _overwriteStore;
```

在 `InitializeAsync()` 中，冲突分析器初始化之后添加：

```csharp
await _overwriteStore.SetCurrentGameAsync(_currentGame);
```

在 `SwitchGameAsync()` 中添加同样的调用。

- [ ] **Step 4: 编译验证**

Run: `dotnet build UEModManager.sln --configuration Debug`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add UEModManager/Converters/ValueConverters.cs UEModManager/App.xaml.cs UEModManager/ViewModels/MainViewModel.cs
git commit -m "feat(phase5): integrate OverwriteStore into DI and MainViewModel"
```

---

## Task 4: OverwriteManagerWindow UI

**Files:**
- Create: `UEModManager/Views/OverwriteManagerWindow.xaml`
- Create: `UEModManager/Views/OverwriteManagerWindow.xaml.cs`

- [ ] **Step 1: 创建 OverwriteManagerWindow.xaml**

UI 风格对齐 RepositoryManagerWindow（Screen 11 变体）：统计卡片 + 生成物列表 + 底部操作按钮。

```xml
<Window x:Class="UEModManager.Views.OverwriteManagerWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="生成物管理" Height="560" Width="540"
        WindowStartupLocation="CenterOwner"
        Style="{StaticResource CyberModalWindow}">

    <Window.CommandBindings>
        <CommandBinding Command="{x:Static SystemCommands.CloseWindowCommand}" Executed="OnCloseWindow"/>
    </Window.CommandBindings>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- 标题 -->
        <Border Grid.Row="0" Padding="24,20,24,12">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="&#xE74C;" FontFamily="Segoe MDL2 Assets" FontSize="18"
                           Foreground="{StaticResource PrimaryBrush}" Margin="0,0,10,0"
                           VerticalAlignment="Center"/>
                <TextBlock Text="生成物管理" FontSize="17" FontWeight="SemiBold"
                           Foreground="{StaticResource Text100Brush}" VerticalAlignment="Center"/>
            </StackPanel>
        </Border>

        <!-- 统计卡片 -->
        <Grid Grid.Row="1" Margin="24,0,24,16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="8"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="8"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- 活跃 -->
            <Border Grid.Column="0" Background="#1822c55e" CornerRadius="{StaticResource RadiusSm}"
                    Padding="12,10">
                <StackPanel>
                    <TextBlock x:Name="ActiveCountText" Text="0" FontSize="18" FontWeight="Bold"
                               Foreground="#22c55e"/>
                    <TextBlock Text="活跃" FontSize="10" Foreground="{StaticResource Text500Brush}"
                               Margin="0,2,0,0"/>
                </StackPanel>
            </Border>

            <!-- 过期 -->
            <Border Grid.Column="2" Background="#18f59e0b" CornerRadius="{StaticResource RadiusSm}"
                    Padding="12,10">
                <StackPanel>
                    <TextBlock x:Name="StaleCountText" Text="0" FontSize="18" FontWeight="Bold"
                               Foreground="#f59e0b"/>
                    <TextBlock Text="过期" FontSize="10" Foreground="{StaticResource Text500Brush}"
                               Margin="0,2,0,0"/>
                </StackPanel>
            </Border>

            <!-- 总占用 -->
            <Border Grid.Column="4" Background="{StaticResource SurfaceBrush}"
                    BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                    CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
                <StackPanel>
                    <TextBlock x:Name="TotalSizeText" Text="0 B" FontSize="18" FontWeight="Bold"
                               Foreground="{StaticResource Text100Brush}"/>
                    <TextBlock Text="总占用" FontSize="10" Foreground="{StaticResource Text500Brush}"
                               Margin="0,2,0,0"/>
                </StackPanel>
            </Border>
        </Grid>

        <!-- 生成物列表 -->
        <Border Grid.Row="2" Margin="24,0,24,0"
                Background="{StaticResource SurfaceBrush}"
                BorderBrush="{StaticResource CyberBorderBrush}" BorderThickness="1"
                CornerRadius="{StaticResource RadiusSm}">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>

                <Border Grid.Row="0" Padding="14,8" BorderBrush="{StaticResource CyberBorderBrush}"
                        BorderThickness="0,0,0,1">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="60"/>
                            <ColumnDefinition Width="60"/>
                            <ColumnDefinition Width="50"/>
                            <ColumnDefinition Width="60"/>
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="名称" FontSize="11"
                                   Foreground="{StaticResource Text500Brush}"/>
                        <TextBlock Grid.Column="1" Text="类型" FontSize="11"
                                   Foreground="{StaticResource Text500Brush}" TextAlignment="Center"/>
                        <TextBlock Grid.Column="2" Text="大小" FontSize="11"
                                   Foreground="{StaticResource Text500Brush}" TextAlignment="Right"/>
                        <TextBlock Grid.Column="3" Text="状态" FontSize="11"
                                   Foreground="{StaticResource Text500Brush}" TextAlignment="Center"/>
                        <TextBlock Grid.Column="4" Text="操作" FontSize="11"
                                   Foreground="{StaticResource Text500Brush}" TextAlignment="Center"/>
                    </Grid>
                </Border>

                <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
                    <StackPanel x:Name="ArtifactListPanel"/>
                </ScrollViewer>

                <!-- 空状态 -->
                <TextBlock x:Name="EmptyState" Grid.Row="1" Text="暂无生成物"
                           FontSize="14" Foreground="{StaticResource Text500Brush}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"
                           Visibility="Collapsed"/>
            </Grid>
        </Border>

        <!-- 底部按钮 -->
        <Border Grid.Row="3" Padding="24,16,24,20">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <Button Grid.Column="0" Click="AddUserFix_Click"
                        Style="{StaticResource CyberSecondaryButton}"
                        Height="36" Padding="12,0">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="&#xE710;" FontFamily="Segoe MDL2 Assets" FontSize="11"
                                   Margin="0,0,6,0" VerticalAlignment="Center"/>
                        <TextBlock Text="添加修复文件" FontSize="12"/>
                    </StackPanel>
                </Button>

                <Button Grid.Column="2" x:Name="CleanupButton" Click="CleanupStale_Click"
                        Style="{StaticResource CyberPrimaryButton}"
                        Height="38" Padding="14,0" Cursor="Hand">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="&#xE74D;" FontFamily="Segoe MDL2 Assets" FontSize="11"
                                   Margin="0,0,6,0" VerticalAlignment="Center"/>
                        <TextBlock x:Name="CleanupButtonText" Text="清理过期" FontSize="13"/>
                    </StackPanel>
                </Button>
            </Grid>
        </Border>
    </Grid>
</Window>
```

- [ ] **Step 2: 创建 OverwriteManagerWindow.xaml.cs**

```csharp
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class OverwriteManagerWindow : Window
    {
        private readonly OverwriteStore _overwriteStore;
        private readonly PackageRepository _packageRepo;

        public OverwriteManagerWindow(OverwriteStore overwriteStore, PackageRepository packageRepo)
        {
            InitializeComponent();
            _overwriteStore = overwriteStore;
            _packageRepo = packageRepo;
            Loaded += (_, _) => RefreshUI();
        }

        private void RefreshUI()
        {
            var all = _overwriteStore.GetAll();
            var active = all.Count(a => a.Status == GeneratedArtifactStatus.Active);
            var stale = all.Count(a => a.Status == GeneratedArtifactStatus.Stale);

            ActiveCountText.Text = active.ToString();
            StaleCountText.Text = stale.ToString();
            TotalSizeText.Text = FormatSize(_overwriteStore.TotalSize);

            var staleSize = _overwriteStore.StaleSize;
            CleanupButtonText.Text = staleSize > 0
                ? $"清理过期 ({FormatSize(staleSize)})"
                : "清理过期";

            ArtifactListPanel.Children.Clear();
            EmptyState.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var artifact in all.OrderByDescending(a => a.CreatedAt))
            {
                AddArtifactRow(artifact);
            }
        }

        private void AddArtifactRow(GeneratedArtifact artifact)
        {
            var border = new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                BorderBrush = (Brush)FindResource("CyberBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 0.5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            // 名称 + 来源
            var nameStack = new StackPanel();
            nameStack.Children.Add(new TextBlock
            {
                Text = artifact.DisplayName,
                FontSize = 13,
                Foreground = (Brush)FindResource("Text200Brush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            nameStack.Children.Add(new TextBlock
            {
                Text = artifact.SourceSummary,
                FontSize = 10,
                Foreground = (Brush)FindResource("Text500Brush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(nameStack, 0);
            grid.Children.Add(nameStack);

            // 类型标签
            var typeLabel = artifact.Type switch
            {
                GeneratedArtifactType.DeploymentSnapshot => "快照",
                GeneratedArtifactType.MergedConfig => "合并",
                GeneratedArtifactType.ToolOutput => "工具",
                GeneratedArtifactType.Cache => "缓存",
                GeneratedArtifactType.UserFix => "修复",
                _ => "其他"
            };
            var typeColor = artifact.Type switch
            {
                GeneratedArtifactType.DeploymentSnapshot => Color.FromRgb(0x06, 0xb6, 0xd4),
                GeneratedArtifactType.MergedConfig => Color.FromRgb(0xf5, 0x9e, 0x0b),
                GeneratedArtifactType.UserFix => Color.FromRgb(0x22, 0xc5, 0x5e),
                GeneratedArtifactType.ToolOutput => Color.FromRgb(0xa8, 0x55, 0xf7),
                _ => Color.FromRgb(0x71, 0x71, 0x7a)
            };
            var typeBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, typeColor.R, typeColor.G, typeColor.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = typeLabel,
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(typeColor)
                }
            };
            Grid.SetColumn(typeBadge, 1);
            grid.Children.Add(typeBadge);

            // 大小
            var size = new TextBlock
            {
                Text = artifact.FormattedSize,
                FontSize = 11,
                Foreground = (Brush)FindResource("Text400Brush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            // 状态
            var (statusLabel, statusColor) = artifact.Status switch
            {
                GeneratedArtifactStatus.Active => ("活跃", Color.FromRgb(0x22, 0xc5, 0x5e)),
                GeneratedArtifactStatus.Stale => ("过期", Color.FromRgb(0xf5, 0x9e, 0x0b)),
                GeneratedArtifactStatus.Promoted => ("已晋升", Color.FromRgb(0x06, 0xb6, 0xd4)),
                _ => ("未知", Color.FromRgb(0x71, 0x71, 0x7a))
            };
            var statusBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, statusColor.R, statusColor.G, statusColor.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = statusLabel,
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(statusColor)
                }
            };
            Grid.SetColumn(statusBadge, 3);
            grid.Children.Add(statusBadge);

            // 操作按钮（晋升 / 删除）
            var actionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (artifact.Status == GeneratedArtifactStatus.Active)
            {
                var promoteBtn = new Button
                {
                    Content = "↑",
                    ToolTip = "晋升为正式包",
                    Style = (Style)FindResource("CyberGhostButton"),
                    Width = 24, Height = 24,
                    FontSize = 12,
                    Tag = artifact.Id
                };
                promoteBtn.Click += PromoteArtifact_Click;
                actionPanel.Children.Add(promoteBtn);
            }

            var deleteBtn = new Button
            {
                Content = "✕",
                ToolTip = "删除",
                Style = (Style)FindResource("CyberGhostButton"),
                Width = 24, Height = 24,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)),
                Tag = artifact.Id
            };
            deleteBtn.Click += DeleteArtifact_Click;
            actionPanel.Children.Add(deleteBtn);

            Grid.SetColumn(actionPanel, 4);
            grid.Children.Add(actionPanel);

            border.Child = grid;
            ArtifactListPanel.Children.Add(border);
        }

        // ─── 事件处理 ───

        private async void PromoteArtifact_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Guid id }) return;
            var artifact = _overwriteStore.GetAll().FirstOrDefault(a => a.Id == id);
            if (artifact == null) return;

            var result = CyberMessageBox.Show(this,
                $"将「{artifact.DisplayName}」晋升为正式包？\n晋升后将复制到包仓库并可被方案引用。",
                "晋升为正式包", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var pkg = await _overwriteStore.PromoteToPackageAsync(id, artifact.DisplayName);
                if (pkg != null)
                    CyberMessageBox.Show(this, $"已成功晋升为包「{pkg.DisplayName}」", "成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    CyberMessageBox.Show(this, "晋升失败，请检查文件是否完整", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshUI();
            }
        }

        private async void DeleteArtifact_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Guid id }) return;
            await _overwriteStore.DeleteAsync(id);
            RefreshUI();
        }

        private async void CleanupStale_Click(object sender, RoutedEventArgs e)
        {
            var staleSize = _overwriteStore.StaleSize;
            if (staleSize == 0)
            {
                CyberMessageBox.Show(this, "没有过期的生成物", "清理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = CyberMessageBox.Show(this,
                $"将清理所有过期生成物，释放 {FormatSize(staleSize)}。\n此操作不可撤销，确认继续？",
                "清理过期生成物", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var count = await _overwriteStore.CleanupStaleAsync();
                CyberMessageBox.Show(this, $"已清理 {count} 个过期生成物", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshUI();
            }
        }

        private async void AddUserFix_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择修复文件",
                Filter = "所有文件|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (var file in dlg.FileNames)
            {
                var name = System.IO.Path.GetFileName(file);
                await _overwriteStore.RegisterAsync(
                    file,
                    GeneratedArtifactType.UserFix,
                    name,
                    sourceDescription: "用户手动添加");
            }
            RefreshUI();
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
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build UEModManager.sln --configuration Debug`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add UEModManager/Views/OverwriteManagerWindow.xaml UEModManager/Views/OverwriteManagerWindow.xaml.cs
git commit -m "feat(phase5): add OverwriteManagerWindow UI with list, promote, cleanup"
```

---

## Task 5: MainWindow 集成 — 添加生成物管理入口

**Files:**
- Modify: `UEModManager/MainWindow.xaml`
- Modify: `UEModManager/MainWindow.xaml.cs`

- [ ] **Step 1: 在 MainWindow.xaml 的 header 区域添加生成物管理按钮**

在仓库管理按钮（`RepositoryManager_Click`）附近添加一个"生成物"按钮，使用相同的样式模式。具体位置参考 header 区域现有按钮布局。

- [ ] **Step 2: 在 MainWindow.xaml.cs 添加点击事件**

```csharp
/// <summary>v2.0 生成物管理面板。</summary>
private void OverwriteManager_Click(object sender, MouseButtonEventArgs e)
{
    e.Handled = true;
    var win = new Views.OverwriteManagerWindow(_vm.OverwriteStore, _vm.PackageRepo) { Owner = this };
    win.ShowDialog();
}
```

注意：与 RepositoryManagerWindow 使用相同的 `new` + 手动传参模式（项目惯例）。

- [ ] **Step 3: 编译验证**

Run: `dotnet build UEModManager.sln --configuration Debug`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add UEModManager/MainWindow.xaml UEModManager/MainWindow.xaml.cs
git commit -m "feat(phase5): add overwrite manager button to main window header"
```

---

## Task 6: DeploymentService 集成 — 部署后自动注册生成物

**Files:**
- Modify: `UEModManager/Services/DeploymentService.cs`

- [ ] **Step 1: 在 DeploymentService 构造函数中注入 OverwriteStore**

添加字段 `private readonly OverwriteStore _overwriteStore;` 和构造函数参数（必需依赖，因为已注册为 Singleton）。

- [ ] **Step 2: 在 ExecuteAsync 的事务提交成功后，为备份的文件注册生成物**

在事务 `Status = Committed` 之后，遍历已执行的 Replace 操作，为其备份文件注册 DeploymentSnapshot 类型的生成物：

```csharp
if (transaction.Status == DeploymentStatus.Committed)
{
    foreach (var op in transaction.ExecutedOperations.Where(o => o.BackupPath != null))
    {
        try
        {
            await _overwriteStore.RegisterAsync(
                op.BackupPath!,
                GeneratedArtifactType.DeploymentSnapshot,
                $"备份: {op.RelativeTargetPath}",
                sourcePackageKey: op.PackageKey,
                sourceTransactionId: transaction.Id,
                sourceDescription: $"部署事务 {transaction.Id:N8} 的备份");
        }
        catch { /* 注册失败不影响部署 */ }
    }
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build UEModManager.sln --configuration Debug`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add UEModManager/Services/DeploymentService.cs
git commit -m "feat(phase5): auto-register deployment backups as generated artifacts"
```

---

## Task 7: 更新开发者文档

**Files:**
- Modify: `v2.0升级指南_开发者接手文档.md`

- [ ] **Step 1: 更新文档**

1. 将 Phase 2/3/4 UI 状态从 ⬜ 更新为 ✅
2. 添加 Phase 5 章节（模型/服务/UI 产出清单）
3. 更新快速检查清单
4. 更新附录 A 入口速查表

- [ ] **Step 2: Commit**

```bash
git add "v2.0升级指南_开发者接手文档.md"
git commit -m "docs: update handoff doc with Phase 2-4 UI completion and Phase 5 details"
```
