# 在 Core 写新的纯函数 Service

**适用场景：** 你想新增一段算法/逻辑（如新的合并策略、新的检测规则、新的格式解析器）
并且这段逻辑可以独立验证（无 IO、无状态、无具体 Service 依赖）。

---

## 决策树

```
你要写的是什么？
│
├─ 数据结构（POCO/record/enum）
│   └─→ Core/Models/
│
├─ 算法/规则（纯函数）
│   ├─ 跟"配置合并"相关 → Core/Services/Config/
│   ├─ 跟"冲突检测"相关 → Core/Services/Conflict/
│   ├─ 跟"日志/诊断"相关 → Core/Logging/ 或 Core/Diagnostics/
│   ├─ 跟"启动/恢复"相关 → Core/Services/Recovery/ 或 Core/Health/
│   └─ 其他 → Core/Services/{NewModule}/
│
└─ 涉及 IO（读文件 / 网络 / 数据库 / 用户输入）
    └─→ 主项目 Services/，调用 Core 完成纯逻辑部分
```

---

## 模板：新建一个纯函数 Service

```csharp
using System;
using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Services.MyModule
{
    /// <summary>
    /// XXX 处理器。纯函数：给定输入，返回结果。无 IO、无状态、可独立单测。
    /// </summary>
    public static class MyAlgorithm
    {
        /// <summary>
        /// 主入口：完成一次 XXX 计算。
        /// </summary>
        public static MyResult Process(MyInput input, IReadOnlyDictionary<string, X> registry)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            // ...
            return new MyResult { /* ... */ };
        }
    }

    public sealed record MyResult(/* ... */);
}
```

放在 `UEModManager.Core/Services/MyModule/MyAlgorithm.cs`。

---

## 模板：主项目的 IO 适配器

```csharp
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.MyModule;

namespace UEModManager.Services
{
    /// <summary>
    /// MyAlgorithm 的 IO 适配器。负责读文件、调用纯函数、记录日志。
    /// </summary>
    public class MyService
    {
        private readonly ILogger<MyService> _logger;

        public MyService(ILogger<MyService> logger) => _logger = logger;

        public async Task<MyResult> RunAsync(string inputFile)
        {
            var raw = await File.ReadAllTextAsync(inputFile);
            var input = ParseInput(raw);

            var result = MyAlgorithm.Process(input, _registry);

            _logger.LogInformation("MyAlgorithm done: {Items}", result.Count);
            return result;
        }
    }
}
```

放在 `UEModManager/Services/MyService.cs`，并在 `App.xaml.cs` 加：

```csharp
services.AddSingleton<MyService>();
```

---

## 模板：测试

```csharp
using UEModManager.Models;
using UEModManager.Services.MyModule;

namespace UEModManager.Core.Tests.Services.MyModule;

public class MyAlgorithmTests
{
    [Fact]
    public void Process_BasicInput_ProducesExpectedOutput()
    {
        var input = new MyInput { /* ... */ };

        var result = MyAlgorithm.Process(input, EmptyRegistry);

        Assert.Equal(/* expected */, result.Something);
    }

    [Fact]
    public void Process_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MyAlgorithm.Process(null!, EmptyRegistry));
    }

    private static readonly IReadOnlyDictionary<string, X> EmptyRegistry = new Dictionary<string, X>();
}
```

放在 `UEModManager.Core.Tests/Services/MyModule/MyAlgorithmTests.cs`。

---

## 检查清单

写完后自查：
- [ ] Core 文件不含 `using System.Windows.*`
- [ ] Core 文件不含 `File.*` / `Directory.*`（除非新增的就是 IO 抽象层）
- [ ] Core 文件不直接 new Logger 或调用具体 Service
- [ ] 测试覆盖了：基础正常路径 + 边界 + null 入参
- [ ] `dotnet build UEModManager.sln` 0 errors / 0 warnings
- [ ] `dotnet test UEModManager.Core.Tests/...` 全绿

---

## 反模式（不要做）

- ❌ 在 Core 里 `new Logger<...>()` —— 改用接收 `ILogger<T>` 参数（如果实在需要）或不打日志
- ❌ 在 Core 里读环境变量 / Registry / `AppDomain.CurrentDomain.BaseDirectory` —— 把这些值由调用方传入
- ❌ 在 Core 里依赖 `PackageRepository` 具体类 —— 用 `IPackageQuery` 接口
- ❌ 在主项目的 IO 适配器里复制粘贴算法 —— 应该调 Core 的纯函数
