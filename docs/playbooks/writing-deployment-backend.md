# 编写自定义部署后端 / Deployment Backend

**适用场景：** 你想加一种新的"把仓库文件呈现到游戏目录"的方式（除现有 Copy/HardLink/Symlink）。
例如：进程级 VFS、WinFsp 挂载、Junction、Mirror Copy 等。

---

## 30 秒理解

`IDeploymentBackend` 是部署后端的统一接口。每种后端封装"如何把单个文件从仓库部署到游戏目录"。
DeploymentService 选择后端 → 按部署计划逐项调用。

3 种现有后端：
- **`CopyBackend`**：直接复制文件（最安全、最慢、最占空间）
- **`HardLinkBackend`**：硬链接（同卷限制，跨卷自动降级 Copy）
- **`SymlinkBackend`**：符号链接（需管理员或开发者模式）

---

## IDeploymentBackend 接口

```csharp
public interface IDeploymentBackend
{
    DeploymentBackendType BackendType { get; }       // 你的后端类型枚举
    string DisplayName { get; }                       // UI 展示用

    /// <summary>当前环境是否能用此后端（如 Symlink 检查权限）。</summary>
    Task<bool> CanUseAsync();

    /// <summary>从源文件部署到目标位置（覆盖已存在）。</summary>
    Task DeployFileAsync(string sourcePath, string targetPath);

    /// <summary>移除目标文件（如果存在）。</summary>
    Task RemoveFileAsync(string targetPath);
}
```

实现位置：`UEModManager/Services/Backends/`（接口本身在 Core：`UEModManager.Core/Services/Backends/IDeploymentBackend.cs`）。

---

## 参考实现（独立可编译 Sample）

[`samples/UEModManager.SampleBackend/`](../../samples/UEModManager.SampleBackend/) 是一个最小可编译的
`IDeploymentBackend` 示例项目（仅引用 Core，不依赖 WPF）。
其中 `SampleMirrorBackend` 演示一种"镜像复制 + 来源记号"模式：部署时在目标文件旁
额外写一个 `{文件名}.uemm-source` 记号文件记录源路径，移除时一并清理。

第三方贡献者可以从该目录复制起步：

```bash
dotnet build samples/UEModManager.SampleBackend/UEModManager.SampleBackend.csproj
```

---

## 完整模板

`UEModManager/Services/Backends/MyBackend.cs`：

```csharp
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services.Backends
{
    public class MyBackend : IDeploymentBackend
    {
        private readonly ILogger<MyBackend> _logger;

        public MyBackend(ILogger<MyBackend> logger) => _logger = logger;

        public DeploymentBackendType BackendType => DeploymentBackendType.Copy;
        // ⚠ 当前枚举只有 Copy/HardLink/Symlink 三个值。
        //   如果你的后端是新类型，需要先在 Models/PackageKind.cs 加枚举值，
        //   再在 UI 文案中加对应翻译。

        public string DisplayName => "我的部署方式";

        public Task<bool> CanUseAsync()
        {
            // 在此检查环境前提（如某 dll 存在、某权限可用）
            return Task.FromResult(true);
        }

        public async Task DeployFileAsync(string sourcePath, string targetPath)
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 真正的部署动作 — 例如做 hardlink、symlink、注入等
            // 这里以普通 copy 示意：
            await using var src = File.OpenRead(sourcePath);
            await using var dst = File.Create(targetPath);
            await src.CopyToAsync(dst);

            _logger.LogDebug("[MyBackend] deployed {Src} → {Dst}", sourcePath, targetPath);
        }

        public Task RemoveFileAsync(string targetPath)
        {
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                _logger.LogDebug("[MyBackend] removed {Path}", targetPath);
            }
            return Task.CompletedTask;
        }
    }
}
```

---

## 注册到 DI

`UEModManager/App.xaml.cs`：

```csharp
services.AddSingleton<CopyBackend>();
services.AddSingleton<HardLinkBackend>();
services.AddSingleton<SymlinkBackend>();
services.AddSingleton<MyBackend>();             // ← 新增
services.AddSingleton<DeploymentPlanner>();
services.AddSingleton<DeploymentService>();
```

`DeploymentService` 内部根据 `Profile.BackendType` 选具体后端。如果你引入了新的 `BackendType` 枚举值，
需要更新 `DeploymentService` 内部选择逻辑（搜索 `switch` 表达式）。

---

## CanUseAsync 的写法

如果你的后端依赖系统能力，应该在 `CanUseAsync` 检查并返回 false 让 DeploymentService 自动 fallback：

```csharp
public Task<bool> CanUseAsync()
{
    // 例：Symlink 需要管理员或开发者模式
    try
    {
        var probe = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        File.CreateSymbolicLink(probe, Path.GetTempPath());
        File.Delete(probe);
        return Task.FromResult(true);
    }
    catch
    {
        return Task.FromResult(false);
    }
}
```

参见 `SymlinkBackend.cs` 实际实现。

---

## 测试

后端是 IO，测试要 mock 文件系统或用临时目录：

```csharp
public class MyBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MyBackend _backend;

    public MyBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MyBackendTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _backend = new MyBackend(NullLogger<MyBackend>.Instance);
    }

    [Fact]
    public async Task DeployFileAsync_CopiesContent()
    {
        var src = Path.Combine(_tempDir, "src.txt");
        var dst = Path.Combine(_tempDir, "dst.txt");
        await File.WriteAllTextAsync(src, "hello");

        await _backend.DeployFileAsync(src, dst);

        Assert.Equal("hello", await File.ReadAllTextAsync(dst));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
```

放在 `UEModManager.Tests/Backends/` 项目（如果有）—— Core.Tests 不适合放 IO 测试。

---

## 反模式（不要做）

- ❌ 在 backend 里编排多个文件操作 —— 让 backend 只处理"一个文件"，编排是 DeploymentService 的事
- ❌ 在 backend 里直接读 PackageRepository —— 接收已计算好的 sourcePath / targetPath
- ❌ 在 backend 里抛异常退出 —— 应该抛具体异常让 DeploymentService 决定回滚
- ❌ 静默 swallow 异常 —— 至少 log 一下，否则诊断时找不到原因
