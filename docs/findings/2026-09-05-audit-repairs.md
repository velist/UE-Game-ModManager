# 2026-09-05 审计问题修复与验收

[消融审计](2026-09-05-ablation-audit.md)中的 F01–F08 已完成本地修复，并通过完整 Debug / Release 构建和测试。改动保留在 `D:/modmangerpd/codex-fix-build`，分支为 `codex/fix-20260810`，HEAD 仍为 `79baf12`；没有提交、推送或部署。

本次接续已有消融结果，由部署、导入、云端三个子代理分别修复，根任务复核改动并完成跨模块和全量验收。此前的删除与简化没有撤回；三个继承的包完整性分析文件与消融前备份的 SHA-256 仍一致。没有修改旧工作树 `D:/modmangerpd/测试`。

## 八项修复

| 编号 | 原级别 | 修复后的行为 | 主要实现 / 回归 |
| --- | --- | --- | --- |
| F01 | P1 | 按游戏和安装目录持久化精确部署归属；切换空方案会移除此前托管文件，保留未托管文件，必要时恢复被覆盖的原件 | `DeploymentStateStore`、`DeploymentPlanner`、`DeploymentService`；`DeploymentConsistencyTests`、`DeploymentCrashProgressTests` |
| F02 | P1 | 同大小文件也比较真实内容哈希；源或目标在计划后变化会阻断执行，提交前再次核对结果 | `DeploymentDiffComputer`、`DeploymentPlanner`、`DeploymentService`；同大小替换、过期计划与外部修改回归 |
| F03 | P2 | 配置候选先合并，启动和开关操作消费完整 ResolvedView；合并配置和 UserFix 实际进入游戏目录 | `ResolvedViewBuilder`、`ResolvedViewLayerBuilder`、`LaunchOrchestrator`；配置、UserFix、启动部署集成回归 |
| F04 | P1 | 解压在实际输出流上执行硬上限；根包、嵌套包共享 4 GiB 累计预算，bundle 也按累计写入限制 | `ExtractionBudget`、`ArchiveExtractor`、两个导入入口；真实 ZIP / RAR / 7z 与导入服务回归 |
| F05 | P1 | 任意内容邮件接口返回 410、零外发；验证码由服务端生成、固定模板发送并核验，客户端只保存 challenge ID | Worker `auth-api.js`、`auth-security.js`、`mail.js`；`CustomOtpService`、`WorkerEmailService`、真实 workerd 回归 |
| F06 | P1 | SQLite Durable Object 在事务内判断并更新限额与验证码消费状态；缺失绑定时停止受保护操作 | `AuthState`、Wrangler 绑定与 migration；双 Worker 并发、时间窗口和完整运行时重启回归 |
| F07 | P2 | 补齐 register / logout / validate；待邮箱确认不会伪登录或误走本机注册，退出失败也清除桌面 token | `CloudAuthService`、`UnifiedAuthService`、Worker 路由；C#、SQLite 与桌面到 workerd 契约验收 |
| F08 | P2 | 覆盖规则属于各个 Profile；lock 导入恢复规则，克隆、切换和重启互不污染，导出采用可迁移的相对路径键 | `InstanceProfile`、`ProfileService`、`ConflictOverridePaths`、`ProfileLockService`；往返、迁移失败与跨安装目录回归 |

### 部署归属、合并与回滚

部署清单独立于当前 Profile，以游戏名、游戏根目录和 MOD 根目录确定安装范围，事务保存前后两个版本。提交、回滚和崩溃恢复同步文件与清单；进程内信号量加文件独占锁防止重复执行，旧计划不能覆盖较新的状态。

收尾时另修复了执行进度日志每 25 项落盘导致的恢复遗漏。回归实际执行 30 次文件写入，在第 26 次取得服务自身保存的第 25 项检查点，再重放已写入 26 / 30 项的崩溃状态；旧代码会漏回滚 1 / 5 个文件却报告成功。崩溃恢复现在使用完整计划，并持久保存恢复范围，让部分失败后重载重试仍包含尾段。进程内捕获的失败继续使用准确执行记录，不扩大到尚未执行的目标。

完整计划中的未记录 Add 操作还会核对目标内容：只有与计划哈希一致才允许删除；缺少哈希或内容不符时保留文件并报告部分回滚。这一回归同时覆盖了外部文件保护，不会把计划中的路径直接当作文件归属证明。

升级迁移只采信旧格式的已提交事务及其实际路径、内容证据，并按历史次序处理后续覆盖、移除和不确定状态。没有可靠事务记录的文件，即使路径和内容与仓库相同，也保留为未知文件。原 F01 探针手工摆放的文件不构成删除授权；本次升级回归使用真实旧格式部署事务，验证升级后切换空方案与回滚。

覆盖用户或游戏原有文件前，原件以流方式保存到独立目录，不随旧事务备份一起清理；停用相关配置时恢复原件。已托管文件被外部修改时，停止自动移除并报告问题。原件备份缺失或部署清单损坏时，也不会继续删除文件。

配置包共享其声明的实际目标；普通 MOD 和 PAK 仍部署在各自包目录，保留加载顺序冲突分析的原有语义。配置冲突指定的胜者决定重复键，其他包的独有键保留。相同优先级遵循 Profile 原顺序，解析结果与实际安装一致。带 Profile 来源的 UserFix 只作用于对应方案。

内容哈希没有持久化缓存，大型仓库的检查成本可能增加。原件与合并产物采用保守保留策略，本次没有引入自动回收这些文件的规则。

### 解压和方案迁移边界

预算统计实际写入字节，不依赖压缩包声明大小；中间压缩包与已清理的失败写入也占用同一预算。超限时立即停止写入，清理本次创建的失败输出并关闭压缩包句柄。既有目标文件不覆盖、不删除；路径穿越和链接条目被拒绝。bundle 的压缩元数据读取另有限额，避免预览先展开无界 JSON。

bundle 沿用逐包提交：后续包失败时，先前完整注册的包保留，失败包清理，导入方案不创建。嵌套展开上限仍为 3 层。

旧游戏级冲突规则复制到当时已有的 Profile。只有原子保存成功，字段存在性才成为迁移完成标记；旧文件保留，保存失败可以重试，新建空方案不继承旧全局规则。新 lock 使用 `@mod/`、`@game/` 路径；旧绝对路径只有在胜者包文件能唯一匹配时才改映射，缺包或有歧义时保留原键。

### 云端协议与发布要求

服务端验证码有效期 10 分钟、重发间隔 60 秒、最多 5 次错误尝试。正确核验与消费发生在同一持久事务内。邮件失败不返回验证码或 challenge，保留冷却时间。桌面网络失败不会回退到本地验证码或客户端 Brevo / SMTP 发信。

`/email/send` 及其 `/v1` 别名已经停用。新客户端与 Worker 必须安排在同一发布窗口，提前配置 `SECURITY_STATE` Durable Object 绑定、SQLite migration 和原有服务端 secrets。具体接口、限额、迁移配置与 Supabase 邮箱确认语义见 [AUTH_PROTOCOL.md](../../cf-workers/modmanger-api/AUTH_PROTOCOL.md)。旧 KV 的远端数据没有被删除。

独立契约验收还复现并修复了 workerd 的连接边界：拒绝旧邮件 POST 时未消费请求体，会使同一 .NET HTTP/1.1 连接的后续请求重置。现有端点在受限读取请求体后返回 410，仍然没有邮件外发。

## 最终验证

环境：Windows、.NET SDK 8.0.416、Node.js 24.11.1。以下结果来自三个子代理冻结代码之后的根任务复验。

| 验证项 | 结果 |
| --- | --- |
| 完整 Debug 构建，`-warnaserror` | 0 警告、0 错误 |
| 完整 Release 构建，`-warnaserror` | 0 警告、0 错误 |
| Core 全量测试 | Debug、Release 各 1211 通过、0 失败；比修复前净增 3 项 |
| 应用全量测试 | Debug、Release 各 582 通过、1 跳过、0 失败；比修复前净增 98 项 |
| Worker 原有自测 | 58 通过 |
| 真实 Miniflare / workerd 测试 | 18 通过；两个独立 Worker 共享持久 DO 状态 |
| 实际 C# 服务到 workerd 的跨进程契约 | 17/17 通过 |
| 锁文件安装与依赖扫描 | `npm ci` 成功，`npm audit` 报告 0 项已知漏洞 |
| Wrangler dry-run | 成功，确认 AuthState DO 与现有 D1 绑定；没有远端部署 |
| 全新 Release publish | 142 个文件，119,081,022 字节，FileVersion 2.1.0.0 |
| 差异与文档 | `git diff --check`、新文件尾部空白检查、已编辑文档本地链接检查通过 |

唯一跳过项是手动主题快照生成器。全量测试包含真实临时文件部署、SQLite、WPF 资源与模板检查；测试不会启动游戏。首次将 .NET 测试输出放在仓库外时，58 个源码/资源定位检查失败；它们沿输出目录向上寻找解决方案。改用仓库内受 Git 忽略的独立输出目录后全部通过，没有为此修改业务代码或降低断言。

`ProfileDeploymentIntegrationTests` 覆盖：源安装导出选择 B 的方案；目标安装保留选择 A 的旧方案；导入恢复 B 的覆盖，合并保留双方独有键；部署后切换 A 触发同大小替换；回滚恢复 B；服务重启保留状态；切换空方案清除托管配置而保留用户文件，再次回滚恢复配置。

Worker 并发测试证明同一分钟 20 路登录只允许 10 路、同一正确验证码 20 路核验只成功 1 路，并覆盖完整 workerd 关闭后重启。所有 Supabase 和 Brevo 请求由测试夹具截获，未知外部 URL 直接失败，没有真实外发兜底。桌面契约使用真实 C# 服务和仅监听回环地址的运行时。

发布目录为 `C:/Users/a/.codex/tmp/modmanager-fixes-20260905/publish-7ca95a7dbb7e4d48985a20990615e837/`。这是框架依赖的本地发布产物。本机没有 Inno Setup 编译器，没有生成或安装新安装包；没有进行真实游戏、线上认证和邮件可达性验收。

## 复验命令与证据

```powershell
Set-Location 'D:/modmangerpd/codex-fix-build'
$repairArtifacts = 'D:/modmangerpd/codex-fix-build/bin/repair-verification-20260905'
dotnet build UEModManager.sln -c Debug --artifacts-path $repairArtifacts -warnaserror
dotnet test UEModManager.sln -c Debug --artifacts-path $repairArtifacts --no-build
dotnet build UEModManager.sln -c Release --artifacts-path $repairArtifacts -warnaserror
dotnet test UEModManager.sln -c Release --artifacts-path $repairArtifacts --no-build

Set-Location 'D:/modmangerpd/codex-fix-build/cf-workers/modmanger-api'
npm ci
npm test
node test/desktop-contract.mjs
npm audit
$env:WRANGLER_SEND_METRICS = 'false'
npm run check:bundle
```

证据统一位于 `C:/Users/a/.codex/tmp/modmanager-fixes-20260905/`：

| 文件 / 目录 | 内容 |
| --- | --- |
| `start-snapshot/`、`start-manifest.json`、`start-status.txt` | 本次修复开始阶段的源码保存；部分子代理测试当时已创建，不是原子基线 |
| `deployment/`、`import/`、`cloud/` | 各组原始失败与修复后的聚焦日志、TRX |
| `acceptance/`、`cloud-contract/` | 跨模块及跨进程验收建立过程 |
| `final-debug-build-complete.log`、`final-release-build-complete.log` | 包含最后崩溃恢复补修的完整构建 |
| `final-debug-tests-complete.log`、`final-release-tests-complete.log`、`final-tests/` | 最终全量测试与 TRX；目录内同时保留前序复验和外部输出目录失败记录 |
| `final-worker-tests.log`、`final-desktop-contract-complete.log` | 最终 Worker 与桌面协议验收 |
| `final-npm-ci.log`、`final-npm-audit.json`、`final-worker-bundle.log` | 依赖重装、漏洞扫描与打包 |
| `final-publish-complete.log`、`publish-check-complete.json`、`installer-availability.json` | 最终发布产物与安装编译器检查 |
| `inherited-integrity-check.json` | 三个继承完整性分析文件的哈希比对 |
| `final-test-summary.json`、`final-diff-check.log`、`documentation-links-check.json` | 测试数量汇总、差异和文档链接复核 |

CI 已加入锁文件安装、Worker 运行时测试、桌面协议验收和 Wrangler dry-run。当前仅验证了这些步骤在本机执行的结果，没有推送或触发远端 CI。
