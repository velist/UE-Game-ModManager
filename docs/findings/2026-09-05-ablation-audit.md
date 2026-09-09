# 2026-09-05 消融与全仓审计

> 历史快照：下文记录消融审计结束时的状态和当时的源码行号。F01–F08 随后已完成本地修复；当前结果、回归证据和发布边界见 [审计问题修复与验收](2026-09-05-audit-repairs.md)。下文的“未修复”均指这份快照形成时。

本轮完成了源码盘点、关键调用链审阅、消融清理、已发现回归的修复和本地验证。改动保留在工作树，尚未提交或发布。**另有 8 项已确认缺陷未在本轮修复，其中 5 项为 P1；编译和现有测试通过不代表这些缺陷已经消失。**

## 审计基线与范围

| 项目 | 本次实际状态 |
| --- | --- |
| 工作树 | `D:/modmangerpd/codex-fix-build` |
| 分支 / 起点 HEAD | `codex/fix-20260810` / `79baf12` |
| 源码版本 | 2.1.0；发布程序 FileVersion 为 2.1.0.0 |
| 本机环境 | Windows；.NET SDK 8.0.416；Node.js 24.11.1 |
| 接手时已有改动 | 15 个已跟踪文件的修改或删除，以及 3 个未跟踪文件 |
| 审计方式 | 本机源码与独立临时环境验证；没有重新执行 SSH 端部署 |
| 证据目录 | `C:/Users/a/.codex/tmp/modmanager-ablation-20260905/` |

接手前的补丁和状态保存在 `inherited.patch`、`inherited-status.txt`。三个包完整性分析文件另存于 `inherited-new/`，收尾时逐文件 SHA-256 比较均与备份一致：

- `UEModManager.Core/Services/Repository/PackageIntegrityAnalyzer.cs`
- `UEModManager.Core.Tests/Services/Repository/PackageIntegrityAnalyzerTests.cs`
- `UEModManager.Tests/Services/PackageRepositoryIntegrityJudgementTests.cs`

已有的包完整性提取、旧窗口删除等改动继续保留。本轮没有改动旧工作树 `D:/modmangerpd/测试`。下文的数量变化均相对于**接手时的未提交状态**，不是相对于 HEAD。

### 源码盘点

扫描覆盖主程序、Core、两个测试工程、示例、Worker 和网站的 `.cs/.xaml/.js/.mjs/.html/.css`，排除生成输出、第三方依赖和 Git 内部目录；构建文件、CI、安装脚本和文档另行审阅。

| 指标 | 接手时 | 本轮结束 | 净变化 |
| --- | ---: | ---: | ---: |
| 源码文件 | 347 | 329 | -18 |
| 文本行数 | 76,798 | 71,637 | -5,161 |
| 扫描器汇总的 C# 类型名称 | 542 | 508 | -34 |

结束时分类为：生产源码 213 个文件 / 49,925 行，测试源码 115 个文件 / 21,622 行，示例 1 个文件 / 90 行。行数包含注释、空行和标记，不是可执行代码行数。

Roslyn 候选扫描同时考虑 Debug / Release 条件、XAML 名称及字符串引用。末次扫描中，“没有名字引用的私有方法”“未读私有字段”“无引用 XAML 资源”三类候选均为 0。**这只是候选筛查结果，不是没有死代码的证明。** 同文件类型、接口、序列化契约、迁移代码与仅有测试引用的兼容机制经过人工辨别后保留；没有按“引用少”直接删除。

### 关键路径审阅

| 范围 | 审阅内容与验证方式 |
| --- | --- |
| WPF 与入口 | App 资源和 DI、MainWindow、所有现存窗口、模板延迟实例化、设置消费者、窗口事件退订 |
| 仓库与持久化 | PackageRepository / ObjectStore / Profile、原子写入、损坏索引备份、旧 JSON 迁移、完整性判定、跨游戏引用与回收 |
| 导入与部署 | 路径净化、包名冲突、压缩展开、期望文件与实际文件差异、启动步骤失败、事务回滚和崩溃恢复 |
| 配置与方案 | 冲突候选、ResolvedView、配置合并、UserFix、Profile lock / bundle 导入导出 |
| 账户与云端 | 本地会话和 SQLite、OTP / CloudAuth 调用链、Worker 路由、邮件权限、限流与统计 |
| 构建与交付 | 五个 .NET 项目、NuGet 依赖、版本号、全新 publish、安装脚本、CI、网站脚本与本地资源引用 |

“全仓”指全源码盘点并沿关键业务链核查，不表示每个业务状态都做过真实交互验收。

## 本轮完成的消融与修复

### 删除没有生产入口的功能

删除六组窗口及对应 code-behind：

- EmailConfigWindow
- GamePathConfirmDialog
- PluginImportDialog
- MigrationWizardWindow
- DeployPreviewDialog
- ConfigManagerWindow

同时删除只为这些旧路径服务或已无消费者的 ConfigManagerViewModel、DummyCommand、XamlRuntimeTracker、DragAdorner、ModConflictRegistry、ModConflictModels、ConflictGroup、HostDefinition，以及没有独立项目且已被主项目排除的 ConflictProbe 源码。移除对应 DI 注册、无调用方法和构造函数依赖；GameProfile 的闲置类删除，实际使用的 GameType 枚举独立保留。

此前已删除的 RepositoryManagerWindow、OverwriteManagerWindow 和 LocalCacheService 属于继承改动，不计为本轮新增删除。实际 Debug 调试使用的 AllocConsole 保留。

### 去掉旧 MOD 数据的重复写入

运行时 MOD 数据以 PackageRepository 和 Profile 为准。原 ModDataService 没有调用方初始化当前游戏，却仍在刷新和删除时写旧格式 `_mods.json`。本轮移除该服务及 MainViewModel 的重复写入、DI 注册和未使用注入。

DataMigrationService、ProfileService 中读取历史 `{game}_mods.json` 的迁移路径保留；没有删除历史用户文件。MainViewModel 的同步仓库刷新继续保留 Task 返回接口。

### 将账户页改成真实只读数据

AdminDashboardWindow 现在显示“本机账户 / Local Accounts”。删除模拟统计、假延迟和占位成功操作，保留实际数据库查询、搜索、刷新、语言切换与事件清理。

原 DataGrid 关闭自动生成列却没有声明任何列，本轮补上实际字段；同时修正表头对比度和邮箱列宽。数据库查询失败时显示“读取失败”和不可用数值，LocalAuthService 记录并上抛异常，避免把失败显示成正常的零账户。

删除没有业务消费者的“启用插件系统”“启用前先确认”设置。真实插件导入和“导入后自动启用”继续保留；隐藏后端选择的兼容持久化字段没有清除。

### 修复前序资源消融造成的界面回归

MainWindow 的 MOD 模板仍使用以下三个转换器，但前序消融删除了它们的全局资源注册：

- PackageKindToBrushConverter
- PackageKindToBgBrushConverter
- PackageKindToLabelConverter

本轮在 App.xaml 恢复注册，并让资源测试实际实例化 MOD 卡片模板，覆盖 WPF 延迟加载的情况。管理中心的无效 `Text500` 引用改为 `Text500Brush`。

删除确认无消费者的文件大小、胜负背景、部署操作类型转换器，以及对应无引用的主题颜色、画刷和样式。主题快照通过现有生成器更新并审阅，生成器测试保持手动跳过。

### 部署失败终止启动

LaunchOrchestrator 原来把部署规划或执行异常转换成 Warning 后继续启动。本轮让异常进入统一失败处理、记录失败并中止后续步骤；打开启动中心出错时，MainWindow 也不再直接启动游戏。

新增回归用例使用真实规划器触发“没有活跃的 Profile”，修复前得到 Warning，修复后得到 Failed，且冲突检查与启动进程步骤都没有执行。移除无调用的直接启动命令和重复 GameConfigService.LaunchGame 路径。

**这个修复只保证已检测到的失败会阻断启动；下面 F01、F02 中“错误地产生空计划”的问题仍然存在。**

### 依赖、发布和网络请求减负

CUE4Parse 只被已排除的探针代码引用，本轮移除它和 14 个传递依赖。依赖锁定结果实际减少的 15 个包为：

`CUE4Parse`、`Blake3`、`BouncyCastle.Cryptography`、`CommunityToolkit.HighPerformance`、`Infrablack.UE4Config`、`K4os.Compression.LZ4`、`K4os.Compression.LZ4.Streams`、`K4os.Hash.xxHash`、`LZMA-SDK`、`OffiUtils`、`Oodle.NET`、`Serilog`、`Serilog.Sinks.Console`、`Zlib-ng.NET`、`ZstdSharp.Port`。

主程序与应用测试工程不再屏蔽已无必要的 MSB3246。Core 的文件版本同步为 2.1.0.0。

Build-Installer.ps1 每次将程序发布到全新暂存目录，再将绝对路径传给 ISCC，防止旧 bin 目录中的残留 DLL 被打包。保留版本与敏感文件检查；成功时只清理本次创建且已核实位于暂存根下的目录，失败时保留现场。脚本保持 ASCII，兼容 Windows PowerShell 5.1 的源码解码。

CI 增加 `codex/**` 分支和 Node.js 22 的 Worker 自测步骤，本轮没有推送或触发远端运行。

CloudAuth 删除无消费者的激活占位、偏好与刷新包装 API、DTO 和配置字段；登录、注册不再发送无人消费的设备信息对象，User-Agent 改用真实程序集版本。现有认证架构保留。模拟 HTTP 测试验证请求形状、登录状态和注册后登录，但不证明服务器提供对应路由，见 F07。

README、文档索引、架构说明和本次未发布变更记录已同步实际状态，包括本地 PBKDF2-SHA256 密码哈希、当前没有 MOD 云同步、真实数据路径及统计标识的限制。历史记录明确标注时间，未将旧“完成”标记作为当前验收结论。

## 仍待修复的确认问题

P1 表示应优先修复的文件一致性或服务安全问题；P2 表示需补齐的功能契约和连接问题。以下全部是**未修复项**，不是本轮已完成内容。

### F01 · P1 · 切换到空方案后，上一方案的 MOD 文件仍保留

**触发与影响：** 已有方案部署 MOD 后，切换到新建空 Profile。空方案应停用此前由管理器部署的 MOD，但生成的计划没有移除操作，游戏目录中仍有旧文件。

**根因：** [ProfileService.cs:209](D:/modmangerpd/codex-fix-build/UEModManager/Services/ProfileService.cs:209) 创建空包列表；[DeploymentPlanner.cs:217](D:/modmangerpd/codex-fix-build/UEModManager/Services/DeploymentPlanner.cs:217) 仅把“出现在当前方案里且被禁用”的文件认作可移除的已知文件。包即使仍在仓库登记，只要不在新方案中，也会被当作未知文件；[DeploymentDiffComputer.cs:87](D:/modmangerpd/codex-fix-build/UEModManager.Core/Services/DeploymentPlanning/DeploymentDiffComputer.cs:87) 随后跳过它。

本地真实服务复现：`DesiredPackages=0, PreviousModStillExists=true, RemoveOperations=0, TotalOperations=0`。复现只生成计划，没有执行部署。

**修复方向：** 建立跨 Profile 的可靠部署文件归属，再按当前期望状态生成移除计划；必须同时保护用户自行放入的未托管文件。不能简单删除所有“新方案没有列出”的路径。

### F02 · P1 · 内容变化但大小相同的文件不会更新

**触发与影响：** 仓库期望内容为 `AAAA`，游戏目录实际为 `BBBB`，两者大小相同。计划仍为零操作，启动流程会认为无需部署。

**根因：** [DeploymentPlanner.cs:214](D:/modmangerpd/codex-fix-build/UEModManager/Services/DeploymentPlanner.cs:214) 和同文件第 245 行提供的实际文件 Hash 始终为 null，“延迟计算”未落实。[DeploymentDiffComputer.cs:65](D:/modmangerpd/codex-fix-build/UEModManager.Core/Services/DeploymentPlanning/DeploymentDiffComputer.cs:65) 对同大小文件只有在两个哈希都存在时才检查替换。

本地真实服务复现：`Operations=0, ActualHash=4a8d8134f29b0b7b, ExpectedHash=63c1dd951ffedf6f`。

**修复方向：** 对大小相同的候选文件进行内容验证，并根据成本设计缓存和失效策略；不能把“没有算哈希”当成“内容一致”。

### F03 · P2 · 配置合并候选已被去重，合并产物也没有进入部署

**触发与影响：** 两个启用的配置包都贡献同一个 INI 文件。实际只留下一个胜者条目，没有生成配置合并结果。

**根因：** [ResolvedViewBuilder.cs:83](D:/modmangerpd/codex-fix-build/UEModManager/Services/ResolvedViewBuilder.cs:83) 先通过 BuildPackageLayer 将同路径候选折叠为一个胜者，随后第 88 行把这些条目交给配置合并规划；[ResolvedViewLayerBuilder.cs:155](D:/modmangerpd/codex-fix-build/UEModManager.Core/Services/ResolvedViews/ResolvedViewLayerBuilder.cs:155) 又要求同路径候选数大于 1。

本地真实服务复现：`EnabledConfigPackages=2, ResolvedEntries=1, MergeResults=0, Conflicts=1`。

另一个连接缺口见 [LaunchOrchestrator.cs:218](D:/modmangerpd/codex-fix-build/UEModManager/Services/LaunchOrchestrator.cs:218)：BuildView 只记录视图哈希，DeployIfNeeded 再调用 DeploymentPlanner 建立独立包文件计划，没有部署配置合并产物或 UserFix 视图条目的消费者。

**修复方向：** 在胜者求解前保留配置候选，明确 ResolvedView 与实际部署之间的契约并连接输出。相关配置模型仍具有业务含义，本轮未因生产连接不足而删除。

### F04 · P1 · 嵌套解压体积上限在写入完成后才统计

**触发与影响：** 一个展开后超过剩余预算的压缩包仍会完整写入，预算不能阻止当次磁盘空间消耗。

**根因：** [ArchiveExtractor.cs:110](D:/modmangerpd/codex-fix-build/UEModManager/Services/ArchiveExtractor.cs:110) 先检查上一个包累积的体积，第 123 行完整展开下一包，第 134 行才统计新增字节。

使用小型本地 ZIP 和测试预算复现：`BudgetBytes=8192, ExtractedBytes=65536`。默认 4 GiB 配置因此不能视为写入过程的硬上限。

**修复方向：** 在提取流写入时限制累计字节，越界立即终止并处理残留；仅信任压缩包声明的展开大小不足以保证限制有效。

### F05 · P1 · 未认证调用方可以通过邮件接口指定收件人和内容

**触发与影响：** 匿名请求可向 `POST /email/send` 传入收件人、主题和 HTML，由应用的 Brevo 身份发送，存在邮件滥发和服务配额消耗风险。

**证据：** [Worker index.js:1048](D:/modmangerpd/codex-fix-build/cf-workers/modmanger-api/src/index.js:1048) 没有身份或动作授权，仅检查邮箱格式、主题中包含品牌词、HTML 长度和限流。品牌词可以由调用方提供，不能建立授权。

对当前 Worker 模块拦截全部 fetch 的本地复现：`status=200, mockedOutboundCalls=1`。**没有向 Brevo 或任何收件人发送真实邮件，也没有据此断言线上当前部署与源码一致。**

**修复方向：** 将验证码生成和模板内容控制放到服务端，按认证动作授权收件人和发送行为。把共享密钥写进桌面客户端不能可靠解决匿名滥用。

### F06 · P1 · KV 限流计数在并发请求下丢失递增

**触发与影响：** 同一窗口的多个请求同时读取旧计数，随后写入同一新值，突破预期限额；认证和邮件路由都依赖该限流函数。

**证据：** [Worker index.js:65](D:/modmangerpd/codex-fix-build/cf-workers/modmanger-api/src/index.js:65) 采用独立的 KV get / put，没有原子递增。本地并发模拟中：`configuredLimit=10, concurrentRequests=20, accepted=20, storedCounters=["1"]`。

该复现证明代码存在丢更新，不代表对线上做过负载测试。它与 D1 统计表的计数逻辑是两个机制。

**修复方向：** 使用具有原子性或服务端串行协调能力的限流机制，并覆盖并发和时间窗口边界。

### F07 · P2 · CloudAuth 客户端调用的部分接口在当前 Worker 中不存在

对仓库中的 Worker 直接构造请求，得到：

| 客户端请求 | 当前 Worker 返回 |
| --- | ---: |
| POST /api/auth/register | 404 |
| POST /api/auth/logout | 404 |
| GET /api/auth/validate | 404 |

调用点见 [CloudAuthService.cs:139](D:/modmangerpd/codex-fix-build/UEModManager/Services/CloudAuthService.cs:139)、同文件第 183 和 218 行，并由 UnifiedAuthService 使用。

**影响边界：** 主登录窗口当前主要经过 CustomOtpService / LocalAuthService，不能把此问题描述为“所有登录必然失败”。问题落在仍保留的云端注册、退出通知、会话验证契约上。本地模拟响应的客户端单测不能发现服务端缺失路由；线上实际部署版本本轮未确认。

**修复方向：** 先明确真正使用的认证入口，再统一客户端和 Worker 的路由、请求及响应契约，补跨边界验证。

### F08 · P2 · Profile lock 导入忽略已导出的冲突覆盖

**证据与影响：** [ProfileLockService.cs:64](D:/modmangerpd/codex-fix-build/UEModManager/Services/ProfileLockService.cs:64) 和第 182 行导出 GetOverrides；[ProfileLockBuilder.cs:53](D:/modmangerpd/codex-fix-build/UEModManager.Core/Services/Lock/ProfileLockBuilder.cs:53) 将其写入 ConflictOverrides。但 [ProfileLockService.cs:116](D:/modmangerpd/codex-fix-build/UEModManager/Services/ProfileLockService.cs:116) 的 ApplyImportAsync 只导入包启用状态和优先级，然后切换 Profile，未恢复这些覆盖规则。

因此导入后的冲突选择可能与导出时不同。此项依据源码调用链确认，没有包含在前述四项 .NET 可执行复现中。

**修复方向：** 明确冲突覆盖应属于每个 Profile 还是现有全局状态，再实现对应导入语义与往返验证，避免导入一个方案时覆盖其他方案的全局规则。

### 后续修复顺序

建议先处理 F01 / F02 的部署一致性，以及 F05 / F06 的邮件与限流边界；随后补 F04 的解压硬限制，再连接 F03 / F07 / F08 的功能契约。它们需要独立行为设计与回归验证，本轮没有用删除子系统代替修复。

## 明确保留的机制

- PackageRepository / ObjectStore / Profile 的实际持久化和历史 JSON 迁移读入。
- 完整性正向检查与仓库反向回收的职责边界；缺失或损坏的备份不能因此自动清理。
- 回收时跨游戏扫描引用，遇到无法读取的索引时保守处理；导入包名同时检查索引和实体仓库，避免跨游戏覆盖。
- 数据与仓库搬移、恢复、墓碑、受保护的复制与删除、事务回滚和崩溃恢复。
- Profile lock / bundle、查询接口、公共序列化兼容形状和 ConfigPatch 等业务模型。
- HardLinkBackend 的真实回滚回归用途；生产 DI 当前只注册 CopyBackend。
- 本地认证、Debug 默认管理员与 Release 清理逻辑；本地密码实际为 PBKDF2-SHA256。

保留意味着这些机制不符合本轮删除依据，不表示其所有边界均已验收。

## 验证结果与限制

| 验证项 | 接手基线 | 本轮结束 |
| --- | --- | --- |
| Debug 构建 | 0 警告 / 0 错误 | 0 警告 / 0 错误，使用 -warnaserror |
| Release 构建 | 接手时未单独重跑 | 0 警告 / 0 错误，使用 -warnaserror |
| Core 测试 | 1208 通过 | Debug、Release 均 1208 通过 |
| 应用测试 | 474 通过 / 1 跳过 | Debug、Release 均 484 通过 / 1 跳过 |
| Worker 自测 | 58 通过 | 58 通过 |
| NuGet 已知漏洞扫描 | — | 五个项目均未报告已知易受攻击的依赖 |
| 真实 XAML 资源与布局 | — | 19 个现存窗口加载与布局通过，0 失败 |
| 发布目录 | — | 全新 Release publish 成功，142 个文件 / 119,011,450 字节 |
| 安装脚本 | — | PowerShell 解析 0 错误，源码 ASCII |
| 网站与差异检查 | — | JS 语法通过，本地资源引用无缺失，git diff --check 通过 |

新增 12 个长期回归用例：SQLite 账户查询 3 个、CloudAuth 模拟 HTTP 2 个、启动失败 1 个、窗口与模板资源 6 个；删除了对应无效设置的 2 个用例，因此应用测试净增 10 个。唯一跳过项是手动主题快照生成器。

19 个窗口检查使用实际 XAML 和资源顺序，在隔离工具中去除 x:Class 与事件绑定后布局；没有实例化生产 App。额外重放“缺少 MOD 类型转换器”的旧资源状态，工具能正确检出失败。账户页、设置页和主界面截图已检查；它们使用夹具数据，主界面卡片宽度等不代表生产 code-behind 的最终计算。

Release 验证覆盖本轮最后的行为和资源变化，之后仅调整文档与注释；收尾时对当前源码重新执行了完整 Debug 编译和测试。

发布产物目录为：

`C:/Users/a/.codex/tmp/modmanager-ablation-20260905/publish-1c5e787bff0b449a88367765d01b826f/`

其中程序版本为 2.1.0.0，未找到已移除依赖的残留文件，也未找到 `.env/.enc/.pfx/.p12/.key` 类文件。该目录是框架依赖的发布暂存产物，**不是已编译安装包**。

本机没有 Inno Setup 编译器，未验证安装包生成与安装。没有进行真实游戏启动、真实 MOD 部署验收、线上认证或邮件验收，也没有提交、推送或部署。上述检查不能替代这些验收，更不能消除 F01–F08 已记录的缺陷。

## 证据与复现

以下路径统一以 `C:/Users/a/.codex/tmp/modmanager-ablation-20260905/` 为根。临时工具不属于 Git 提交内容；报告已保留关键结果，若需长期归档应连同此目录保存。

| 文件或目录 | 内容 |
| --- | --- |
| inherited.patch / inherited-status.txt / inherited-new/ | 接手前已有改动的备份 |
| source-inventory.json / source-inventory-final.json | 接手与结束源码盘点，含扫描范围和候选 |
| scanner/ | 临时 Roslyn 候选扫描器；只运行下述扫描命令 |
| baseline-build.log / baseline-tests/ | 接手基线 |
| final-debug-*.log / final-release-*.log / final-tests/ | 编译、测试日志与 TRX |
| final-worker-tests.log / final-vulnerability-scan.json | Worker 自测和依赖扫描结果 |
| final-publish.log / publish-path.txt | 全新发布日志与目录 |
| ui-harness/ / ui-check.log / ui-check/ | 隔离窗口检查、结果与截图 |
| findings-probe/ / findings-reproductions.jsonl | F01–F04 的真实服务夹具程序与结果 |
| worker-findings.mjs / worker-findings.json | F05–F07 的本地 Worker 模拟与结果 |

常规验证命令在审计工作树运行：

```powershell
Set-Location 'D:/modmangerpd/codex-fix-build'
dotnet build UEModManager.sln --configuration Debug -warnaserror
dotnet test UEModManager.sln --configuration Debug --no-build
dotnet build UEModManager.sln --configuration Release -warnaserror
dotnet test UEModManager.sln --configuration Release --no-build
node cf-workers/modmanger-api/verify.mjs
dotnet list UEModManager.sln package --vulnerable --include-transitive
git diff --check
```

本次使用的独立复现工具：

```powershell
$auditEvidence = 'C:/Users/a/.codex/tmp/modmanager-ablation-20260905'
dotnet run --project "$auditEvidence/findings-probe/FindingsProbe.csproj" -- "$auditEvidence"
node "$auditEvidence/worker-findings.mjs"
dotnet run --project "$auditEvidence/scanner/Scanner.csproj" -- 'D:/modmangerpd/codex-fix-build' "$auditEvidence/source-inventory-final.json"
```

.NET 复现每次创建新的 fixtures 子目录，只对临时仓库、方案、游戏目录与小型 ZIP 操作，不执行部署或启动进程。Worker 复现先替换全局 fetch，再调用当前源码模块，外发请求全部由本地模拟处理。F08 的复核入口是报告中列出的导出与导入方法。

早期 `reviewed-removals*.json` 是过程中的候选计划，有些条目后来已恢复；**最终源码和本报告才代表交付状态，不能直接重放旧删除计划。**
