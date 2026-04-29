# 文档索引

## 架构

- [架构总览](architecture/overview.md) — 30 秒理解项目分层、Phase 状态、关键决策

## 操作指南（Playbooks）

- [编写自定义 Host Adapter](playbooks/writing-host-adapter.md) — 给新游戏加支持
- [编写自定义部署后端](playbooks/writing-deployment-backend.md) — 加 VFS / Junction / 自定义部署方式
- [在 Core 写新的纯函数 Service](playbooks/writing-core-service.md) — 扩展 Domain 内核
- [Package 仓库与 manifest.json 格式](playbooks/package-manifest-format.md) — 外部工具生成包格式

## 第三方扩展示例

- [`samples/UEModManager.SampleAdapter/`](../samples/UEModManager.SampleAdapter/) — 自定义 Host Adapter 最小独立可编译示例
- [`samples/UEModManager.SampleBackend/`](../samples/UEModManager.SampleBackend/) — 自定义 Deployment Backend 最小独立可编译示例

## 设计漏洞记录（Findings）

- [2026-04-28 ConflictDetector 永不触发 (已修复)](findings/2026-04-28-conflict-detector-noop-by-design.md)

## 上层文档

- 项目根目录的 [`v2.0升级指南_开发者接手文档.md`](../v2.0升级指南_开发者接手文档.md) —— Phase 0-13 详细实施 + Core 17 轮拆分记录
- [`游戏插件管理器升级计划_全Phase极致细化版_重新生成.md`](../游戏插件管理器升级计划_全Phase极致细化版_重新生成.md) —— 长期愿景
- [`CLAUDE.md`](../CLAUDE.md) —— 老式项目概览（部分过时）

## 跑测试

```bash
dotnet test UEModManager.Core.Tests/UEModManager.Core.Tests.csproj
```

预期：**552 测试全绿**，约 27 ms 完成。

## 跑 Build

```bash
dotnet build UEModManager.sln --configuration Debug
```

预期：**0 errors / 0 warnings**，5 个项目协同（主项目 + Core + Tests + SampleAdapter + SampleBackend）。
