# 游戏插件管理器升级计划（全 Phase 极致细化版）

> 适用对象：第一次接触该项目的开发者、架构设计者、技术负责人  
> 目标：把当前“UE 游戏 MOD 管理器”逐步升级为“面向多宿主/多引擎生态的扩展包编排平台”

---

# 目录

1. 项目最终定位  
2. 本次升级的核心目标  
3. 总体设计原则  
4. 系统总架构  
5. 建议的项目目录结构  
6. 核心对象模型  
7. 核心运行流程  
8. 升级总路线图  
9. 各 Phase 详细计划  
   - Phase 0：重构准备与基线冻结  
   - Phase 1：Profile / Instance 骨架  
   - Phase 2：Package Repository 与对象仓库  
   - Phase 3：Deployment Planner 与事务部署  
   - Phase 4：冲突治理系统  
   - Phase 5：Overwrite / Generated Artifacts  
   - Phase 6：Host Adapter 化  
   - Phase 7：配置系统成为一等公民  
   - Phase 8：Resolved View / 最终视图构建器  
   - Phase 9：Launch Orchestrator / 启动编排  
   - Phase 10：多部署后端与高级虚拟部署  
   - Phase 11：工程硬化、测试、可观测性  
   - Phase 12：整合包、分享、复现、同步  
   - Phase 13：SDK、扩展生态、文档体系  
10. 数据库设计建议  
11. 测试体系设计  
12. 团队协作与编码规范  
13. 新开发者 7 天上手路线  
14. 里程碑与发布策略  
15. 常见坑与规避建议  
16. 一句话结论

---

# 1. 项目最终定位

这个项目最终不应再定义成“支持更多游戏的 MOD 管理器”，而应该定义成：

> **一个以 Instance / Profile 为中心、以 Resolved View 为核心、以多部署后端为落地手段、以 Host Adapter 为扩展边界的宿主扩展编排平台。**

它要管理的对象不只是传统 MOD，还包括：

- 文件覆盖型资源（pak、loose files、textures、scripts）
- 配置文件（ini、cfg、json、yaml 等）
- 插件 / DLL / Loader
- 运行时生成物
- 启动参数
- 输出目录与日志目录
- 用户备注、预览图、标签、分类
- 多游戏 / 多实例 / 多后端切换

**换句话说：**
你不是在做“装东西的软件”，而是在做“对游戏/宿主修改进行编排、隔离、排序、冲突治理、回滚和复现的基础设施”。

---

# 2. 本次升级的核心目标

## 2.1 从当前状态升级到什么

从：

- 单项目内聚型 UE MOD 管理器
- 功能点相对散，核心规则没有彻底抽出
- 以启停与备份为主的管理模型

升级为：

- 规则驱动的内核
- 多实例管理
- 非侵入式部署优先
- 统一冲突治理
- 最终视图可解释
- 部署后端可切换
- 新游戏支持通过 Adapter 扩展
- 可以逐步演进到高级 VFS / runtime 插件 / 整合包平台

## 2.2 升级不追求什么

前期明确 **不追求**：

- 一开始支持所有引擎
- 一开始做最复杂的真 VFS
- 一开始重做所有 UI
- 一开始做完整云协作
- 一开始做社区平台与市场系统

## 2.3 这次升级优先解决的问题

1. **实例模型缺失**：当前更多是“游戏 + 模组列表”，而不是“实例 + 运行环境”
2. **部署模型过于直接**：长期不能继续以“直接改宿主目录”为主
3. **规则系统不统一**：包、配置、dll、冲突没有统一求解中枢
4. **宿主扩展方式不清晰**：新增支持不能总靠分支逻辑硬堆
5. **系统真相源不明确**：磁盘现状不能成为唯一真相

---

# 3. 总体设计原则

## 原则 1：以实例为中心，不以安装状态为中心

系统真正管理的是“某个 Profile 在某个 Host 上的最终运行环境”。

不是：

- 当前游戏目录里有什么

而是：

- 当前 Host 是什么
- 当前 Profile 启用了哪些 Package
- 顺序如何
- 配置如何叠加
- 最终视图是什么
- 用什么后端部署
- 启动方式是什么

---

## 原则 2：宿主目录尽量不可变

长期方向应该是：

- 原始宿主目录尽量只读
- 所有扩展内容放独立仓库
- 输出产物也放独立目录
- 最终通过部署层或运行时注入为宿主提供可见视图

---

## 原则 3：冲突是常态，不是异常

系统应天然假设：

- 多个包会争同一路径
- 多份配置会改同一键
- 多个插件会竞争同一入口
- 多个 loader 会争启动链顺序

所以必须有：

- 冲突发现
- 冲突解释
- 默认求解
- 手工 override
- 胜者链与失败者链追踪

---

## 原则 4：VFS 是后端，不是核心

核心永远是：

- Package 模型
- Profile 模型
- Resolver
- Resolved View
- Transaction / Snapshot

VFS、HardLink、Copy、Symlink、Junction、Inject 都只是“呈现最终视图”的不同方式。

---

## 原则 5：先做稳定内核，再做全能前端

升级顺序必须是：

1. 抽核心模型
2. 抽部署与规则
3. 抽宿主适配器
4. 再做高级后端与生态能力

---

## 原则 6：所有状态都必须可重建

系统真相源应该是：

- 数据库
- 包仓库
- 规则配置
- 快照
- 事务日志
- Resolved View 哈希

不是游戏目录现状。

---

## 原则 7：支持新游戏 = 新 Adapter，而不是新分支逻辑

未来接新宿主时，第一反应应该是：

- 能不能新增一个 Adapter？
- 能不能只配置规则而不改核心？

如果经常不能，说明核心抽象还不够成熟。

---

# 4. 系统总架构

## 4.1 逻辑分层

```text
UI / App Shell
    ↓
Application Use Cases
    ↓
Domain Core
    ├─ Package Model
    ├─ Profile / Instance Model
    ├─ Resolver / Rule Engine
    ├─ Conflict System
    ├─ Resolved View Builder
    ↓
Infrastructure
    ├─ Storage
    ├─ Deployment
    ├─ Launch
    ├─ Snapshot / Transaction
    ↓
Host Adapters
    ├─ Unreal Adapter
    ├─ Generic File Overlay Adapter
    ├─ Runtime Plugin Adapter（后续）
```

## 4.2 各层职责

### UI / App Shell
负责：
- 游戏选择
- Profile 选择
- 包列表
- 冲突展示
- 预览图与备注
- 最终视图预览
- 启动与部署入口

### Application
负责：
- 用例编排
- 事务边界
- 进度管理
- 跨模块协调

### Domain
负责：
- 纯业务规则
- 包、实例、冲突、求解、最终视图

### Infrastructure.Storage
负责：
- SQLite
- 对象仓库
- Package Repository
- 缩略图
- 缓存
- 快照

### Infrastructure.Deployment
负责：
- Deployment Plan
- Backend
- Apply / Rollback

### Infrastructure.Launch
负责：
- 启动任务链
- 参数组装
- 环境变量
- 进程监控
- 日志收集

### Adapters
负责：
- 游戏识别
- 路径映射
- 规则解析
- 能力暴露
- 启动约束

---

# 5. 建议的项目目录结构

```text
/src
  /GamePluginManager.App.Wpf
  /GamePluginManager.Application
  /GamePluginManager.Domain
  /GamePluginManager.Infrastructure.Storage
  /GamePluginManager.Infrastructure.Deployment
  /GamePluginManager.Infrastructure.Launch
  /GamePluginManager.Adapters.Unreal
  /GamePluginManager.Adapters.Generic
  /GamePluginManager.Shared

/tests
  /GamePluginManager.Domain.Tests
  /GamePluginManager.Application.Tests
  /GamePluginManager.Adapter.Tests
  /GamePluginManager.Integration.Tests

/experimental
  /GamePluginManager.Backends.ProcessVfs
  /GamePluginManager.Backends.WinFsp

/docs
  /architecture
  /phases
  /api
  /playbooks
```

---

# 6. 核心对象模型

## 6.1 HostDefinition

```csharp
public sealed class HostDefinition
{
    public Guid Id { get; init; }
    public string Key { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string Engine { get; init; } = default!;
    public string RootPath { get; init; } = default!;
    public string ExecutablePath { get; init; } = default!;
    public string AdapterKey { get; init; } = default!;
}
```

## 6.2 Package

```csharp
public sealed class Package
{
    public Guid Id { get; init; }
    public string PackageKey { get; init; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string Version { get; init; } = "1.0.0";
    public string? Note { get; private set; }
    public string? PreviewImagePath { get; private set; }
    public PackageKind Kind { get; init; }
    public IReadOnlyList<PackageArtifact> Artifacts { get; init; } = [];
}
```

## 6.3 PackageArtifact

```csharp
public sealed class PackageArtifact
{
    public Guid Id { get; init; }
    public Guid PackageId { get; init; }
    public string RelativeSourcePath { get; init; } = default!;
    public string RelativeTargetPath { get; init; } = default!;
    public ArtifactType ArtifactType { get; init; }
    public DeployStrategy DeployStrategy { get; init; }
}
```

## 6.4 InstanceProfile

```csharp
public sealed class InstanceProfile
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public string Name { get; private set; } = default!;
    public DeploymentBackendType BackendType { get; private set; }
    public IReadOnlyList<ProfilePackageEntry> Packages { get; init; } = [];
}
```

## 6.5 ConflictRecord

```csharp
public sealed class ConflictRecord
{
    public string TargetPath { get; init; } = default!;
    public ConflictType ConflictType { get; init; }
    public Guid WinnerPackageId { get; init; }
    public IReadOnlyList<Guid> LoserPackageIds { get; init; } = [];
    public string Reason { get; init; } = default!;
}
```

## 6.6 ResolvedView

```csharp
public sealed class ResolvedView
{
    public Guid HostId { get; init; }
    public Guid ProfileId { get; init; }
    public IReadOnlyList<ResolvedEntry> Entries { get; init; } = [];
    public IReadOnlyList<ConflictRecord> Conflicts { get; init; } = [];
    public string ViewHash { get; init; } = default!;
}
```

## 6.7 DeploymentTransaction

```csharp
public sealed class DeploymentTransaction
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public Guid ProfileId { get; init; }
    public string BeforeHash { get; init; } = default!;
    public string AfterHash { get; init; } = default!;
    public DeploymentStatus Status { get; private set; }
}
```

---

# 7. 核心运行流程

## 7.1 导入包流程

```text
导入 zip / folder
→ 解压 / 标准化
→ Host Adapter 参与解析
→ 识别 Artifacts
→ 建立 Package Manifest
→ 写入对象仓库 / 包仓库
→ 生成缩略图与元数据
→ 写 DB
```

## 7.2 构建最终视图流程

```text
读取 Host
→ 读取 Profile
→ 读取启用包列表
→ 读取 Adapter 规则
→ 依赖求解
→ 冲突检测
→ 配置合并
→ 纳入 Generated Artifacts
→ 生成 Resolved View
```

## 7.3 部署流程

```text
Resolved View
→ 生成 Deployment Plan
→ 创建 Transaction
→ 写 staging
→ Apply Backend
→ 校验结果
→ 成功提交 / 失败回滚
```

## 7.4 启动流程

```text
选择 Profile
→ 检查 View 是否过期
→ 如过期则重新 Resolve / Deploy
→ 构建 LaunchContext
→ 启动宿主
→ 收集日志 / 输出 / 子进程状态
```

---

# 8. 升级总路线图

推荐总路线：

1. **先把实例、仓库、部署、冲突四件事抽出来**
2. **再把 Overwrite、Adapter、Config Merge 补上**
3. **最后做最终视图、启动编排、高级后端、复现与生态**

也就是：

- 第 1 优先级：Phase 1 ~ 4
- 第 2 优先级：Phase 5 ~ 7
- 第 3 优先级：Phase 8 ~ 13

---

# 9. 各 Phase 详细计划

## Phase 0：重构准备与基线冻结
### 目标
建立安全重构基线，为后续拆层做准备。

### 为什么现在做
没有基线就直接重构，很容易把现有可用功能打崩。

### 范围
包含：
- 冻结当前稳定版
- 盘点现有模块
- 标出高耦合代码
- 产出迁移清单

不包含：
- 新功能开发
- 大规模 UI 改版
- 高级后端接入

### 核心任务
1. 给当前版本打 tag：`legacy-stable`
2. 列出现有项目中的 ViewModel、Service、Model、文件写盘逻辑、云同步逻辑、UE 特定逻辑
3. 为每个模块标记归属：UI / Application / Domain / Infrastructure / Adapter
4. 列出“绝对不能继续留在主工程中的逻辑”

### 测试重点
- 对现有关键功能做冒烟测试
- 记录当前行为作为基线

### 验收标准
- 有完整迁移清单
- 有模块归属表
- 团队知道接下来怎么拆

### 常见坑
- 没立基线就重构
- 没有回退版本
- 先做大 UI 改版

---

## Phase 1：Profile / Instance 骨架
### 目标
把“按游戏启停模组”升级为“按实例管理运行环境”。

### 为什么现在做
没有实例，就无法真正做隔离、切换、复现、分享。

### 范围
包含：
- 一个 Host 多个 Profile
- Profile 的创建、复制、导入、导出、切换
- 每个 Profile 保存：启用包列表、优先级、启动参数、配置覆盖、后端类型

不包含：
- 高级配置合并
- 真 VFS
- 多进程接管

### 核心任务
1. 新增 `profiles`、`profile_packages`、`profile_settings`
2. 建立 Domain 模型
3. 建立 `CreateProfileUseCase`
4. 建立 `CloneProfileUseCase`
5. 建立 `SwitchProfileUseCase`
6. 改启停逻辑，只作用于当前 Profile
7. UI 增加 Profile 选择器

### 推荐核心类
- `InstanceProfile`
- `ProfilePackageEntry`
- `ProfileSettings`
- `ProfileService`
- `ProfileQueryService`

### 测试重点
- 同一 Host 可有多个 Profile
- 启停只影响当前 Profile
- 复制 Profile 后保持配置与顺序一致

### 验收标准
- 同一游戏至少支持 3 套组合
- 所有启停行为依赖 Profile 状态
- 默认 Profile 自动创建

### 常见坑
- 启停仍直接改宿主目录
- 把 Profile 做成 UI 临时状态
- 没有 ProfileId 就往下运行

---

## Phase 2：Package Repository 与对象仓库
### 目标
建立独立包仓库，彻底把包存储和宿主目录解耦。

### 为什么现在做
只有建立仓库，后续的重建、回滚、快照、锁文件、分享和同步才有基础。

### 范围
包含：
- 包导入
- 解压与标准化
- 对象仓库存储
- 包 manifest
- 预览图与元数据
- 基础去重

不包含：
- 高级云同步
- 复杂版本解析
- 完整市场系统

### 核心任务
1. 设计 `PackageManifest`
2. 设计对象仓库（按 hash）
3. 支持 zip / folder 导入
4. 生成 `PackageArtifact` 列表
5. 支持缩略图和备注
6. 支持标签、分类、基础搜索
7. 记录导入来源与内容哈希

### 推荐核心类
- `Package`
- `PackageArtifact`
- `PackageManifest`
- `PackageImportService`
- `ObjectStore`
- `PackageRepository`

### 测试重点
- 相同文件导入两次不重复存储
- 压缩包与文件夹导入结果一致
- 缩略图与备注不影响内容哈希

### 验收标准
- 删除宿主目录内容不影响包仓库
- 系统能从仓库重新解析包
- 一个包可被多个 Profile 引用

### 常见坑
- 仓库路径和宿主路径混用
- manifest 信息不够
- 元数据混进内容哈希

---

## Phase 3：Deployment Planner 与事务部署
### 目标
建立“计划部署 -> 事务执行 -> 可回滚”的统一部署模型。

### 为什么现在做
没有这一层，启用/禁用/卸载/还原都会长期脆弱。

### 范围
包含：
- Deployment Plan
- 事务日志
- staging
- Apply / Rollback
- Copy / HardLink / Symlink/Junction 第一批后端

不包含：
- 真正的进程级 VFS
- 高级运行时注入

### 核心任务
1. 建立 `DeploymentPlan`
2. 建立 `DeploymentTransaction`
3. 建立 `DeploymentOperation`
4. 所有写盘先写 staging
5. 支持 Apply / Rollback
6. 支持后端切换能力检测

### 第一批后端建议
- `CopyBackend`
- `HardLinkBackend`
- `SymlinkBackend`
- `JunctionBackend`

### 测试重点
- 部署中断是否能回滚
- 不同后端下结果是否一致
- 删除某包后能否自动回退到次优先级来源

### 验收标准
- 启用/禁用/删除/还原都通过事务层完成
- 没有 UI 直接写宿主目录
- 崩溃后可以用事务日志恢复

### 常见坑
- 直接写正式目录
- 回滚变成“尽力恢复”
- 后端逻辑和业务逻辑混在一起

---

## Phase 4：冲突治理系统
### 目标
把“优先级”升级为真正的冲突治理系统。

### 为什么现在做
当包数一多，没有冲突系统项目就不可维护。

### 范围
包含：
- 文件路径冲突
- 目录遮蔽冲突
- 配置键冲突（初版）
- 依赖缺失
- 循环依赖
- 手工 override

### 冲突类型建议
- `PathConflict`
- `DirectoryShadowConflict`
- `ConfigKeyConflict`
- `DependencyConflict`
- `VersionConflict`
- `LoadOrderConflict`

### 核心任务
1. 设计 `ConflictRecord`
2. 设计 `ResolutionReason`
3. 建立默认求解规则
4. 建立用户 override 规则
5. UI 冲突面板
6. 路径赢家链追踪

### 测试重点
- 改包顺序会刷新冲突图
- 手工 override 优先于默认规则
- 配置冲突和文件冲突分开显示

### 验收标准
- 任意一个被覆盖文件都能追踪来源链
- 用户能知道谁赢、谁输、为什么
- 冲突面板可用于调试

### 常见坑
- 只显示赢家，不保存失败者链
- 把所有冲突都塞成优先级覆盖
- 没有人工 override

---

## Phase 5：Overwrite / Generated Artifacts
### 目标
纳管一切运行中或处理中产生的“非原始输入”。

### 为什么现在做
如果不纳管，系统会越来越脏，最终没人知道哪些文件是谁生成的。

### 范围
包含：
- 合并后的配置文件
- 启动前生成的辅助文件
- 工具输出
- 缓存
- 临时修复产物
- 输出目录查看与清理

### 核心任务
1. 建立 `GeneratedArtifact`
2. 建立 Overwrite 目录模型
3. UI 展示 Generated / Overwrite
4. 支持“晋升为正式 Package”
5. 支持清理当前 Profile 生成物

### 测试重点
- 生成物不会污染包仓库
- 清理 overwrite 不影响原始包
- 晋升为正式包后可被多个 Profile 使用

### 验收标准
- 用户能看见当前实例生成了什么
- 能一键清理或晋升
- 系统能解释生成物来源

### 常见坑
- 生成物直接落在宿主目录不纳管
- 生成物和正式包混在一起
- 没有晋升机制

---

## Phase 6：Host Adapter 化
### 目标
把“支持多游戏”升级为“支持多宿主规则”。

### 为什么现在做
否则每接一个新游戏都要侵入核心代码。

### 范围
包含：
- Unreal Adapter
- Generic File Overlay Adapter
- Adapter 能力矩阵
- 启动上下文构建
- 路径映射规则

### 核心任务
1. 定义 `IHostAdapter`
2. 抽出 Unreal 规则
3. 实现 Generic Adapter
4. UI 展示宿主能力与限制
5. UseCase 通过 Adapter 访问规则

### 测试重点
- 新接入一个 UE 游戏不应改核心代码
- Generic Adapter 至少能接一种非 UE 宿主
- 不同 Adapter 的结果都能进入统一部署层

### 验收标准
- 新增宿主时主要新增 Adapter 项目
- 核心求解、部署、快照基本不变
- UI 能展示宿主能力与限制

### 常见坑
- Adapter 承接部署逻辑
- 游戏特定规则继续散落主工程
- 没有能力矩阵

---

## Phase 7：配置系统成为一等公民
### 目标
让 ini / cfg / json 等配置层不再只是普通文件替换。

### 为什么现在做
很多 UE 场景里，真正难的是配置叠加和键级冲突。

### 范围
包含：
- 整文件替换
- 按 Section / Key 合并
- 追加策略
- Patch 策略
- 合并预览

### 核心任务
1. 建立配置类型识别
2. 建立配置 patch 模型
3. 建立配置合并器
4. 建立配置冲突展示
5. 合并结果纳入 Generated Artifacts

### 测试重点
- 多个包修改同一键时的赢家规则
- 合并结果预览是否正确
- 失败是否可解释

### 验收标准
- 用户能看到最终配置文本
- 用户能追踪每个键来自哪个包
- 合并失败不会污染正式环境

### 常见坑
- 把配置文件当普通二进制文件处理
- 合并结果无法预览
- 没有键级追踪信息

---

## Phase 8：Resolved View / 最终视图构建器
### 目标
正式引入“最终运行视图”作为系统核心对象。

### 为什么现在做
前面 Phase 都是为它打基础。

### 范围
包含：
- Host Base Layer
- Package Layers
- Config Layers
- Generated Layers
- Override Layers
- 可序列化、可哈希的最终视图

### 核心任务
1. 设计 `ResolvedEntry`
2. 设计 `ResolvedViewBuilder`
3. 实现 ViewHash
4. UI 增加“预览最终视图”
5. Deployment / Launch 改为依赖 ResolvedView

### 测试重点
- 相同输入应生成相同 hash
- 改顺序会改变受影响路径但不会无故全量变化
- UI 模拟预览与实际部署结果一致

### 验收标准
- 部署前能看到最终会生效的结果
- 可通过 ViewHash 判断部署是否过期
- Launch 不再依赖分散状态

### 常见坑
- 把 ResolvedView 和 DeploymentPlan 混为一谈
- 无法稳定序列化导致 hash 不可靠
- View 中混入宿主副作用

---

## Phase 9：Launch Orchestrator / 启动编排
### 目标
让系统从“静态管理器”升级成“运行环境管理器”。

### 为什么现在做
没有统一启动编排，很多高级能力都只能停留在静态部署阶段。

### 范围
包含：
- 启动前检查
- 自动 Resolve / Deploy
- 环境变量
- 启动参数模板
- 前置任务
- 进程监控
- 日志收集

### 核心任务
1. 建立 `LaunchContext`
2. 建立 `LaunchPipeline`
3. 支持启动参数模板
4. 支持环境变量注入
5. 支持 stdout/stderr 收集
6. 记录 Session

### 测试重点
- 部署过期时自动重建
- 前置任务失败时不会误启动
- 启动日志可回放

### 验收标准
- 用户能从统一入口启动任意 Profile
- 启动前状态可自动校验
- 失败点可明确定位

### 常见坑
- 启动逻辑散落在多个按钮事件里
- 只支持完整接管一种模式
- 没有 Session 记录

---

> **实施状态说明（2026-04-24 更新）：**
> Phase 0-9 + UX 优化已全部完成，且构建已收敛至 **0 warnings / 0 errors**。
> 告警治理收尾已完成（CS0105/CS1998/CS8632/CS4014/CS0618/CS0414/CS0649/CS0067/SYSLIB0012，NU1903 已独立处理）。
> 2026-04-24 追加完成前端可读性优化：用户可见术语降级、深色主题未选中态对比增强、WPF 字体渲染修正（Display/ClearType、中文优先字体栈、TextBlock 全局渲染设置）。
> **最新进展（2026-04-30）：** Phase 11 / Phase 12 / Phase 13 已落地，Core 17 轮拆分 + 第十八轮 SDK 完整化（IDeploymentBackend 下沉 + SampleBackend）；`dotnet build UEModManager.sln --configuration Debug` 为 **0 warnings / 0 errors**（**5 个项目协同**），`dotnet test UEModManager.Core.Tests` 为 **552 passed / 0 failed**。
> Phase 10 仍保持实验性 R&D，不作为 v2.0 主线发布阻塞项。
> **后续建议顺序：Core 高 ROI 纯化 → Phase 13 文档/模板补齐 → Phase 10 独立 spike**
> - Core 高 ROI 纯化：优先 DataMigrationService 进度状态机、PackageImportService 压缩包分组/嵌套解压决策。
> - Phase 13 文档/模板：继续补充 Adapter / Backend / Package schema 示例。
> - Phase 10（VFS）实现难度高：需 Windows 内核驱动 / WinFsp / DLL 注入研究，建议单独立项。

## Phase 10：多部署后端与高级虚拟部署
### 目标
把 Deployment Backend 彻底做成可替换能力。

### 为什么现在做
前面已经把“后端不是核心”准备好了。

### 范围
包含：
- Copy
- HardLink
- Symlink / Junction
- Mirror Copy
- 实验性 ProcessScopedVfs
- 实验性 WinFsp Mount

### 核心任务
1. 后端能力描述模型
2. 后端选择器
3. 每个 Host 指定推荐后端
4. 高级后端隔离到 `experimental/`
5. UI 展示后端能力和限制

### 测试重点
- 不同后端输出结果一致性
- 失败回滚一致性
- 后端切换后的恢复能力

### 验收标准
- 同一 Profile 能切换至少两种后端
- 后端失败不会破坏仓库和 Profile
- UI 能明确提示后端适用范围

### 常见坑
- 让高级 VFS 成为主线依赖
- 把进程注入逻辑混进 Domain
- 切换后端需要重设计模型

---

## Phase 11：工程硬化、测试、可观测性

> **实施状态（2026-04-29）：已完成。** 已落地 xUnit Core 测试体系、半结构化日志、诊断包导出、崩溃恢复、健康检查；后续 Core 拆分把测试基线提升到 **552 passed / 0 failed**。

### 目标
把“能跑”升级为“可维护、可诊断、可持续发布”。

### 为什么现在做
没有这一层，后面越复杂越难继续。

### 范围
包含：
- 单元测试
- 集成测试
- 回归测试
- structured logging
- 错误码
- 诊断导出
- 性能监控
- 数据迁移策略

### 核心任务
1. 全链路日志规范
2. 错误码与错误分类
3. 关键路径指标
4. 数据库迁移脚本体系
5. 自动回归测试
6. 崩溃诊断包导出
7. 健康检查页

### 测试重点
- 大量包、深冲突链、异常中断
- 旧版本数据库升级
- 损坏 manifest / 丢失对象仓库文件

### 验收标准
- 关键异常都可定位
- 发布前有自动回归基线
- 用户问题可通过诊断包快速复现上下文

### 常见坑
- 只有文本日志没有结构化字段
- 没有 migration 流程
- 测试只测 happy path

---

## Phase 12：整合包、分享、复现、同步

> **实施状态（2026-04-29）：已完成。** 已落地 `profile.lock.json` 轻量导出/导入与 `profile.bundle.zip` 完整整合包导出/导入，ProfileManagerWindow 已集成 4 个入口。

### 目标
从本地工具升级到“可复制、可分享、可复现”的平台能力。

### 为什么现在做
当前面的模型稳定后，这是最自然、最有价值的扩展。

### 范围
包含：
- `profile.lock.json`
- `modpack.manifest.json`
- 导入 / 导出 Profile
- 环境一致性校验
- 缺失包检查
- Profile 分享
- 可选的元数据同步

### 核心任务
1. 设计 lockfile schema
2. 实现导出当前 Profile
3. 实现导入并检查缺失项
4. 实现一致性检查
5. 可选实现元数据同步

### 测试重点
- 新机器导入 lockfile 的体验
- 缺失包提示是否明确
- 同版本不同仓库路径下的复现能力

### 验收标准
- 用户能导出并分享自己的实例
- 另一台机器能尽可能复现
- 系统能解释无法复现的原因

### 常见坑
- lockfile 依赖本机路径
- 同步混入太多临时状态
- 没有缺失项与不兼容项诊断

---

## Phase 13：SDK、扩展生态、文档体系

> **实施状态（2026-04-29）：已完成第一版，并持续补强。** 已落地 `docs/architecture/overview.md`、4 篇 playbook、findings 记录、`samples/UEModManager.SampleAdapter/`。后续可继续扩充 Backend 模板和包格式示例。

### 目标
让项目从“你自己维护的工具”升级成“可以被别人接入和扩展的平台”。

### 为什么现在做
到这个阶段，内核已经足够稳定，应该开始降低新接入成本。

### 范围
包含：
- Adapter SDK
- Backend SDK
- Package Schema 文档
- 示例项目
- 扩展模板
- 架构文档
- 开发者指南

### 核心任务
1. 提炼公共接口
2. 输出 SDK 文档
3. 提供最小 Adapter 模板
4. 提供最小 Backend 模板
5. 提供 Package Schema 说明
6. 补齐 docs 目录结构

### 测试重点
- 第三方能否按文档接入一个最小 Adapter
- 第三方能否按模板实现一个测试 Backend
- 文档是否足够让新人完成最小示例

### 验收标准
- 新开发者能在 1~2 天内写出一个最小 Adapter
- 文档和模板足以支持外部扩展
- 核心接口有版本策略

### 常见坑
- 接口过度暴露内部状态
- 文档滞后于实现
- 模板不可运行

---

# 10. 数据库设计建议

建议至少包含以下表：

- `hosts`
- `profiles`
- `profile_packages`
- `profile_settings`
- `packages`
- `package_artifacts`
- `generated_artifacts`
- `conflicts`
- `resolved_view_snapshots`
- `deployment_transactions`
- `deployment_operations`
- `media_assets`
- `launch_sessions`
- `diagnostic_exports`

## 设计原则
- 核心逻辑状态入库
- 可重建状态可缓存但不强依赖
- 事务日志与快照分开
- 尽量保留哈希和来源链

---

## 10.1 当前落地快照（2026-04-30，v2.0-rc1 已打）

当前实现已经超过原计划中的"Phase 11 工程硬化"最低要求，进入持续 Core 纯化阶段。**v2.0-rc1 标签已于 2026-04-30 打出（commit 30e4a32）**：

- **解决方案结构**：主项目 `UEModManager/` 负责 WPF / IO / Service 编排；`UEModManager.Core/` 负责 Domain 模型、纯函数、算法、决策；`UEModManager.Core.Tests/` 提供 xUnit 回归网；`samples/UEModManager.SampleAdapter/` 验证第三方 Adapter 接口；`samples/UEModManager.SampleBackend/` 验证第三方 Deployment Backend 接口。
- **Core 约束**：TFM=`net8.0`，单依赖 Newtonsoft.Json，防止 WPF / System.Windows 依赖回灌。
- **验证状态（2026-04-30）**：`dotnet build UEModManager.sln --configuration Debug` → 0 warnings / 0 errors（**5 个项目**：主项目 + Core + Tests + SampleAdapter + SampleBackend）；`dotnet test UEModManager.Core.Tests/UEModManager.Core.Tests.csproj` → **552 passed / 0 failed**。
- **17 轮 Core 拆分 + 第十八轮 SDK 完整化已完成**：Config / Conflict / Recovery / Lock / Health / Logging / Diagnostics / ResolvedViews / DeploymentPlanning / Deployment / Launch / Detection / Profile / Migration / Import / Backends 等关键规则均已有 Core 承载。
- **最近第十八轮（2026-04-30）**：`IDeploymentBackend` 接口从主项目下沉到 Core（`Services/Backends/`），新建 `samples/UEModManager.SampleBackend/`（`SampleMirrorBackend` 镜像复制 + 来源记号 demo），加入解决方案。docs/README.md / architecture/overview.md / writing-deployment-backend.md 全部同步基线到 2026-04-30。
- **v2.0-rc1 标签（2026-04-30）**：commit `30e4a32`，annotated tag 含 313 文件（仅源码 + 文档），ux-copy-audit 与 main 分支同指此 commit；`.gitignore` 已就位阻断 bin/obj / 构建日志 / `.playwright-mcp/` / `recovered_20250320/` / `resume-projects-company-personal.md` / `untitled.pen` / `.claude/` 等。**未 push 到远程**——需用户提供 GitHub URL。

**下一步建议（按 ROI）：**
1. ~~仓库 git 初始化 + 打 v2.0-rc1 标签~~（✅ 2026-04-30 commit 30e4a32）
2. **远程仓库 push**（需用户提供 GitHub URL，建议 `github.com/velist/UE-Game-ModManager`）
3. **Windows 实机手动验收主流程**（启动 / 导入 / 部署 / 冲突 / 一键启动），通过后升 v2.0 正式标签
4. 主项目继续审视可拆纯函数（边际收益已显著下降）
5. Phase 10 VFS 仍建议作为单独 R&D spike

---

# 11. 测试体系设计

## 11.1 Domain Tests
测试：
- 冲突求解
- 依赖图
- 配置合并
- View 构建
- Hash 稳定性

## 11.2 Adapter Tests
测试：
- Host 识别
- 路径映射
- 能力矩阵
- 启动上下文

## 11.3 Deployment Tests
测试：
- Copy / Link / Junction
- Rollback
- Interrupted Apply
- Cross-volume 行为

## 11.4 Launch Tests
测试：
- 启动前校验
- PreTask 链
- 环境变量注入
- 日志采集

## 11.5 Integration Tests
完整链路：
- 导入包
- 建 Profile
- 启用多个包
- 冲突求解
- 构建 View
- 部署
- 启动

---

# 12. 团队协作与编码规范

## 12.1 分层纪律
- ViewModel 不直接写文件
- UI 不直接访问 DB
- Adapter 不直接部署
- Backend 不直接求解业务规则

## 12.2 命名建议
- UseCase：`CreateProfileUseCase`
- Service：`ConflictAnalyzer`
- Repository：`IPackageRepository`
- DTO：`PackageDto`
- Snapshot：`ResolvedViewSnapshot`

## 12.3 提交建议
- 一个 PR 只做一个主题
- 涉及 schema 变化必须带 migration
- 涉及规则变化必须带测试

## 12.4 文档纪律
- 新增核心类必须补架构说明
- 新增扩展点必须补最小示例

---

# 13. 新开发者 7 天上手路线

## Day 1
理解 4 个对象：
- Package
- Profile
- ResolvedView
- DeploymentTransaction

## Day 2
阅读 3 条流程：
- Import
- Resolve
- Deploy

## Day 3
跑通本地环境，观察数据库与仓库目录

## Day 4
阅读一个 Adapter 和一个 Backend 的最小实现

## Day 5
实现一个小功能：
- 给 Profile 增加标签
- 或给 Package 增加备注字段

## Day 6
写一个测试：
- 冲突求解或配置合并

## Day 7
独立完成一个最小改动 PR

---

# 14. 里程碑与发布策略

## M1：实例化基础
完成：
- Phase 0
- Phase 1

结果：
- 系统正式进入 Profile 模式

## M2：仓库与部署骨架
完成：
- Phase 2
- Phase 3

结果：
- 系统不再依赖“直接改宿主目录”作为主模型

## M3：冲突与生成物治理
完成：
- Phase 4
- Phase 5

结果：
- 多包场景开始可维护

## M4：宿主适配与配置系统
完成：
- Phase 6
- Phase 7

结果：
- 从 UE 工具升级为可扩展内核

## M5：最终视图与启动编排
完成：
- Phase 8
- Phase 9

结果：
- 从静态工具升级为运行环境管理器

## M6：高级后端与硬化
完成：
- Phase 10
- Phase 11

结果：
- 项目具备长期演进与发布能力

## M7：平台化与生态
完成：
- Phase 12
- Phase 13

结果：
- 项目具备整合包、复现和扩展生态能力

---

# 15. 常见坑与规避建议

## 坑 1：太早追求真 VFS
规避：
- 先把架构兼容 VFS
- 不要让 VFS 决定核心模型

## 坑 2：继续让宿主目录承担真相源
规避：
- 所有状态都从 DB + 仓库 + View 重建

## 坑 3：把“多游戏支持”做成更多 if-else
规避：
- 必须走 Adapter

## 坑 4：把优先级误当冲突系统
规避：
- 必须有冲突记录、解释和人工 override

## 坑 5：忽略配置层
规避：
- Phase 7 必须单独做，不要混在普通文件里凑合

## 坑 6：没有 Overwrite / Generated 模型
规避：
- 所有非原始输入都纳管

## 坑 7：没有日志、快照、诊断包
规避：
- Phase 11 不可省略

## 坑 8：没有文档和模板就开放扩展
规避：
- SDK 之前先文档驱动

---

# 16. 一句话结论

**这次升级的本质，不是“继续把一个 UE MOD 管理器做大”，而是“把它重构为一个以 Profile、非侵入部署、冲突治理、Overwrite、Resolved View、启动编排和多后端为核心的扩展编排内核”。**

当这个内核稳定后：

- 你才能真正支持多游戏
- 才能安全演进到更高级的 VFS / 注入后端
- 才能支持整合包、复现、分享与生态扩展
- 才能让新开发者快速理解并接手

---

# 附：最务实的实施顺序（推荐）

## 第一优先级
- Phase 1：Profile / Instance
- Phase 2：Package Repository
- Phase 3：Deployment Planner
- Phase 4：冲突治理系统

## 第二优先级
- Phase 5：Overwrite / Generated
- Phase 6：Host Adapter
- Phase 7：Config Merge

## 第三优先级
- Phase 8：Resolved View
- Phase 9：Launch Orchestrator
- Phase 10：高级后端
- Phase 11：工程硬化
- Phase 12：整合包与复现
- Phase 13：SDK 与生态

---

# 附：给第一次接触项目的开发者的最小理解模型

只要先理解下面四句话，就已经足够开始开发：

1. **Package 是输入。**
2. **Profile 决定当前组合。**
3. **Resolved View 是最终会生效的结果。**
4. **Deployment Backend 只负责把结果呈现给宿主。**
