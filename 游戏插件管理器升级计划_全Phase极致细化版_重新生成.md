# 游戏插件管理器升级计划（精简版）

> 原"全 Phase 极致细化版" 1425 行已浓缩为本文档（~350 行）。
> 详细每轮重构记录见 [`CHANGELOG.md`](./CHANGELOG.md)。
> 接手指南见 [`v2.0升级指南_开发者接手文档.md`](./v2.0升级指南_开发者接手文档.md)。

**当前状态：v2.0-rc2 已打标签（commit `4d8e5ba`，5 项目协同 0/0，598 测试全绿）。**

---

# 1. 项目最终定位

不再定义为"支持更多游戏的 MOD 管理器"，而是：

> **一个以 Instance / Profile 为中心、以 Resolved View 为核心、以多部署后端为落地手段、以 Host Adapter 为扩展边界的宿主扩展编排平台。**

管理对象：文件覆盖型资源（pak / loose files / textures / scripts）、配置文件、插件 / DLL / Loader、运行时生成物、启动参数、用户元数据（备注 / 预览图 / 标签）、多游戏 / 多实例 / 多后端切换。

**换句话说：** 不是"装东西的软件"，而是"对宿主修改进行编排、隔离、排序、冲突治理、回滚和复现的基础设施"。

---

# 2. 升级核心目标

**从：** 单项目内聚型 UE MOD 管理器（启停 + 备份）。

**到：**

- 规则驱动的内核
- 多实例管理
- 非侵入式部署优先
- 统一冲突治理
- 最终视图可解释
- 部署后端可切换
- 新游戏 = 新 Adapter
- 可演进到 VFS / 整合包

**前期不追求：** 全引擎支持 / 真 VFS / UI 重做 / 云协作 / 社区市场。

---

# 3. 总体设计原则

| 原则 | 含义 |
|------|------|
| 1. 以实例为中心 | 不以"安装状态"为中心 |
| 2. 宿主目录尽量不可变 | 部署可回滚、可重建 |
| 3. 冲突是常态 | 不是异常，必须有治理 |
| 4. VFS 是后端 | 不是核心 |
| 5. 先做稳定内核 | 再做全能前端 |
| 6. 所有状态都必须可重建 | 事务 + 备份 + 来源链 |
| 7. 支持新游戏 = 新 Adapter | 不是新分支逻辑 |

---

# 4. 系统总架构

## 逻辑分层

```
┌─────────────────────────────────────────────┐
│  UI 层（WPF MainWindow / ViewModels）       │
├─────────────────────────────────────────────┤
│  Service 编排层（DeploymentService 等）       │
├─────────────────────────────────────────────┤
│  Core 内核层（纯函数 / Domain 模型）          │
├─────────────────────────────────────────────┤
│  IO 适配层（FS / SQLite / HttpClient）        │
├─────────────────────────────────────────────┤
│  Backend 抽象（Copy / HardLink / Symlink）    │
└─────────────────────────────────────────────┘
```

## 各层职责

- **UI** 只做绑定 + 触发 ViewModel 命令；不直接调 Service
- **Service 编排** 串联 Core + IO + Backend；事务管理；UI 通知
- **Core 内核** 纯函数 / Domain 模型 / 决策算法；TFM=net8.0；零 IO；可独立单测
- **IO 适配** 读盘 / 写盘 / 数据库 / 网络
- **Backend** 文件部署的多种实现（复制 / 硬链 / 符号链 / 未来 VFS）

---

# 5. 核心对象模型

| 模型 | 作用 |
|------|------|
| `HostDefinition` | 游戏宿主（路径 / 引擎 / 约定） |
| `Package` | 一个 MOD 包（资源 + manifest） |
| `PackageArtifact` | 包内单个文件（来源 / 目标相对路径 / 类型） |
| `InstanceProfile` | 一个 MOD 方案（包列表 + 启用状态 + 优先级 + 后端） |
| `ProfilePackageEntry` | Profile 内的包条目（IsEnabled / Priority / Kind） |
| `ConflictRecord` | 冲突记录（Type / Severity） |
| `ResolvedView` | 最终视图（Profile + Overwrite + Conflict 合并后的"应有状态"） |
| `DeploymentPlan` | 部署计划（操作列表 + 后端类型） |
| `DeploymentTransaction` | 部署事务（Status / ExecutedOperations / RollbackFailures / BackupDirectory） |
| `DeploymentOperation` | 单个文件操作（Add / Replace / Remove） |

---

# 6. 核心运行流程

| 流程 | 关键步骤 |
|------|---------|
| **导入** | 拖拽 → `ImportFileKindClassifier.Classify` 分流 → 解压 → `ModFileGrouper` 分组 → `PreviewImageSelector` 选预览 → `PackageKindDetector` 识别 → 写 `ObjectStore` |
| **构建视图** | `ResolvedViewBuilder` 调 `ResolvedViewLayerBuilder`（Layer 1 包合并 + Layer 2 配置合并 + Layer 3 Overwrite 注入）|
| **部署** | `DeploymentPlanner.CreateTogglePlanAsync` → `DeploymentService.ExecuteAsync`（事务即时持久化 + 备份 + 逐步执行 + 失败回滚）|
| **回滚** | 逆序 → `RollbackActionPlanner.PlanRollback(op, File.Exists)` → 单步失败累积 `RollbackFailures` → 状态如实标记 |
| **启动** | `LaunchOrchestrator.LaunchAsync` 调 `LaunchPipelineBuilder` 决定步骤 → `LaunchStepEvaluator` 评估每步结果 |
| **崩溃恢复** | 启动扫 `Data/Backups/*/transaction.json` → `CrashRecoveryScanner.Scan` 分类 → UI 选择回滚/标记/Dismiss |

---

# 7. 升级路线图与当前实施

| Phase | 内容 | 状态 |
|-------|------|------|
| 0 | 重构准备与基线冻结 | ✅ |
| 1 | Profile / Instance 骨架 | ✅ |
| 2 | Package Repository 与对象仓库 | ✅ |
| 3 | Deployment Planner 与事务部署 | ✅ |
| 4 | 冲突治理系统（ConflictDetector LoadConflictKey 修复） | ✅ |
| 5 | Overwrite / Generated Artifacts | ✅ |
| 6 | Host Adapter 化 | ✅ |
| 7 | 配置系统成为一等公民（ConfigMerger + 3 Parser） | ✅ |
| 8 | Resolved View / 最终视图构建器（Layer 1+2+3 全部纯函数） | ✅ |
| 9 | Launch Orchestrator / 启动编排 | ✅ |
| 10 | 多部署后端与高级 VFS | 部分（3 后端 + Sample），VFS 留 R&D spike |
| 11 | 工程硬化、测试、可观测性 | ✅ + rc2 修复批 A 大幅强化 |
| 12 | 整合包、分享、复现、同步（lock JSON + bundle ZIP） | ✅ |
| 13 | SDK、扩展生态、文档体系（5 篇文档 + 双 Sample 项目） | ✅ |

---

# 8. 当前落地快照（2026-04-30，v2.0-rc2 已打）

## 项目结构

| 项目 | 职责 | TFM |
|------|------|-----|
| `UEModManager/` | WPF / IO / Service 编排 | net8.0-windows |
| `UEModManager.Core/` | 模型 / 纯函数 / 算法 / 决策 | **net8.0**（栏杆挡 WPF 回灌）|
| `UEModManager.Core.Tests/` | xUnit | net8.0-windows |
| `samples/UEModManager.SampleAdapter/` | 第三方 Adapter SDK 示例 | net8.0 |
| `samples/UEModManager.SampleBackend/` | 第三方 Backend SDK 示例 | net8.0 |

**Core 单依赖：** Newtonsoft.Json。

## 验证状态

```bash
dotnet build UEModManager.sln --configuration Debug    # 0 errors / 0 warnings（5 项目）
dotnet test UEModManager.Core.Tests/...                # 598 passed / 0 failed (~26 ms)
```

## 18 轮 Core 拆分 + 修复批 A 累积成果

- Config / Conflict / Recovery / Lock / Health / Logging / Diagnostics 全部已有 Core 承载
- ResolvedViews（Layer 1+2+3 纯函数）/ DeploymentPlanning / Deployment / Launch / Detection / Profile / Migration / Import / Backends / Repository
- **rc2 修复批 A 新增**：`PackageReferenceCounter` + `PackageDeletionPlanner`；扩 `DeploymentStatus` +3 状态值；`RollbackActionType` +2；加 `RollbackFailure` record；事务 `SchemaVersion=2`

## 已知未修复（留给后续批次）

- **修复批 B（推荐下批）**：S1 SQLite 单库共享 / S2 ObjectStore 全局共享 / W7 Profile 切换不触发部署 / W1 ConflictAnalyzer 内存全局 / W2 启用入口分散 / W3 事务无并发锁
- **修复批 C / v2.1**：S8 跨包依赖图 / W4 备份链多层 / N1-N4 Schema 版本兼容 / 幽灵文件检测

详见 [`CHANGELOG.md`](./CHANGELOG.md) 和 [接手文档](./v2.0升级指南_开发者接手文档.md)。

---

# 9. 数据库设计建议

## 设计原则

- 事务日志与快照分开
- 尽量保留哈希和来源链
- 便于复现：包索引 + 部署日志 + Profile 状态可独立重放
- v2.1 候选：分库（每个游戏独立 SQLite）+ GameId 外键

## 当前实现

`Data/local.db` 单库 + `Data/Backups/{txid}/transaction.json` 事务日志 + `Data/{gameName}_packages.json` 包索引。

> ⚠ S1/S2 已识别：单库 + 全局 ObjectStore 跨游戏污染风险，留给修复批 B。

---

# 10. 测试体系

| 层 | 当前覆盖 |
|----|---------|
| **Domain Tests** | 冲突求解 / 依赖图 / 配置合并 / View 构建 / Hash 稳定性 — 552 个起步，rc2 增至 **598** |
| **Adapter Tests** | HostAdapterResolver / SampleAdapter（契约测试通过 SampleAdapter 编译验证）|
| **Deployment Tests** | RollbackActionPlanner / DeploymentResultBuilder / TogglePlanBuilder / DeploymentTargetPathBuilder |
| **Recovery Tests** | CrashRecoveryScanner（rc2 扩展新状态）|
| **Repository Tests** | PackageReferenceCounter / PackageDeletionPlanner（rc2 新增）|
| **Launch Tests** | LaunchPipelineBuilder / LaunchStepEvaluator |
| **Integration Tests** | 缺，需补（修复批 B 前置） |

---

# 11. 团队协作与编码规范

## 分层纪律

- 主项目代码**永远不要**做纯函数能做的事 —— 拆出来下沉 Core
- Core 代码**永远不要**做 IO —— 用 `Func<T>` 注入或交给主项目适配器
- TFM 守卫：Core=net8.0，加 `using System.Windows.*` 立即编译失败

## 命名

- Core 纯函数：动词开头（`Compute*`, `Build*`, `Plan*`, `Classify*`, `Detect*`, `Evaluate*`）
- 主项目 Service：`*Service`（带 IO / 事件 / 状态）
- 数据模型：名词，无动词

## 提交

- `feat(scope):` / `fix(scope):` / `chore:` / `refactor:` 风格（见 git log）
- 提交前跑 `dotnet build` + `dotnet test` 必须 0/0

## 文档纪律

- 改动一个 Service → 同步检查相关 playbook
- 加新规则 → 同步 `CHANGELOG.md`
- 重大里程碑 → 打 git tag + 更新接手文档

---

# 12. 新开发者上手路线（精简版）

| Day | 任务 |
|-----|------|
| 1 | 读 `docs/architecture/overview.md` + 接手文档 + `CHANGELOG.md` 顶部段；跑 `dotnet build` + `dotnet test` 确认基线 |
| 2 | 看 `samples/UEModManager.SampleAdapter` + `SampleBackend` 各编译运行一次；读对应 playbook |
| 3 | 选一个未拆点尝试做"纯函数下沉"小练手（参考第十四 ~ 第十八轮的 ROI 模式）|
| 4-5 | 看 `docs/findings/` 已知设计选择；读 `CrashRecoveryService` / `DeploymentService` 体会事务模式（rc2 修复后版本）|
| 6 | 实机验收主流程（启动 / 导入 / 部署 / 冲突 / 一键启动 / 字体观感）|
| 7 | 决定下一步：修复批 B 前置（加集成测试）/ 远程 push / Phase 10 VFS R&D spike |

---

# 13. 里程碑

| 里程碑 | 内容 | 状态 |
|--------|------|------|
| M1 | 实例化基础（Profile / Package / 端口接口）| ✅ |
| M2 | 仓库与部署骨架（PackageRepository + DeploymentService）| ✅ |
| M3 | 冲突与生成物治理（Phase 4 修复 + OverwriteStore）| ✅ |
| M4 | 宿主适配与配置系统（Adapter + 3 Parser + ConfigMerger）| ✅ |
| M5 | 最终视图与启动编排（ResolvedView Layer 1+2+3 + LaunchOrchestrator）| ✅ |
| M6 | 高级后端与硬化（多 Backend + Phase 11 + 修复批 A）| ✅ → **rc2** |
| **M7** | **平台化与生态（SDK + 双 Sample + Phase 12+13 + 实机验收）** | **进行中**（待远程 push + 实机验收）|

---

# 14. 常见坑与规避

| 坑 | 规避 |
|----|------|
| 太早追求真 VFS | Phase 10 单独 R&D spike，不进入主线 |
| 把 IO 写进 Core | TFM 栏杆 + 代码审查；用 `Func<T>` 注入 |
| 启用/禁用与部署不同步 | rc2 已修：`SetPackageEnabledFlagAsync` 明确"仅元数据"；走 `DeployToggleAsync` 走部署事务 |
| 删包导致游戏目录残留 | rc2 已修：`PackageDeletionPlanner` + `force` 显式语义 |
| 回滚静默成功 | rc2 已修：`PartiallyRolledBack` 状态 + `RollbackFailures` 详情 |
| 备份缺失 | rc2 已修：`BackupMissing` 类型冒泡 |
| 取消恢复死循环 | rc2 已修：`Dismissed` 状态 + Reset API |
| 多游戏数据库共享 | **未修，留批 B**：S1/S2 |
| Profile 切换不触发部署 | **未修，留批 B**：W7 |
| 跨包依赖未管理 | **留 v2.1**：S8 / N2 |

---

# 15. 一句话结论

> v2.0 不是"加几个游戏"或"换个 UI"，而是**把"装东西的软件"重构成"宿主修改的编排平台"**：以实例为中心、纯函数化决策、事务化部署、可回滚、可恢复、可扩展、可解释。

**rc2 当前已具备生产 RC 候选条件**（事务安全网完整、状态机收紧、字体合规、598 测试覆盖），下一步走实机验收 + 远程 push + 修复批 B 加固，然后升 v2.0 正式标签。

---

**最后更新：** 2026-04-30（v2.0-rc2，commit `4d8e5ba`，598 测试全绿）
