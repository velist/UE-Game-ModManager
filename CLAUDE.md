# UEModManager - 技术文档

## 项目概述

.NET 8.0 WPF 游戏 MOD 管理器，提供 MOD 导入/部署/冲突处理、方案（Profile）管理、云端认证与离线模式。
起步于虚幻引擎游戏，**现已不限于 UE**：引擎类型由 `EngineType` 描述
（UnrealEngine / Unity / REEngine / Godot / Decima / Diablo4Engine / Unknown），
每种引擎的文件扩展名、默认 MOD 路径等规则集中在 `UEModManager/Models/EngineProfile.cs`。

**核心功能：**
- 多游戏支持，内置 11 款（`GameConfigService.GetAvailableGames`）：
  黑神话·悟空、剑星、剑星 (CNS)、光与影：33号远征队、明末·渊虚之羽、暗黑破坏神4、
  生化危机9、识质存在、无主之地4、死亡搁浅2、杀戮尖塔2；其中后 5 款里有 4 款非 UE
  （`GameConfigService.GetEngineType` 显式映射 Godot / Decima / REEngine / Diablo4Engine），
  另可由用户添加自定义游戏并指定引擎
- MOD 导入（含压缩包/整合包）、按方案部署、冲突检测、事务化回滚与崩溃恢复
- 云端认证 + 本地离线模式
- MOD 备份恢复、分类管理
- Cloudflare Workers API 网关 + Brevo 邮件

---

## 技术栈

- **前端：** .NET 8.0 WPF + C# 12 + XAML
- **后端：** Cloudflare Workers (TypeScript) + Supabase + Brevo
- **存储：** SQLite (本地) + PostgreSQL (云端)

---

## 目录结构

```
UEModManager/
├── UEModManager/              # 主程序（WPF）
│   ├── Services/             # IO 编排层：认证、导入、部署、仓库、配置…（约 37 个服务）
│   │   └── Backends/         # 部署后端实现（CopyBackend / HardLinkBackend）
│   ├── ViewModels/           # MainViewModel / ModListViewModel / ModDetailViewModel…
│   ├── Views/                # 各窗口与对话框
│   ├── Models/               # WPF 侧数据模型（含 EngineProfile）
│   ├── Data/                 # EF Core 上下文
│   ├── Infrastructure/       # SafeEvent 等横切基础设施
│   ├── Migrations/           # EF Core 迁移
│   └── MainWindow.xaml       # 主窗口
├── UEModManager.Core/        # 核心库：纯函数 + 纯模型，无 WPF 依赖
├── UEModManager.Core.Tests/  # Core 单元测试（1145 个）
├── UEModManager.Tests/       # 主程序测试（少量，需要 net8.0-windows）
├── samples/                  # 示例工程（SampleBackend：IDeploymentBackend 实现范例）
├── docs/                     # 架构说明、playbooks、审计报告
├── cf-workers/               # Cloudflare Workers API
├── UEModManager.sln
└── Build-Installer.ps1       # 安装包构建
```

---

## 分层：Core 与主程序

**依赖方向是单向的：主程序 → Core，Core 绝不反向引用主程序。**

- **`UEModManager.Core`**：纯函数 + 纯模型。部署计划计算、冲突检测、路径清洗、
  lock 文件构建、崩溃恢复分类、原子写等算法都在这里，不碰 WPF，也基本不碰 IO
  （`Services/Persistence/AtomicFileWriter` 是被明确划出来的 IO 抽象例外）。
  测试都压在这一层——`UEModManager.Core.Tests` 有 1145 个测试。
- **`UEModManager/Services`**：Core 的 IO 适配层。负责读写文件、访问数据库、记日志、
  调度 UI，纯逻辑部分转调 Core。新增算法应优先落在 Core 并配单测，
  见 `docs/playbooks/writing-core-service.md`。
- 两个程序集**共用 `UEModManager.Models` / `UEModManager.Services.*` 根命名空间**，
  只靠 `using` 判断不出类型来自哪一侧，改动前建议先确认文件所在项目。

---

## 核心组件

### 认证系统

**UnifiedAuthService** - 统一认证，协调本地/云端
- `LoginAsync()` / `SyncUserToLocal()` / `ForceSetAuthStateAsync()`

**LocalAuthService** - 本地会话管理
- `_currentUser` / `ForceSetAuthStateAsync()` / `UpdateUserAsync()`

**CloudAuthService** - 云端认证
- `LoginAsync()` / `SignUpAsync()` / `_accessToken`

### Cloudflare Workers API

- `/api/auth/login` - 登录（POST）
- `/auth/reset` - 密码重置邮件（POST）
- `/reset-password` - 重置页面（GET）
- `/app/update` - 更新检查 + 匿名统计心跳（POST，body `{d,v,o,a?}`）
- `/admin` + `/admin/stats` - 用量看板页面与数据（GET，后者要 `Authorization: Bearer $DASH_TOKEN`）

**UUID → int32 转换：** 使用哈希算法确保在 int32 范围内。

### 用量统计（注册数 / 在线数）

方案口径见 `.claude/audit_reports/2026-07-27-telemetry-options.md`。要点：

- **上报字段就是 `TelemetryReport` 的四个属性**——随机设备 UUID、应用版本、Windows 版本号、
  可选的邮箱哈希。README「安全与隐私」一节是这份清单对用户的公开承诺，加字段必须同步改它。
- **没告知过就绝不上报**：`TelemetryConsent.Decide` 里 `Asked=false` 时 `ShouldReport` 恒为
  false，压过默认开的 `Enabled`。`TelemetryAsked` 与 `TelemetryEnabled` 是两个正交字段，
  别合并。
- 设备标识是**随机 UUID v4**，存 `%LOCALAPPDATA%\UEModManager\device.id`（本机层，
  漫游会让域环境里多台机器共用一个"设备"）。**不要**改成 MachineGuid / MAC / 硬盘序列号。
- 存储只能是 **D1**，不要用 KV：免费档 1000 写/天不够，且 KV 最终一致下的读改写会让计数
  永久失真（现有 `rateLimit()` 就是这个 bug）。
- 上报端点的响应对**任何**输入都完全一致（含格式错误、数据库异常），不回传错误细节。
- 心跳超时硬编码 5 秒，绝不用 `HttpClient` 默认的 100 秒。

---

## 开发指南

### 编译
```bash
dotnet build UEModManager.sln --configuration Debug
dotnet build UEModManager.sln --configuration Release
.\Build-Installer.ps1
```

### 调试
- 日志：`UEModManager/bin/Debug/net8.0-windows/console.log`
- 数据库：`%APPDATA%\UEModManager\local.db`

### Cloudflare Workers 部署

`cf-workers/modmanger-api/` 没有 `package.json`（Worker 是单文件 `src/index.js`，无构建步骤），
因此不存在 `npm install` / `npm run deploy`，直接用 `npx wrangler`：

```bash
cd cf-workers/modmanger-api
npx wrangler deploy
```

secret 需逐个设置（`wrangler secret put` 每次只接受一个键名，会交互式提示输入值）：

```bash
npx wrangler secret put SUPABASE_URL
npx wrangler secret put SUPABASE_ANON_KEY
npx wrangler secret put SUPABASE_SERVICE_KEY
npx wrangler secret put BREVO_API_KEY
npx wrangler secret put BREVO_FROM
npx wrangler secret put BREVO_FROM_NAME
npx wrangler secret put DASH_TOKEN          # 用量看板口令，未设置时 /admin/stats 一律 401
```

以上 7 个即 `src/index.js` 实际读取的全部 secret。KV 绑定 `RATE_LIMIT` 与 D1 绑定 `DB` 在
`wrangler.toml` 中声明，不是 secret，无需 `secret put`。
`wrangler deploy` 不会清除已有 secret 值。

**首次部署统计功能前必须先建 D1 库**，否则 `wrangler deploy` 会因 `database_id` 是占位符而报错：

```bash
npx wrangler d1 create modmanger-stats                                   # 把返回的 uuid 填进 wrangler.toml
npx wrangler d1 execute modmanger-stats --remote --file=schema.sql       # 建表，可重复执行
```

Worker 侧无测试框架，自测用 Node 直接把 fetch handler 跑起来（D1 用内存假实现）：

```bash
cd cf-workers/modmanger-api && node verify.mjs
```

---

## 常见问题

### 1. 登录后显示"离线模式"
`UpdateUserAsync` 只更新数据库，需额外调用 `ForceSetAuthStateAsync` 设置登录状态。

### 2. UUID 转 int32 溢出
使用哈希算法：`hash & 0x7FFFFFFF`

### 3. 密码重置邮件发送失败
API Key 需 `.trim().replace(/[\r\n]/g, '')` 处理换行符。

### 4. JSON 字段名大小写
API 统一用 snake_case，模型用 `[JsonPropertyName("snake_case")]`

---

## 安全

- 本地密码 BCrypt 哈希
- 敏感信息存环境变量，勿提交仓库
- 速率限制：每分钟 10 次登录

---

## 部署

1. 安装包：`.\Build-Installer.ps1` → `installer_output/`
2. Workers：`cd cf-workers/modmanger-api && npx wrangler deploy`

---

## 注意事项

- 结构是**混合的**：已有 `ViewModels/` 一层（`MainViewModel` 及其子 VM，`MainWindow` 通过
  `DataContext` 绑定），但窗口仍保留大量 code-behind（`MainWindow.xaml.cs` 约 1670 行），
  改 UI 前先确认逻辑在 VM 还是 code-behind
- MOD 分类存储在 `ModInfo.Categories` (`List<string>`，默认 `["未分类"]`)，
  `ModInfo.PrimaryCategory` 是取首个元素的只读派生属性（无 `Type` 字段）；
  **权威存储是包仓库的 `Package.Tags`**——`RefreshFromRepositoryAsync` 每次都据此重建
  `AllMods`，只改 `ModInfo.Categories` 的话下一次刷新就被打回去
- 右键菜单"移动到分类"（卡片模式与列表模式各一份 `ContextMenu`，共用 `MainWindow.xaml`
  里的 `MoveToCategoryMenuItem` 样式）：子项由 `CategoryViewModel.AssignableCategories`
  动态生成，落盘走 `MainViewModel.MoveModToCategoryAsync`。
  可选目标不含"全部/已启用/已禁用"——这三个是按 `IsEnabled` 现算的筛选视图、不存储归属，
  名单归口 `Core` 的 `ModCategoryAssignment.SystemCategoryNames`
- 带子菜单的 `MenuItem` 必须用 `CyberMenuItemSubmenuHeader` 样式；`CyberMenuItem` 的模板里
  没有 `Popup` 也没有 `IsItemsHost`，套着它的菜单项填满 `Items` 也弹不出任何东西
- `MainWindow.CategoryList_Drop` 只处理**分类之间的拖拽排序**（数据类型 `CategoryItem`），
  不接受 MOD 拖入；把 MOD 归类只有右键菜单一条路径
- UI 事件处理器统一用 `Infrastructure/SafeEvent.Run` 包裹，不要新写裸 `async void` 处理器
