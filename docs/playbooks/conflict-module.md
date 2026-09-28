# Playbook：给新引擎接上冲突模块

适用场景：新增一款游戏，需要让它也检出"哪几个 MOD 改到了同一批资源"，
或需要调整既有引擎的冲突判据。

先读 [`docs/architecture/overview.md`](../architecture/overview.md) 的 §4 与 §4.5 ——
本文只讲"怎么加"，格式细节与判定顺序在那里。

---

## 1. 决定 `ConflictStrategy`

`EngineProfile.ConflictStrategy` 只有两个取值：

| 取值 | 判据 | 用于 |
|---|---|---|
| `FilePath`（默认） | 归一化的 `RelativeTargetPath` | 非 UE 引擎，以及所有 `Kind != Mod` 的包 |
| `ContainerAssets` | 容器内部读出来的资产键 | UE 的 MOD 包 |

`FilePath` 是"同一个相对路径被两个包声明"。这对把文件铺在游戏根目录的插件是对的，
但对 UE 的 `.pak` 完全不成立：两个 MOD 部署到不同目录，内部替换的却是同一张贴图。

```csharp
[EngineType.BepInEx] = new EngineProfile
{
    DisplayName = "BepInEx",
    // 插件式：文件铺在 BepInEx/plugins 下，路径本身就是判据。
    ConflictStrategy = ConflictStrategy.FilePath,
    SharedComponentRules = [],
},
```

只有容器格式能被 `IContainerIndexReader` 读懂时才用 `ContainerAssets`。
本仓库目前实现了 `.pak`（`PakIndexReader`）与 `.utoc`（`IoStoreTocReader`）。

---

## 2. 登记共享组件规则

多个 MOD 各带一份同样的运行时是常态，不该报成冲突。规则按组件名分组：

```csharp
new SharedComponentRule(
    ComponentName: "BepInEx 运行时",
    PathPrefixes: ["bepinex/core/", "bepinex/"],
    FileNames: ["doorstop_config.ini", "winhttp.dll"]),
```

- `PathPrefixes` 用于"整个目录都是运行时"的情况，末尾带 `/`。
- `FileNames` 用于"散落在游戏根、只认文件名"的情况（代理 DLL 都是这种）。
- 两者都要给足：UE4SS 有的包按 `ue4ss/` 目录带，有的只把 `dwmapi.dll` 放在 `Binaries/Win64` 根部。

**规则只作用于 `Kind != Mod` 的包**，它们条目本来就是相对游戏根的文件路径。
容器内部的资产不走这里 —— pak 里是引擎资产路径，和一个 MOD 自带的 `ue4ss/` 目录没有可比性。

裁决顺序（`AssetConflictDetector.Build` 步骤 ⑧）：

1. Providers 内容指纹全同 → 保留 `indexes` 里靠前者，理由"内容相同"；
2. 不同 → 取 PE 文件版本最高者（`FileVersionInfo`，仅 `.dll` / `.exe` 有），理由"版本较高 {v}"；
3. 都没有版本 → 取 `File.GetLastWriteTimeUtc` 最新者，理由"文件较新"。

只有**内容不同**的那些会写成 `ConflictOverrides` 覆盖规则 —— 部署层据此决定同路径落盘哪一份。
内容相同的不写：写进去没有行为差异，只会撑大方案文件。

---

## 3. 需要新增容器读取器时

只有目标引擎用的是本仓库还没实现的容器格式时才需要，否则跳过。

1. 在 `UEModManager.Core/Services/Conflict/Indexing/` 加一个 `IContainerIndexReader` 实现。
2. 在 `ContainerFormat.Detect` 里用**文件内容**（不是扩展名）判定它：`.ucas` 是数据体不是索引载体，
   必须落回 `ContainerKind.Unknown`。
3. 在 `AssetIndexService.ReadContainerAsync` 的 switch 里接线。

约束：

- **入参只能是 `Stream`**。Core 是 `net8.0` 纯函数库，不碰文件系统 —— 打开文件在主程序侧。
- **每个条目都要带 `SourceFile`**（调用方传进来的 `sourceFile`），否则指纹回读时找不到容器。
- **解析异常一律捕获成 `IndexStatus.Failed` + 异常消息，不向上抛**。一个 MOD 的容器格式异常
  不该让整个冲突分析失败。
- **读不出索引时返回 `Partial` / `Encrypted`，绝不返回空清单**。空清单会被上游当成
  "这个包和谁都不冲突"，那是静默漏报；`Partial` 才会进入后续比对（有什么比什么）。

### 指纹怎么填

- 容器自带内容哈希（如 utoc 的 `ChunkHash`）→ 直接用，`FingerprintKind.ContentHash`。
- 只能读到数据区（如 pak）→ 填 `DataOffset` / `StoredSize` / `CompressionMethod`，
  由 `AssetIndexService.FillFingerprintsAsync` 去读字节算；它先比 `Size + StoredSize + CompressionMethod`，
  三者有任何不同就已断定内容不同，不必碰磁盘。
- 什么都读不出 → `FingerprintKind.None`，指纹留 `null`。**任一指纹为空时上游保守视为不同**
  （宁可报冲突，不可漏报），所以留空是安全的降级，不是错误。

---

## 4. 验证

1. `dotnet build UEModManager.sln -c Debug` 必须 0 警告 0 错误。
2. 给读取器写夹具测试：在内存里**写出**索引字节，让读取器**读回**。
   夹具是格式理解的第二份实现 —— 两边对格式的理解只要有一处不一致，路径就会拼错。
   `PakFixtureBuilder` / `UtocFixtureBuilder` 是可参考的例子。
3. 有真实样本时补一条 smoke 测试，文件不存在就直接 `return`（记为通过）：
   不要把仓库内容当成测试前提。见 `RealPakSmokeTests`。
4. 跑一遍全量：`dotnet test UEModManager.Core.Tests` 与 `dotnet test UEModManager.Tests`。

---

## 5. 不能碰的约定

重做冲突模块时踩过的坑，加新引擎时同样适用：

- **面向普通玩家，不出现"胜者""败者""覆盖规则""优先级"。** 一个冲突组只给一个决策，
  按钮上写 MOD 名。这些词是实现的词，用户不知道自己的 MOD 该叫胜者还是败者。
- **不显示"当前生效的是谁"。** 那取决于引擎的加载顺序，本程序不掌握，显示出来就是把猜测当事实。
- **不要在 XAML 里用水平 `StackPanel` 装可变长文本。** StackPanel 以**无限宽度**测量子元素，
  `TextTrimming` 永不生效，长 MOD 名会把卡片撑出面板宽度。用单行 `TextBlock` + `TextTrimming`，
  或两列 `Grid`（`Auto` + `*`）。
- **UI 事件处理器统一用 `Infrastructure/SafeEvent.Run` 包裹**，否则异常会在 WPF 里静默吞掉。
- **每个面向用户的中文文案都要在 `UEModManager/Localization/en-US.json` 补英文**，
  占位符 `{0}` 要对得上。`LanguageIntegrationTests` 会扫 XAML 里的 `loc:Text`，缺了会红。