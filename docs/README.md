# 文档索引

## 架构

- [架构总览](architecture/overview.md) — 30 秒理解项目分层、Phase 状态、关键决策

## 操作指南（Playbooks）

- [编写自定义部署后端](playbooks/writing-deployment-backend.md) — 加 VFS / Junction / 自定义部署方式
- [在 Core 写新的纯函数 Service](playbooks/writing-core-service.md) — 扩展 Domain 内核
- [Package 仓库与 manifest.json 格式](playbooks/package-manifest-format.md) — 外部工具生成包格式

## 第三方扩展示例

- [`samples/UEModManager.SampleBackend/`](../samples/UEModManager.SampleBackend/) — 自定义 Deployment Backend 最小独立可编译示例

> 新增游戏支持**不需要**写扩展：引擎规则维护在 `UEModManager/Models/EngineProfile.cs` 静态表，
> 具体要改哪几处见[架构总览](architecture/overview.md)"引擎规则维护在哪里"。
> 原先的 `IHostAdapter` 扩展点与 `SampleAdapter` 示例因生产调用点为 0，已于 2026-07 删除。

## 设计漏洞记录（Findings）

- [2026-09-05 审计问题修复与验收](findings/2026-09-05-audit-repairs.md) — F01–F08 修复、完整验证和发布要求
- [2026-09-05 消融与全仓审计](findings/2026-09-05-ablation-audit.md) — 修复前的历史快照、清理依据与原始缺陷
- [2026-04-28 ConflictDetector 永不触发 (已修复)](findings/2026-04-28-conflict-detector-noop-by-design.md)

## 上层文档

- [`CLAUDE.md`](../CLAUDE.md) —— 老式项目概览（部分过时）

## 跑测试

```bash
dotnet test UEModManager.sln --configuration Release
cd cf-workers/modmanger-api
npm ci
npm test
node test/desktop-contract.mjs
```

2026-09-05 修复后验证结果：Debug / Release 的 Core 均 **1211 通过**，应用均 **582 通过、1 跳过**；Worker **58 项原有自测 + 18 项真实运行时测试通过**，实际桌面到 Worker 的协议验收 **17 项通过**。跳过项是手动主题快照生成器。测试边界与日志路径见修复报告。

Worker 测试要求 Node.js 22 及以上；桌面协议检查还需要 Windows 与 .NET 8 SDK。部署绑定、验证码协议及客户端配套发布要求见 [AUTH_PROTOCOL.md](../cf-workers/modmanger-api/AUTH_PROTOCOL.md)。

## 跑 Build

```bash
dotnet build UEModManager.sln --configuration Debug
```

预期：**0 errors / 0 warnings**，5 个项目协同（主项目 + Core + Core.Tests + UEModManager.Tests + SampleBackend）。
