# UEModManager 架构总览

**版本：** v2.0-rc 候选
**最后更新：** 2026-04-30
**面向：** 接手项目的开发者、做扩展的第三方

---

## TL;DR — 30 秒理解

```
┌─────────────────────────────────────────────────────────┐
│  UEModManager (WPF 主项目)                              │
│  - UI / 事件 / IO / Service 编排 / DI 容器              │
│  - 通过 ProjectReference 引用 Core                       │
└──────────────────────┬──────────────────────────────────┘
                       │
                       ↓
┌─────────────────────────────────────────────────────────┐
│  UEModManager.Core (net8.0, 单依赖 Newtonsoft.Json)     │
│  - 纯领域模型 + 纯函数 Service                            │
│  - 无 WPF / Windows / IO 依赖（栏杆生效）                 │
│  - 所有"算法"都在这里                                    │
└─────────────────────────────────────────────────────────┘
                       ↑
                       │
┌──────────────────────┴──────────────────────────────────┐
│  UEModManager.Core.Tests (xUnit)                        │
│  - 552 个单测，27 ms 跑完，覆盖 Domain 行为               │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  samples/UEModManager.SampleAdapter (net8.0)            │
│  samples/UEModManager.SampleBackend  (net8.0)           │
│  - 第三方扩展 SDK 起点（仅引用 Core）                      │
└─────────────────────────────────────────────────────────┘
```

**主项目 = "干活的"（IO/UI/Service 编排）**
**Core = "想清楚的"（数据/算法）**

---

## 项目分工

### Core 项目（`UEModManager.Core/`）

承载（截至第十七轮 Core 拆分）：
- `Models/` 领域模型（Package/Profile/ConflictRecord/ResolvedView/HostDefinition/EngineType 等 16 个）
- `Adapters/` IHostAdapter + AdapterCapabilities + HostAdapterResolver
- `Services/Backends/` IDeploymentBackend 接口
- `Services/Config/` 配置解析 + 合并（4 种策略纯函数）
- `Services/Conflict/` 冲突求解 + 检测 + 查询（无 IO 静态分析）
- `Services/Recovery/` 崩溃恢复扫描分类器
- `Services/Lock/` Profile lock 构造 + 对比
- `Services/ResolvedViews/` ResolvedViewLayerBuilder（Layer 1+2+3 全部纯函数）
- `Services/DeploymentPlanning/` DeploymentDiffComputer + TargetPathBuilder + TogglePlanBuilder
- `Services/Deployment/` RollbackActionPlanner + DeploymentResultBuilder
- `Services/Launch/` LaunchPipelineBuilder + LaunchStepEvaluator
- `Services/Detection/` PackageKindDetector + ArtifactTypeDetector + ModCategoryClassifier + GameNameNormalizer + EngineDetector
- `Services/Migration/` Models + Decision + Step + StepCatalog + ProgressTracker
- `Services/Import/` CompressedArchive + ImportFileKindClassifier + ModFileGrouper + PreviewImageSelector
- `Services/Profile/` LegacyModEntry + LegacyProfileMigrator + ProfileSyncPlanner
- `Services/IXxxQuery.cs` 端口接口（让 Domain 不依赖具体 Service）
- `Health/` 健康检查报告聚合 + 渲染
- `Logging/` 结构化日志包装器 + 脱敏
- `Diagnostics/` 诊断包清单构造

**绝对禁止**：
- `using System.Windows.*` —— 已通过 TFM=net8.0 编译失败挡住
- `File.ReadAllText` / `Directory.*` 等 IO（除非把这些抽到接口给主项目实现）
- 依赖具体 Service（`PackageRepository`/`ProfileService` 等）
- Logger 实例（用 `ILogger<T>` 接口由调用方传入）

### 主项目（`UEModManager/`）

承载：
- `Views/` WPF 窗口
- `ViewModels/` UI 状态绑定
- `Services/` Service 编排和 IO 适配（如 ConfigMergeEngine 是 ConfigMerger 的 IO 层）
- `Adapters/` 宿主适配器具体实现（UnrealEngineAdapter / GenericFileOverlayAdapter 等）
- `Services/Backends/` 三种内置后端实现（CopyBackend / HardLinkBackend / SymlinkBackend）
- `App.xaml.cs` DI 容器配置 + 启动钩子
- `MainWindow.*` 顶层窗口

**主项目调用 Core 模式**：

```csharp
// 主项目 Service 是"IO 适配器"，纯逻辑委托给 Core
public class ConfigMergeEngine          // 主项目
{
    public async Task<ConfigMergeResult> MergeAsync(ConfigMergePlan plan)
    {
        // 1. IO：读所有源文件
        var sourceContents = await LoadSourceContentsAsync(plan);

        // 2. 纯函数：调 Core
        return ConfigMerger.Merge(plan, sourceContents, _parsers);
    }
}
```

### 测试项目（`UEModManager.Core.Tests/`）

- xUnit 2.5.3，TFM=net8.0-windows（仅为兼容引用 Core dll）
- 只测 Core 的纯函数 / 纯模型行为
- 不引用主项目，不测 UI 或 IO
- **当前 552 测试，27 ms 跑完**

### 扩展示例项目

- [`samples/UEModManager.SampleAdapter/`](../../samples/UEModManager.SampleAdapter/) —— 自定义 Host Adapter 示例
- [`samples/UEModManager.SampleBackend/`](../../samples/UEModManager.SampleBackend/) —— 自定义 Deployment Backend 示例

两者都仅引用 Core，独立可编译，是第三方贡献者的起点。

---

## 关键设计决策

### 1. 为什么 Core 没有 Application 层？

总计划提到 Application（UseCase）层，但当前 v2.0 实施保守路线：
- Core 只含 Models + 纯函数 Service
- 主项目 Service 直接编排（充当 Application + Infrastructure 双重角色）
- 这是"先做扎实再升级"的渐进策略

未来需要做 UseCase 层时，可以新建 `UEModManager.Application/` 项目独立放 UseCase 类。

### 2. 为什么主项目仍是 net8.0-windows 单工程？

- Views/Adapters/Service/认证 全堆在一起
- 拆分需要重新设计 DI 边界，工作量大且风险高
- "保守起步" — 等 Core 真的扎实之后再考虑

### 3. 为什么 Service 接口只定义"读"契约？

`IPackageQuery` / `IProfileQuery` / `IObjectStoreQuery` 都只暴露查询方法。
写操作（Register/Update/Delete）留在具体类，让 Domain 不能"越权"修改仓库。
未来 Application 层定义 UseCase 时，写操作应通过 UseCase 触发，不通过 Domain 直调。

### 4. 为什么 ConflictDetector 用"无 PackageKey 路径"作 key？

参见 `docs/findings/2026-04-28-conflict-detector-noop-by-design.md`。

部署路径含 PackageKey 子目录隔离，但加载顺序冲突应该忽略子目录差异——
否则同名 .pak 永远不会被识别为冲突候选。这是 Phase 4 修复的核心设计。

### 5. 为什么 IDeploymentBackend / IHostAdapter 接口在 Core？

下沉到 Core 后，`samples/UEModManager.SampleAdapter` 和 `samples/UEModManager.SampleBackend`
仅引用 Core 即可独立编译，无需拖入 WPF / 主项目。这是 Phase 13 SDK 的关键设计。

---

## v2.0 Phase 状态

| Phase | 内容 | 状态 |
|-------|------|------|
| 0–9 | 后端 + UI 全部完成（Profile/Package/Deployment/Conflict/Overwrite/Adapter/ConfigMerge/ResolvedView/Launch） | ✅ |
| UX 优化 | Header 简化 + 管理中心 + 文案可读性 | ✅ |
| **10** | 多部署后端（VFS） | ⬜ 实验性，未做 |
| **11** | 工程硬化（测试/日志/诊断/崩溃恢复/健康检查） | ✅ |
| **12** | 整合包（lock JSON + bundle ZIP） | ✅ |
| **13** | SDK / Adapter 模板 / Backend 模板 / 开发者文档 | ✅ |
| **Core 拆分** | 第六至第十七轮（共 12 轮持续 ROI 拆分） | ✅ |

---

## 启动流程

```
App.OnStartup
  ├── AttachGlobalExceptionHandlers
  ├── SetupFileLogging          (StructuredLogWriter 包装 console.log)
  ├── BuildHost (DI 容器)
  └── ShowAuthenticationWindow
        ↓
LoginWindow → MainWindow.OnLoaded
  └── InitializeAsync
        ├── 加载游戏配置
        ├── _vm.InitializeAsync (Profile/包仓库/适配器/扫描)
        ├── ☑ 崩溃恢复扫描 (CrashRecoveryService.ScanForCrashesAsync)
        │     └── 发现未完成事务 → 弹窗 → 用户决定回滚或清理
        └── ☑ 启动健康检查 (HealthCheckService.CheckAsync)
              └── 结果写入 console.log
```

---

## 测试覆盖（截至第十七轮）

```
UEModManager.Core.Tests/                              552 个测试
├── Models/                                          12+ 测试
├── Services/Config/                                 32  测试
├── Services/Conflict/                               44+ 测试 (Resolver + Detector + AnalysisResult + Queries)
├── Services/Recovery/                                9  测试
├── Services/Lock/                                   10  测试
├── Services/ResolvedViews/                          16+ 测试 (Layer 1+2+3)
├── Services/DeploymentPlanning/                     25+ 测试 (Diff + TargetPath + TogglePlan)
├── Services/Deployment/                             20+ 测试 (Rollback + ResultBuilder)
├── Services/Launch/                                 25+ 测试 (Pipeline + StepEvaluator)
├── Services/Detection/                              80+ 测试 (PackageKind + ArtifactType + ModCategory + GameName + Engine)
├── Services/Migration/                              50+ 测试 (Decision + Step + Catalog + Tracker)
├── Services/Import/                                 70+ 测试 (CompressedArchive + Classifier + Grouper + PreviewSelector)
├── Services/Profile/                                30+ 测试 (LegacyMigrator + SyncPlanner)
├── Adapters/                                        15+ 测试 (HostAdapterResolver)
├── Health/                                           9  测试
├── Logging/                                         30  测试
└── Diagnostics/                                      7  测试
```

跑测试：

```bash
dotnet test UEModManager.Core.Tests/UEModManager.Core.Tests.csproj
```

---

## 相关文档

- [项目概览](../../CLAUDE.md) — 老式版，部分内容已过时
- [v2.0 升级指南](../../v2.0升级指南_开发者接手文档.md) — 详细 Phase 实施记录 + Core 17 轮拆分记录
- [总计划](../../游戏插件管理器升级计划_全Phase极致细化版_重新生成.md) — 长期愿景
- [findings/](../findings/) — 设计漏洞记录
- [playbooks/](../playbooks/) — 操作指南（如 Adapter / Backend / Core Service / Manifest 写法）
