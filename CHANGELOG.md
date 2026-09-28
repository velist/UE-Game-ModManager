# 更新日志 / Changelog

本文档记录UE Mod Manager的所有版本更新内容。

## 第五轮实测问题修复 - 2026-09-23

- **冲突面板"一键处理 / 禁用"看起来无效**：磁盘上其实已经生效（方案文件、`~mods` 目录、日志三方印证），失效的是主窗口显示。冲突面板是独立窗口，关掉后没人通知主窗口重算左栏"已启用 / 已禁用"计数（这几个数字是 code-behind 手工算的）。现在 `MainViewModel` 在每次仓库刷新、`AllMods` 重建后于 UI 线程触发 `RepositoryRefreshed`，主窗口订阅一次即可，不再依赖各调用点自己记得重算。面板动作的异常改为写日志并在状态行显示；批量禁用时"找不到 MOD"（接线错误，报错）与"本来就是禁用"（无事可做）分开处理。
- **冲突检测弹窗右上角 × 点了没反应**：`CyberModalWindow` 自绘标题栏的关闭按钮发的是 `SystemCommands.CloseWindowCommand`，WPF 不会替窗口执行它，没有绑定时按钮的 CanExecute 为 false。只有自己在 XAML 里写了 `CommandBindings` 的窗口能关，冲突面板、输入框、消息框、捐赠、MOD 详情这 5 个都关不掉。现在 `WindowCommands.RegisterClassBindings` 在 App 启动时给所有 `Window` 注册类级绑定（关闭 / 最小化 / 最大化 / 还原），窗口自带的实例级绑定仍然优先。
- **列表模式选中样式难看**：选中态把整条描边换成主色，底部 1px 分隔线跟着变青，多选时就是一排青色横线，底色 `ItemActiveBrush` 又和表面色几乎一样。现在底色用主题已有的选中色 `SetupSelectedBrush`（悬停叠加 `SetupSelectedHoverBrush`），左侧是一根独立的圆角指示条，分隔线保持中性色。侧边栏分类的选中项同步改为与"全部 MOD"等导航项一致的青色文字。
- **列表右侧写着分类，左侧"分类目录"却是空的，也没有地方改分类**：分类的权威存储是 `Package.Tags`，侧边栏读的却是另一份分类文件，从来没有人把前者同步给后者；右键"移动到分类"的子项由目录生成，于是也是空的。现在每次仓库刷新都把列表里出现的分类补进目录（没有标签的包显示为"未分类"，目录里也会有"未分类"；内置分类按固定顺序排，内置名不标成自定义，英文界面下与列表一样显示译名）。补上同步后，改名 / 删除分类必须连包上的标签一起改，否则下一次刷新会按旧标签把旧分类补回来：`MainViewModel.RenameCategoryAsync` 就地替换标签（不改变主分类），`DeleteCategoryAsync` 从所有包上摘掉该标签（附带标签也摘，摘空即未分类），两者都只在最后刷新一次；有包写失败时保留目录条目并如实报错。"未分类"不能改名，里面还有 MOD 时不能删；改成已有的名字会被拒绝。标签变换规则落在 Core 的 `ModCategoryAssignment`（`DisplayCategoriesFor` / `SelectMissingCategories` / `RenameTag` / `RemoveTag`）。
- **测试偶发失败**：`ButtonTextFitTests` 的英文用例会把进程级界面语言切成英文，但它在可并行的主题集合里，同一时刻断言中文文案的用例（冲突模块的"找不到 MOD"）会偶发失败。已移入禁用并行的语言集合。
- 顺带修正：删除分类确认框的英文词条键名在写入时丢了单引号，英文界面下永远查不到翻译。

新增回归测试：`WindowCommandsTests`（模板里的 × 真能关掉没写绑定的窗口、实例级绑定优先、启动时注册）、`CategorySyncTests`（补齐目录落盘、刷新即同步、改名 / 删除全链路且重读仓库后不复活、改成已有名字被拒绝）、`ModCategoryAssignmentTests` 的显示规则 / 缺失分类排序 / 改名与删除标签变换九条、`ConflictModuleIntegrationTests` 的批量禁用触发刷新事件。

## 冲突检测模块重做 - 2026-09-22

**为什么重做。** 旧冲突面板的判据是"文件路径 + 胜者/败者"，对普通玩家不成立：装 20 个 MOD 报出 582 条冲突，其中 580 条是"内容完全相同的重复"（两个包各带一份同样的 UE4SS 载荷，装哪个都一样），真正的分歧只有 2 处 —— 用户会数自己的 MOD 个数然后判定软件在乱报。更要紧的是判据本身错位：两个 UE MOD 完全可以部署到不同目录、却替换**同一张** `T_WuKong_Born_Cloth_Ring_D.uasset`，只比文件路径永远看不见这种冲突。

**新定义。** 判据换成**资产**：读 `.pak` / `.utoc` 内部的资产索引，按资产键聚合所有者，内容真的不同才算冲突。呈现上：

- **一个冲突组 = 一个决策**，两成员给"禁用 X"、三成员及以上给"只保留 X"，按钮上写 MOD 名。
- 不出现"胜者""败者""覆盖规则""优先级"；不显示"当前生效的是谁"（那取决于引擎加载顺序，本程序不掌握）。
- 组按共同资源数降序；内容相同的重叠归入"重复"而不是"冲突"。
- 面板底部只有一个"关闭"，每个动作点击即生效并重新部署，**没有"应用"按钮**。
- 主列表卡片加角标（红色数字 = 冲突组数，灰色"重复"标签）与右键"查看冲突"。
- 启动中心的冲突步骤读 `ConflictReport.Groups.Count`。

**容器格式。** `PakIndexReader` 支持 v8–v11，含黑神话·悟空 pak 的 **+2 字节页脚变体**（判定方式是"校验 IndexOffset 落点是否在页脚之前"这个格式不变量，而不是匹配某版特有字段）；`IoStoreTocReader` 支持 v2–v5，路径取自末尾的 `FIoDirectoryIndexResource` 目录树、指纹直接取自 ChunkMetas 的 `ChunkHash`（因此**不读 `.ucas`**）。指纹一律先比 `Size + StoredSize + CompressionMethod`，三者有任何不同即断定内容不同，绝大多数比较不碰磁盘。

**删掉了什么。** `ConflictGrouping`、`ConflictAnalysisResult`、`ConflictResultWindow`、`ConflictAnalyzer`、`ConflictPanelLayoutTests`、`ConflictPanelRenderProbe`，以及面板上的这些入口：整组改用它 / 改用它 / 撤销决定 / 清除全部决定 / 自动解决全部 / 设为胜者。`ResolvedView` 的 `PendingConflictCount` / `DecidedConflictCount`、`EngineProfile.SupportsConflictDetection` 一并删除。**部署层的覆盖规则保留** —— 它仍是"关掉一个 MOD 之后同路径落盘哪一份"的求解器。

**导入侧三处。** ① 重复内容拦截：按"逐文件哈希的多重集"判重（不用把 `RelativeSourcePath` 也算进去的 `Package.ContentHash`），命中即清理并提示"仓库已有相同内容的 MOD：{名}"，压缩包与直接拖入 `.pak` 两条路径都拦。② 说明文件不进包：单元根上的 `readme*` / `说明*` / `*.txt` 不再被复制进游戏 Win64 根；单元被包了一层 MOD 目录时靠"是否只有一个顶层目录"识别，`ue4ss/Mods/mods.txt` 这类运行时附件不受影响。③ 导入后 `WarmUpAsync` 后台预热索引。

**探针结果（对 `D:\UEModManager\Repository` 的 20 个包 / 15 个 MOD 跑真实索引）。** 7 组冲突、2 处重复、2 个共享组件、2 处内容相同重叠被隐藏、1172 个共享组件文件不参与比对、0 个无法分析。设计文档期望的 8 对成员关系里 7 对成立（4 个披风 MOD 合并成 1 个 4 成员组，覆盖其中 4 对）；**1 处偏离**：`nezha_110` ↔ `nezha_1145` 在真实数据里**零共同资产**（前者挂载 `b1/content/xjdyz/`，后者挂载 `b1/content/00mainhz/characters`），该条期望不成立，以代码与真实数据为准。

## 第四轮实测问题修复 - 2026-09-21

针对第四轮实机复核的六条问题：

- **代理 DLL 被隔离进包子目录，插件 MOD 一个都没生效**：真缺陷，已修。UE4SS 靠 `dwmapi.dll` 代理注入、CSharpLoader 靠 `version.dll`，两者必须与 `b1-Win64-Shipping.exe` 同目录才会被 Windows 加载器拾取。旧实现给非 Mod 包的**裸文件名**一律加一层包目录隔离，于是它们落在 `.../Win64/超级筋斗云/dwmapi.dll`、`.../Win64/黑神话26年8月最新版自动闪避MOD/version.dll` —— 布局看着"有文件"，对游戏完全无效。根因是"这个包的相对路径本身就是安装位置"这件事没被记录，只靠路径里有没有斜杠猜。现在 `Package.PreservesInstallLayout` 显式记录它（目录导入 / 压缩包整体导入 / `ImportPluginAsync` 的目录分支为 true，随 manifest 一起持久化），`DeploymentTargetPathBuilder.ComputeTargetPath` 对它不再套包目录。同名冲突改由冲突检测 + 覆盖规则处理 —— 目录隔离恰恰是把代理 DLL 关进无效位置的元凶。
- **单 MOD 无包装目录时包名变成 `b1`**：真缺陷，已修。导入单元按解压根下第一段目录名命名，而"用户单独拖入解压后的「超级识破」文件夹"（内部直接是 `b1\Binaries\Win64\...`）和"压缩包内容直接是 `b1\Binaries\...`"这两种常见形状里，第一段是项目名而不是 MOD 名。现在 `ArchiveTargetPathResolver.InstallAnchors` 纳入 `b1`，新增纯函数 `ArchiveImportUnitResolver.LooksLikeSingleModLayout` 识别"整个输入就是一个 MOD 的内部结构"，命中即退回单单元、用压缩包名 / 文件夹名命名，并记一条 `LogWarning`。
- **「应用」冻结裁决后没有任何撤销入口**：真缺陷，已修。`Apply_Click` 把待处理项按当时优先级写成**显式用户覆盖**，规则一旦写下就钉在旧胜者上：用户后来调整方案优先级、或更新了某个 MOD，面板都不会跟着变。`ConflictAnalyzer` 早有 `RemoveOverrideAsync` / `ClearAllOverridesAsync`，但 UI 里一个调用方都没有 —— 想反悔只能手改方案文件。现在冲突面板补两个入口：组标题右侧的「撤销决定」（对该组 `IsUserOverride` 的路径批量移除后重算）与底部「清除全部决定」（`CyberMessageBox` 二次确认后清空本方案全部覆盖）；两者都置 `OverridesChanged`，已有的重新部署链路随即使磁盘跟上。
- **导入失败提示在对话框关闭之后才弹出**：真缺陷，已修。原实现先赋 `DialogResult = true`（赋值即关窗），再调 `ReportFailedImports`；`CyberMessageBox.Show(owner)` 只在 `owner.IsVisible` 时设 Owner，于是提示丢掉 Owner、落到主窗口后面或屏幕中央，用户根本看不到。现在改为"先报失败、再关窗"，并按 `DialogResult` 赋值即关窗的语义去掉多余的 `Close()`。
- **「整组改用它」逐条落盘**：「超级识破 ↔ 超级筋斗云」那一组有 569 个文件，旧实现循环调 `SetOverrideAsync`，每次触发一次方案文件全量序列化 + 写盘，点一次卡顿明显。新增 `ConflictAnalyzer.SetOverridesAsync` / `RemoveOverridesAsync` 批量接口（只落盘一次），`SetOverrideAsync` / `RemoveOverrideAsync` 改为其单元素形式，三处调用方（整组改用它、自动解决全部、组级撤销）全部改走批量路径。
- **`UEModManager.Core` 里出现文件系统 IO**：`ArchiveImportUnitResolver.ResolveUnitName` 曾用 `Directory.Exists` 判断"第一段是目录还是文件"。Core 层是纯函数 + 纯模型，不碰 IO。判据其实已经现成：走到那一步说明相对路径还有第二段，而文件不可能有子路径 —— 第一段必然是目录，那次 IO 纯属多余，已删除。
- **游戏根下多出一个以 MOD 命名的杂项目录**（T1 实机验收的残留项）：真缺陷，已修。压缩包的真实形状是 `MOD名/自动闪避/b1/Binaries/Win64/version.dll` 加 `MOD名/说明与事项.txt`。共有包装目录（MOD 名）原本是在**剥掉游戏根锚点之后**才算的，而剥锚点会把最短的那条路径削成裸文件名（`version.dll`），共有前缀从此算不出来：命中锚点的文件正确落到游戏根，没命中锚点的说明文档却带着 MOD 名目录一起落进游戏根，磁盘上于是留下 `Win64/黑神话26年8月最新版自动闪避MOD/说明与事项.txt`。现在把"相对解压根的原始路径"（新纯函数 `ArchiveTargetPathResolver.ResolveRelativePath`，不剥锚点）与"剥锚点后的目标路径"分开，共有前缀在**未剥锚点**的路径上算出、作为 `commonPrefix` 传回 `Resolve`，顺序固定为"先剥包装目录、再剥锚点"。

新增回归测试：`ImportFailureSummaryTests`（导入失败汇总的三种计数形态）、`ArchiveImportUnitResolverTests` 的 `LooksLikeSingleModLayout_*` 四条、`ArchiveTargetPathResolverTests` 的 `Resolve_CommonPrefixBeforeAnchor_StripsModNameFromFileWithoutAnchor` / `ResolveRelativePath_DoesNotStripGameAnchor`、`DeploymentTargetPathBuilderTests` 的 `ComputeTargetPath_LayoutPreservingPlugin_*` 与旧数据兼容三条、`FolderImportIntegrationTests` 的代理 DLL 落点、`FolderWithProxyDllAndTopLevelReadme_LeavesNoModNamedDirectoryAtGameRoot` 与"单 MOD 用文件夹名 / 压缩包名而非 `b1`"四条、`ConflictDecisionIntegrationTests` 的 `LayoutPreservingPlugins_IdenticalProxyDll_ResolvesToOneFileAtGameRoot` / `RemovingOverride_RestoresPriorityRulingAndRedeploysLoserFile` / `SetOverridesAsync_WritesAllRulesInOneSave` / `RemoveOverridesAsync_ClearsSeveralRulesAtOnce`，以及 `ConflictPanelLayoutTests` 新增的撤销入口断言（按钮真的被构造出来、无覆盖时"清除全部决定"置灰）。

## 首次实测问题修复 - 2026-09-17

针对 v2.1.0 首次实机测试反馈，逐条排查五个体感问题后的结论与修复：

- **RAR 导入失败**：设计如此，非缺陷。RAR/7z 依赖用户电脑的解压环境，可能失败或让中文文件名乱码，客户端会提示先用 WinRAR/7-Zip 手动解压。**手动解压后的文件夹现在可以直接拖入导入**（此前被 `File.Exists` 判定拒成"文件不存在"）。
- **解压后丢文件夹进去不成功**：真缺陷，已修。`ImportSingleAsync` 不接受目录，而唯一支持目录的 `ImportPluginAsync` 在生产链路上零调用方。现在文件夹按内容分流：含 `.pak` 走 MOD 分组，否则整包保留目录结构。导入面板也不再丢弃文件夹并会给出提示。
- **手动压缩后导入只保留 2% 文件**：真缺陷，已修。旧实现用引擎扩展名白名单过滤，插件载荷（`.lua`/`.dll`/`.txt`）整包丢弃且不留日志（实测 1194 个文件丢 1174 个）。现改为黑名单排除调试符号/日志/第三方可执行程序，并把丢弃数量写进日志。分流只看 `.pak/.ucas/.utoc`，其余载荷整包保留。
- **冲突检测到但"替换"不生效**：真缺陷，已修。用户覆盖规则用加载顺序键（`@mod/shared.pak`）保存，而 `ResolvedViewBuilder` 只用含包目录的实际路径比对，规则永远匹配不上候选——磁盘上两个包目录都还在。现在覆盖规则能命中候选，败者条目不再进入最终视图，部署时按托管归属移除；冲突面板新增"设为胜者"，面板关闭后自动重新部署。
- **邮件验证码发送失败**：不是 Brevo 的问题，是 Worker 从未重新部署。线上 `/api/auth/otp/request` 与 `/api/auth/otp/verify` 均返回 404，而源码早已包含这两个路由（2026-09-09 引入）。Brevo 密钥与发件授权均正常。**已重新部署 Worker**，`/api/auth/otp/request` 现已返回 200 与 challenge。客户端同时补上非 2xx 的日志（此前 404 是"正常响应"，日志里一个字都没有）。

顺带修复：拖入文件夹不再无提示失败、中国 MOD 名分类(仅 5 组关键词导致中文名全落"其他")、背景图拖动时刷 62 行日志、非 MOD 包目录结构丢失（`ue4ss/Mods/mods.json` 曾被部署成 `<gamePath>/mods.json`）。

## 第二轮实测问题修复 - 2026-09-17

针对第二轮实机测试反馈的三条体感问题：

- **导入 `0916.zip` 解压后只有一个名叫 `0916` 的条目**：真缺陷，已修。整包被并成一个包 —— 旧实现把全部 `.pak` 丢进同一分组池按基础名分组、把全部非 MOD 载荷折成一个以压缩包名命名的包，压缩包内顶层目录的边界在这一步被丢掉。现在按**顶层目录**切成多个导入单元，一个目录 = 一个 MOD（实测该压缩包由 1 个巨型包变为 20 个包），每个单元内部再按原有规则分组；单元名天然互不相同，此前被迫产生的 `_2` 后缀也不再出现。
- **导入过程无感，需要 loading 动画**：真缺陷，已修。`LoadingOverlay` 早先只是主窗口里一段没有任何消费者的 XAML（`IsLoading` / `LoadingMessage` 改了也没人听），遮罩永远停在 `Collapsed`；同时导入直接跑在 UI 线程上，解压 + 写 1189 个文件约 15 秒全程把消息泵堵死。现在抽成带转圈弧的 `LoadingOverlay` 用户控件（沿用既有主题令牌），主窗口绑定 `IsLoading` / `LoadingMessage`，导入确认对话框在导入期间显示遮罩并把导入整批搬到后台线程。
- **冲突检测没生效**：与第一条同一根因，已随之修复。所有文件同属一个 `PackageKey` 时，`ConflictDetector` 按包去重后只剩一个声称者，“两个包抢同一路径”永远不成立。按顶层目录拆包后，实测该压缩包的 **582 条冲突全部浮现**，其中 2 条 `Warning` 是内容真实分歧（`CSharpLoader/b1cs.ini`、`version.dll`），其余为内容相同的 `Info` 重复。

顺带补上 `应用冲突选择...` / `应用冲突选择失败` 的英文条目：加载提示此前从未渲染过，这类只在加载遮罩里出现的文案一直没被发现缺翻译。

### 弹窗按钮文字溢出（同批次）

第二条实测反馈：弹窗的确认按钮"样式没有显示完全，文字溢出"。根因在主题而不是某个窗口：

- `CyberPrimaryButton` / `CyberSecondaryButton` 的 `ControlTemplate` 里 Border 上既没有 `TemplateBinding Padding`、样式也没有默认 `Padding`，于是调用方写的 `Padding="16,0"` 被**静默丢弃**。`CyberMessageBox.CreateButton` 早就写了 `Padding` 与 `MinWidth=80`，一直没生效。内边距被丢掉后按钮缩到只剩内容宽度，文字直接顶到圆角上，首尾字符被切。
- 各弹窗又把按钮宽度写成固定值（60~140），长文案与英文译文进一步压边。

修复：模板消费 `Padding` 并给出默认值 `16,0`；全部固定 `Width` 改为 `MinWidth`，让按钮按内容撑开。同时修掉 `ManagementCenterWindow` 底部"说明文字与导出按钮同处一个无分列 Grid 单元格"造成的文字重叠。

新增回归测试 `ButtonTextFitTests`：对全部窗口在中文与英文下逐一检查每个可见按钮，"内容 + 内边距 + 边框"是否能被实际宽度容纳。判据刻意不重新挂载按钮 Content —— 确认按钮的内容是内联 `StackPanel`，已有父级，重新挂载会量出 0 宽度而恰好漏掉出问题的那个按钮。测试另有"至少检查到 20 个按钮"的下限断言，防止遍历失效后退化成空转。已反向验证：把主题改回修复前状态，该测试会精准报出全部被挤压的按钮。

## 第三轮实测问题修复 - 2026-09-20

针对第三轮实机测试反馈的三条问题：

- **圆角矩形按钮圆角统一 8px**：真缺陷，已修。修复前同一屏上并存 **6 / 7 / 10 / 14** 四种圆角（主工具栏 14、侧栏导航 10、视图切换 6、方案卡片"新建方案"10、存储位置向导按钮 7），视觉上像五套不同控件。主题新增 `RadiusButton = 8` 令牌，四个按钮样式与两处窗口自带模板全部改指它；主窗口、启动中心、设置的二十余个"Border 充当按钮"也统一到 8。圆形按钮（头像、圆形图标钮）与卡片/图标预览框刻意不动 —— 改成 8 会把圆变方。
- **20 个 MOD 报出 582 个冲突**：不是检测过量，是**呈现粒度**错了。检测粒度是目标路径，用户的心智模型是 MOD 之间。582 条里 580 条是"内容完全相同的重复"（两个包各带一份同样的 UE4SS 载荷，装哪个都一样），真正的分歧只有 2 处（`CSharpLoader/b1cs.ini`、`version.dll`）。把 580 和 2 加在一起报"582 个冲突"，用户只会数自己的 MOD 个数然后判定软件在乱报。现在按**当事方**（胜者 + 败者集合）折叠成"哪几个 MOD 之间有冲突"：实测该数据集由 582 条折叠为 **4 组**，徽标与说明只报待处理数（2），重复单列且默认折叠，分组标题带"· N 处待处理 / · 已决定"，路径行分三态（内容相同 / 内容不同 / 已决定）。
- **自动解决 / 应用都没能消除冲突**：真缺陷，已修，且比表面更严重 —— `AutoResolve_Click` 只做一次 `AnalyzeAsync()` 后重画界面，输入相同输出必然相同，**纯空操作**；`Apply_Click` 只设 `DialogResult` 就关窗，不写任何状态。而"设为胜者"是逐条路径写覆盖规则，面对 582 行平铺，用户点一次等于什么都没变。现在"整组改用它"把组内所有路径一次性判给某包、"自动解决全部"把每处内容分歧按优先级定案并写成显式规则，"应用"再依据 `OverridesChanged` 触发重新部署。内容相同的重复不写规则：写进去只会撑大方案文件而没有行为差异。

日志对照：修复前连续 4 次 `冲突分析完成: 20 个包, 1187 个文件, 582 个冲突 (0 个用户覆盖)` —— 用户每点一次"自动解决/应用"，用户覆盖数始终是 0，这正是"点了没用"的直接证据。

新增回归测试：`ConflictGroupingTests`（分组折叠）、`ConflictDecisionIntegrationTests`（定义胜者 → 待处理归零、败者的分歧文件不再进入最终视图、磁盘上确实写的是胜者内容）、`ButtonCornerRadiusTests`（按钮角色圆角必须为 8，并带检查数量下限；已反向验证：把侧栏导航改回 10px 会精准报出该元素）。

### 冲突面板"应用成功却看起来没成功"的剩余两处读数缺陷（同批次，紧随其后）

决策链路修好后复测，面板仍有两点在继续输出"你没点成功"的信号。它们与决策无关，只关乎读数，因此单独记：

- **标题图标写死橙色警告三角**：状态已变成"已全部决定"，标题旁照旧是黄三角 —— 文字说"已决定"、图标说"还有问题"。现在图标随状态切换（待处理橙三角 / 已全部决定绿勾）。
- **路径行溢出叠字**：路径与右侧状态徽标套在水平 `StackPanel` 里，而 `StackPanel` 以**无限宽度**测量子元素，路径 `TextBlock` 于是按全文宽度报告所需尺寸，`TextTrimming` 永不生效 —— 实测长路径右边界压过徽标左边界约 15px，糊成一团。改用两列 `Grid`（图标 `Auto` + 路径 `*`）后路径拿到确定可用宽度，省略号与完整 `ToolTip` 才真正生效。

同时改掉 `ConflictAnalyzer` 的完成日志：原来只报"582 个冲突"，正是把用户推向"软件在乱报"的那句话；现在拆成"目标路径重叠 582 处 = 内容分歧 2 处待处理 + 0 处已决定 + 580 处内容相同"，与面板一致。

新增回归测试 `ConflictPanelLayoutTests`：用**量出来的几何**断言路径右边界不越过徽标左边界（待处理/已决定两种状态都查），并断言标题图标在两种状态下的字形与颜色。已反向验证：把路径行改回水平 `StackPanel` 会报"路径右边界 562.0px，徽标左边界 547.0px"；把标题图标写死橙色会报字形不符。
## 群聊入口更新 - 2026-09-10

- 软件、中英文 README、官网与随包说明统一使用[爱酱MOD管理器交流群](https://qm.qq.com/q/5PeqQxiszC)，群号 **147223127**。
- 软件反馈页、README 和官网帮助页展示新的 QQ 入群二维码。

## 工作树改动 - 2026-09-05（未发布）

- 接续 9 月 4 日消融，删除无入口窗口、重复旧 MOD 写入服务、无效设置和闲置依赖。
- 恢复主界面仍需要的类型转换器注册，修复管理中心错误资源引用；本机账户页改为真实只读数据。
- 部署异常现在终止启动；启动中心出错时不再绕过流程直接运行游戏。
- 安装脚本改用全新 publish 暂存目录，防止打入残留 DLL；CI 纳入 Worker 自测。
- 修复审计 F01–F08：空方案部署清理、同大小内容更新、配置合并与 UserFix 部署、解压硬预算、邮件验证码协议、持久原子限流、云端账户契约、Profile 冲突规则往返与隔离。
- 新增安装范围部署清单，回滚同步归属并保留游戏原件；旧文件仅凭可靠事务记录迁移。覆盖规则采用可移植路径，旧全局规则按方案迁移。
- 修复崩溃时执行日志落后于文件写入造成的恢复遗漏，覆盖部分回滚后的重载重试；未记录的新增文件只有哈希匹配才删除。
- 旧任意内容邮件端点停用；新版客户端与 Worker 需配套发布，Worker 需新增 Durable Object 绑定及 migration。
- Debug / Release 均 0 警告、0 错误；Core 1211、应用 582 项通过，1 项手动快照生成器跳过；Worker 58 + 18 项、实际桌面协议 17 项通过。

当前实现与设计边界见[架构总览](docs/architecture/overview.md)，验证命令见[开发文档](docs/README.md)。以下记录保留各版本当时的状态。

---

## [v2.0-rc2] - 2026-04-30（数据隔离 + 三态机 + 事务回滚 全面审查 / 修复批 A）

本次发布做了一次彻底的代码库审查（数据隔离、启用/禁用/卸载状态机、事务/回滚/崩溃恢复），按"严重 / 重要 / 建议"三档识别 14 项风险，本批次完成 7 项严重 + 重要修复（修复批 A）。

### 🛡 事务安全网（S5 / S6 / S7 / W5 / W6）

| 修复点 | 之前的问题 | 修复方式 |
|--------|----------|---------|
| **S5** RollbackAsync 假成功 | 单步失败 catch 吞异常，最终仍标 `RolledBack`，CrashRecoveryScanner 永远扫不到 | 加 `DeploymentStatus.PartiallyRolledBack` + `RollbackFailure` 列表；中途失败累积失败记录，状态不再静默成功 |
| **S6** 事务消失 | 写盘前崩溃 → transaction.json 不存在 → 孤儿备份永远清不掉 | `ExecuteAsync` 创建事务后立即 `SaveTransactionLogAsync` 一次（`InProgress` 状态可见），写盘前崩溃也能被恢复扫描器拾取 |
| **S7** 备份缺失静默 | `RollbackActionPlanner` 在 BackupPath 文件不存在时返回 `None`，回滚"成功"实际未恢复 | 加 `BackupMissing` / `NoBackupRecorded` 两个动作类型，加重载 `PlanRollback(op, backupExists)` 让纯函数可注入 IO；遇到 `BackupMissing` 必冒泡为 `PartiallyRolledBack` |
| **W5** 日志写盘失败静默 | `SaveTransactionLogAsync` catch 吞异常 + 状态已是 Committed → 程序以为成功 | 写盘失败时降级为 `LogPersistenceFailed`，扫描器分类为 `VerifyAndResubmit` |
| **W6** 取消恢复死循环 | 用户取消恢复对话框 → 事务保持 InProgress → 下次启动又扫到 → 循环弹窗 | 加 `Dismissed` 状态 + `DismissedAt` / `DismissedReason`；`CrashRecoveryService.DismissTransactionAsync` / `ResetDismissedAsync` 管理 API |

### 🛡 状态机收紧（S3 / S4）

- **S3** 元数据/物理状态分离：`ProfileService.SetPackageEnabledAsync` 重命名为 `SetPackageEnabledFlagAsync`，文档明确"仅元数据，不部署"。旧名标 `[Obsolete]`，唯一调用点 `MainViewModel.DeployToggleAsync` 已切换。
- **S4** 删包污染：新增 Core 纯函数 `PackageReferenceCounter` + `PackageDeletionPlanner`。`PackageRepository.DeletePackageAsync` 新签名 `(packageKey, allProfiles, force)` 默认拒绝删除被启用包；`ManagementCenterWindow` / `RepositoryManagerWindow` 三处调用点全部切换。

### 📊 当前基线

```
dotnet build UEModManager.sln --configuration Debug    # 0 errors / 0 warnings（5 项目协同）
dotnet test UEModManager.Core.Tests/...                # 598 passed / 0 failed（~29 ms）
```

测试增量：552 → **598**（+46）。新增覆盖：`PackageReferenceCounterTests`、`PackageDeletionPlannerTests`、`DeploymentTransactionTests`，`RollbackActionPlannerTests` / `CrashRecoveryScannerTests` 扩展新状态。

### 🎨 字体优化

- 主题字体栈改为思源黑体优先：`CyberStyles.xaml:76` 把 `InterFont` 资源升级为多字体回退链 `Inter → Source Han Sans CN → Source Han Sans SC → Noto Sans CJK SC → Microsoft YaHei UI`，拉丁字符仍由嵌入 Inter 渲染，中文字符按系统字体回退。
- 修复了之前 App.xaml 外层 `<Style TargetType="TextBlock">` 覆盖 CyberDarkTheme 主题字体设置导致中文渲染落到系统默认 fallback（"处"等字显示异常）的问题。

### 📋 已识别但未在本批次修复（留给修复批 B / C）

- **S1** SQLite 单库多游戏共享，无 GameId 外键 — 需要数据库分库
- **S2** ObjectStore 全局共享，包文件不按游戏隔离 — 需要按游戏分层
- **W7** Profile 切换不触发部署 — 需要 SwitchProfileAsync 主动调度部署计划
- **S8 / W4 / N1-N4** 事务粒度、备份链、依赖图、幽灵文件检测 — v2.0.4 候选

当前数据与部署边界见[架构总览](docs/architecture/overview.md)。

---

## [v2.0-rc 候选] - 2026-04-30（Core 17 轮拆分 + Phase 13 SDK 完整化）

### 🏗 第十八轮（2026-04-30）：SDK 完整化

- **`IDeploymentBackend` 接口下沉 Core**：从 `UEModManager/Services/Backends/` 迁到 `UEModManager.Core/Services/Backends/`，与 `IHostAdapter` 同模式。三个内置后端（CopyBackend / HardLinkBackend / SymlinkBackend）保持原 namespace 无需改 using。
- **新增 `samples/UEModManager.SampleBackend/`**：独立可编译 csproj + `SampleMirrorBackend`（演示"镜像复制 + 来源记号"）+ README。仅引用 Core，无 WPF 耦合。
- **基线文档同步到 2026-04-30**：`docs/README.md`（156 → 552 测试 / 3 → 5 项目）+ `docs/architecture/overview.md` 重写 + `docs/playbooks/writing-deployment-backend.md` 加"参考实现"段落。
- **`.gitignore` 创建**：阻止误提交 bin/obj、构建日志（after_*/baseline*/build_diag/final_*/snapshot）、`.playwright-mcp/`、`recovered_20250320/`、个人简历类敏感文件、`untitled.pen` 等。

### 🏗 Core 第十四 ~ 第十七轮（2026-04-29）：持续 ROI 拆分

| 轮次 | Core 新增 | 主项目变化 | 测试 |
|------|----------|------------|------|
| 第十四 | `MigrationStep` 枚举 + `MigrationStepCatalog` + `MigrationProgressTracker` | DataMigrationService.MigrateAsync 5 处 ReportProgress 改委托，魔法数和文案下沉 | 377 → 414 |
| 第十五 | `CompressedArchive` + `ImportFileKindClassifier` + `ModFileGrouper` + `PreviewImageSelector`（4 个 Import 模块） | PackageImportService 扩展名 if-else 链改 switch，分组算法/预览选择/嵌套压缩判定全部委托 Core | 414 → 484 |
| 第十六 | `ModCategoryClassifier`（5 类中英文关键词分类规则） | ModManagementService.DetermineModType 单行委托；删 ImageExtensions/IsImageFile 重复代码；FindPreviewInDirectory 改用 Selector | 484 → 523 |
| 第十七 | `GameNameNormalizer` + `EngineDetector`（注入 3 Func 路径探测器） | GameConfigService.AutoDetectEngine 35 行 if-else 链压缩到 6 行 + Detect 注入 lambda | 523 → 552 |

### 📊 当前基线

```
dotnet build UEModManager.sln --configuration Debug    # 0 errors / 0 warnings（5 个项目协同）
dotnet test UEModManager.Core.Tests/...                # 552 passed / 0 failed（~23 ms）
```

5 个项目：主项目 + Core + Tests + SampleAdapter + SampleBackend。

### 🎯 下一步候选

1. 主项目继续审视可拆纯函数（边际收益已显著下降，剩下多带 IO/CUE4Parse 依赖）
2. Phase 10 VFS 实验性 R&D（独立 spike，不进入主线发布阻塞项）
3. 仓库初始 commit + 打 v2.0-rc1 标签（用户级决策——`.gitignore` 已就位）
4. Windows 实机手动验收 v2.0 主流程

---

## [v2.0-dev] - 2026-04-28（Phase 11 工程硬化 + Phase 12 整合包 + Phase 4 修复）

### 🏗 架构重构 / Architecture

#### Core 项目"做扎实"
- **依赖瘦身**：`UEModManager.Core` PackageReference 从 7 个减到 1 个（仅 `Newtonsoft.Json`）
- **TFM 收紧**：`net8.0-windows` → **`net8.0`**（编译栏杆挡 WPF 依赖回灌）
- **Domain 模型迁移**：16 个领域模型从主项目下沉到 Core
- **纯函数 Service 下沉**：
  - `ConfigMerger` + 3 Parser（Ini/Json/Cfg）
  - `ConflictResolver` + `ConflictDetector`（含 Phase 4 修复）
  - `CrashRecoveryScanner`
  - `ProfileLockBuilder` + `ProfileLockComparator`
  - `HealthReport` + `HealthReportFormatter`
  - `StructuredLogWriter` + `LogRedactor`
  - `DiagnosticManifestBuilder`
  - `ResolvedViewLayerBuilder`（最终视图 Layer 1）
- **端口接口**：`IPackageQuery` / `IProfileQuery` / `IObjectStoreQuery`（只读契约）
- **测试体系**：xUnit 项目 `UEModManager.Core.Tests`，**166 个单测，~14 ms 跑完**

### ✨ Phase 11 工程硬化 / Engineering Hardening

#### 半结构化日志
- 新增 `StructuredLogWriter` 包装 Console，零业务代码改动给 153+ 处日志加：
  - ISO-8601 时间戳
  - LEVEL（INFO/WARN/ERROR/FATAL/DEBUG/TRACE）
  - Category（从原 `[Tag]` 前缀提取）

#### 诊断包导出
- 管理中心底部新增 **"导出诊断包"** 按钮
- 一键打包：`metadata.txt` + `health-report.txt` + `logs/` + `data/` + `transactions/`
- `LogRedactor` 自动脱敏：JWT、Bearer Token、API key、邮箱本地部分

#### 崩溃恢复
- 启动时自动扫描未完成事务（`Status = InProgress / Pending / Failed`）
- 弹窗显示候选项 + 推荐动作（回滚 / 标记失败 / 忽略）
- 用户确认后自动批量处理

#### 健康检查
- 启动时跑 7 项检查：游戏路径 / MOD 路径 / 备份路径 / 包仓库 / 当前 Profile / SQLite DB / 备份目录可写性
- 结果写入 `console.log` 并并入诊断包

### ✨ Phase 12 整合包 / Profile Lock & Bundle

ProfileManagerWindow 操作栏新增 4 个按钮：

#### 导出 / 导入 `.profile.lock.json`
- 轻量元数据快照：方案设置 + 包列表 + 优先级 + 冲突 override
- 接收方需自行确保已导入对应 MOD
- 不含包文件，体积小（KB 级）

#### 导出 / 导入 `.profile.bundle.zip`
- **整合包**：lock JSON + 所有引用包的物理文件
- 接收方导入即可立即使用，无需单独导 MOD
- 内部结构 `profile.lock.json` + `packages/{key}/manifest.json + files/`

### 🐛 Phase 4 设计漏洞修复 / Phase 4 Design Fix

**问题：** `ConflictAnalyzer` 在当前部署模型下永不触发 ——
`ComputeTargetPath` 公式 `modPath/{PackageKey}/{file}` 让两个不同包永远不会落同一路径。
设计说明见[部署路径与加载顺序冲突](docs/architecture/overview.md#load-order-conflicts)。

**修复（方案 B）：**
- 新增 `ConflictDetector.ComputeLoadConflictKey`：归一化路径忽略 PackageKey 子目录
- 字典 key 改用 LoadConflictKey；多个包声明同名 RelativeTargetPath → 同 key → 冲突候选
- `ConflictRecord.Type` 由 `Path` 改为 **`LoadOrder`**（语义更准）
- `ResolvedViewBuilder` 同步采用相同语义
- 部署路径不变（`DeploymentPlanner.ComputeTargetPath` 仍含 PackageKey）—— 不影响备份/回滚

### 📚 Phase 13 起步 / Developer Docs

新增文档：
- [`docs/README.md`](docs/README.md) — 文档索引
- [`docs/architecture/overview.md`](docs/architecture/overview.md) — 架构总览（30 秒理解）
- 旧 Host Adapter 指南已随对应扩展点移除；当前扩展方式见[架构总览](docs/architecture/overview.md)。
- [`docs/playbooks/writing-core-service.md`](docs/playbooks/writing-core-service.md) — 写 Core 纯函数 Service

### 🧹 清理 / Cleanup

- 删除 `UEModManager.Core/Class1.cs`（VS 默认空模板）
- 删除 `UEModManager.Core/DummyClasses.cs`（无引用的旧 Mod 类）
- 删除 `Package.ToModInfo()` / `HostDefinition.FromGameProfile()`（死代码）
- `Package.FromModInfo()` → 移到主项目 `PackageMappers.cs`

### ✅ 编译/测试状态

```
dotnet build UEModManager.sln                  # 0 errors / 0 warnings
dotnet test UEModManager.Core.Tests/...        # 166 passed / 0 failed
```

---

## [v1.7.38] - 2025-10-07

### 🔧 重要修复 / Critical Fixes

#### Brevo SMTP认证失败问题
- **问题**: 邮件验证码发送失败，用户无法注册或使用邮箱登录
- **原因**: Brevo使用系统分配的专用SMTP登录账户，配置文件中的"apikey"只是占位符
- **解决**: 智能检测"apikey"占位符并自动替换为正确的SMTP账户 (984a39001@smtp-brevo.com)
- **影响**: 修复后邮件发送功能恢复正常，支持API和SMTP双通道fallback

#### 分类配置文件路径错误
- **问题**: 分类重命名、删除、拖拽排序功能失效
- **原因**: 配置文件路径错误导致无法正确保存分类数据
- **解决**: 修正配置文件路径逻辑
- **影响**: 分类管理功能完全恢复

### ✨ 新功能 / New Features

- **点击头像打开账户设置**: 统一用户体验，头像点击与账户设置按钮功能一致
- **移除重复菜单项**: 删除"更换头像"菜单项，避免功能重复

### ⚡ 性能优化 / Performance Improvements

- **API超时优化**: Brevo API超时从30秒减少到10秒，快速fallback到SMTP
- **SMTP参数验证**: 构造函数增加邮箱格式验证，提前发现配置错误
- **详细日志输出**: 增强SMTP认证调试日志，便于问题排查

### 🙏 鸣谢 / Thanks

- 新增捐赠感谢：**枪王**

### 📝 技术细节 / Technical Details

**修改文件**:
- `App.xaml.cs`: 智能SMTP_LOGIN修正逻辑
- `BrevoEmailService.cs`: 增强构造函数日志和参数验证
- `BrevoApiEmailService.cs`: 减少超时时间
- `MainWindow.xaml.cs`: 头像点击事件统一
- `installer_clean.iss`: 版本号更新到1.7.38

**新增文件**:
- `故障排查_Brevo_SMTP认证问题.md`: 详细的问题排查文档
- `UEModManager/Security/SecretFileProtector.cs`: 配置文件加密保护

---

## [v1.7.37] - 2025-10-04

### 🔧 修复 / Fixes

- 修复分类配置文件路径问题
- 修复MOD重命名、删除、排序功能
- 优化邮件发送服务稳定性

### ✨ 新功能 / New Features

- 为使用说明文档添加完整的中英文双语支持
- 增强网站SEO自动化
- 完善项目文档结构

### 🙏 鸣谢 / Thanks

- 捐赠感谢：胖虎、YUki、Tarnished、春告鳥、蘭、虎子、神秘不保底男、文铭、阪、林墨、Daisuke、虎子哥
- 特别感谢：爱酱MOD管理器交流群全体群友

---

## [v1.7.36] - 2025-10-02

### ✨ 新功能 / New Features

- 集成Cloudflare Workers API网关
- 实现云端认证服务
- 添加离线模式支持
- 完善用户认证流程

### 🔧 优化 / Improvements

- 重构项目结构，清理冗余文件
- 优化数据库连接管理
- 增强错误处理机制

---

## [v1.7.35] - 2025-09-30

### 🎮 游戏支持 / Game Support

- 支持剑星/Stellar Blade MOD管理
- 支持黑神话悟空 MOD管理
- 支持明末无双 MOD管理

### ✨ 核心功能 / Core Features

- MOD自动备份与恢复
- MOD分类管理系统
- 批量操作支持
- 云端同步功能

---

## 版本说明 / Version Notes

### 版本号格式
- **主版本号**: 重大功能变更或架构升级
- **次版本号**: 新功能添加或重要修复
- **修订号**: Bug修复和小幅优化

### 更新频率
- **稳定版**: 每月1-2次重大更新
- **修复版**: 根据问题严重程度随时发布
- **测试版**: 内部测试，不公开发布

---

## 贡献者 / Contributors

感谢所有为本项目做出贡献的开发者和用户！

### 核心开发
- **主要维护者**: 爱酱工作室

### 技术支持
- AI助手 (Claude Code)

### 社区贡献
- 所有提交Bug报告的用户
- 所有提供功能建议的用户
- 所有捐赠支持的用户

---

## 支持与反馈 / Support

- **项目主页**: https://github.com/velist/UE-Game-ModManager
- **官方网站**: https://www.modmanger.com
- **问题反馈**: GitHub Issues
- **技术支持**: mr.xzuo@foxmail.com

---

**最后更新**: 2026-04-30
**当前版本**: v2.0-rc 候选（Phase 0-13 + Core 17 轮拆分 + 第十八轮 SDK 完整化）
