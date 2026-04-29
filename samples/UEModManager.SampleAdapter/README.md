# UEModManager.SampleAdapter

UEModManager 自定义 Host Adapter 最小示例项目。

---

## 这是什么

一个独立可编译的 .NET 8 类库，演示如何为新游戏添加 MOD 支持。

包含一个完整 `SampleEngineAdapter.cs` 实现，覆盖 `IHostAdapter` 全部成员。

## 为什么独立项目

- 仅依赖 `UEModManager.Core`（net8.0，无 WPF）
- 第三方贡献者可以 fork 此目录作为起点
- CI 编译此项目即可验证 IHostAdapter 接口的兼容性

## 如何用

### 方式 A：合并回主项目

1. 把 `SampleEngineAdapter.cs` 复制到 `UEModManager/Adapters/`
2. 改 namespace / 类名为目标游戏（如 `Adapters/MyGameAdapter.cs`）
3. 调整 `AdapterKey` / 关键词 / 路径
4. 在 `App.xaml.cs` 加 `services.AddSingleton<IHostAdapter, MyGameAdapter>()`
5. 重新编译，DI 容器自动注入到 `HostAdapterRegistry`

### 方式 B：作为外部贡献的发布

1. 在自己的 repo fork 此项目
2. 改完代码后 `dotnet build`
3. 把 dll 提交回上游或在贡献 PR 中包含整个 Adapter 类

## 编译

```bash
cd samples/UEModManager.SampleAdapter
dotnet build
```

预期：0 errors / 0 warnings。

## 完整说明

参见 [`docs/playbooks/writing-host-adapter.md`](../../docs/playbooks/writing-host-adapter.md)。
