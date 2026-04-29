# UEModManager - 技术文档

## 项目概述

.NET 8.0 WPF 虚幻引擎游戏 MOD 管理器，提供云端同步、离线模式、MOD 分类管理。

**核心功能：**
- 多款 UE 游戏支持（剑星、黑神话悟空、明末无双）
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
├── UEModManager/           # 主程序
│   ├── Services/          # 认证服务
│   ├── Models/            # 数据模型
│   ├── Data/              # EF Core 上下文
│   └── MainWindow.xaml    # 主窗口
├── UEModManager.Core/     # 核心库
├── cf-workers/            # Cloudflare Workers API
├── UEModManager.sln
└── Build-Installer.ps1    # 安装包构建
```

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

- 当前代码为旧式单体 code-behind 结构（非 MVVM）
- MOD 分类存储在 `Mod.Categories` (List<string>) 和 `Mod.Type`
- 右键菜单"移动到分类"通过 `ContextMenu` 实现
- 拖拽到左侧分类使用 `CategoryList_Drop` 处理
