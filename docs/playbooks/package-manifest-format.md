# Package 仓库与 manifest.json 格式

**适用：** 想用外部工具（脚本/CI/其他应用）生成或读取 UEModManager 仓库中的包

---

## 仓库目录结构

```
%APPDATA%/UEModManager/Repository/
└── {packageKey}/                        # 每个包一个独立目录，名字 = PackageKey（大小写敏感）
    ├── manifest.json                    # 包元数据 + 文件清单（必需）
    ├── files/                           # 实际文件（按 RelativeSourcePath 组织）
    │   ├── foo.pak
    │   ├── subdir/
    │   │   └── bar.pak
    │   └── ...
    └── preview.png                      # 预览图（可选，文件名在 manifest 里指定）
```

**注意：**
- 仓库根可由用户在设置中改（默认 `%APPDATA%/UEModManager/Repository/`）
- 主项目通过 `ObjectStore.RepositoryRoot` 暴露当前根

---

## manifest.json schema (v1)

```jsonc
{
  "manifestVersion": 1,                  // 当前固定为 1
  "packageId": "8a9b1c2d-...",          // GUID，主键
  "packageKey": "MyMod",                 // 唯一标识（人类可读）
  "displayName": "我的 MOD",
  "kind": 0,                             // 0=Mod  1=Plugin  2=Config（PackageKind 枚举值）
  "version": "1.0.0",
  "tags": ["UI", "Quality of Life"],     // 用户自定义标签
  "note": "重做 UI 颜色",                 // 用户备注（可空）
  "previewFileName": "preview.png",      // 相对于包目录（可空）
  "contentHash": "a1b2c3...",            // SHA-256 前 16 字符（可空，去重用）
  "totalSize": 1234567,                  // 文件总字节数
  "hostGameName": "Stellar Blade",
  "pluginTargetPath": null,              // Plugin 类型时为目标根目录（如 "Engine/Plugins"）
  "importSourcePath": "/some/zip.zip",   // 原始导入文件路径（可空）
  "importedAt": "2026-04-28T10:00:00",
  "lastModified": "2026-04-28T11:00:00",
  "artifacts": [
    {
      "fileName": "foo.pak",
      "relativeSourcePath": "foo.pak",        // 相对于 files/ 目录
      "relativeTargetPath": "foo.pak",        // 部署到游戏目录的相对路径
      "fileSize": 1024000,
      "fileHash": "abc123...",                // SHA-256 前 16 字符（可空）
      "artifactType": 0                       // 0=ModFile 1=PluginFile 2=ConfigFile 3=PreviewImage 4=Other
    },
    ...
  ]
}
```

---

## 字段语义

| 字段 | 必需 | 说明 |
|------|------|------|
| `manifestVersion` | ✅ | 当前 v1，未来加新字段时升 v2，老版本读 v2 应 fallback |
| `packageId` | ✅ | GUID，应用内主键（导入时分配） |
| `packageKey` | ✅ | 唯一字符串，文件夹名，跨方案共享 |
| `displayName` | ✅ | UI 展示名 |
| `kind` | ✅ | 包类型枚举 |
| `version` | ⚠ | 推荐填，便于 lock 文件版本对比 |
| `contentHash` | ⚠ | 推荐填，整合包导入时校验 |
| `totalSize` | ✅ | 字节数（应用启动时统计用） |
| `hostGameName` | ✅ | 该包属于哪款游戏（多游戏隔离） |
| `pluginTargetPath` | 仅 Plugin | 部署到 `gameRoot/{pluginTargetPath}/{packageKey}/` |
| `artifacts` | ✅ | 文件清单 |

---

## 枚举值

### `PackageKind`
| 值 | 名称 | 用途 |
|----|------|------|
| 0 | Mod | 普通 MOD（.pak/.ucas/.utoc 等）|
| 1 | Plugin | DLL/插件 |
| 2 | Config | 配置文件（.ini/.json 等）|

### `ArtifactType`
| 值 | 名称 |
|----|------|
| 0 | ModFile |
| 1 | PluginFile |
| 2 | ConfigFile |
| 3 | PreviewImage |
| 4 | Other |

---

## 部署路径计算

应用计算 artifact 在游戏目录中的位置：

**Mod / Config 包：**
```
{游戏 MOD 根}/{packageKey}/{relativeTargetPath}
```

**Plugin 包：**
```
{游戏根目录}/{pluginTargetPath}/{packageKey}/{relativeTargetPath}
```

参见 `DeploymentPlanner.ComputeTargetPath` / `ConflictDetector.ComputeTargetPath`。
**所有包都自带一层 `{packageKey}` 子目录隔离**，因此外部工具生成 `relativeTargetPath` 时
不应自己加包名前缀。

---

## 外部工具最小生成示例（Python）

```python
import hashlib
import json
import os
import shutil
import uuid
from pathlib import Path

def build_repo_package(
    src_dir: Path,        # 源 MOD 文件目录
    repo_root: Path,      # UEModManager Repository 根
    package_key: str,
    display_name: str,
    host_game: str,
    kind: int = 0,        # 0=Mod
):
    pkg_dir = repo_root / package_key
    files_dir = pkg_dir / "files"
    files_dir.mkdir(parents=True, exist_ok=True)

    artifacts = []
    total_size = 0
    sha = hashlib.sha256()

    for path in sorted(src_dir.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(src_dir).as_posix()
        dst = files_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, dst)

        size = dst.stat().st_size
        h = hashlib.sha256(dst.read_bytes()).hexdigest()[:16]
        sha.update(dst.read_bytes())

        artifacts.append({
            "fileName": dst.name,
            "relativeSourcePath": rel,
            "relativeTargetPath": rel,
            "fileSize": size,
            "fileHash": h,
            "artifactType": 0,
        })
        total_size += size

    manifest = {
        "manifestVersion": 1,
        "packageId": str(uuid.uuid4()),
        "packageKey": package_key,
        "displayName": display_name,
        "kind": kind,
        "version": "1.0.0",
        "tags": [],
        "contentHash": sha.hexdigest()[:16],
        "totalSize": total_size,
        "hostGameName": host_game,
        "importedAt": "2026-04-28T00:00:00",
        "lastModified": "2026-04-28T00:00:00",
        "artifacts": artifacts,
    }

    (pkg_dir / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

build_repo_package(
    src_dir=Path("/path/to/my/mod/files"),
    repo_root=Path.home() / "AppData/Roaming/UEModManager/Repository",
    package_key="MyMod",
    display_name="我的 MOD",
    host_game="Stellar Blade",
)
```

生成后启动 UEModManager，包会被自动扫描出来（或在管理中心 → MOD 库 → "检查缺失文件"触发刷新）。

---

## 完整性检查

应用提供"检查缺失文件"功能：
- 比对 `manifest.json` 中的 `artifacts` 与 `files/` 实际内容
- 报告缺失文件、孤立文件、哈希不一致

代码：`PackageRepository.CheckIntegrityAsync()`。

---

## 反模式

- ❌ 把 `relativeTargetPath` 写成绝对路径或包含 `{packageKey}` 子目录 —— 应用会自动加
- ❌ `packageKey` 含特殊字符（如 `/`、`?`、`*`）—— 文件夹名兼容性问题
- ❌ 多个包用同一 `packageKey` —— 应用按 key 唯一性管理，会冲突
- ❌ 改 `manifestVersion` 但没添加新字段处理逻辑 —— 当前版本只支持 v1
