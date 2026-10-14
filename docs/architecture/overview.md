# UEModManager 架构总览

**版本：** 2.1.1 源码
**最后更新：** 2026-10-14
**面向：** 接手项目的开发者、做扩展的第三方

---

## 项目分层

主项目目前混用 ViewModel 与 code-behind。运行时 MOD 数据来自 `PackageRepository`、`ObjectStore` 和 `ProfileService`；旧 `{game}_mods.json` 仅保留迁移读取。客户端支持邮箱登录，MOD 文件与方案存储在本地。版本变化见[更新日志](../../CHANGELOG.md)。

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
│  - 覆盖领域规则及文件系统辅助                            │
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
- `Services/Import/` CompressedArchive + ImportFileKindClassifier + ModFileGrouper + PreviewImageSelector + ArchiveImportUnitResolver
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

另有 `UEModManager.Tests` 验证主项目的服务、临时文件、SQLite、主题与窗口资源。Worker 测试覆盖端点、workerd 运行时和桌面服务协议；外部服务使用测试替身。运行命令见[文档索引](../README.md)，最新结果以 CI 输出为准。

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

<a id="load-order-conflicts"></a>

### 4. 部署路径与同路径文件裁决

MOD 和插件按 `PackageKey` 使用独立部署目录。例如，两个包的 `shared.pak` 分别写入 `Mods/a/shared.pak` 和 `Mods/b/shared.pak`；而插件包带的是相对游戏根的文件路径（`ue4ss/Mods/mods.json`、`version.dll`），落在游戏根下的同一位置上。

部署层仍需要一套"同一路径被多个包声明时落哪一份"的规则，由 `ConflictDetector.ComputeLoadConflictKey` + `ConflictResolver` 提供：前者忽略 `PackageKey` 计算归一化键（MOD 用 `modPath/RelativeTargetPath`，其他用 `gamePath/TargetRootPath/RelativeTargetPath`），后者按优先级与 `InstanceProfile.ConflictOverrides` 求解。对应回归见 [`ConflictDetectorTests`](../../UEModManager.Core.Tests/Services/Conflict/ConflictDetectorTests.cs)。

**这一层只服务部署，不再是玩家看到的冲突判定。** 玩家看到的一切来自下文的冲突模块（`Services/Conflict/`）：它直接读容器的资产索引判断"哪几个 MOD 改到了同一批资源"，粒度是**资产**而不是文件路径，因此 MOD 与 MOD 之间哪怕部署路径完全不同也能检出冲突。两者是正交的：冲突模块负责"该关掉谁"，部署层负责"关掉之后同路径落盘谁"。

`ResolvedViewBuilder` 用 `ResolvedEntry.LoadConflictKey` 携带归一化路径，覆盖规则据此命中候选，败者条目不再进入最终视图，下一次部署会按托管归属把它从游戏目录移除。只有"败者与胜者落在不同目标路径"时才丢弃败者 —— 目标路径相同的配置文件必须保留全部候选，键级合并（`ConfigMergeEngine`）依赖它们才能把 `OnlyA` / `OnlyB` 合成一份。

<details>
<summary>历史：同路径裁决曾经是玩家看到的冲突面板（2026-09-17 ～ 09-21，已由冲突模块取代）</summary>

面板曾是"路径级三态 + 胜者/败者"：每行一条目标路径，标 `内容相同` / `内容不同` / `已决定`，按钮写"设为胜者""整组改用它""自动解决全部""应用"。实测下来这套呈现对普通玩家不成立：

- 装了 20 个 MOD 报出 **582 条冲突**，其中 580 条是"内容完全相同的重复"（两个包各带一份同样的 UE4SS 载荷，装哪个都一样），真正的分歧只有 2 处。用户会数自己的 MOD 个数然后判定软件在乱报。
- "胜者""败者""应用""优先级"是实现的词，不是玩家的词。用户的心智模型是"这两件披风我都想要，但只能开一个"。
- 决策与呈现分家：点"应用"只设 `DialogResult` 就关窗、不写任何状态；"自动解决全部"只重画一次界面（输入相同、输出必然相同，纯空操作）。

该面板及其分组模型（`ConflictGrouping`、`ConflictAnalysisResult`、`ConflictResultWindow`、`ConflictAnalyzer`）已整体删除，见下一节。

</details>

### 4.1 非 MOD 包的部署路径

`DeploymentTargetPathBuilder` 对非 Mod 包（Plugin / Config）分两种：

- **声明了"路径即安装位置"**：直接落到 `{gamePath}/{TargetRootPath}/{RelativeTargetPath}`，**不套包目录**。命中条件是 `package.PreservesInstallLayout == true`，或 `RelativeTargetPath` 自带目录层级（如 `ue4ss/Mods/mods.json`）。这两类都是文件夹 / 压缩包整体导入的产物，它们的相对路径就是"相对游戏根的安装位置"。
- **没有声明、且是裸文件名**（如 `dll.dll`）：落到 `{gamePath}/{TargetRootPath}/{packageKey}/{RelativeTargetPath}`，靠包目录隔离同名文件。老的手动导入单文件插件包走这条，行为与修复前一致（`PreservesInstallLayout` 缺省为 `false`）。

**`PreservesInstallLayout` 的语义（2026-09-21 修复）。** 裸文件名加包目录隔离的初衷是"防止两个包的同名 `dll.dll` 互相覆盖"，但对目录 / 压缩包整体导入的产物是错的：这类包里裸文件名意味着"**必须在游戏根**"。现场症状正是如此 —— UE4SS 的 `dwmapi.dll` 与 CSharpLoader 的 `version.dll` 被套进包目录，落到
`.../Win64/超级筋斗云/dwmapi.dll` 与 `.../Win64/黑神话26年8月最新版自动闪避MOD/version.dll`，
而 Windows 加载器只在 `b1-Win64-Shipping.exe` 同目录找代理 DLL，于是三个插件 MOD 一个都没挂上（布局看起来"有文件"，对游戏完全无效）。判据不能靠"路径里有没有斜杠"猜，所以改为在 `Package` 上显式记录：`ImportFilesAsPackageAsync` 收到 `keepDirectoryStructure: true` 时置 `true`，`ImportPluginAsync` 的目录分支置 `true`、单文件分支保持 `false`。该字段随 `manifest.json` 一起持久化 —— 整合包（lock）导入是用 `PackageManifest.ToPackage()` 重建 `Package` 的，漏掉它就会退回旧行为。

同名冲突不再靠目录隔离处理，而是靠**冲突检测 + 覆盖规则**：两个包都带 `dwmapi.dll` 时 `ConflictDetector` 按 `ComputeLoadConflictKey` 报出同路径冲突（内容相同 → `Info`，内容不同 → `Warning`），`ResolvedViewBuilder` 按优先级 / 用户覆盖选一份落盘。目录隔离恰恰是把代理 DLL 关进无效位置的元凶。回归覆盖见 [`DeploymentTargetPathBuilderTests`](../../UEModManager.Core.Tests/Services/DeploymentPlanning/DeploymentTargetPathBuilderTests.cs) 与 [`ConflictDecisionIntegrationTests`](../../UEModManager.Tests/Services/ConflictDecisionIntegrationTests.cs) 的 `LayoutPreservingPlugins_IdenticalProxyDll_ResolvesToOneFileAtGameRoot`。

导入侧由 `ArchiveTargetPathResolver` 保证层级正确：剥掉 `b1/Binaries/Win64`、`Content/Paks` 等游戏根锚点（游戏根本身已经是 `.../b1/Binaries/Win64`），并刻意**不**剥掉 `ue4ss`、`CSharpLoader` 这类安装锚点目录。包装目录前缀最多只剥一层。

**剥包装目录必须发生在剥游戏根锚点之前（2026-09-21 追加）。** 这两步的顺序不能颠倒，否则会漏掉同一包里"没命中锚点"的文件。压缩包的真实形状是 `MOD名/自动闪避/b1/Binaries/Win64/version.dll` 加 `MOD名/说明与事项.txt`：剥锚点会把前者削成裸文件名 `version.dll`，把后者留成 `MOD名/说明与事项.txt`。若在这之后再找"所有文件共有的包装目录"，最短的那条路径已经只剩裸文件名，共有前缀再也算不出来 —— 说明文档就带着 MOD 名目录落进游戏根，磁盘上留下一个以 MOD 命名的杂项目录（`Win64/黑神话26年8月最新版自动闪避MOD/说明与事项.txt`）。因此共有前缀一律在**未剥锚点**的相对路径上计算：新纯函数 `ArchiveTargetPathResolver.ResolveRelativePath` 只算相对解压根的路径、不剥锚点，`StripCommonPrefix` 在它上面跑，结果作为 `commonPrefix` 传回 `Resolve`，`Resolve` 内部固定按"先 `StripPrefix(commonPrefix)`、再 `StripLeadingGameAnchor`"处理。回归覆盖见 `ArchiveTargetPathResolverTests.Resolve_CommonPrefixBeforeAnchor_StripsModNameFromFileWithoutAnchor` 与 `FolderImportIntegrationTests.FolderWithProxyDllAndTopLevelReadme_LeavesNoModNamedDirectoryAtGameRoot`。

拖入文件夹与压缩包共用同一套分流逻辑：只有 `.pak/.ucas/.utoc` 参与 MOD 分组，其余载荷整包保留目录结构。不能用 `EngineProfile.ModFileExtensions` 分流 —— 它含 `.json`，会把 UE4SS 的 `mods.json` 当 MOD 处理，并让同包的 `.lua`/`.dll` 变成无人认领的孤儿文件被静默丢弃。

### 4.2 导入单元：一个顶层目录 = 一个 MOD

**现场症状（2026-09-17 修复）。** 导入 `0916.zip`（435 MB、1310 条目、7 个顶层 MOD 文件夹、17 个 `.pak`）后，仓库里只多出一个名叫 `0916` 的巨型包（`Kind=Plugin`、594 个 artifact，混着 UE4SS 运行时与 CSharpLoader），界面里只有一个条目 —— 用户看到“一个 MOD”，实际整包内容都在里面。

**原因。** `ImportCollectedFilesAsync` 把整包所有 `.pak` 丢进同一个分组池按基础名分组，再把**全部**非 MOD 载荷折成一个以压缩包名命名的包。压缩包内顶层目录的信息在这一步被丢掉，打包者用“一个文件夹 = 一个 MOD”表达出来的结构完全消失。

**更严重的后果是冲突检测失效。** `ConflictDetector` 对每个目标路径先按 `PackageKey` 去重（见上一节），只把**不同包之间**的覆盖报给用户。并包之后所有文件同属一个 `PackageKey`，去重后只剩一个声称者，`distinct.Count <= 1` 永远成立 —— 实测该压缩包内有 583 处跨 MOD 路径重叠（581 处内容相同、2 处内容不同），全部漏报为 0 个冲突。这也解释了“第一次安装检测出冲突，重装同一个合集却报 0 个”的体感：不是检测器退化。

**规则。** `ArchiveImportUnitResolver.Partition` 以解压根为基准，取文件路径的**第一段**作为导入单元名；第一段不是目录（压缩包根下直接平铺 `SomeMod.pak`）或路径在根之外时归入根单元（`RootUnitName == ""`），根单元以压缩包名命名。每个单元内部再按原有逻辑分组：单元内没有 `.pak` 时整单元做成一个保留目录结构的插件/配置包；有 `.pak` 时按基础名分成 MOD 包，剩下的非 MOD 载荷另成一个包。单元名天然互不相同，此前每个单元抢同一批基础名而被迫产生的 `_2` 后缀因此不再出现。

实测同一压缩包切分后得到 20 个包（`超级筋斗云` 584 个 artifact、`超级识破` 572 个……），冲突检测浮现 **582 条**，其中 2 条 `Warning`（`CSharpLoader/b1cs.ini` 与 `version.dll` 在两个 MOD 间内容确实不同），其余为 `Info`（内容相同的重复）。新建包仍可能与磁盘上既有包重名，此时依旧走 `EnsureUniquePackageName` 加序号。

**单 MOD 无包装目录的退回规则（2026-09-21 追加）。** 上面那条规则有个直接后果：以下两种常见输入的第一段是**项目名**而不是 MOD 名，会得到名叫 `b1` 的包（第二个变成 `b1_2`），MOD 真名全丢。

- 用户单独把解压后的「超级识破」文件夹拖进来，文件夹内部直接是 `b1\Binaries\Win64\...` —— 此时 `rootDirectory` 就是「超级识破」本身，第一段是 `b1`。
- 某个单 MOD 压缩包的内容直接是 `b1\Binaries\...`，没有外层 MOD 名目录。

因此 `ArchiveTargetPathResolver.InstallAnchors` 纳入 `b1`，并新增纯函数 `ArchiveImportUnitResolver.LooksLikeSingleModLayout(units)`：**任一单元名命中安装锚点目录名**（`b1`、`Binaries`、`Content`、`ue4ss` …）时，说明这不是"一个压缩包里装了多个 MOD"，而是"这一个 MOD 自带游戏目录层级"。`ImportCollectedFilesAsync` 在决定 `multiUnit` 时据此退回单单元路径，改用 `archiveName`（压缩包名 / 文件夹名）命名，并记一条 `LogWarning` 说明"若这是把多个 MOD 打进了同一个压缩包，请重新打包"。

判断只认锚点名，不改动切分本身：`0916.zip` 里每个单元名都是 MOD 名，不受影响；一个压缩包同时含 `b1\...` 与 `超级识破\...` 两个顶层属于打包者错误，按单单元处理后留痕，不做智能拆分。回归覆盖见 `ArchiveImportUnitResolverTests.LooksLikeSingleModLayout_*` 与 [`FolderImportIntegrationTests`](../../UEModManager.Tests/Services/FolderImportIntegrationTests.cs) 的 `SingleModFolder_WithoutWrapperDirectory_UsesFolderNameNotB1` / `SingleModArchive_WithoutWrapperDirectory_UsesArchiveName`。

对应回归覆盖见 [`ArchiveImportUnitResolverTests`](../../UEModManager.Core.Tests/Services/Import/ArchiveImportUnitResolverTests.cs) 与 [`FolderImportIntegrationTests`](../../UEModManager.Tests/Services/FolderImportIntegrationTests.cs)。

### 4.3 长操作的加载反馈

`LoadingOverlay` 早先只是 `MainWindow.xaml` 里一段**没有任何消费者**的 XAML：全项目没有任何代码或绑定去切换它的 `Visibility`，`MainViewModel.IsLoading` / `LoadingMessage` 也没有 XAML 消费者（`BeginLoading` 一直在改这两个属性，只是没人听），于是遮罩永远停在 `Collapsed`。导入整段（解压 + 写文件，实测约 15 秒）因此完全无反馈。

现在抽成 `Views/LoadingOverlay.xaml` 用户控件（转圈弧 + 提示文字，颜色与字体全部取自既有 `CyberDarkTheme` 令牌），由 `MainWindow` 绑定 `IsLoading` / `LoadingMessage`，`ImportConfirmDialog` 在导入期间显式显示。控件内所有资源引用都用 `DynamicResource`：`StaticResource` 要求资源在元素实例化时已处于同一 namescope，而遮罩是独立控件、资源由调用方提供，`DynamicResource` 沿逻辑树向上解析才能在真实 `App.Resources` 与测试时内联到 `window.Resources` 两种场景下都命中。

`ImportConfirmDialog.ConfirmButton_Click` 同时改为 `await Task.Run(() => _importService.ImportAsync(...))`：解压与落盘整段是同步文件 IO，直接在 UI 线程 `await` 会把消息泵堵死，遮罩根本渲染不出来。导入链路（`PackageImportService` / `ObjectStore` / `PackageRepository` / `ArchiveExtractor`）没有任何一处碰 `Dispatcher`，`PackageImported` 与 `PackagesChanged` 也都没有订阅者，因此整批搬离 UI 线程是安全的。

### 4.4 按钮内边距与自适应宽度

**现场症状（2026-09-19 修复）。** 弹窗的确认按钮"样式没显示完全，文字溢出"：文字顶到圆角上，首尾字符被切掉。

**根因在主题，不在某个窗口。** `CyberPrimaryButton` / `CyberSecondaryButton` 的 `ControlTemplate` 里，承载内容的 `Border` 上既没有 `Padding="{TemplateBinding Padding}"`、样式也没有默认 `Padding`，因此调用方写的 `Padding="16,0"` 被**静默丢弃**——`CyberMessageBox.CreateButton` 早就写了 `Padding` 和 `MinWidth=80`，从未生效。内边距一丢，按钮缩到只剩内容宽度，文字与圆角直接相切。各弹窗再把按钮宽度写成固定值（60~140），长文案与英文译文进一步压边。

**修法。** 模板消费 `Padding` 并给出默认值 `16,0`；全部固定 `Width` 改为 `MinWidth`，让按钮按内容撑开。这与本仓库既有的规则一致：`CyberGhostButton`、`CyberCompactButton` 等早就是 `TemplateBinding Padding`，只有这两个按钮漏了。

同一批还修掉 `ManagementCenterWindow` 底部一处分列缺失：说明文字与"导出诊断包"按钮同处一个没有 `ColumnDefinitions` 的 Grid 单元格，必然互相压住；现改为 `*` / `Auto` 两列，长文案走 `TextTrimming`。

回归覆盖见 [`ButtonTextFitTests`](../../UEModManager.Tests/Views/ButtonTextFitTests.cs)：在中文与英文下遍历全部窗口的可见按钮，校验"内容 + 内边距 + 边框"能被实际宽度容纳。它刻意不重新挂载按钮 Content（确认按钮的内容是内联 `StackPanel`，已有父级，重新挂载会量得 0 宽度而漏掉出问题的那个按钮），并带"至少检查到 20 个按钮"的下限断言以防遍历失效后空转。

<a id="conflict-decisions"></a>

### 4.5 冲突模块：一个冲突组 = 一个决策

**重做理由（2026-09-22）。** 旧面板的判据是"文件路径 + 胜者/败者"，对玩家不成立：装 20 个 MOD 报 582 条、按钮写着实现的词、决策与呈现分家（点"应用"什么都不写）。重做后判据换成**资产**：UE 的 `.pak` / `.utoc` 内部才是真正的冲突现场 —— 两个 MOD 完全可以部署到不同目录却替换同一张 `T_WuKong_Born_Cloth_Ring_D.uasset`。

#### 玩家看到的面板

```
冲突检测                                 3 组冲突 · 1 处重复 · 2 个共享组件

┌ 指头大佬-披风  ⇄  红底龙纹披风                      [禁用 指头大佬-披风] [禁用 红底龙纹披风] ┐
│ 都修改了 2 个相同资源：1 张贴图、1 个模型。同时开启时只有一个会生效。                     │
│ ▸ 查看资源列表                                                                            │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

- **一个组 = 一个决策。** 两成员给"禁用 X"（每个成员一个），三成员及以上给"只保留 X"。按钮上写 MOD 名，不出现"胜者""败者""覆盖规则""优先级"。
- **不显示"当前生效的是谁"**：那取决于引擎的加载顺序，本程序不掌握，显示出来就是把猜测当事实。
- 组按共同资源数降序；内容相同的重叠不进组（那是"重复"，不是"冲突"）。
- 面板底部只有一个"关闭"。每个动作点击即生效并重新部署，**没有"应用"按钮**。
- 已删除的入口：整组改用它 / 改用它 / 撤销决定 / 清除全部决定 / 自动解决全部 / 设为胜者。

#### 流水线

```
包 ──→ AssetIndexService.GetIndexAsync ──→ ModAssetIndex（AssetEntry 列表）
        （按引擎策略：ContainerAssets / FilePath）
                    │
   AssetConflictDetector.FindOverlaps  ← 找出被 2 个以上包提供的资产键
                    │
   AssetIndexService.FillFingerprintsAsync  ← 只对重叠条目补算内容指纹
                    │
   AssetConflictDetector.Build  ──→ ConflictReport
        （共享组件归类 → 整包重复 → 无法分析 → 聚合 → 隐藏内容相同 → 分组 → 裁决）
```

`Build` 的步骤顺序是有语义的，不能重排：① 非 MOD 包的条目先过 `SharedComponentClassifier` 归入共享组件或丢弃说明文件；② `ContentHash` 相同的包进 `Duplicates` 且**不再参与后续比对**；③ `Encrypted` / `Unsupported` / `Failed` 的包进 `Unanalyzable`（`Partial` 参与，有什么比什么）；④ 按 `AssetKey` 聚合所有者；⑤ 指纹全同的重叠计入 `IdenticalOverlapsHidden` 并丢弃（任一指纹为空则**保守视为不同**，宁可报冲突不可漏报）；⑥ 剩余按"所有者包集合"合并成组；⑦ 组按 `SharedAssets.Count` 降序；⑧ 共享组件裁决。

#### 指纹

判据是"内容是否真的不同"，所以要读到字节：

- **pak**：条目数据在 `Offset + 条目头长度` 处，先按 `CompressionMethod` 与 `BlockCount` 算出头长再读 `StoredSize` 字节算 SHA1。
- **utoc**：ChunkMetas 里自带 32 字节 `ChunkHash`，直接就是内容哈希，**不用读 `.ucas`**。
- 算指纹之前先比 `Size + StoredSize + CompressionMethod`：三者有任何不同就已能断定内容不同，绝大多数"确实不同"的比较因此不碰磁盘。退化路径是 `SizeOnly`（只够判断"肯定不同"，判不了"肯定相同"）。

#### 容器格式要点

见 [`docs/playbooks/conflict-module.md`](../playbooks/conflict-module.md)。两处最容易踩的：

- **pak 页脚有 +2 字节变体。** 黑神话·悟空的 pak 在 Version 之后多 2 字节，`IndexOffset` 因此要再往后挪 2 字节。判定方式是"先按标准布局读、校验 `0 < IndexOffset` 且 `IndexOffset + IndexSize <= M - 17` 不成立就按变体再读一次" —— 而不是按字段名匹配某版特有字段（那个字段的内容会随版本漂移，而"Offset 落点是否在页脚之前"是格式本身的不变量）。
- **utoc 的路径与指纹分放两处。** 路径在文件末尾的 `FIoDirectoryIndexResource`（名字全部下沉到字符串表的目录树，`FirstChild`/`NextSibling` 串兄弟），指纹在紧邻的 ChunkMetas 里。没有目录索引（未置 `Indexed` 或 `DirectoryIndexSize == 0`）时降级为 `Partial`，改用 ChunkId 前 8 字节的包 ID 十六进制作键。

#### 共享组件

`ue4ss/`、`CSharpLoader/` 以及代理 DLL 是"带一份就够"的运行时，多个 MOD 各带一份是常态，不该报成冲突。`SharedComponentClassifier` 按引擎的 `SharedComponentRule[]` 归类，归类后的条目**移出参与比对的集合**，单独裁决：内容指纹全同 → 保留 `indexes` 里靠前者，理由"内容相同"；不同 → 取 PE 版本最高，理由"版本较高 {v}"；都没有版本 → 取文件较新。**内容不同的那些**会写成 `ConflictOverrides` 覆盖规则，部署层据此决定同路径落盘哪一份；内容相同的不写（没有行为差异，只会撑大方案文件）。

#### 主列表与启动中心

- MOD 卡片角标：红色数字 = 与几个已启用 MOD 冲突（`ModInfo.ConflictCount`，0 时隐藏）；灰色"重复"标签（`ModInfo.IsDuplicate`）。
- 卡片右键"查看冲突"打开面板并滚到该组。
- 启动中心的冲突步骤读 `ConflictReport.Groups.Count`：0 → `无 MOD 冲突`，N → `{N} 组 MOD 冲突待处理`。

#### 导入侧三处

1. **重复内容拦截**：导入时按"逐文件哈希的多重集"判重（不用 `Package.ContentHash` —— 它把 `RelativeSourcePath` 也算进去，同一个 MOD 换个文件夹名导入就会算出不同的值）。命中即清理本次落盘的文件并返回 `仓库已有相同内容的 MOD：{已有包名}`。压缩包与直接拖入 `.pak` 两条路径都要拦。
2. **附件不部署**：单元根上的 `readme*` / `说明*` / `*.txt` 不进包。判据是 `SharedComponentClassifier.IsAttachment`，而单元边界是**解压根下的第一段目录**（见 §4.2），所以「MOD名/steam属性口令.txt」在这里看是低了一层。两重区分缺一不可：

   - **只有一个顶层目录**（`MOD名/`）→ 那层是包装，说明文件就在单元根上，计入附件；
   - **那层是安装锚点**（`ue4ss/`、`b1/`、`content/` …，名录见 `ArchiveTargetPathResolver.IsInstallAnchor`）→ 不是包装。否则用户单独拖入解压后的 UE4SS MOD（单元内容直接就是 `ue4ss/Mods/…`）时，`load_order.txt` 会被当说明丢掉；
   - 有多个顶层目录（`ue4ss/` 与 `b1/` 并列）→ 那些是真实安装位置，里面的 `mods.txt` 是运行时的一部分，同样不能动。
3. **导入后预热**：`AssetIndexService.WarmUpAsync` 后台建索引，用户打开面板时不必再等一遍。

#### 回归覆盖

[`AssetConflictDetectorTests`](../../UEModManager.Core.Tests/Services/Conflict/AssetConflictDetectorTests.cs)、[`PakIndexReaderTests`](../../UEModManager.Core.Tests/Services/Conflict/PakIndexReaderTests.cs)、[`IoStoreTocReaderTests`](../../UEModManager.Core.Tests/Services/Conflict/IoStoreTocReaderTests.cs)、[`SharedComponentClassifierTests`](../../UEModManager.Core.Tests/Services/Conflict/SharedComponentClassifierTests.cs)、[`RealPakSmokeTests`](../../UEModManager.Core.Tests/Services/Conflict/RealPakSmokeTests.cs)（对 `D:\UEModManager\Repository` 的真实 pak 跑一遍）、[`ConflictModuleIntegrationTests`](../../UEModManager.Tests/Services/ConflictModuleIntegrationTests.cs)（真实 pak → 导入 → 索引 → 分析 → 禁用）、[`ConflictWindowLayoutTests`](../../UEModManager.Tests/Views/ConflictWindowLayoutTests.cs)（离屏渲染：按钮文案、无内部词汇、长 MOD 名不撑出面板）。

### 4.6 按钮圆角统一 8px

同批次反馈："所有的圆角矩形按钮，圆角统一为 8px"。修复前同一屏上的圆角有 **6 / 7 / 10 / 14** 四种：主工具栏 14、侧栏导航 10、视图切换 6、方案卡片里的新建按钮 10、存储位置向导 7 —— 视觉上像五套不同的控件。

主题令牌新增 `RadiusButton = 8`，四个按钮样式（`CyberPrimaryButton` / `CyberSecondaryButton` / `CyberGhostButton` / `CyberCompactButton`）与两处窗口自带模板全部改指它，改一处即全局生效。主窗口、启动中心、设置的二十余个"Border 充当按钮"也统一到 8。

**刻意不动的**是圆形按钮（圆角 = 边长一半，如头像、圆形图标钮）与卡片/图标预览框（`CardRoot`、`GameIconBorder`）：它们不是"圆角矩形按钮"，改成 8 会把圆变方、把卡片外观打散。回归测试按"可点击 + Cursor=Hand + 非正方形 + 名称不含 Card/Icon/Avatar/Badge/Thumbnail"识别按钮角色，并对检查数量设下限防止退化成空转。
### 5. 为什么 IDeploymentBackend 接口在 Core？

下沉到 Core 后，`samples/UEModManager.SampleBackend` 仅引用 Core 即可独立编译，
无需拖入 WPF / 主项目。这是 Phase 13 SDK 的关键设计。

### 6. 引擎规则维护在哪里？（新增游戏要改哪些地方）

**引擎规则以 `UEModManager/Models/EngineProfile.cs` 的静态表维护**——
扩展名集合、直接导入扩展名、文件对话框过滤器、默认 MOD 路径模式、分组优先级、
冲突策略（`ConflictStrategy`）与共享组件规则（`SharedComponentRules`），全部按 `EngineType` 索引。

新增一款游戏需要改**四处**：

| # | 位置 | 改什么 |
|---|---|---|
| 1 | `Services/GameConfigService.cs` `GetAvailableGames()` | 加入可选游戏列表 |
| 2 | `Services/GameConfigService.cs` `BuiltInGames` | 标记为内置游戏 |
| 3 | `Services/GameConfigService.cs` `GetEngineType()` | 游戏名 → EngineType 映射 |
| 4 | `Models/EngineProfile.cs` `Profiles` 字典 | 该引擎尚未登记时新增一条 |

**UE 之外的新引擎**还要额外决定冲突策略与共享组件规则（见
[`docs/playbooks/conflict-module.md`](../playbooks/conflict-module.md)）：
`ConflictStrategy.FilePath` 按文件路径比对（默认），`ContainerAssets` 才去读 `.pak` / `.utoc`。
带"一份就够"的运行时目录（BepInEx、MelonLoader 之类）要在 `SharedComponentRules` 里登记，
否则每个 MOD 各带一份会被报成冲突。

路径/可执行文件的自动探测关键词另见 `Views/GamePathDialog.xaml.cs`
（`gameKeywords` 与 `GetGameKeywords`）和 `GameConfigService.AutoDetectExecutable`。

> **不要再造 Adapter 抽象。** 2026-07 之前存在一套 `IHostAdapter` + `HostAdapterRegistry`
> 扩展点，文档承诺"新增游戏 = 新增 Adapter 类，核心代码零改动"，但**全部 14 个接口成员
> 生产调用点为 0**：注册表只被存进 `MainViewModel` 的字段就再没被调用过。第三方照文档写出的
> Adapter 编译得过、注册得进 DI，却永远不会被执行。该体系已整体删除，避免继续误导。

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

## 相关文档

- [文档索引](../README.md) — 构建、验证与贡献入口
- [更新日志](../../CHANGELOG.md) — 版本变化
- [部署后端指南](../playbooks/writing-deployment-backend.md)、[Core 服务指南](../playbooks/writing-core-service.md)、[包格式说明](../playbooks/package-manifest-format.md)
