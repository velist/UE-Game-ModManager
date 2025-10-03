# UEModManager - Technical Documentation for AI Developers

## 项目概述

UEModManager 是一个专为虚幻引擎 (Unreal Engine) 游戏设计的 MOD 管理器，采用 .NET 8.0 + WPF 架构，提供云端同步、本地离线、自动备份等功能。

**核心特性：**
- 🎮 支持多款虚幻引擎游戏（剑星/Stellar Blade、黑神话悟空、明末无双）
- ☁️ 云端认证与本地离线模式
- 🔄 自动 MOD 备份与恢复
- 📦 MOD 分类管理与批量操作
- 🌐 Cloudflare Workers API 网关
- 📧 邮件验证（Brevo 集成）

---

## 项目架构

### 技术栈

**前端：**
- .NET 8.0 WPF (Windows Presentation Foundation)
- C# 12.0
- XAML for UI

**后端：**
- Cloudflare Workers (TypeScript)
- Supabase (PostgreSQL + Auth)
- Brevo (Transactional Email Service)

**数据存储：**
- SQLite (本地数据库)
- Entity Framework Core 8.0
- Supabase PostgreSQL (云端数据库)

---

## 目录结构

```
UEModManager/
├── UEModManager/                  # 主程序
│   ├── Services/                  # 服务层
│   │   ├── LocalAuthService.cs    # 本地认证服务
│   │   ├── CloudAuthService.cs    # 云端认证服务
│   │   ├── UnifiedAuthService.cs  # 统一认证服务
│   │   ├── AuthenticationService.cs # 邮件认证服务
│   │   ├── LocalCacheService.cs   # 本地缓存服务
│   │   └── OfflineModeService.cs  # 离线模式服务
│   ├── Models/                    # 数据模型
│   │   ├── LocalModels.cs         # 本地数据模型
│   │   └── CloudModels.cs         # 云端数据模型
│   ├── Data/                      # 数据访问
│   │   └── LocalDbContext.cs      # EF Core 上下文
│   ├── Windows/                   # 窗口
│   │   └── AuthenticationWindow.xaml # 认证窗口
│   └── MainWindow.xaml            # 主窗口
├── UEModManager.Core/             # 核心库
│   ├── Models/                    # 核心数据模型
│   └── Services/                  # 核心服务
├── cf-workers/                    # Cloudflare Workers
│   └── modmanger-api/
│       └── src/
│           └── index.ts           # API 网关
├── .gitignore                     # Git 忽略规则
├── UEModManager.sln               # 解决方案文件
├── installer_clean.iss            # Inno Setup 安装脚本
└── Build-Installer.ps1            # 安装包构建脚本
```

---

## 核心组件

### 1. 认证系统

#### UnifiedAuthService
统一认证服务，协调本地和云端认证。

**关键方法：**
- `InitializeAsync()` - 初始化认证系统
- `LoginAsync(email, password)` - 统一登录入口
- `SyncUserToLocal(cloudUser, password)` - 同步云端用户到本地

**认证流程：**
```
用户登录
  ↓
CloudAuthService.LoginAsync()
  ↓
Cloudflare Workers /api/auth/login
  ↓
Supabase Auth Token
  ↓
SyncUserToLocal()
  ↓
ForceSetAuthStateAsync() ← 设置本地登录状态
  ↓
触发 AuthStateChanged 事件
  ↓
UI 更新为"云端在线"
```

**重要修复（2025-10-03）：**
在 `SyncUserToLocal` 方法中，更新已存在用户后必须调用 `ForceSetAuthStateAsync` 来设置登录状态，否则 UI 会显示"离线模式"。

```csharp
// UnifiedAuthService.cs:577-580
await _localAuthService.UpdateUserAsync(existingUser);
// 强制设置登录状态，确保 _currentUser 和会话被正确设置
await _localAuthService.ForceSetAuthStateAsync(cloudUser.Email, cloudUser.Username);
```

#### LocalAuthService
本地认证服务，管理本地用户会话和数据库。

**关键字段：**
- `_currentUser` - 当前登录用户
- `_currentSession` - 当前会话

**关键方法：**
- `LoginAsync(email, password)` - 本地密码验证
- `ForceSetAuthStateAsync(email, username)` - 强制设置登录状态（供云端认证调用）
- `UpdateUserAsync(user)` - 更新用户信息（包含事件触发）
- `OnAuthStateChanged(event)` - 触发认证状态变化事件

**LocalAuthEventType 枚举：**
```csharp
public enum LocalAuthEventType
{
    SignedIn,      // 登录成功
    SignedOut,     // 登出
    SessionRestored, // 会话恢复
    PasswordChanged, // 密码修改
    UserUpdated    // 用户信息更新（2025-10-03 新增）
}
```

#### CloudAuthService
云端认证服务，通过 Cloudflare Workers 调用 Supabase。

**API 端点：** `https://api.modmanger.com`

**关键方法：**
- `LoginAsync(email, password)` - 云端登录
- `SignUpAsync(email, password, username)` - 云端注册
- `IsConnected` - 判断云端连接状态

**Token 管理：**
- `_accessToken` - JWT 访问令牌
- `_tokenExpiresAt` - 令牌过期时间

---

### 2. Cloudflare Workers API

#### 核心端点

**位置：** `cf-workers/modmanger-api/src/index.ts`

##### `/api/auth/login` (POST)
云端登录端点

**请求：**
```json
{
  "email": "user@example.com",
  "password": "password123"
}
```

**响应：**
```json
{
  "success": true,
  "access_token": "jwt_token",
  "refresh_token": "refresh_token",
  "user": {
    "id": 426339508,          // UUID → int32 hash
    "email": "user@example.com",
    "username": "user",
    "display_name": "user",
    "is_verified": true,
    "subscription_type": "free"
  }
}
```

**UUID to Int32 转换（关键修复）：**
```typescript
// index.ts:82-97
function uuidToInt(uuid: string): number {
  if (!uuid) return 0;
  const cleanUuid = uuid.replace(/-/g, '');
  let hash = 0;
  for (let i = 0; i < cleanUuid.length; i++) {
    const char = cleanUuid.charCodeAt(i);
    hash = ((hash << 5) - hash) + char;
    // 确保在正数 int32 范围内 (0x7FFFFFFF = 2147483647)
    hash = hash & 0x7FFFFFFF;
  }
  return hash;
}
```

**问题背景：** Supabase 返回 UUID 格式的用户 ID，但 C# 客户端期望 int32。直接将 UUID 转换会导致溢出错误。使用哈希算法确保结果在 int32 范围内。

##### `/reset-password` (GET)
密码重置页面

**参数（Hash Fragment）：**
- `#access_token` - Supabase 恢复令牌
- `#type=recovery` - 恢复类型

**页面功能：**
- 解析 Supabase 令牌
- 提供密码重置表单
- 调用 Supabase API 更新密码

##### `/auth/reset` (POST)
触发密码重置邮件

**请求：**
```json
{
  "email": "user@example.com"
}
```

**三层降级方案：**
1. Supabase 原生恢复（优先）
2. Admin API + Brevo 发送（降级）
3. link_only 返回链接（最后降级）

**Brevo 集成：**
```typescript
const brevoResponse = await fetch('https://api.brevo.com/v3/smtp/email', {
  method: 'POST',
  headers: {
    'accept': 'application/json',
    'api-key': env.BREVO_API_KEY.trim(),
    'content-type': 'application/json'
  },
  body: JSON.stringify({
    sender: { email: env.BREVO_FROM, name: env.BREVO_FROM_NAME },
    to: [{ email }],
    subject: '重置您的 UE Mod Manager 密码',
    htmlContent: emailHtml
  })
});
```

---

### 3. 数据模型

#### CloudUser (云端用户)
```csharp
public class CloudUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }  // UUID 哈希值

    [JsonPropertyName("email")]
    public string Email { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; }

    [JsonPropertyName("is_verified")]
    public bool IsVerified { get; set; }

    [JsonPropertyName("subscription_type")]
    public string SubscriptionType { get; set; }
}
```

**注意：** 所有字段使用 snake_case JSON 命名，与 API 响应格式匹配。

#### LocalUser (本地用户)
```csharp
public class LocalUser
{
    public int Id { get; set; }
    public string Email { get; set; }
    public string Username { get; set; }
    public string? DisplayName { get; set; }
    public string? Avatar { get; set; }
    public string PasswordHash { get; set; }  // BCrypt 哈希
    public bool IsActive { get; set; }
    public bool IsAdmin { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
```

---

## 开发指南

### 环境配置

#### 1. .NET 开发环境
- Visual Studio 2022 或更高版本
- .NET 8.0 SDK
- Windows 10/11

#### 2. 环境变量文件

**supabase.env：**
```env
SUPABASE_URL=https://your-project.supabase.co
SUPABASE_ANON_KEY=your-anon-key
```

**brevo.env：**
```env
BREVO_API_KEY=your-api-key
BREVO_FROM=noreply@yourdomain.com
BREVO_FROM_NAME=UE Mod Manager
```

**注意：** 这些文件应该被 .gitignore 排除，不要提交到仓库。

#### 3. Cloudflare Workers 配置

**安装依赖：**
```bash
cd cf-workers/modmanger-api
npm install
```

**设置环境变量：**
```bash
wrangler secret put SUPABASE_URL
wrangler secret put SUPABASE_ANON_KEY
wrangler secret put SUPABASE_SERVICE_KEY
wrangler secret put BREVO_API_KEY
wrangler secret put BREVO_FROM
wrangler secret put BREVO_FROM_NAME
```

**部署：**
```bash
npm run deploy
```

---

### 编译项目

```bash
# 编译 Debug 版本
dotnet build UEModManager.sln --configuration Debug

# 编译 Release 版本
dotnet build UEModManager.sln --configuration Release

# 生成安装包
.\Build-Installer.ps1
```

---

### 调试技巧

#### 1. 查看本地数据库
```bash
sqlite3 "%APPDATA%\UEModManager\local.db"
```

**常用查询：**
```sql
-- 查看用户
SELECT * FROM Users;

-- 查看会话
SELECT * FROM UserSessions;

-- 查看 MOD 缓存
SELECT * FROM ModCaches;
```

#### 2. 日志文件
- 应用日志：`UEModManager/bin/Debug/net8.0-windows/console.log`
- Cloudflare Workers 日志：`wrangler tail`

#### 3. 重置本地状态
```bash
# 删除本地数据库
del "%APPDATA%\UEModManager\local.db"

# 删除配置
del "%APPDATA%\UEModManager\auth_config.json"
```

---

## 常见问题与解决方案

### 1. 登录后 UI 显示"离线模式"

**症状：** 云端登录成功，但主窗口显示"离线模式"，需要重启才显示"云端在线"。

**根本原因：** `UpdateUserAsync` 只更新数据库，不设置 `_currentUser` 和会话状态。

**解决方案：**
```csharp
// UnifiedAuthService.cs:577-580
await _localAuthService.UpdateUserAsync(existingUser);
await _localAuthService.ForceSetAuthStateAsync(cloudUser.Email, cloudUser.Username);
```

**关键点：**
- `UpdateUserAsync` 负责数据库更新
- `ForceSetAuthStateAsync` 负责设置登录状态和触发事件
- 必须两者都调用才能完整同步状态

### 2. UUID to Int32 转换溢出

**症状：**
```
System.FormatException: Either the JSON value is not in a supported format,
or is out of bounds for an Int32.
Path: $.user.id
```

**原因：** Supabase UUID `a9e44861-...` 转换为十进制超过 int32 最大值 (2,147,483,647)。

**解决方案：** 使用哈希算法（见上文 UUID to Int32 转换部分）。

### 3. 密码重置邮件发送失败

**症状：** 502 错误，`TypeError: Invalid URL string`

**原因：**
1. API Key 包含换行符
2. Supabase URL 格式错误

**解决方案：**
```typescript
const apiKey = env.BREVO_API_KEY.trim().replace(/[\r\n]/g, '');
const supabaseUrl = env.SUPABASE_URL.trim().replace(/\/+$/, '');
```

### 4. 字段名大小写不匹配

**症状：** API 返回 `Success` 但客户端期望 `success`，导致反序列化失败。

**解决方案：** 统一使用 snake_case：
```typescript
// API 响应
return json({
  success: true,  // 小写
  access_token: token,
  user: { /* ... */ }
});
```

```csharp
// C# 模型
[JsonPropertyName("success")]
public bool Success { get; set; }
```

---

## 性能优化建议

### 1. 本地缓存
- 使用 `LocalCacheService` 缓存常用 MOD 信息
- 离线模式下优先使用本地数据

### 2. 数据库索引
```sql
CREATE INDEX idx_users_email ON Users(Email);
CREATE INDEX idx_sessions_user ON UserSessions(UserId);
CREATE INDEX idx_modcache_game ON ModCaches(GameName);
```

### 3. Cloudflare Workers 优化
- 使用 KV 存储减少 Supabase 调用
- 实现请求速率限制（已实现）
- 缓存认证响应

---

## 安全考虑

### 1. 密码存储
- 本地：BCrypt 哈希（成本因子 12）
- 云端：Supabase 自动处理

### 2. 会话管理
- 30 天过期时间
- 自动清理过期会话
- 设备信息记录

### 3. API 安全
- CORS 配置
- 速率限制（每分钟 10 次登录尝试）
- 环境变量保护敏感信息

### 4. 不要提交到仓库
- `*.env` 文件
- `local.db` 数据库
- `console.log` 日志
- 编译输出 (`bin/`, `obj/`)

---

## 测试

### 单元测试
```bash
dotnet test UEModManager.sln
```

### 集成测试
1. 本地认证测试
2. 云端认证测试
3. 邮件发送测试
4. MOD 操作测试

### 手动测试清单
- [ ] 用户注册
- [ ] 用户登录（本地密码）
- [ ] 用户登录（云端）
- [ ] 密码重置
- [ ] 邮件验证
- [ ] MOD 安装/卸载
- [ ] MOD 备份/恢复
- [ ] 离线模式
- [ ] 云端同步

---

## 部署

### 1. 构建安装包
```powershell
.\Build-Installer.ps1
```

生成的安装包位于 `installer_output/` 目录。

### 2. 部署 Cloudflare Workers
```bash
cd cf-workers/modmanger-api
npm run deploy
```

### 3. Supabase 配置
1. 创建项目
2. 配置 Auth Providers
3. 添加 Redirect URLs
4. 设置邮件模板

---

## 贡献指南

### 代码风格
- C#: 遵循 Microsoft C# 编码约定
- TypeScript: 使用 Prettier 格式化
- 注释: 中文优先，关键部分提供英文

### Git Workflow
1. 从 `main` 分支创建功能分支
2. 提交清晰的 commit message
3. 创建 Pull Request
4. 代码审查后合并

### Commit Message 格式
```
<type>: <subject>

<body>
```

**Type:**
- `feat`: 新功能
- `fix`: Bug 修复
- `docs`: 文档更新
- `refactor`: 代码重构
- `test`: 测试相关

---

## 许可证

本项目采用 MIT 许可证。详见 LICENSE 文件。

---

## 联系方式

- 项目维护者：mr.xzuo@foxmail.com
- 技术支持：通过 GitHub Issues 提问

---

## 更新日志

### v1.7.37 (2025-10-03)
- ✅ 修复云端登录后 UI 不更新问题
- ✅ 修复 UUID to Int32 转换溢出
- ✅ 实现密码重置功能（Brevo 集成）
- ✅ 统一 API 字段命名为 snake_case
- ✅ 添加 `UserUpdated` 事件类型

### v1.7.36 (2025-10-02)
- ✅ 集成 Cloudflare Workers API
- ✅ 实现云端认证服务
- ✅ 添加离线模式支持

---

**最后更新：** 2025-10-03
**文档版本：** 1.0.0
