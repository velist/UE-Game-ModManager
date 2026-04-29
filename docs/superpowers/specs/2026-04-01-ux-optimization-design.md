# UX 优化设计规范 — 方案 A

> **日期：** 2026-04-01
> **状态：** 已确认
> **原型文件：** `updateplan.pen`（画布底部 3 个新屏幕）
> **目标用户：** 有一定经验的 MOD 玩家

---

## 1. 问题诊断

### 1.1 「仓库管理」身份错乱
- XAML 元素名仍为 `PluginImportBorder`（原"导入插件"按钮）
- v2.0 升级时被改为打开 `RepositoryManagerWindow`
- 用户看到"仓库管理"不知所云，期望的"插件导入"功能消失

### 1.2 「生成物」术语晦涩
- 按钮打开 `OverwriteManagerWindow`（管理部署备份、运行时生成文件）
- "生成物"是纯开发者术语，普通 MOD 玩家完全无法理解
- 且大多数用户不需要频繁访问此功能，不应占据 header 黄金位置

### 1.3 路径未配置时的迷茫
- 导入 MOD 可以自动检测，但用户不知道文件被导入到了哪里
- MOD 路径和插件路径通常不一致，当前共用一个导入入口但无路径区分
- 底部状态栏显示"MOD目录: 未配置"但不够醒目

### 1.4 Header 操作按钮过载
- 当前 header 有 5 个操作按钮（冲突检测、导入MOD、仓库管理、配置、生成物）+ 启动游戏
- 搜索框和视图切换为浏览控件，不计入操作按钮
- 高频操作和低频管理功能混在一起，层级不清晰

---

## 2. 设计方案

### 2.1 Header 简化

> 以下仅计操作按钮，搜索框和视图切换为浏览控件不计入。

**优化前（5 个操作按钮 + 启动）：**
```
[冲突检测] [导入MOD] [仓库管理] [配置] [生成物]   [启动游戏]
```

**优化后（2 个操作按钮 + 启动）：**
```
[冲突检测] [导入] [管理中心]   [启动游戏]
```

**完整 Header 布局：**
```
[Logo] [游戏选择] ──────── [搜索框] [视图切换] | [冲突检测] [导入] | [管理中心] [启动游戏]
```

**改动点：**
- 「仓库管理」「配置」「生成物」三按钮合并为一个 **「管理中心」** 按钮
- 「导入MOD」简化为「导入」（统一入口，自动识别 MOD/插件/配置）
- 用分隔符将按钮分为：浏览区 | 操作区 | 系统区

**按钮样式：**
| 按钮 | 填充 | 图标 | 优先级 |
|------|------|------|--------|
| 冲突检测 | `#1f2937` 暗灰 | `shield-alert` 黄色 | 次要 |
| 导入 | 青色渐变 `#06b6d4→#0891b2` | `download` 白色 | 主要 |
| 管理中心 | `#1f2937` 暗灰 | `settings-2` 灰色 | 次要 |
| 启动游戏 | 绿色渐变 `#10b981→#059669` | `play` 白色 | 主要 |

### 2.2 管理中心窗口（新建 `ManagementCenterWindow`）

**入口：** Header「管理中心」按钮
**形式：** 单窗口，4 个标签页
**尺寸：** 520×620
**窗口样式：** `ShowDialog()` 弹出，Owner 设为 MainWindow。使用与 RepositoryManagerWindow / OverwriteManagerWindow 一致的窗口样式（深色背景 + 自定义标题栏 + 关闭按钮）。

| Tab | 图标 | 原入口 | 对应服务 |
|-----|------|--------|---------|
| **MOD 库** | `database` | 仓库管理 | `PackageRepository` |
| **部署记录** | `history` | *(新增)* | `DeploymentService` |
| **配置合并** | `file-cog` | 配置 | `ConfigMergeEngine` |
| **生成文件** | `file-output` | 生成物 | `OverwriteStore` |

**术语映射：**
- "仓库管理" → **"MOD 库"**
- "生成物" → **"生成文件"**
- "配置" → **"配置合并"**

**MOD 库 Tab 内容：**
- 统计卡片行（总占用 / 总包数 / 未引用 / 重复包）
- 包列表表格（包名称 / 大小 / 引用 / 状态）
- 底部操作：检查完整性 / 合并重复 / 清理未引用

**部署记录 Tab 内容：**
- 最近部署事务列表（时间 / 操作数 / 状态 / 回滚按钮）
- 来源：扫描 `Data/Backups/*/transaction.json` 目录加载历史事务
- **需新增后端方法：** `DeploymentService.GetTransactionHistoryAsync()` — 扫描 Backups 目录，反序列化并返回 `List<DeploymentTransaction>`

**配置合并 Tab 内容：**
- 当前 Profile 中有配置冲突的文件列表
- 每个键的来源追踪
- 来源：为当前活跃 Profile 构建 `ConfigMergePlan`（收集所有已启用包中 Kind=Config 的 Artifact），调用 `ConfigMergeEngine.PreviewAsync(plan)`

**生成文件 Tab 内容：**
- 统计（活跃 / 过期 / 总占用）
- 生成文件列表（名称 / 来源 / 类型 / 状态 / 操作）
- 操作：晋升为正式包 / 删除 / 清理过期

### 2.3 增强版导入确认对话框

**入口：** Header「导入」按钮 → 步骤1（选择文件）→ **步骤2（确认，本设计）**
**也支持：** 拖拽文件到主窗口 → 直接进入步骤2

**新增区域 — 部署目标路径：**
```
┌─ 部署目标路径 ──────────────────────────────┐
│ ● MOD →  D:\Games\SB\Content\Paks\~mods\   │
│ ● 插件 → D:\Games\SB\Binaries\Win64\       │
│ ✏️ 修改路径                                   │
└─────────────────────────────────────────────┘
```
- 按类型色标分别显示不同类型文件的目标路径
- 路径可点击「修改路径」打开 GamePathDialog
- 色标与类型标签一致：MOD=`#06b6d4` 青、插件=`#a855f7` 紫、配置=`#f59e0b` 橙

**新增区域 — 路径未配置警告：**
```
┌─ ⚠️ 插件路径未配置 ─────────────────── [配置] ┐
│ 插件(.dll)需要单独的目标路径，点击右侧配置      │
└────────────────────────────────────────────────┘
```
- 黄色警告条（`#f59e0b` 边框 + 半透明背景）
- 仅在检测到对应类型路径未配置时显示
- 「配置」按钮直接打开 GamePathDialog 并定位到对应路径字段

**完整导入确认布局（从上到下）：**
1. Header（标题 + 步骤指示器）
2. 源文件卡片（文件名 + 解压信息）
3. 方案选择器（导入到哪个 Profile）
4. 文件列表（文件名 / 类型色标 / 大小 / 导入勾选）
5. **[新增] 部署目标路径区域**
6. **[新增] 路径未配置警告**（条件显示）
7. 底部按钮（返回 / 取消 / 确认导入）

---

## 3. 需要修改的代码文件

### 3.1 删除的 UI 元素
| 文件 | 删除内容 | 定位 |
|------|---------|------|
| `MainWindow.xaml` | `PluginImportBorder`（原仓库管理按钮） | XAML Line ~543, x:Name="PluginImportBorder" |
| `MainWindow.xaml` | 配置管理按钮 | XAML Line ~562, ConfigManager 相关 Border |
| `MainWindow.xaml` | 生成物管理按钮 | XAML Line ~580, OverwriteManager 相关 Border |
| `MainWindow.xaml.cs` | `RepositoryManager_Click` handler | Line ~1150 |
| `MainWindow.xaml.cs` | `ConfigManager_Click` handler | Line ~1166 |
| `MainWindow.xaml.cs` | `OverwriteManager_Click` handler | Line ~1158 |

### 3.2 旧窗口处置
| 文件 | 处置方式 |
|------|---------|
| `Views/RepositoryManagerWindow.xaml/.cs` | **保留但不再从 MainWindow 直接打开**。管理中心的 MOD 库 Tab 将核心 UI 内联实现（参考其布局但重新构建），后续可考虑提取为 UserControl 复用 |
| `Views/OverwriteManagerWindow.xaml/.cs` | **保留但不再从 MainWindow 直接打开**。管理中心的生成文件 Tab 同上 |
| `Views/ConfigManagerWindow.xaml/.cs` | **保留但不再从 MainWindow 直接打开**。管理中心的配置合并 Tab 同上 |
| `App.xaml.cs` | 上述三个窗口的 DI 注册保留（其他入口可能仍需要），新增 `ManagementCenterWindow` 注册 |

### 3.2 新增/修改的 UI 元素
| 文件 | 内容 |
|------|------|
| `MainWindow.xaml` | 「管理中心」按钮（替代上述三个） |
| `MainWindow.xaml.cs` | `ManagementCenter_Click` → 打开 ManagementCenterWindow |
| **`Views/ManagementCenterWindow.xaml`** | **新建** — 管理中心窗口（4 Tab） |
| **`Views/ManagementCenterWindow.xaml.cs`** | **新建** — 管理中心 code-behind |
| `Views/ImportConfirmDialog.xaml` | 新增路径显示区域 + 未配置警告；窗口高度从 540 调至 640 |
| `Views/ImportConfirmDialog.xaml.cs` | 路径检测逻辑 + 配置按钮跳转 |
| `App.xaml.cs` | DI 注册 ManagementCenterWindow |
| `Services/DeploymentService.cs` | **新增** `GetTransactionHistoryAsync()` 方法 |

### 3.4 新增按钮画刷（CyberDarkTheme.xaml）

以下渐变色在现有主题中没有对应 StaticResource，需新增：

| 画刷名 | 色值 | 用途 |
|--------|------|------|
| `ImportButtonGradient` | `#06b6d4→#0891b2` | 导入按钮 |
| `LaunchButtonGradient` | `#10b981→#059669` | 启动游戏按钮 |

其余按钮色值（`#1f2937`、`#374151`）已有对应 `SurfaceBrush`、`BorderLightBrush`。

### 3.5 警告区域共存规则

导入确认对话框中，**冲突警告**（检测到文件冲突时显示）和**路径未配置警告**（路径未配置时显示）可同时显示。布局顺序：
1. 部署目标路径区域（始终显示）
2. 路径未配置警告（条件显示）
3. 冲突警告（条件显示）

两者均使用 `Visibility.Collapsed` 默认隐藏，条件触发时切换为 `Visible`。

### 3.3 术语替换
| 位置 | 旧术语 | 新术语 |
|------|--------|--------|
| Header 按钮 | 导入 MOD | 导入 |
| Header 按钮 | 仓库管理 / 配置 / 生成物 | 管理中心 |
| ManagementCenterWindow Tab | 仓库管理 | MOD 库 |
| ManagementCenterWindow Tab | 生成物 | 生成文件 |

---

## 4. 风格一致性要求

- 图标用 Lucide Path 矢量（导入确认对话框标题区现用 Segoe MDL2 的 `&#xE896;`，本次一并替换为 Lucide `download` 图标）
- 字体用 `{StaticResource InterFont}`
- 类型色标：MOD=`#06b6d4`、插件=`#a855f7`、配置=`#f59e0b`
- 状态色标：正常=`#22c55e`、警告=`#f59e0b`、错误=`#ef4444`
- 背景色沿用 CyberDark 主题：`#0B1426`（主背景）、`#111827`（header）、`#161b22`（卡片）、`#1f2937`（按钮）
- 边框色：`#21262d`（卡片边框）、`#374151`（按钮边框）
- 文字色：`#e5e7eb`（主文字）、`#d1d5db`（次要文字）、`#9ca3af`（标签）、`#6b7280`（占位符）
- Tab 激活态：底部 2px `#06b6d4` 下划线 + 文字/图标变为 `#06b6d4`
- Tab 非激活态：文字/图标 `#6b7280`
- **1:1 匹配 updateplan.pen 原型**

---

## 5. 原型屏幕索引

| 屏幕 | 节点 ID | 名称 |
|------|---------|------|
| Header 简化 | `jzdi7` | UX优化-主界面Header |
| 管理中心 | `6oeSE` | UX优化-管理中心 |
| 导入确认增强 | `QaUEg` | UX优化-导入确认(增强版) |

---

## 6. 管理中心操作后行为

| Tab | 操作 | 行为 |
|-----|------|------|
| MOD 库 | 清理未引用 / 合并重复 | 刷新统计卡片 + 包列表 |
| 部署记录 | 回滚事务 | 关闭管理中心后自动触发 MainWindow 的 `RefreshModsAsync()` |
| 配置合并 | 仅预览，无修改操作 | — |
| 生成文件 | 晋升为正式包 | 刷新 MOD 库 Tab + 主窗口 MOD 列表 |
| 生成文件 | 删除 / 清理过期 | 刷新统计 + 列表 |

管理中心窗口关闭时，如有数据变更则通知 MainWindow 刷新（通过 `PackageRepository.PackagesChanged` 事件）。
