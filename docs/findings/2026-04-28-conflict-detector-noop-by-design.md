# Phase 4 ConflictAnalyzer 在当前部署模型下永不触发

**日期：** 2026-04-28
**发现者：** Claude（Phase 11 Core 扎实化第二轮）
**严重度：** 设计漏洞（不是实现 bug），影响 Phase 4 验收点
**状态：** ✅ 已修复（2026-04-28，方案 B）

---

## 问题描述

`ConflictAnalyzer` 文件路径冲突这一层（`ConflictType.Path`）在当前部署模型下**永远不会触发**——
即用户启用任何 MOD 组合，路径冲突列表始终为空。

## 根因

`ConflictDetector.ComputeTargetPath`（与 `DeploymentPlanner.ComputeTargetPath` 同源）公式为：

```
Mod 路径   ：modPath / {PackageKey} / {RelativeTargetPath}
Plugin 路径：gamePath / {PluginTargetPath} / {PackageKey} / {RelativeTargetPath}
```

每个包在游戏/MOD 目录下有自己的**独立子文件夹**（按 PackageKey 命名）。
两个不同 PackageKey 的包永远不会落在同一目标路径上：

```
"a" 的 shared.pak → C:/Games/Demo/Mods/a/shared.pak
"b" 的 shared.pak → C:/Games/Demo/Mods/b/shared.pak   ← 不同绝对路径
```

`ConflictAnalyzer` 把绝对路径作为 dict key 收集 owners，永远只会有 1 个 owner，
所以 `if (owners.Count <= 1) continue;` 直接跳过，不可能产生 ConflictRecord。

## 与 Phase 4 文档的差距

接手文档（`v2.0升级指南_开发者接手文档.md` §10）声称：

> Phase 4 后端 ✅
> - ConflictRecord 含胜者/败者链 + 解决原因 + 严重程度
> - ConflictAnalyzer 检测文件路径冲突 + 优先级自动求解

后半句"检测文件路径冲突"在当前部署模型下事实上无效。`ConflictResolver` /
`ConflictDetector` 的纯求解逻辑**本身是对的**（已被 ConflictResolverTests 11 个 +
ConflictDetectorTests 13 个测试覆盖），但**没有真实输入能让它返回非空结果**。

## 真正的运行时冲突

UE 引擎按 mod 列表加载顺序处理同名 `.pak`（后加载覆盖前加载）。这是**运行时**行为，
不是部署时静态分析能解决的。当前 `ModConflictService`（CUE4Parse 深度分析）覆盖了
"pak 内容冲突"这一更深层维度，与 `ConflictAnalyzer` 互补。

## 修复方向（未来工作）

要让 `ConflictAnalyzer` 真的有用，需要二选一：

### 方案 A：扁平部署模式
改 `ComputeTargetPath` 不带 PackageKey 子目录：

```
Mod 路径：modPath / {RelativeTargetPath}
```

需要同步改 `DeploymentPlanner` / `DeploymentService`（涉及备份/回滚路径模式）。
影响面大，是 Phase 4 设计的根本改动。

### 方案 B：保留独立子目录，转向"加载顺序冲突"维度
不改部署路径，但 `ConflictAnalyzer` 改为检测 `RelativeTargetPath` 重复（与 PackageKey 无关）：

- key = `RelativeTargetPath`
- 多个包声明同一 RelativeTargetPath → 视为"加载冲突候选"
- ConflictType 改为 `LoadOrder`（已有枚举值）

侵入面小，但语义从"路径冲突"改为"同名加载顺序冲突"。

### 推荐
**方案 B**，因为它符合用户对"看到 MOD A 和 MOD B 都要改 `Engine.ini`"的直觉，
且不破坏现有部署/回滚模型。

## 现状证据

测试文件中已经文档化此事实：

- `UEModManager.Core.Tests/Services/Conflict/ConflictDetectorTests.cs`
  - 类 XML 注释（顶部）
  - `DetectConflicts_PerPackageDirectoryModel_NeverProducesPathConflicts` 测试方法

未来重做 Phase 4 时，**先删除上述断言**，让测试反映新设计。

---

## ✅ 修复记录（2026-04-28，方案 B）

### 改动

1. **`ConflictDetector.ComputeLoadConflictKey`**（新增静态方法）
   - 计算"无 PackageKey 子目录"的归一化路径
   - Mod/Config: `modPath/{RelativeTargetPath}`
   - Plugin: `gamePath/{PluginTargetPath}/{RelativeTargetPath}`
   - 与 `ComputeTargetPath`（实际部署路径，含 PackageKey）解耦

2. **`ConflictDetector.CollectOwners`**
   - 字典 key 从绝对部署路径改为 `ComputeLoadConflictKey` 的输出
   - 多个包声明同名 RelativeTargetPath → 落入同一 key → 冲突候选

3. **`ConflictDetector.DetectConflicts`**
   - `ConflictRecord.Type` 由 `Path` 改为 `LoadOrder`（语义更准）
   - `ConflictRecord.TargetPath` 现在保存 LoadConflictKey
   - 用户覆盖 dict 也以 LoadConflictKey 作 key

4. **测试**
   - 旧"NeverProducesPathConflicts"反向测试已删除
   - 新增：`TwoPackagesSameRelativePath_ProducesLoadOrderConflict` / `SameContent_SeverityIsInfo` /
     `ComputeLoadConflictKey_OmitsPackageKey` / `DifferentPluginTargetPath_DistinctKeys` 等
   - 17 个 ConflictDetector 测试全部反映新行为

### 不变的部分

- **DeploymentPlanner / DeploymentService** 仍使用 `ComputeTargetPath`（含 PackageKey 子目录）部署，
  避免破坏现有备份/回滚路径模式
- ConflictResolver 纯求解逻辑零改动（优先级 + 用户 override 仍然适用）
- ConflictAnalyzer 公共 API 零改动（调用方自动受益）

### 用户感知

- 启用同名 .pak 的两个 MOD → 冲突面板会显示"加载顺序冲突"
- 显示胜者（高优先级）+ 败者链
- 用户可手动 override 改变胜者
- ConflictType 在 UI 上用"加载顺序冲突"措辞（待 UI 文案同步）
