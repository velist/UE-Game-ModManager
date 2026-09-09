# UEModManager 架构总览

**版本：** 2.1.0 源码
**最后更新：** 2026-09-05
**面向：** 接手项目的开发者、做扩展的第三方

---

## 项目分层

主项目目前混用 ViewModel 与 code-behind。运行时 MOD 数据来自 `PackageRepository`、`ObjectStore` 和 `ProfileService`；旧 `{game}_mods.json` 仅保留迁移读取。客户端未实现 MOD 配置云同步。删除依据和原始缺陷见 [消融审计](../findings/2026-09-05-ablation-audit.md)，当前修复结果与验证范围见 [修复报告](../findings/2026-09-05-audit-repairs.md)。

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
│  - 无 WPF；少量文件系统辅助见下文                        │
│  - 所有"算法"都在这里                                    │
└─────────────────────────────────────────────────────────┘
                       ↑
                       │
┌──────────────────────┴──────────────────────────────────┐
│  UEModManager.Core.Tests (xUnit)                        │
│  - 1211 个测试，覆盖领域规则及文件系统辅助                │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  samples/UEModManager.SampleBackend  (net8.0)           │
│  - 第三方扩展 SDK 起点（仅引用 Core）                      │
└─────────────────────────────────────────────────────────┘
```

**主项目 = "干活的"（IO/UI/Service 编排）**
**Core = "想清楚的"（数据/算法）**

---

## 项目分工

### Core 项目（`UEModManager.Core/`）

承载：
- `Models/` 领域模型（Package/Profile/ConflictRecord/ResolvedView/EngineType 等）
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
- `Services/Repository/` 完整性判据、包引用与删除判定、回收规划
- `Services/Paths/` 数据布局、搬移与恢复判定、磁盘空间预检
- `Services/Persistence/` AtomicFileWriter（文件系统辅助）
- `Services/IXxxQuery.cs` 端口接口（让 Domain 不依赖具体 Service）
- `Health/` 健康检查报告聚合 + 渲染
- `Logging/` 结构化日志包装器 + 脱敏
- `Diagnostics/` 诊断包清单构造

**绝对禁止**：
- `using System.Windows.*` —— 已通过 TFM=net8.0 编译失败挡住
- 把业务编排和应用路径写进 Core。现有 `AtomicFileWriter`、`EmptyDirectoryCleaner` 是接收显式路径的文件系统辅助例外，不能把整个 Core 描述为“完全无 IO”。
- 依赖具体 Service（`PackageRepository`/`ProfileService` 等）
- Logger 实例（用 `ILogger<T>` 接口由调用方传入）

### 主项目（`UEModManager/`）

承载：
- `Views/` WPF 窗口
- `ViewModels/` UI 状态绑定
- `Services/` Service 编排和 IO 适配（如 ConfigMergeEngine 是 ConfigMerger 的 IO 层）
- `Services/Backends/` 当前只注册 CopyBackend。HardLinkBackend 保留供旧事务和回滚回归使用；SymlinkBackend 已移除。
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

- xUnit 2.9.3，TFM=net8.0-windows；Core 本身为 net8.0
- 测领域模型、规则与文件系统辅助，不引用主项目
- **当前 1211 通过**

另有 `UEModManager.Tests` 验证主项目的服务、真实临时文件、SQLite、主题与窗口资源，当前 **582 通过、1 项手动生成器跳过**。Debug / Release 均通过完整验证。Worker 有 **58 项原有自测、18 项真实 workerd 测试**，另有 **17 项真实桌面服务到 workerd 的协议验收**；外部服务全部用测试替身，不代表线上认证和发信已验收。

### 扩展示例项目

- [`samples/UEModManager.SampleBackend/`](../../samples/UEModManager.SampleBackend/) —— 自定义 Deployment Backend 示例

仅引用 Core，独立可编译，是第三方贡献者的起点。

> 曾经还有一个 `samples/UEModManager.SampleAdapter`（配套 `IHostAdapter` 扩展点），
> 已于 2026-07 连同整个 Host Adapter 体系删除，原因见下方"引擎规则维护在哪里"。

---

## 关键设计决策

### 当前部署、方案与认证边界（2026-09-05）

- `ResolvedViewBuilder` 保留完整配置候选，生成合并配置和 UserFix 的最终文件视图。启动与单包开关均交给 `DeploymentPlanner` 按此视图规划，MOD / PAK 保持包目录隔离。
- `DeploymentStateStore` 按游戏及安装根持久化托管文件和版本；`DeploymentService` 在事务提交、回滚和崩溃恢复时同步归属。原始文件备份独立于可清理的事务备份，旧部署只凭可靠事务证据迁移。
- `InstanceProfile.ConflictOverrides` 保存方案自己的规则。新编辑和 lock 导出使用 `@mod/`、`@game/` 路径，旧游戏级规则按既有 Profile 迁移。
- 导入的 `ExtractionBudget` 限制实际写入；根包和嵌套包共用预算，bundle 逐包注册但累计计算展开体积。
- 邮箱登录由 Worker 生成和核验验证码，桌面只持有 challenge。SQLite Durable Object 原子保存限流与消费状态；云端密码账户通过 Supabase 契约独立处理。接口与配套发布要求见 [AUTH_PROTOCOL.md](../../cf-workers/modmanger-api/AUTH_PROTOCOL.md)。

### 1. 为什么 Core 没有 Application 层？

总计划提到 Application（UseCase）层，但当前 v2.0 实施保守路线：
- Core 以 Models + 纯函数 Service 为主，另含明确的文件系统辅助
- 主项目 Service 直接编排（充当 Application + Infrastructure 双重角色）
- 这是"先做扎实再升级"的渐进策略

未来需要做 UseCase 层时，可以新建 `UEModManager.Application/` 项目独立放 UseCase 类。

### 2. 为什么主项目仍是 net8.0-windows 单工程？

- Views/Service/认证 全堆在一起
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

### 5. 为什么 IDeploymentBackend 接口在 Core？

下沉到 Core 后，`samples/UEModManager.SampleBackend` 仅引用 Core 即可独立编译，
无需拖入 WPF / 主项目。这是 Phase 13 SDK 的关键设计。

### 6. 引擎规则维护在哪里？（新增游戏要改哪些地方）

**引擎规则以 `UEModManager/Models/EngineProfile.cs` 的静态表维护**——
扩展名集合、直接导入扩展名、文件对话框过滤器、默认 MOD 路径模式、分组优先级、
是否支持冲突检测，全部按 `EngineType` 索引。

新增一款游戏需要改**四处**：

| # | 位置 | 改什么 |
|---|---|---|
| 1 | `Services/GameConfigService.cs` `GetAvailableGames()` | 加入可选游戏列表 |
| 2 | `Services/GameConfigService.cs` `BuiltInGames` | 标记为内置游戏 |
| 3 | `Services/GameConfigService.cs` `GetEngineType()` | 游戏名 → EngineType 映射 |
| 4 | `Models/EngineProfile.cs` `Profiles` 字典 | 该引擎尚未登记时新增一条 |

路径/可执行文件的自动探测关键词另见 `Views/GamePathDialog.xaml.cs`
（`gameKeywords` 与 `GetGameKeywords`）和 `GameConfigService.AutoDetectExecutable`。

> **不要再造 Adapter 抽象。** 2026-07 之前存在一套 `IHostAdapter` + `HostAdapterRegistry`
> 扩展点，文档承诺"新增游戏 = 新增 Adapter 类，核心代码零改动"，但**全部 14 个接口成员
> 生产调用点为 0**：注册表只被存进 `MainViewModel` 的字段就再没被调用过。第三方照文档写出的
> Adapter 编译得过、注册得进 DI，却永远不会被执行。该体系已整体删除，避免继续误导。

---

## 历史 v2.0 Phase 记录（2026-04-30）

下表保留当时的实施记录。当前已移除 Adapter 和无入口窗口，配置合并与方案部署的连接缺陷已在 2026-09-05 修复；实际覆盖范围见修复报告，历史完成标记不能作为当前功能验收结论。

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
        ├── _vm.InitializeAsync (Profile/包仓库/旧数据迁移)
        ├── ☑ 崩溃恢复扫描 (CrashRecoveryService.ScanForCrashesAsync)
        │     └── 发现未完成事务 → 弹窗 → 用户决定回滚或清理
        └── ☑ 启动健康检查 (HealthCheckService.CheckAsync)
              └── 结果写入 console.log
```

---

## 历史测试结构（2026-04-30，第十七轮）

以下数字用于理解旧拆分记录；当前数量与验证命令见 [文档索引](../README.md)。

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
- [findings/](../findings/) — 设计漏洞记录
- [playbooks/](../playbooks/) — 操作指南（如 Backend / Core Service / Manifest 写法）
