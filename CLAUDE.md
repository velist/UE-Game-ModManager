# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目

.NET 8.0 WPF 游戏 MOD 管理器（爱酱MOD管理器）。MOD 导入/部署/冲突处理、Profile 管理、云端认证 + 离线模式。
不限于 UE：引擎规则集中在 `UEModManager/Models/EngineProfile.cs`（`EngineType` = UnrealEngine / Unity /
REEngine / Godot / Decima / Diablo4Engine / Unknown）。内置游戏列表在 `GameConfigService.GetAvailableGames`，
引擎映射在 `GameConfigService.GetEngineType`。加游戏不需要写扩展，改这两处静态表即可。

后端：Cloudflare Workers (`cf-workers/modmanger-api/src/index.js`，单文件无构建) + Supabase + Brevo。

## 构建与测试

**必须在 Windows 上构建**，五个项目全是 Windows-only（含两个测试项目，TFM `net8.0-windows`）。
开发机是 Mac，没装 .NET SDK，本地一行都编不了 —— 走 SSH 到 Windows 构建机。

```bash
# Windows 构建机：172.10.10.4，用户 a，dotnet 8.0.416
# 本分支的专用构建工作树（不要碰 D:\modmangerpd\测试，那是 main）
ssh -i ~/.ssh/modmanager_win_ed25519 a@172.10.10.4 \
  "powershell -NoProfile -Command \"chcp 65001 > \$null; cd 'D:\modmangerpd\codex-fix-build'; dotnet build UEModManager.sln -c Debug --nologo\""
```

Windows 的 SSH 默认落到 `cmd.exe`，必须显式套 `powershell -NoProfile -Command`；
不加 `chcp 65001` 输出中文会乱码，且 `Select-String '通过'` 这类过滤会静默匹配不到。

```bash
dotnet build UEModManager.sln -c Debug          # 0 error / 0 warning，5 个项目，约 8 s
dotnet test  UEModManager.Core.Tests/UEModManager.Core.Tests.csproj   # 1151 通过，157 ms
dotnet test  UEModManager.Tests/UEModManager.Tests.csproj             # 457 通过 + 1 跳过（ThemeSnapshotGenerator.生成快照 是生成器，非回归）
dotnet test  UEModManager.Core.Tests/UEModManager.Core.Tests.csproj --filter "FullyQualifiedName~ConflictDetector"
.\Build-Installer.ps1                          # → installer_output/
```

以上为 `f10b3a8` 实测（2026-08-24）。`Directory.Build.props` 设了 `NuGetAuditMode=all`，
依赖漏洞以**构建警告**出现，不要 `NoWarn` 压掉。

日志 `%LOCALAPPDATA%\UEModManager\Logs\console.log`（不在 `bin/` 下）；数据库 `%APPDATA%\UEModManager\local.db`。

## 分层

依赖单向：主程序 → Core，Core 绝不反向引用。

- **`UEModManager.Core`**（`net8.0`，仅依赖 Newtonsoft.Json）：纯函数 + 纯模型。部署计划、冲突检测、
  路径清洗、lock 构建、崩溃恢复分类都在这。禁止 `System.Windows.*`、禁止 `File.*`/`Directory.*`
  （唯一例外 `Services/Persistence/AtomicFileWriter`）、禁止依赖具体 Service、禁止 Logger 实例（传 `ILogger<T>`）。
- **`UEModManager/Services`**：Core 的 IO 适配层。IO + DB + 日志 + UI 调度，纯逻辑转调 Core。
  新算法优先落 Core 并配单测，见 `docs/playbooks/writing-core-service.md`。
- 两个程序集**共用 `UEModManager.Models` / `UEModManager.Services.*` 命名空间**，`using` 分辨不出来源，
  改动前先确认文件属于哪个项目。

## 前端（WPF）

**混合架构**：`ViewModels/` 已存在（`MainViewModel` + `ModListViewModel` / `ModDetailViewModel` /
`CategoryViewModel`），但 `MainWindow.xaml.cs` 仍有约 2000 行 code-behind。改 UI 前先确认逻辑在哪一侧。

- MVVM 用 **CommunityToolkit.Mvvm** 源生成器（`[ObservableProperty]` / `[RelayCommand]`），不要手写
  `INotifyPropertyChanged` 样板。
- `MainViewModel` 把所有服务以只读属性暴露（`GameConfig` / `PackageRepo` / `DeployService`…）供 code-behind
  取用——这是过渡桥，新代码别再扩大它。
- **加载遮罩用 `BeginLoading(msg)` 返回的句柄**（引用计数，支持嵌套），不要直接赋值 `IsLoading`；
  直接赋值会让批量操作闪烁 N 次，且内层 `finally` 会提前关掉外层遮罩。
- ViewModel 里的 `await` **故意不加 `ConfigureAwait(false)`**，依赖续体回到 UI 线程；因此
  `_loadingDepth` 等状态无锁访问。加 `ConfigureAwait(false)` 会破坏这个前提。
- UI 事件处理器一律用 `Infrastructure/SafeEvent.Run` 包裹，不要新写裸 `async void`（未包裹的异常落到全局
  handler，对用户表现为"点了没反应"）。
- **数据目录只能经 `Infrastructure/AppPaths`**，禁止自己用 `BaseDirectory` / `SpecialFolder` 拼路径。
  纯计算部分在 Core 的 `AppDataLayout`。
- 列表刷新**不要拔插 `ItemsSource`**（`= null; = mods;`）。集合是实例不变的 `ObservableCollection`，
  拔插只会重建容器、丢滚动位置和选中项。
- `ModList.ModSelected` **只允许 `MainViewModel` 订阅一次**，code-behind 一次都不许订阅。
- 带子菜单的 `MenuItem` 必须用 `CyberMenuItemSubmenuHeader` 样式；`CyberMenuItem` 模板里没有 `Popup`
  也没有 `IsItemsHost`，套它的菜单项填满 `Items` 也弹不出来。
- **侧边栏底部不要再用 `Popup` 做浮层。** `StaysOpen=False` 的 Popup 会抓走 mouse capture：配
  `MouseLeftButtonDown` 触发就是"按住才显示、松手即消失"；它开着时下一次点击被消耗在关闭它上面，
  派发不到光标下的控件，撞上 `ShowDialog()` 模态窗就是"账户设置打不了字"。而底部
  捐赠 → 使用说明书 → 头像 是鼠标移向头像的必经路径，悬停触发必然误弹。
  捐赠二维码已因此改为独立窗口 `Views/DonateWindow`（用 `CyberModalWindow` 样式，自带标题栏和关闭按钮）。
- `Views/` 下的窗口引用项目根目录的图片资源要用 `pack://application:,,,/xxx.png`；根目录的
  `MainWindow.xaml` 才能写相对路径。中文文件名没问题（资源名是 UTF-8 百分号编码，解析端做同样转换）。
- `MainWindow.CategoryList_Drop` 只处理**分类之间的拖拽排序**（`CategoryItem`），不接受 MOD 拖入；
  MOD 归类只有右键菜单一条路径（`CategoryViewModel.AssignableCategories` 生成子项 →
  `MainViewModel.MoveModToCategoryAsync` 落盘）。

`docs/` 里的「552 测试」已过时；数量以实测为准。

**源码守卫测试**：`UEModManager.Tests/Views/` 下三个 `*GuardTests` 用正则对**源文件文本**断言
（`MainWindowSourceGuardTests` / `StartupSequenceGuardTests` / `RepositoryRelocationGuardTests`，均在 458 个测试内）。
重构上述接线时它们会失败——这是设计意图，不是误报，改代码不要改守卫。

**启动时序**（`App.ShowAuthenticationWindow`）：首次运行的仓库位置引导必须夹在
`DataLocationMigrator` **之后**、任何拉起 `ObjectStore` 的解析**之前**。两头都没有运行期信号，错了只表现为
"老用户被莫名问一次"或"选的位置本次会话不生效"。

**测试并行**：加载主题字典的测试类必须挂 `[Collection(ThemeResourceCollection.Name)]`；WPF XAML 解析器有
进程级静态缓存，并发首解析会炸出与断言无关的报错，且只在满负载并行时偶发。

## 分类存储

`ModInfo.Categories`（`List<string>`，默认 `["未分类"]`）是显示用，`PrimaryCategory` 是取首元素的派生属性。
**权威存储是 `Package.Tags`**——`RefreshFromRepositoryAsync` 每次据此重建 `AllMods`，只改 `ModInfo.Categories`
下次刷新就被打回。系统分类（全部/已启用/已禁用）按 `IsEnabled` 现算、不存储，名单归口 Core 的
`ModCategoryAssignment.SystemCategoryNames`。

## 认证

`UnifiedAuthService` 协调 `LocalAuthService`（本地会话）与 `CloudAuthService`（云端）。

- 登录后显示"离线模式"：`UpdateUserAsync` 只写库，还要调 `ForceSetAuthStateAsync` 设登录状态。
- UUID → int32 用 `hash & 0x7FFFFFFF` 防溢出。
- API 统一 snake_case，模型标 `[JsonPropertyName("snake_case")]`。
- Brevo API Key 需 `.trim().replace(/[\r\n]/g, '')`。

## 遥测（注册数 / 在线数）

- 上报字段就是 `TelemetryReport` 的四项：随机设备 UUID、应用版本、Windows 版本号、可选邮箱哈希。
  README「安全与隐私」是对用户的公开承诺，**加字段必须同步改它**。
- `TelemetryConsent.Decide` 里 `Asked=false` 时 `ShouldReport` 恒 false，压过默认开的 `Enabled`。
  `TelemetryAsked` 与 `TelemetryEnabled` 正交，别合并。
- 设备标识是随机 UUID v4，存 `%LOCALAPPDATA%\UEModManager\device.id`。**不要**改成 MachineGuid / MAC / 硬盘序列号。
- 存储只能用 **D1，不要 KV**（免费档 1000 写/天不够，且最终一致下的读改写会让计数永久失真）。
- 上报端点对任何输入响应完全一致，不回传错误细节。心跳超时硬编码 5 秒。

## Cloudflare Workers

```bash
cd cf-workers/modmanger-api
npx wrangler deploy          # 无 package.json，没有 npm install / npm run deploy
node verify.mjs              # 自测：Node 直接跑 fetch handler，D1 用内存假实现
```

端点：`/api/auth/login`、`/auth/reset`、`/reset-password`、`/app/update`（更新检查 + 遥测心跳）、
`/admin` + `/admin/stats`（后者要 `Authorization: Bearer $DASH_TOKEN`）。

secret 共 7 个，逐个 `npx wrangler secret put`（每次只接受一个键名）：`SUPABASE_URL`、`SUPABASE_ANON_KEY`、
`SUPABASE_SERVICE_KEY`、`BREVO_API_KEY`、`BREVO_FROM`、`BREVO_FROM_NAME`、`DASH_TOKEN`（未设则 `/admin/stats` 恒 401）。
KV 绑定 `RATE_LIMIT` 与 D1 绑定 `DB` 在 `wrangler.toml` 声明，不是 secret。`deploy` 不清除已有 secret。

首次部署统计前必须先建 D1，否则 `database_id` 占位符会让 deploy 报错：

```bash
npx wrangler d1 create modmanger-stats                              # uuid 填进 wrangler.toml
npx wrangler d1 execute modmanger-stats --remote --file=schema.sql  # 可重复执行
```

## 注意事项

- **`docs/` 的数量和清单已漂移**：overview 说三种后端（含 Symlink），实际只有 `CopyBackend` / `HardLinkBackend`；
  「552 测试」也过时。文档讲结构和意图可信，讲数量以代码为准。入口 `docs/README.md`，扩展点看 `docs/playbooks/`。
- 本目录是 **git worktree**，父仓库 `/Volumes/测试` 未挂载时所有 `git` 命令报 `not a git repository`，非仓库损坏。
