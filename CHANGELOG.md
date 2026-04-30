# 更新日志 / Changelog

本文档记录UE Mod Manager的所有版本更新内容。

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
- **S8 / W4 / N1-N4** 事务粒度、备份链、依赖图、幽灵文件检测 — v2.1 候选

详细分析见 [`memory/project_v2_upgrade_progress.md`](memory/project_v2_upgrade_progress.md) 中的全面审查报告。

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
详见 [`docs/findings/2026-04-28-conflict-detector-noop-by-design.md`](docs/findings/2026-04-28-conflict-detector-noop-by-design.md)。

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
- [`docs/playbooks/writing-host-adapter.md`](docs/playbooks/writing-host-adapter.md) — 写自定义 Adapter
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
- 特别感谢：爱酱游戏群全体群友

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
