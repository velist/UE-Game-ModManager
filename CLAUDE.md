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
│   ├── Services/             # IO 编排层：认证、导入、部署、仓库、配置…（约 36 个服务）
│   │   └── Backends/         # 部署后端实现（CopyBackend / HardLinkBackend）
│   ├── ViewModels/           # MainViewModel / ModListViewModel / ModDetailViewModel…
│   ├── Views/                # 各窗口与对话框
│   ├── Models/               # WPF 侧数据模型（含 EngineProfile）
│   ├── Data/                 # EF Core 上下文
│   ├── Infrastructure/       # SafeEvent 等横切基础设施
│   ├── Migrations/           # EF Core 迁移
│   └── MainWindow.xaml       # 主窗口
├── UEModManager.Core/        # 核心库：纯函数 + 纯模型，无 WPF 依赖
├── UEModManager.Core.Tests/  # Core 单元测试（660 个）
├── UEModManager.Tests/       # 主程序测试（少量，需要 net8.0-windows）
├── samples/                  # 第三方扩展示例（SampleBackend / SampleAdapter）
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
  测试都压在这一层——`UEModManager.Core.Tests` 有 660 个测试。
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

**UUID → int32 转换：** 使用哈希算法确保在 int32 范围内。

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
```bash
cd cf-workers/modmanger-api
npm install
wrangler secret put SUPABASE_URL SUPABASE_ANON_KEY BREVO_API_KEY ...
npm run deploy
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
2. Workers：`npm run deploy`

---

## 注意事项

- 结构是**混合的**：已有 `ViewModels/` 一层（`MainViewModel` 及其子 VM，`MainWindow` 通过
  `DataContext` 绑定），但窗口仍保留大量 code-behind（`MainWindow.xaml.cs` 约 1670 行），
  改 UI 前先确认逻辑在 VM 还是 code-behind
- MOD 分类存储在 `ModInfo.Categories` (`List<string>`，默认 `["未分类"]`)，
  `ModInfo.PrimaryCategory` 是取首个元素的只读派生属性（无 `Type` 字段）
- 右键菜单"移动到分类"通过 `ContextMenu` 实现
- 拖拽到左侧分类使用 `MainWindow.CategoryList_Drop` 处理
- UI 事件处理器统一用 `Infrastructure/SafeEvent.Run` 包裹，不要新写裸 `async void` 处理器
