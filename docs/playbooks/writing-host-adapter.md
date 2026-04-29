# 编写自定义 Host Adapter

**适用场景：** 给一个新游戏（不属于已有 9 款 UE 游戏）添加支持。

---

## 30 秒理解

`IHostAdapter` 是宿主规则的统一接口。要支持一个新游戏，**首选写一个新 Adapter 类**，
而不是改核心代码加 if-else 分支。

3 种典型类型：
- **Unreal 系**：继承或参考 `UnrealEngineAdapter`
- **其他引擎**：仿照 `GenericFileOverlayAdapter` 自己实现 `IHostAdapter`
- **特殊变种**：继承现有 Adapter 重写少数方法（如 `StellarBladeCNSAdapter` 继承 UE）

---

## IHostAdapter 接口契约

```csharp
public interface IHostAdapter
{
    // 标识
    string AdapterKey { get; }                          // "my-engine"
    string DisplayName { get; }                          // "My Engine 适配器"
    EngineType EngineType { get; }                       // 自有引擎选 EngineType.Unknown
    AdapterCapabilities Capabilities { get; }            // 能力矩阵

    // 文件识别
    IReadOnlySet<string> ModFileExtensions { get; }      // {".mod", ".pak"}
    IReadOnlySet<string> DirectImportExtensions { get; } // {".mod"}
    string FileDialogFilter { get; }                     // "MOD|*.mod;*.pak"
    IReadOnlyList<string> DefaultModPathPatterns { get; }// {"Mods", "Content/Paks/~mods"}
    IReadOnlyList<string> GroupPriorityExtensions { get; }// 主扩展名优先

    // 选择 / 检测
    bool CanHandle(string gameName, EngineType? engineType = null);
    IReadOnlyList<string> GetSearchKeywords(string gameName);
    IReadOnlyList<string> GetExecutableKeywords(string gameName);

    // 路径
    string GetDeployTargetPath(string gameRootPath, string gameName);
    string GetBackupPath(string gameRootPath, string gameName);

    // 扫描过滤
    bool ShouldSkipDirectory(string directoryName, string gameName);

    // 部署偏好
    DeploymentBackendType RecommendedBackend { get; }
}
```

---

## 完整模板

`UEModManager/Adapters/MyEngineAdapter.cs`：

```csharp
using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Adapters
{
    public class MyEngineAdapter : IHostAdapter
    {
        public string AdapterKey => "my-engine";
        public string DisplayName => "My Engine 适配器";
        public EngineType EngineType => EngineType.Unknown;

        public AdapterCapabilities Capabilities => new()
        {
            SupportsConflictDetection = true,
            SupportsConfigMerge = false,
            SupportsPlugins = false,
            SupportsHardLink = true,
            SupportsSymlink = true,
            SupportsAutoDetect = true,
        };

        public IReadOnlySet<string> ModFileExtensions =>
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { ".mod", ".pak" };

        public IReadOnlySet<string> DirectImportExtensions =>
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { ".mod" };

        public string FileDialogFilter =>
            "MyEngine MOD|*.mod;*.pak|压缩包|*.zip;*.rar;*.7z|所有文件|*.*";

        public IReadOnlyList<string> DefaultModPathPatterns =>
            new[] { "Mods", "MyGame/Content/Mods" };

        public IReadOnlyList<string> GroupPriorityExtensions =>
            new[] { ".mod" };

        public bool CanHandle(string gameName, EngineType? engineType = null)
        {
            // 严格匹配游戏名（或按 EngineType 过滤）
            return gameName?.Contains("MyGame", System.StringComparison.OrdinalIgnoreCase) == true;
        }

        public IReadOnlyList<string> GetSearchKeywords(string gameName)
            => new[] { gameName, "MyGame" };

        public IReadOnlyList<string> GetExecutableKeywords(string gameName)
            => new[] { "MyGame.exe", "MyGameLauncher.exe" };

        public string GetDeployTargetPath(string gameRootPath, string gameName)
            => System.IO.Path.Combine(gameRootPath, "Mods");

        public string GetBackupPath(string gameRootPath, string gameName)
            => System.IO.Path.Combine(gameRootPath, ".UEModManagerBackup");

        public bool ShouldSkipDirectory(string directoryName, string gameName)
            => directoryName.StartsWith(".", System.StringComparison.Ordinal);

        public DeploymentBackendType RecommendedBackend
            => DeploymentBackendType.Copy;
    }
}
```

---

## 注册到 DI

`UEModManager/App.xaml.cs` 的 DI 注册段：

```csharp
services.AddSingleton<IHostAdapter, Adapters.MyEngineAdapter>();
```

`HostAdapterRegistry` 自动收集所有 `IHostAdapter` 实现，无需额外注册。

---

## Registry 解析顺序

`HostAdapterRegistry.Resolve(gameName, engineType)` 按以下顺序查找：

1. 遍历所有 adapter，调用 `CanHandle(gameName, engineType)` 找首个返回 true 的
2. 若无匹配，按 `EngineType` 找（适配器自报的 EngineType 与请求一致）
3. 若仍无匹配，fallback 到 `GenericFileOverlayAdapter`

要确保你的 Adapter 被选中：
- `CanHandle` 返回 true 用最严格的判断（避免误抢其他游戏）
- `EngineType` 设置为你支持的引擎类型

---

## 测试

测试项目 `UEModManager.Core.Tests` 的 TFM 是 `net8.0-windows`，能引用主项目类型。
但建议把 Adapter 类型保持简单（无 WPF 依赖），将来可能下沉到 Core。

```csharp
public class MyEngineAdapterTests
{
    private readonly MyEngineAdapter _adapter = new();

    [Fact]
    public void GetDeployTargetPath_PutsModsUnderGameRoot()
    {
        var result = _adapter.GetDeployTargetPath("C:/Games/MyGame", "MyGame");
        Assert.Equal(Path.Combine("C:/Games/MyGame", "Mods"), result);
    }

    [Fact]
    public void CanHandle_OnlyAcceptsMyGame()
    {
        Assert.True(_adapter.CanHandle("MyGame Demo"));
        Assert.False(_adapter.CanHandle("Other Game"));
    }
}
```

---

## 已有 Adapter 速查

| Adapter | 用途 |
|---------|------|
| `UnrealEngineAdapter` | UE 通用：剑星、黑神话悟空、明末等 9 款 |
| `StellarBladeCNSAdapter` | 剑星 CNS 模式专用（继承 UE） |
| `GenericFileOverlayAdapter` | 兜底：未识别游戏的简单文件覆盖 |

---

## 参考代码

- `UEModManager/Adapters/IHostAdapter.cs` — 接口定义
- `UEModManager/Adapters/AdapterCapabilities.cs` — 能力矩阵
- `UEModManager/Adapters/UnrealEngineAdapter.cs` — UE 完整实现示例
- `UEModManager/Adapters/HostAdapterRegistry.cs` — 解析规则

---

## 反模式（不要做）

- ❌ 在 `MainWindow.xaml.cs` 加 `if (gameName == "MyGame") ...` —— 应该写 Adapter
- ❌ 在 `GameConfigService.cs` 加新游戏的硬编码路径 —— 应该写 Adapter
- ❌ 直接修改 `UnrealEngineAdapter` 加你的游戏特殊处理 —— 应该继承或新建
- ❌ 在 Adapter 里写 IO（读文件、扫目录）—— 让 Adapter 只提供"规则"，IO 由 Service 做
