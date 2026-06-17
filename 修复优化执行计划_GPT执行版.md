# UEModManager v2.0.4 修复优化执行计划（AI 执行版）

> **执行者必读：** 本计划由 2026-06-11 的四路全代码库审计产出，供 AI 工程师（GPT-5.5）逐任务执行。
> 所有行号以 commit `1de4f63`（分支 `ux-copy-audit`）为基线。**执行时以实际代码为准**——若发现代码与计划描述不符（行号漂移属正常，逻辑不符则异常），先核实再动手，不要硬套。
> 每个任务使用复选框（`- [ ]`）跟踪进度。

**目标：** 修复审计发现的安全风险、数据丢失风险，删除死代码，统一双链路架构，提升 UI 质量。

**架构背景（必须先理解）：** 本项目是 .NET 8.0 WPF MOD 管理器，存在 **v1.8 旧链路**（`ModManagementService` 直接复制/删除目录）与 **v2.0 新链路**（`PackageImportService → DeploymentPlanner → DeploymentService`，带事务日志/回滚/崩溃恢复）**并存**的状态。v2.0 是主路径（由 `MainViewModel` 注入），v1.8 是 ViewModel 的 fallback。纯逻辑下沉在 `UEModManager.Core`（有 446 个测试），IO/状态服务在 WPF 工程（零测试）。UI 是 code-behind 为主，MVVM 是半成品（XAML 中无任何 Command 绑定）。

**技术栈：** .NET 8.0 WPF / C# 12 / EF Core + SQLite / xUnit

---

## 第 0 部分：全局执行规则

### 0.1 环境与命令

```bash
# 工作目录
cd D:\modmangerpd\测试

# 编译（每个任务完成后必须 0 error）
dotnet build UEModManager.sln --configuration Debug

# 测试（每个任务完成后必须全绿，当前基线 446+ 个测试）
dotnet test UEModManager.Core.Tests --configuration Debug
```

### 0.2 分支与提交纪律

- [ ] 开始前从 `ux-copy-audit` 切出新分支：`git checkout -b fix/v2.0.4-hardening`
- **每个任务一个独立 commit**，message 格式参照仓库现有风格（`fix:`/`refactor:`/`chore:` + 中文描述）。
- 提交前必须：`dotnet build` 0 error → `dotnet test` 全绿 → `git add <相关文件>`（**禁止 `git add -A` 盲加**）。
- **严禁提交**：`*.env` 文件（`allcfkey.env`/`brevo.env` 含真实密钥，已在 .gitignore）、`用户反馈/` 目录（用户隐私数据）、`.wrangler/` 构建产物。
- 涉及 Core 的修改必须先写失败测试再实现（TDD）；WPF 工程服务无测试基建，以编译 + 代码审查 + 手动验证点代替。

### 0.3 批次依赖

批次 A → B → C → D 顺序执行。**批次 C（删代码）必须在 B 之后**，因为 B7 会移除对部分待删代码的引用。

批次内顺序约束：
- 批次 A、B 内任务相互独立，可任意顺序；
- **批次 C 必须按 C1→C2→C3→C4 顺序**（C4 删除的代码的引用方在 C2/C3 中被删，乱序会导致 grep 发现引用仍在而卡住）；
- **批次 D 中 D3 必须先于 D4**（D4 依赖 D3 合并后的警告弹窗），其余任意。

### 0.4 人工待办（不归 AI 执行，转告项目负责人）

1. **轮换密钥**：git 历史中 `UEModManager/Services/SupabaseConfig.cs`（已删但历史可见，`git show 1da6325:UEModManager/Services/SupabaseConfig.cs`）硬编码了 Supabase URL + anon key；仓库目录的 `allcfkey.env`（Cloudflare token）、`brevo.env`（Brevo key）虽未入库但已长期落盘。**全部需要在对应平台轮换。**
2. 确认 Supabase RLS 策略完备（anon key 设计上可公开，但需 RLS 兜底）。
3. 实机验收：批次 B 完成后用真实游戏目录跑一轮 导入→启用→禁用→删除 链路。
4. 历史 `console.log` 轮转文件中已落盘的验证码/密钥（A1 只止血新日志），发布说明建议老用户清理程序目录下的旧日志。

---

## 批次 A：安全止血（P0，预计 2 个任务）

### Task A1：日志敏感信息脱敏

**问题：** OTP 验证码明文写入日志。`App.xaml.cs:141-160` 把 Console 重定向到程序目录 `console.log` 并持久化，因此验证码落盘——能读日志的本地程序可绕过邮箱验证登录任意账号。

**文件：**
- 修改：`UEModManager/Services/CustomOtpService.cs:54`（`_logger.LogInformation($"[CustomOTP] 为 {email} 生成验证码: {otp}")`）
- 修改：`UEModManager/App.xaml.cs:602` 附近（`ParseEnv` 打印 env 配置值前 10 字符，对 `BREVO_API_KEY` 同样生效）
- 排查：全文件搜索 `CustomOtpService.cs` 内其他打印 `otp` 变量的日志行

**步骤：**

- [ ] 1. 将验证码日志改为不含验证码本体：
```csharp
_logger.LogInformation($"[CustomOTP] 为 {MaskEmail(email)} 生成验证码（已脱敏）");
```
  在该类内新增私有方法 `MaskEmail`（保留首字符和域名，如 `a***@qq.com`），并把本文件中所有打印完整 email 的日志统一替换。
- [ ] 2. `App.xaml.cs` 的 ParseEnv 日志：对 key 名含 `KEY`/`TOKEN`/`SECRET`/`PASSWORD`（不区分大小写）的值，只打印 `key=<redacted>`，其余 key 维持现状。
- [ ] 3. 全仓库搜索验证残留：`grep -rn "验证码: \|password.*Log\|token.*Log" UEModManager/Services --include="*.cs"`，确认无其他明文敏感日志。
- [ ] 4. `dotnet build` + `dotnet test` 通过。
- [ ] 5. 提交：`fix(security): OTP 验证码与密钥配置日志脱敏`

### Task A2：移除硬编码默认管理员

**问题：** `LocalAuthService.cs:1094-1109`（`EnsureDefaultAdminAsync`）每次启动确保存在 `admin@uemodmanager.com / Admin@123456` 账户，该账户可打开管理面板（`MainWindow.xaml.cs:362-365`）。任何拿到安装包的人都知道这组凭据。

**文件：**
- 修改：`UEModManager/Services/LocalAuthService.cs:1094-1109`
- 修改：`UEModManager/App.xaml.cs:324`（调用点 `EnsureDefaultAdminAsync`）

**步骤：**

- [ ] 1. **保留 `App.xaml.cs:324` 的无条件调用**，把分支移到 `EnsureDefaultAdminAsync` 方法内部：
   - `#if DEBUG` 分支：维持现有逻辑（创建默认管理员，供开发调试）；
   - `#else`（Release）分支：**不创建**默认管理员；若检测到已存在 `admin@uemodmanager.com` 且**从未改过密码**，则禁用该账户（`IsActive = false`，字段名以实际 Users 实体为准）并记 Warning 日志。
   - 在方法 XML 注释中说明默认管理员仅供开发调试。
- [ ] 2. **"从未改过密码"的判断必须用 `VerifyPassword`，不能比较哈希**：`HashPassword` 是 PBKDF2 + 每次随机盐（`LocalAuthService.cs:328-345`），`PasswordHash == HashPassword("Admin@123456")` 每次结果不同、**永远不相等**（且编译通过、静默失效）。正确写法：
```csharp
if (VerifyPassword("Admin@123456", existingAdmin.PasswordHash))
{
    existingAdmin.IsActive = false;
    // ... SaveChangesAsync + LogWarning
}
```
   （`VerifyPassword` 是本类私有方法，约 :345，同类内直接调用。）
- [ ] 3. `dotnet build`（**Debug 和 Release 都编译一次**，因为有条件编译）+ `dotnet test`。
- [ ] 4. 提交：`fix(security): 默认管理员仅 DEBUG 创建，Release 禁用未改密的遗留管理员`

---

## 批次 B：数据安全与正确性（P0，8 个任务）

### Task B1：修复部署 diff 哈希恒为 null（全量 Replace 根因）

**问题：** `DeploymentPlanner.cs:199` 和 `:229` 的 `Hash: null, // 延迟计算`——全仓库没有任何"延迟计算"实现。`DeploymentDiffComputer.cs:65-66` 比较 `want.FileHash != actualFile.Hash`，期望侧非空、实际侧恒 null → 永不相等 → **每个已部署文件每次部署都生成 Replace 操作**。后果三重放大：每次部署全量复制 + `DeploymentService.BackupTargetFilesAsync`（:344-358）重复备份所有目标文件 + `OverwriteStore` 重复 SHA256 注册。这是磁盘膨胀和部署慢的共同根因。

**文件：**
- 修改：`UEModManager.Core/Services/DeploymentPlanning/DeploymentDiffComputer.cs:58-76`
- 修改：`UEModManager/Services/DeploymentPlanner.cs:174-237`（`ScanDeployedFiles`）
- 测试：`UEModManager.Core.Tests/Services/DeploymentPlanning/DeploymentDiffComputerTests.cs`（已有 10 个测试）

**修复策略（先比大小，大小相同才算哈希）：**

- [ ] 1. **先写失败测试**（Core）：在 `DeploymentDiffComputerTests` 新增用例：
   - `大小不同_应生成Replace`：desired 与 actual 同路径、FileSize 不同、actual.Hash 为 null → 期望 1 个 Replace。
   - `大小相同且actual哈希为null_应跳过`：同路径、FileSize 相同、actual.Hash 为 null → 期望 0 个操作（信任大小一致）。
   - `大小相同但哈希不同_应生成Replace`：同路径、FileSize 相同、双方哈希都有但不同 → 期望 1 个 Replace。
- [ ] 2. 运行测试确认前两个失败：`dotnet test UEModManager.Core.Tests --filter DeploymentDiffComputer`
- [ ] 3. 修改 `DeploymentDiffComputer.ComputeDiff` 的比较逻辑（替换 65-71 行）。注意 `want.FileSize <= 0` 视为"大小未知"（v1.8 迁移来的旧包 manifest 可能缺 FileSize），回落哈希比较，否则旧包会退化为每次 Replace：
```csharp
if (want.FileSize > 0 && want.FileSize != actualFile.FileSize)
{
    operations.Add(MakeOp(DeploymentOperationType.Replace, want));
}
else if (!string.IsNullOrEmpty(want.FileHash)
    && !string.IsNullOrEmpty(actualFile.Hash)
    && want.FileHash != actualFile.Hash)
{
    operations.Add(MakeOp(DeploymentOperationType.Replace, want));
}
// 大小相同（或未知）且哈希缺失或相同 → 跳过
```
- [ ] 4. 运行测试确认全绿（已核实：现有 10 个旧测试默认两侧 FileSize 相等，在新逻辑下仍通过，无需改动旧测试）。
- [ ] 5. （可选增强，若改动可控）`ScanDeployedFiles` 中对 `FileSize` 与期望相同的文件惰性计算 SHA256 填入 Hash，进一步消除"大小恰好相同但内容不同"的漏报。注意 `ScanDeployedFiles` 拿不到期望表，此项如需重构传参则**跳过**，大小比较已覆盖绝大多数场景。
- [ ] 6. **验收：** 手动验证点（写入提交说明）——同一 Profile 连续两次执行部署，第二次生成的操作列表应为空（或仅 Remove），不再出现全量 Replace。
- [ ] 7. 提交：`fix(deploy): diff 比较改为大小优先，消除哈希恒空导致的每次全量 Replace`

### Task B2：JSON 持久化原子写 + 损坏文件保护

**问题：** `ProfileService.cs:422` 直接 `File.WriteAllTextAsync`，进程中断 → JSON 截断；`LoadProfilesAsync`（:393-414）解析失败后 `_profiles = []`，随后 `SetCurrentGameAsync` 创建默认 Profile 并**覆盖掉损坏但可救的文件**——一次崩溃丢全部方案。同样非原子写：`GameConfigService.SaveConfigSync`（:131-143）、`OverwriteStore.SaveIndexAsync`（:330-335）、`ConflictAnalyzer.SaveOverridesAsync`（:223-238）。仓库内已有正确范式：`PackageRepository.SaveIndexAsync`（约 :367）用了 `.tmp + File.Replace`。

**文件：**
- 创建：`UEModManager.Core/Services/Persistence/AtomicFileWriter.cs`
- 测试：`UEModManager.Core.Tests/Services/Persistence/AtomicFileWriterTests.cs`
- 修改：`UEModManager/Services/ProfileService.cs`、`GameConfigService.cs`、`OverwriteStore.cs`、`ConflictAnalyzer.cs`、`PackageRepository.cs`（统一改用新 helper）

**步骤：**

- [ ] 1. 先写测试（用 `Path.GetTempPath()+Guid` 临时目录，测试结束清理）：写入成功后内容正确；目标已存在时替换成功；写入函数抛异常时原文件保持不变、无 .tmp 残留。
- [ ] 2. 实现 helper（命名空间遵循 Core 现有约定 `UEModManager.Services.*`）：
```csharp
namespace UEModManager.Services.Persistence;

public static class AtomicFileWriter
{
    /// <summary>临时文件 + File.Replace 原子写。目标不存在时用 File.Move。</summary>
    public static async Task WriteAllTextAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content);
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* 尽力清理 */ } }
        }
    }
}
```
- [ ] 3. 测试全绿后，把上述 5 个服务的保存方法全部替换为 `AtomicFileWriter.WriteAllTextAsync`。`GameConfigService.SaveConfigSync` 若必须同步，加一个 `WriteAllText` 同步重载。
- [ ] 4. **损坏保护**：修改 `ProfileService.LoadProfilesAsync` 的 catch 块——解析失败时先把损坏文件复制为 `{原名}.corrupt-{yyyyMMddHHmmss}.bak` 再置空列表，并记 Warning 日志。`SaveProfilesAsync` 的 catch（:424-427）不再吞异常，改为记日志后 **rethrow**，让调用方知道保存失败。排查 `SaveProfilesAsync` 全部调用点，确认上抛不会打崩 async void 事件（必要时在调用点补 try-catch + 用户提示）。
- [ ] 5. `dotnet build` + `dotnet test`。
- [ ] 6. 提交：`fix(persistence): JSON 持久化统一原子写，Profile 损坏时备份而非静默清空`

### Task B3：数据库 schema 升级策略修复

**问题：** `App.xaml.cs:318` → `LocalDbContext.EnsureDatabaseCreatedAsync`（`LocalDbContext.cs:154` 用 `EnsureCreatedAsync`），但项目存在迁移 `UEModManager/Migrations/20250825183311_AddUserAdminAndLockFields.cs`。`EnsureCreated` 不写 `__EFMigrationsHistory` 且对已存在的库不做任何事——**未来任何新迁移对老用户的 local.db 都不会生效**，触碰新列即 "no such column" 崩溃。注意：现有唯一迁移是全量建表的基线迁移（内容与当前模型一致）。

**文件：**
- 修改：`UEModManager/Data/LocalDbContext.cs:147-170`（`EnsureDatabaseCreatedAsync`）
- 修改：`UEModManager/App.xaml.cs:316-320`（检查返回值）

**步骤：**

- [ ] 1. 把 `EnsureDatabaseCreatedAsync` 内部改为迁移驱动：
```csharp
// 老库基线接管：库已存在但无迁移历史 → 手工补 __EFMigrationsHistory 基线行，再 Migrate
var conn = Database.GetDbConnection();
await conn.OpenAsync();
bool hasUsersTable, hasHistoryTable;
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Users'";
    hasUsersTable = Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
    hasHistoryTable = Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
}
if (hasUsersTable && !hasHistoryTable)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"CREATE TABLE __EFMigrationsHistory (
        MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY,
        ProductVersion TEXT NOT NULL);
        INSERT INTO __EFMigrationsHistory VALUES ('20250825183311_AddUserAdminAndLockFields', '<从 LocalDbContextModelSnapshot.cs 头部注解抄实际 EF Core 版本，如 8.0.x>');";
    await cmd.ExecuteNonQueryAsync();
}
await Database.MigrateAsync();
```
- [ ] 2. **老库列自愈**：基线迁移含 `IsAdmin` 等列，但更早期 `EnsureCreated` 建的库可能缺列。Migrate 后增加自检：`PRAGMA table_info(Users)` 对照 `LocalDbContextModelSnapshot.cs` 中 Users 实体的全部列，缺失的用 `ALTER TABLE Users ADD COLUMN ...`（SQLite 支持加列）补齐，列类型/默认值照 snapshot。对 `UserSessions`/`FailedLoginAttempts`/`UserPreferences` 同样处理（这些表若整个缺失，`MigrateAsync` 不会创建——因为历史行已标记完成；用 `CREATE TABLE IF NOT EXISTS` 按迁移文件中的定义补建）。
- [ ] 3. `App.xaml.cs:318` 检查返回值：初始化失败时弹错误提示并允许用户选择"以离线只读方式继续/退出"，至少不再静默吞掉。
- [ ] 4. 手动验证点（写入提交说明）：① 删除 `%APPDATA%\UEModManager\local.db` 后启动 → 全新库正常创建含 `__EFMigrationsHistory`；② 用旧版程序生成的 local.db（或手工删掉 `__EFMigrationsHistory` 表模拟）启动 → 基线接管成功、登录正常。
- [ ] 5. `dotnet build` + `dotnet test`。
- [ ] 6. 提交：`fix(db): EnsureCreated 改为 Migrate + 老库基线接管与缺列自愈`

### Task B4：路径净化（防穿越/防误删）

**问题：** `PluginTargetPath`/`TargetRootPath` 未拦截绝对路径和 `..`：`ModManagementService.cs:238/282/618` 的 `Path.Combine(gamePath, mod.PluginTargetPath, ...)`——若为绝对路径则 gamePath 被丢弃，可写/删任意盘符目录（含 `Directory.Delete(recursive:true)`）；`PackageImportService.NormalizeTargetRootPath`（:428-433）只去前导分隔符；`ObjectStore.StoreFileAsync`（:92）对压缩包内相对路径无校验。

**文件：**
- 创建：`UEModManager.Core/Services/Security/PathSanitizer.cs`
- 测试：`UEModManager.Core.Tests/Services/Security/PathSanitizerTests.cs`
- 修改：上述三个服务的拼接点

**步骤：**

- [ ] 1. 先写测试，覆盖：正常相对路径通过；`..\foo`、`foo\..\..\bar` 拒绝；`C:\evil`、`\\server\share`、`C:foo`（驱动器相对）拒绝；前导 `/`、`\` 清理后通过；空串/null 返回空串。
- [ ] 2. 实现（**注意：UNC 判断必须在剥离前导分隔符之前对原始字符串做**，否则 `\\server\share` 被剥成 `server\share` 后会漏过校验；命名空间遵循 Core 现有约定 `UEModManager.Services.*`，不要新造 `UEModManager.Core.*`）：
```csharp
namespace UEModManager.Services.Security;

public static class PathSanitizer
{
    /// <summary>校验相对子路径：拒绝绝对路径/盘符/UNC/.. 上跳。非法时抛 ArgumentException。</summary>
    public static string SanitizeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
        var raw = relativePath.Trim();

        // 先在原始字符串上拒绝 UNC / 绝对路径 / 盘符（剥离之后再判就漏了）
        if (raw.StartsWith(@"\\") || raw.StartsWith("//"))
            throw new ArgumentException($"目标路径不允许为 UNC 路径: {relativePath}");
        if (Path.IsPathRooted(raw) || raw.Contains(':'))
            throw new ArgumentException($"目标路径不允许为绝对路径: {relativePath}");

        var p = raw.TrimStart('/', '\\');
        var parts = p.Split('/', '\\');
        if (parts.Any(s => s == ".."))
            throw new ArgumentException($"目标路径不允许包含 ..: {relativePath}");
        return string.Join(Path.DirectorySeparatorChar, parts.Where(s => s.Length > 0 && s != "."));
    }

    /// <summary>组合并二次校验结果确实落在 baseDir 内（防御纵深）。</summary>
    public static string SafeCombine(string baseDir, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(baseDir, SanitizeRelative(relativePath)));
        var baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) 
            && !string.Equals(combined.TrimEnd(Path.DirectorySeparatorChar), baseFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"路径越出基目录: {relativePath}");
        return combined;
    }
}
```
   **TDD 纪律：跑到红灯时只许改实现，不许弱化测试用例。**
- [ ] 3. 接入点（逐个替换 `Path.Combine(gamePath, <用户可控相对路径>, ...)`）：
   - `ModManagementService.cs:238、282、618` → `PathSanitizer.SafeCombine(gamePath, mod.PluginTargetPath)` 再 Combine 文件名；
   - `PackageImportService.NormalizeTargetRootPath`（:428）→ 内部改调 `SanitizeRelative`；
   - `ObjectStore.StoreFileAsync`（:92）→ 对 `targetRelativeName` 调 `SanitizeRelative`；
   - `DeploymentTargetPathBuilder.ComputeTargetPath`（Core，:28 附近）→ 对 entry/package 的 TargetRootPath 调 `SanitizeRelative`。
   - 接入处的异常处理：导入路径非法时该文件**跳过并记 Warning**（不要让一个恶意文件名中断整个导入）；部署/删除路径非法时**中止该操作并报错**。
- [ ] 4. `dotnet build` + `dotnet test`。
- [ ] 5. 提交：`fix(security): 统一路径净化，阻止 TargetRootPath/PluginTargetPath 路径穿越`

### Task B5：部署失败时恢复"执行中"的那个操作

**问题：** `DeploymentService.cs:130-137`——`operation` 只有**成功后**才 `transaction.ExecutedOperations.Add(operation)`（:134）。若一个 Replace 在 `File.Copy` 中途失败，目标文件已半截损坏，但该 op 不在 ExecutedOperations 里，`RollbackAsync`（:205）不会用已备份的 `op.BackupPath` 恢复它。

**文件：**
- 修改：`UEModManager/Services/DeploymentService.cs:120-145`（执行循环）

**步骤：**

- [ ] 1. 把入列时机改为**执行前**：先 `transaction.ExecutedOperations.Add(operation)` 再 `ExecuteOperationAsync`。回滚侧容错**已核实满足前提**：`DeleteAdded` 有 `File.Exists` 守卫（RollbackActionPlanner IO 侧 :213 附近）、`RestoreFromBackup` 带 overwrite（:226 附近），即半途失败的操作回滚时不会二次出错。
- [ ] 2. 该行为变化在 WPF 工程（无测试基建），Core 的 `RollbackActionPlanner` 是纯函数、不感知执行状态，**写 Core 测试无法守护本修复**。改为在提交说明中记录手动验证点：人为制造一次中途失败（如部署时锁定某个目标文件），确认回滚后已备份的文件被恢复原状。
- [ ] 3. `dotnet build` + `dotnet test`。
- [ ] 4. 提交：`fix(deploy): 操作执行前入列事务，半途失败的文件可被回滚恢复`

### Task B6：ScanDeployedFiles 与 TargetPathBuilder 回退语义一致

**问题：** 期望路径计算 `DeploymentTargetPathBuilder.cs:28` 用 `entry?.TargetRootPath ?? package.TargetRootPath ?? ""`，而扫描侧 `DeploymentPlanner.cs:210-212` 只看 `entry.TargetRootPath`，为空就 `continue`。若 entry 没带 TargetRootPath（如经 `ProfileSyncPlanner` 重建），已部署的插件文件永远扫不到 → diff 永远 Add（重复部署）、禁用时永远不会 Remove（孤儿文件残留游戏目录）。

**文件：**
- 修改：`UEModManager/Services/DeploymentPlanner.cs:207-216`

**步骤：**

- [ ] 1. 扫描侧改用与 Builder 相同的回退链：
```csharp
var package = _packageRepository.GetByKey(entry.PackageKey);
var targetRootPath = entry.TargetRootPath ?? package?.TargetRootPath;
if (string.IsNullOrEmpty(targetRootPath) || string.IsNullOrEmpty(gamePath))
    continue;
```
   （注意 :218 已有一次 `GetByKey`，上移合并，避免查两次。）
- [ ] 2. `dotnet build` + `dotnet test`。
- [ ] 3. 提交：`fix(deploy): 部署扫描的 TargetRootPath 回退链与路径构建器对齐，消除插件孤儿文件`

### Task B7：v1.8 旧链路熔断

**问题：** `ModManagementService` 旧链路仍是活代码且有数据丢失风险：
1. `EnableModAsync`（:242-255）先 `Directory.Delete` 目标再复制，复制失败无恢复；
2. `DisableModAsync`（:286-288）直接删目标目录，**不校验备份目录是否存在/完整**——备份缺失时禁用 = 永久删除用户 MOD；
3. `DeleteModAsync`（:611-623）**先删备份再删 MOD 目录**，顺序反了；
4. 批量方法 `EnableAllAsync/DisableAllAsync/DeleteModsAsync`（:312-335、:639-648）调单体方法时**漏传 `gamePath`**，插件装错位置/删不干净；
5. `ModListViewModel.cs:213-231、289-346` 与 `ModDetailViewModel.cs:190-208` 在委托未注入时 fallback 到旧链路。

**文件：**
- 修改：`UEModManager/Services/ModManagementService.cs`
- 修改：`UEModManager/ViewModels/ModListViewModel.cs`、`ModDetailViewModel.cs`、`MainViewModel.cs`

**步骤：**

- [ ] 1. **移除 ViewModel fallback**：`ModListViewModel`/`ModDetailViewModel` 中 `_toggleModAsync`/`_deleteModAsync` 为 null 时不再回落旧链路，改为记 Error 日志 + 抛 `InvalidOperationException("部署服务未初始化")`。先确认 `MainViewModel.ConfigureActions`（:145-147 附近）在所有启动路径下都会注入（搜索 `ConfigureActions` 全部调用点）。
- [ ] 2. **修单体方法**（旧链路仍被扫描/导入等场景使用，不能直接删）：
   - `DisableModAsync`：删除目标前校验备份目录存在且文件数 ≥ 目标目录文件数，不满足则返回 false 并给出明确错误信息（禁止删除）；
   - `DeleteModAsync`：调整顺序——先删 MOD 目录，成功后再删备份目录；MOD 目录删除失败（文件占用）时保留备份并返回错误；
   - `EnableModAsync`：复制到临时目录 `{target}.staging`，全部成功后再原子替换（删旧 target → Move staging），失败时清理 staging、target 原样保留；
   - 批量方法补传 `gamePath` 参数。
- [ ] 3. 全文搜索 `ModManagementService` 的剩余调用方，列在提交说明里（为后续 v1.8 彻底下线做台账）。
- [ ] 4. `dotnet build` + `dotnet test`。
- [ ] 5. 提交：`fix(legacy): v1.8 链路加固——备份校验/删除顺序/staging 启用/批量补参，ViewModel 移除 fallback`

### Task B8：备份清理接入

**问题：** `DeploymentService.CleanupOldBackups`（:288-309）**从未被调用**，`Data\Backups` 无限膨胀（B1 修复前每次部署都全量备份，存量可能已很大）。且该方法按目录时间保留最近 5 个，不区分状态——会误删 `InProgress`/`PartiallyRolledBack` 等崩溃恢复还需要的备份。

**文件：**
- 修改：`UEModManager/Services/DeploymentService.cs:288-309`
- 修改：调用点（部署成功提交后）

**步骤：**

- [ ] 1. 修改 `CleanupOldBackups`：只清理状态为 `Committed`/`RolledBack`/`Dismissed` 的事务对应备份（读取各备份目录的 `transaction.json` 判断；无法解析的目录**跳过不删**），保留最近 10 个。
- [ ] 2. 在部署事务成功 Commit 之后（`DeploymentService` 状态置 Committed 的位置，约 :140）异步调用 `Task.Run(() => CleanupOldBackups())`，包 try-catch 只记日志。
- [ ] 3. `dotnet build` + `dotnet test`。
- [ ] 4. 提交：`fix(deploy): 部署提交后清理旧备份，仅清终态事务，保护崩溃恢复所需数据`

---

## 批次 C：死代码清理（P1，4 个任务，约 -6000 行）

> 删除原则：每删一组立即编译验证；`grep` 确认零引用后再删；保持每个任务一个 commit 方便回退。

### Task C1：删除 Agents 孤岛链

**审计结论：** 引用链 `Agents/* ← AgentManagerService ← EnhancedAuthService ← 无人调用`，整体是模仿 AI 多代理编排的历史实验代码，约 3000+ 行，App.xaml.cs 无任何相关注册。

**步骤：**

- [ ] 1. 逐个 grep 验证零引用（排除链内互引）后删除：
   - `UEModManager/Agents/` 整个目录（ISubAgent、BaseSubAgent、ControlAgent、AuthenticationAgent、ProjectAnalyzer、TestingAgent、OutputAgent、ProjectOptimizerAgent 等全部文件）
   - `UEModManager/Services/AgentManagerService.cs`
   - `UEModManager/Services/EnhancedAuthService.cs`
   - `UEModManager/Services/EmailService.cs`（唯一引用方是 EnhancedAuthService；**该文件含 4 个类型：EmailService/EmailConfig/EmailProvider/EmailResult，grep 需逐个覆盖**，已预核实全部仅本文件自用）
   - `UEModManager/Services/MailerSendEmailService.cs`（实现了 IEmailSender 但从未注册/构造）
   - `UEModManager/Services/AuthenticationService.cs`（注意：**不是** `LocalAuthService`/`UnifiedAuthService`，看清文件名）
- [ ] 2. `dotnet build` —— 若报错，说明有计划外引用，**停下来报告**，不要顺手乱删。
- [ ] 3. `dotnet test` + 提交：`chore: 删除 Agents 多代理框架及 EnhancedAuth/Email 死代码链（约 -3000 行）`

### Task C2：删除死窗口与无效 DI 注册

**步骤：**

- [ ] 1. grep 验证零打开点后删除：
   - `UEModManager/Views/AuthSettingsWindow.xaml` + `.xaml.cs` + `AuthSettingsWindow.Localization.cs`（若存在）
   - `UEModManager/Views/RegisterWindow.xaml` + `.xaml.cs` + `RegisterWindow.Localization.cs`（已确认存在，勿漏）
   - `UEModManager/Views/DatabaseConfigWindow.xaml` + `.xaml.cs`
- [ ] 2. 清理 `App.xaml.cs:275-276` 附近这两个窗口的 DI 注册行。
- [ ] 3. 顺带清理：`LoginWindow.xaml.cs:13、28` 的 `_unifiedAuth` 字段（解析后从未使用）。
- [ ] 4. `dotnet build` + `dotnet test` + 提交：`chore: 删除无入口的死窗口（AuthSettings/Register/DatabaseConfig）`

### Task C3：删除 ModConflictService 与伪测试文件

**审计结论：** `UEModManager/Services/ModConflictService.cs`（684 行）未注册 DI、应用内零调用；唯一消费者 `Tools/ConflictProbe` 用的是自己目录下的另一份拷贝。内部还有 8 处 `.Wait()` 阻塞和 per-MOD 重复挂载，不值得救。`TestLocalStorage.cs` 无人调用；`Tests/LocalStorageTest.cs` 仅 DEBUG 自检且直打真实 AppData 数据库。

**步骤：**

- [ ] 1. 删除 `UEModManager/Services/ModConflictService.cs`（先 grep `ModConflictService` 确认主工程零引用；`Tools/ConflictProbe/` 下的同名拷贝**保留**，那是独立工具）。
- [ ] 2. 删除 `UEModManager/TestLocalStorage.cs`。
- [ ] 3. `UEModManager/Tests/LocalStorageTest.cs`：删除，同时移除 `App.xaml.cs:87-100` 附近 `#if DEBUG` 的调用块。
- [ ] 4. `dotnet build` + `dotnet test` + 提交：`chore: 删除未接入的 ModConflictService 与产品程序集内的伪测试文件`

### Task C4：认证遗留收敛（谨慎级）

**审计结论：** 运行期实际只用 `LocalAuthService` + `UnifiedAuthService` 的 3 个方法（Initialize/SetAuthMode/RestoreSession）+ `CustomOtpService` + 邮件三件套。`CloudAuthService` 启动即被 `SetAuthModeAsync(OfflineOnly)` 锁死（`App.xaml.cs:331`），激活码登录是 mock（`CloudAuthService.cs:142` 返回 `mock_activation_token_`）。**云端登录是产品规划保留项，不要删除**，只做收敛。

**步骤：**

- [ ] 1. `OfflineModeService.cs`：唯一消费方已随 C3 删除，grep 确认后删除该服务及 `App.xaml.cs:209` 的注册。
- [ ] 2. `CloudAuthService.cs:142` 附近的 mock 激活码逻辑：加 `// TODO(v2.x): mock 实现，云端激活上线前不可对用户开放` 注释 + 在方法入口直接返回失败（含"功能尚未开放"消息），防止误触发假登录。
- [ ] 3. `UnifiedAuthService` 中仅被已删窗口引用的方法（`LoginWithActivationCodeAsync`/`SyncFromCloudAsync`/`SyncToCloudAsync` 等）：grep 确认零引用后删除；`LoginAsync`/`RegisterAsync` 若仍被 LoginWindow 引用则保留。
- [ ] 4. `dotnet build` + `dotnet test` + 提交：`refactor(auth): 收敛认证遗留——删除 OfflineModeService，封堵 mock 激活码，清理零引用方法`

---

## 批次 D：体验与质量优化（P2，7 个任务）

### Task D1：GamePathDialog 自动搜索移出 UI 线程

**问题：** `GamePathDialog.xaml.cs:222-366`（`AutoSearchPaths`）：`Task.Run` 启动后 :226 又 `await Dispatcher.InvokeAsync(...)` 把**整个搜索体搬回 UI 线程**——全盘 `Directory.GetFiles(*.exe, AllDirectories)`（:240）、Steam vdf 解析（:830）、Epic manifest 读取（:876）全在 UI 线程跑，游戏目录大时对话框冻结。:224 还有个 500ms"模拟搜索延迟"。

**步骤：**

- [ ] 1. 重构：磁盘扫描/注册表/vdf/manifest 解析全部在后台线程执行，只把**结果列表的 UI 更新**用 `Dispatcher.InvokeAsync` 编组回 UI 线程。删除 500ms 假延迟。
- [ ] 2. 注意 `Directory.GetFiles(AllDirectories)` 对无权限目录会抛异常——改用手写递归枚举 + try-catch 跳过无权限子目录（或 `EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }`）。
- [ ] 3. 手动验证点：打开游戏路径对话框点自动搜索，UI 不冻结、可点取消。
- [ ] 4. `dotnet build` + 提交：`perf(ui): 游戏路径自动搜索移至后台线程，UI 不再冻结`

### Task D2：async void 事件处理统一保护

**问题：** 全仓库 67 处 async void 事件处理器，部分无 try-catch，异常直接打崩进程。高危清单：`MainWindow.xaml.cs:588`（ShowGamePathDialog）、`:868-959`（导入/拖拽链路）、`:1097-1129`（批量启停删）、`ProfileManagerWindow.xaml.cs:49-61`（New/CloneProfile，且 `_ = ExecuteAsync()` 后立即刷新有竞态）、`AdminDashboardWindow.xaml.cs:102-108`（定时器回调）。

**步骤：**

- [ ] 1. 创建 `UEModManager/Infrastructure/SafeEventHandler.cs`（弹窗用项目统一的 `CyberMessageBox`，用法参考 `MainWindow.xaml.cs` 现有调用，**不要用裸 MessageBox.Show**）：
```csharp
public static class SafeEvent
{
    public static async void Run(Func<Task> action, ILogger? logger, string operationName)
    {
        try { await action(); }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[UI] {Op} 失败", operationName);
            CyberMessageBox.Show($"操作失败：{ex.Message}", "错误"); // 签名以 CyberMessageBox 实际定义为准
        }
    }
}
```
- [ ] 2. 仅改造上述**高危清单**中无 try-catch 的处理器（全部 67 处不必都动，已有 try-catch 的不碰）：处理器体改为 `SafeEvent.Run(async () => { ...原逻辑... }, _logger, "导入MOD")` 或直接补 try-catch，取改动更小者。
- [ ] 3. `ProfileManagerWindow` 的两处竞态：`_ = ExecuteAsync(null)` 改为 `await`（处理器本身已是 async void），刷新放在 await 之后。
- [ ] 4. `dotnet build` + 提交：`fix(ui): 高危 async void 事件处理补异常保护，修复 Profile 创建/重命名竞态`

### Task D3：重复代码合并

**问题与位置：**
1. `FormatSize` 4 份：`ManagementCenterWindow.xaml.cs:682-693`、`ImportConfirmDialog.xaml.cs:389-395`、`SettingsWindow.xaml.cs:432-436`、`Converters/ValueConverters.cs:348`（FileSizeConverter）；
2. BitmapImage 加载 6 处雷同：`MainWindow.xaml.cs:471-487/1358-1389`、`GamePathDialog`、`SettingsWindow`、`ModDetailWindow`、`ModDetailViewModel`；
3. 解压/嵌套解压/清理逐字重复：`ModManagementService.cs:853-927` vs `PackageImportService.cs:465-548`（含两份硬编码 GBK 936）；
4. 游戏图标选择+复制+预览：`GamePathDialog.xaml.cs:163-220` vs `SettingsWindow.xaml.cs:303-365`；
5. RAR/7z 警告文案+逻辑：`MainWindow.xaml.cs:898-907` vs `ImportDialog.xaml.cs:96-108`。

**步骤：**

- [ ] 1. `FormatSize`：在 `UEModManager.Core` 建 `Utils/FileSizeFormatter.cs`（带 xUnit 测试），4 处全部替换。
- [ ] 2. 图片加载：建 `UEModManager/Infrastructure/ImageLoader.cs`（封装 File.Exists + BeginInit/DecodePixelWidth/Freeze），6 处替换。
- [ ] 3. 解压三件套：保留 `PackageImportService` 一份，`ModManagementService` 改为调用它（或抽到 Core 的 `Import/ArchiveExtractor.cs`——若 SharpCompress 等依赖在主工程，放主工程共享类即可，**不强求下沉 Core**）。
- [ ] 4. 第 4、5 项：各抽一个共享方法/常量，两处调用。
- [ ] 5. 每完成一项就 `dotnet build` + `dotnet test`，全部完成后一次提交：`refactor: 合并 FormatSize/图片加载/解压/图标选择/导入警告五组重复实现`

### Task D4：用户可见文案修复（ux-copy-audit 收尾）

**清单（均为普通用户可见）：**

- [ ] 1. `Views/ImportDialog.xaml:118`——拖拽区 `.pak .utoc .ucas` 改为"MOD 文件 / 压缩包"；`:58` 的支持格式说明与实际行为对齐（**代码 :100-107 会拒绝 RAR/7z，文案却宣称支持——以产品决策为准：若保留拒绝逻辑，文案删掉 .rar/.7z；不要反过来私自实现 RAR 支持**）。
- [ ] 2. RAR/7z 警告弹窗正文（`MainWindow.xaml.cs:905`、`ImportDialog.xaml.cs:103`，D3 已合并为一处）："ZIP、PAK/UCAS/UTOC" 改为"ZIP 压缩包或 MOD 文件"。
- [ ] 3. `Views/ImportConfirmDialog.xaml:148` 及 `.xaml.cs:127-128`——目标路径下拉显示 `Content/Paks/~mods`、`Binaries/Win64` 等：保留路径值（高级用户需要），但显示文本加友好前缀，如"游戏 MOD 目录（Content/Paks/~mods）"。
- [ ] 4. `Views/SettingsWindow.xaml.cs:586` + `.xaml:443` 关于页"专为虚幻引擎游戏设计的 MOD 管理器"→"面向普通玩家的游戏 MOD 管理器，已适配多款热门游戏"（英文版 :541 同步：`A mod manager for popular games, built for everyone`）。**依据产品定位：严禁出现"专为 UE/UE-only"表述。**
- [ ] 5. 崩溃恢复弹窗（`MainWindow.xaml.cs:210-235`）：`[{c.Status}]` 英文枚举改中文映射（Committed→已完成、Failed→失败、InProgress→未完成、RolledBack→已回滚、PartiallyRolledBack→部分回滚）；`ManagementCenterWindow.xaml.cs:241` 部署状态、`:223` 的 `{tx.BackendType}`（Copy/HardLink/Symlink→文件复制/硬链接/符号链接）同样处理。建一个集中映射类 `UEModManager/Infrastructure/DisplayNameMapper.cs`，禁止散落三元表达式。
- [ ] 6. `Views/ProfileManagerWindow.xaml:158/169` "导出 .lock"/"导入 .lock" → "导出方案文件"/"导入方案文件"；`.xaml.cs:448/493/516/560/605/627` 弹窗里的"Lock 服务""哈希不一致"改为"方案服务""文件校验不一致"。
- [ ] 7. `dotnet build` + 提交：`fix(copy): 清理用户可见技术术语与 UE 专用表述，状态枚举中文化`

### Task D5：修复"33号远征队"可执行文件映射

**问题：** `GameConfigService.cs:328` 把"光与影：33号远征队"（Clair Obscur: Expedition 33）映射到 `enshrouded` 可执行名——Enshrouded（雾锁王国）是另一款游戏，自动检测必然失败。`GameType.Enshrouded` 枚举名同样张冠李戴。

**步骤：**

- [ ] 1. 查证正确 exe 名：Clair Obscur: Expedition 33 是 UE5 游戏，shipping 可执行名为 `Expedition33Steam-Win64-Shipping.exe`（Steam 版，位于 `Sandfall/Binaries/Win64/`；若无法联网核实，至少改成 `expedition33` 前缀匹配并加 TODO 注释）。
- [ ] 2. 修改 :328 的匹配串；`GameType.Enshrouded` 枚举重命名为 `Expedition33`（全引用更新，检查枚举是否被序列化持久化——若 local.db/JSON 中存了枚举**字符串**，需加兼容读取：旧值 `Enshrouded` 映射到新值；若存的是数字则保持枚举数值不变即可）。
- [ ] 3. **枚举重命名抓不到字符串字面量**——全仓库 `grep -rn "Enshrouded" --include="*.cs"`，已知至少 5 处功能性字符串需同步修改（改为 `"Expedition 33"` / `"Sandfall"` 等正确关键词）：
   - `UnrealEngineAdapter.cs:51/61`（33号远征队的安装目录搜索关键词）
   - `Views/GamePathDialog.xaml.cs:765/920`（自动搜索路径匹配）
   - `GameConfigService.cs:453`（`gameName.Contains("Enshrouded")` 反向映射）
- [ ] 4. `dotnet build` + `dotnet test` + 提交：`fix(game): 修正33号远征队的可执行文件映射与搜索关键词（原误用雾锁王国）`

### Task D6：崩溃恢复对话框补"忽略"入口

**问题：** `MainWindow.xaml.cs:197-242`（`CheckForCrashesAsync`）是"全有或全无"：对 `ManualReviewRequired` 的事务，`ApplyRecoveryAsync` 只记日志不改状态（`CrashRecoveryService.cs:92-97`），导致**每次启动都重复弹窗**；Core 已实现 Dismiss 防死循环机制（`CrashRecoveryService.cs:133` 有 `→Dismissed` 转换），但 UI 没接。

**步骤：**

- [ ] 1. 弹窗从 Yes/No 改为三选：恢复 / 本次跳过 / 不再提醒（用 `CyberMessageBox` 若支持三按钮，否则两层弹窗）。"不再提醒"调用 `CrashRecoveryService` 的 Dismiss 路径（先读该服务确认方法签名，约 :133）。
- [ ] 2. 把 :220/:234 的裸 `MessageBox.Show` 换成 `CyberMessageBox`（项目统一弹窗）。
- [ ] 3. `dotnet build` + `dotnet test` + 提交：`fix(recovery): 崩溃恢复弹窗接入 Dismiss，ManualReview 事务不再每次启动重弹`

### Task D7：Build-Installer.ps1 参数化

**问题：** 版本号散落（标题 `v2.0.3`、产物名 :66、`LocalDbContext.cs:125` 种子值、.iss 内）；路径依赖当前目录；首选 Inno 路径是作者私人机器的 `D:\安装`（:3 用 char code 拼出）；:45 整段 Python 塞单行；:60 `Start-Process` 不带 `/cc` 可能弹 GUI。

**步骤：**

- [ ] 1. 头部加 `param([string]$Version = "2.1.0", [string]$Configuration = "Release")` + `$ErrorActionPreference = 'Stop'` + `Set-Location $PSScriptRoot`。
- [ ] 2. 产物名、.iss 传参（Inno 支持 `/D` 定义预处理变量）统一从 `$Version` 取；删除 `D:\安装` 私人路径首选项，只保留 ProgramFiles 探测 + 报错提示安装 Inno Setup。
- [ ] 3. :45 的内联 Python 抽成 `Setup/extract_logo.py` 已存在的独立文件（先确认 `Setup/extract_logo.py` 是否就是它，是则直接调用），并加 python 可用性检查。
- [ ] 4. 编译器调用确保走 `ISCC.exe`（命令行编译器）而非 `Compil32.exe`。
- [ ] 5. 手动验证点：`powershell -File Build-Installer.ps1` 在干净 shell 中完整跑通。
- [ ] 6. 提交：`chore(build): 安装包脚本参数化，移除私人路径，版本号单一来源`

---

## 完成定义（DoD）

全部任务完成后：

- [ ] `dotnet build UEModManager.sln -c Debug` 与 `-c Release` 均 0 error；
- [ ] `dotnet test UEModManager.Core.Tests` 全绿，测试数 ≥ 基线 446（B1/B2/B4/B5 新增的测试在内）；
- [ ] `git log --oneline ux-copy-audit..HEAD` 显示约 21 个独立 commit，每个可单独 revert；
- [ ] 输出一份《执行结果汇总.md》：每个任务的状态（完成/跳过+原因）、新增测试数、删除代码行数、遗留风险清单（特别是 B3/B7 需要实机验收的项）。

## 明确不做的事（防止扩大化）

- 不做 MVVM 全面重构（XAML 零 Command 绑定的现状保持，死的 `[RelayCommand]` 留给下一轮——删它们牵连 ViewModel 结构，风险大于收益）；
- 不删 `CloudAuthService`/`UnifiedAuthService`（云端是产品保留规划，只做 C4 的收敛）；
- 不动 `cf-workers/` 后端（另行安排）；
- 不实现新功能（如 RAR 解压支持）；
- 不碰 `用户反馈/`、`*.env`、`.pen` 文件。
