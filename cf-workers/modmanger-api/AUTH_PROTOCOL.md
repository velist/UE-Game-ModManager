# 邮箱登录、云端账户与持久化限流

本文件对应 2026-09-05 审计的 F05、F06、F07 修复。源码与本地测试已更新，**尚未部署到 Cloudflare，也没有使用真实 Supabase 或 Brevo 验收**。

## 桌面邮箱登录

`LoginWindow → CustomOtpService → WorkerEmailService` 使用以下协议。服务器只开放 `login` 用途，客户端无法指定发件人、主题、HTML、验证码或邮件链接。

| 请求 | 请求体 | 成功响应 |
| --- | --- | --- |
| `POST /api/auth/otp/request` | `email`, `purpose: "login"` | `success`, `challenge_id`, `expires_in`, `retry_after`, `message` |
| `POST /api/auth/otp/verify` | `email`, `purpose: "login"`, `challenge_id`, `code` | `success: true`, `verified_email`, `purpose: "login"` |

邮箱统一去除首尾空白并转为小写。新认证端点最多读取 8 KiB 请求体；验证码接口拒绝所有额外字段。`challenge_id` 是 32 字节密码学随机数的十六进制表示，不含验证码。6 位验证码由服务端以拒绝采样生成，正文由服务端固定模板构造。

每个邮箱与用途对应一个 SQLite Durable Object。它保存 challenge、验证码的带盐 SHA-256、到期时间、重发冷却、尝试次数和消费状态。验证码有效期为 10 分钟，60 秒内不能重发；5 次错误核验后失效。重发替换旧 challenge，成功核验在同一个持久事务内消费验证码。通过多个 Worker 同时核验也最多成功一次。邮件发送失败不会返回 challenge、验证码或恢复链接，并保留重发冷却。

桌面只缓存 opaque challenge ID，并等待服务端验证结果。成功回包必须同时匹配当前邮箱和 `login` 用途，之后才执行现有的 `LocalAuthService.ForceSetAuthStateAsync` 与“记住我”流程。网络失败时不会回退到本地验证码比对或客户端 Brevo/SMTP 发信。本机账户继续由本机管理；此登录路径不会创建 Supabase access token。

`/email/send`（含 `/v1/email/send`）固定返回 HTTP 410，无邮件外发。旧版客户端的任意内容发信接口不能继续使用。因此**新客户端与本次 Worker 应安排在同一发布窗口交付**，先备齐新安装包并确认部署配置。不要为了兼容重新开放旧邮件转发端点。

## 云端密码账户契约

CloudAuthService 保留独立的 Supabase 密码账户路径，Worker 补齐其实际请求的三个缺失端点。

| 端点 | 上游行为 | 客户端语义 |
| --- | --- | --- |
| `POST /api/auth/register` | Supabase `POST /auth/v1/signup`，邮箱/密码及受限用户名 | 返回 `success`, `user_id`, `verification_required`, `verification_email_sent`, `message` |
| `POST /api/auth/login` | Supabase password grant | 返回 snake_case token 与整数 ID 的 `user` |
| `GET /api/auth/validate` | 携带用户 Bearer token 查询 `/auth/v1/user` | 上游确认后才返回 `valid: true` 与 `user` |
| `POST /api/auth/logout` | 携带用户 token 调用 `/auth/v1/logout?scope=local` | 撤销当前云端会话；桌面无论网络结果如何都会清除本机 token |

注册若需要邮箱确认，桌面返回 `RequiresEmailVerification = true`，不尝试密码登录，也不生成已登录本机会话。这个标记继续传递到 UnifiedAuthResult。无须确认的注册保留原来的自动登录行为。登录成功回包缺少 token、用户、正的有效期，或返回了其他邮箱时，客户端拒绝建立会话。

`/auth/password` 兼容端点与 `/api/auth/login` 共用限额。现有 `/auth/otp/send` 和 `/auth/reset` 保留 Supabase/服务端邮件模板行为，其发信也受共享邮件限额约束。`/v1` 前缀继续兼容。认证响应不缓存，上游错误详情与密钥不返回给客户端。

Supabase 的“邮箱确认”策略仍由项目配置决定；本次代码不会通过 service-role API 将新用户擅自标记为已验证。

## 原子限流与数据生命周期

旧的 Workers KV `get → put` 计数已移除。`SECURITY_STATE` 绑定的 `AuthState` 使用 SQLite Durable Object 的 `storage.transactionSync` 在同一事务内读取、判断并递增计数。相同主体从不同 Worker 实例进入同一个对象。计数、OTP 尝试和消费都能跨运行实例重启恢复。

| 操作 | 限额 |
| --- | --- |
| 密码登录（含兼容路径） | 每 IP 每固定分钟 10 次 |
| 云端注册 | 每 IP 每固定分钟 5 次，另受发信限额 |
| 会话验证/退出 | 每 IP 每固定分钟合计 60 次 |
| OTP 核验 | 每 IP 每固定分钟 30 次，并有每 challenge 5 次错误限制 |
| 发信请求（OTP、注册、旧 OTP、密码恢复共用） | 每 IP 每分钟 10 次；每邮箱每小时 6 次；全服务每小时 300 次 |
| 密码恢复 | 另限每 IP 每分钟 5 次 |

限流采用固定时间窗口，跨窗口允许新的配额。被拒请求返回 HTTP 429 与 `Retry-After`；这些限制属于滥用成本和邮件配额控制，公开的登录发信入口仍需结合运营流量观察调整配额。

对象名仅使用规范化主体的 SHA-256。OTP 记录在 10 分钟到期后由 alarm 删除，限流记录在对应窗口到期后清理。alarm 会重新检查当前记录到期时间，避免旧 alarm 删除新状态。若 `SECURITY_STATE` 缺失或限流服务出错，受保护操作返回错误，**不会跳过限流继续认证或发信**。更新检查与统计仍使用其原有可用性策略。

## 部署配置

`wrangler.toml` 已包含可部署的绑定与首次 SQLite migration：

```toml
[[durable_objects.bindings]]
name = "SECURITY_STATE"
class_name = "AuthState"

[[migrations]]
tag = "2026-09-05-auth-state-v1"
new_sqlite_classes = ["AuthState"]
```

迁移 tag 发布后不能复用作不同的 schema/class 迁移。现有 D1 统计绑定不变；旧 `RATE_LIMIT` KV 绑定不再使用，没有代码去删除远端 KV namespace 或其中数据。

需要在 Cloudflare 保留或设置的 secret：

- 邮件：`BREVO_API_KEY`、`BREVO_FROM`、可选 `BREVO_FROM_NAME`。`BREVO_FROM_EMAIL` 仍作为发件地址的兼容名称。
- 云端密码认证：`SUPABASE_URL`、`SUPABASE_ANON_KEY`。
- 原有密码恢复/旧 OTP fallback：`SUPABASE_SERVICE_KEY`。
- 原有统计看板：`DASH_TOKEN`。

没有新的客户端共享 secret，也没有在仓库加入真实凭据。由发布操作在 Cloudflare 配置好 secret 后运行部署命令；本次只执行了下面的本地打包检查。

## 本地验证

要求 Node.js 22 及以上。测试工具固定版本并有 package-lock，CI 可复现安装。Miniflare 保持稳定的 4.x；将其传递依赖 undici 固定到已修复安全公告的 7.29.0，避免引入有已知漏洞的旧版本，也无需切换到 Miniflare 5 alpha。

```powershell
Set-Location 'D:/modmangerpd/codex-fix-build/cf-workers/modmanger-api'
npm ci
npm test
$env:WRANGLER_SEND_METRICS = 'false'
npm run check:bundle
```

`npm test` 运行现有 58 项统计/更新自测和 Node 原生测试。认证测试加载真实生产 Worker 模块，通过 Miniflare/workerd 使用 SQLite Durable Objects；两个独立 Worker 共用同一 DO namespace。覆盖匿名 relay、固定模板、邮箱规范化、动作与 challenge 绑定、过期、重发、错误次数、20 路并发消费、20 路并发限流、窗口边界、缺失绑定、云端认证契约，以及关闭 workerd 后从同一持久目录重启。过期与窗口边界的时间调整只存在 `test/state-fixture.js` 的测试子类，不会打进生产入口。

所有认证与邮件上游 fetch 都由 `test/runtime.mjs` 的 `outboundService` 截获。未知外部 URL 直接失败，没有真实网络 fallback。可启动独立回环夹具供桌面跨进程契约检查：

```powershell
node test/runtime.mjs --serve
```

启动后输出仅监听 `127.0.0.1` 的 URL。夹具的 `/__fixture/mail?email=user@example.test` 仅用于读取模拟收到的验证码邮件，`/__fixture/outbound` 返回拦截记录；生产 Worker 没有这些端点。

桌面回归覆盖发送和核验的字段/路由、服务端拒绝与网络失败、错误身份/用途、请求限流、challenge 消费，以及待邮箱确认状态和退出失败清理。真实 .NET 客户端与本地 workerd 的跨进程检查入口为 `test/desktop-contract.mjs`。

这些验证证明本地实现与契约；正式发布仍需配置真实 secret 并验收邮件可达性、Supabase 的邮箱确认策略和线上 Worker 绑定。
