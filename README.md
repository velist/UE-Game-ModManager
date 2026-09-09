# 爱酱 MOD 管理器

<p align="center">
  <strong>简体中文</strong> &nbsp; · &nbsp; <a href="README.en.md">English</a>
</p>

<p align="center">
  <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/docs/assets/readme/hero.png" width="100%" alt="爱酱 MOD 管理器：多引擎适配，本地优先，离线可用">
</p>

<p align="center">
  <a href="https://www.modmanger.com/#download"><img src="https://img.shields.io/badge/Release-2.1.0-25cce1?style=flat-square&labelColor=15171b" alt="版本 2.1.0"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20%C2%B7%20x64-94b5c0?style=flat-square&labelColor=15171b" alt="Windows 10 / 11 x64">
  <img src="https://img.shields.io/badge/.NET-8%20Desktop-94b5c0?style=flat-square&labelColor=15171b" alt=".NET 8 桌面运行时">
  <a href="https://github.com/velist/UE-Game-ModManager/actions/workflows/ci.yml"><img src="https://github.com/velist/UE-Game-ModManager/actions/workflows/ci.yml/badge.svg?branch=main" alt="构建与测试"></a>
</p>

<p align="center">
  <a href="https://www.modmanger.com/#download"><strong>前往官网下载</strong></a>
  &nbsp; · &nbsp; <a href="https://www.modmanger.com/help">使用帮助</a>
  &nbsp; · &nbsp; <a href="https://github.com/velist/UE-Game-ModManager/issues">反馈问题</a>
  &nbsp; · &nbsp; <a href="#捐赠支持">捐赠支持</a>
  &nbsp; · &nbsp; <a href="Setup/bundled/Quickstart.en.md">English quick start</a>
</p>

把导入、分类、启用、方案和恢复放在一处的 **多引擎游戏 MOD 管理器**。MOD 文件与配置保存在本地，核心管理无需登录，可离线使用。

## 下载与开始

下载最新版本，请前往 **[官网 modmanger.com](https://www.modmanger.com/#download)**。

需要 **Windows 10 / 11 64 位环境**和 **[.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)**。安装向导会检查运行时，并在缺少时提供下载入口。

1. **选存储位置** — 首次启动选择 MOD 仓库，程序和 MOD 可以分开放。
2. **选游戏** — 使用内置预设或添加自定义游戏，核对游戏与 MOD 路径。
3. **导入并启用** — 拖入压缩包或 MOD 文件，确认导入结果，启用后启动游戏。

支持中英文界面。安装器可选择语言，首次启动的存储设置窗口、登录窗口以及 **设置 → 常规 → 语言** 都可切换，重启后保留选择。免安装版在未保存语言偏好时，中文系统使用中文，其他系统使用英文。

## 支持的游戏

目前内置 **10 款游戏、11 个预设入口**，剑星另有 CNS 模式。游戏列表可继续扩展，自定义游戏也能使用对应的引擎规则。

<table>
  <tr>
    <td align="center" width="33%">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/black-myth-wukong.png" width="52" height="52" alt=""><br>
      <strong>黑神话：悟空</strong><br>
      <sub>Black Myth: Wukong · Unreal Engine</sub>
    </td>
    <td align="center" width="33%">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/stellar-blade.png" width="52" height="52" alt=""><br>
      <strong>剑星</strong><br>
      <sub>Stellar Blade · Unreal Engine</sub>
    </td>
    <td align="center" width="33%">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/stellar-blade.png" width="52" height="52" alt=""><br>
      <strong>剑星 · CNS 模式</strong><br>
      <sub>Stellar Blade CNS · 独立预设</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/clair-obscur-expedition-33.png" width="52" height="52" alt=""><br>
      <strong>光与影：33号远征队</strong><br>
      <sub>Clair Obscur: Expedition 33 · Unreal Engine</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/wuchang-fallen-feathers.png" width="52" height="52" alt=""><br>
      <strong>明末：渊虚之羽</strong><br>
      <sub>Wuchang: Fallen Feathers · Unreal Engine</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/borderlands-4.png" width="52" height="52" alt=""><br>
      <strong>无主之地4</strong><br>
      <sub>Borderlands 4 · Unreal Engine</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/diablo-iv.png" width="52" height="52" alt=""><br>
      <strong>暗黑破坏神4</strong><br>
      <sub>Diablo IV · 暗黑 4 引擎</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/resident-evil-requiem.png" width="52" height="52" alt=""><br>
      <strong>生化危机9</strong><br>
      <sub>Resident Evil Requiem · RE Engine</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/pragmata.png" width="52" height="52" alt=""><br>
      <strong>识质存在</strong><br>
      <sub>PRAGMATA · RE Engine</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/death-stranding-2.png" width="52" height="52" alt=""><br>
      <strong>死亡搁浅2</strong><br>
      <sub>Death Stranding 2 · Decima</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/UEModManager/Assets/GameIcons/slay-the-spire-2.png" width="52" height="52" alt=""><br>
      <strong>杀戮尖塔2</strong><br>
      <sub>Slay the Spire 2 · Godot</sub>
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/website/assets/brand.png" width="52" height="52" alt=""><br>
      <strong>添加你的游戏</strong><br>
      <sub>选择引擎 · 设置路径 · 开始管理</sub>
    </td>
  </tr>
</table>

**自定义游戏可选引擎：** Unreal Engine、Unity、RE Engine、Godot、Decima、暗黑 4 引擎，以及通用规则。程序按引擎配置识别文件格式与路径；MOD 所需的加载器、前置组件和兼容版本请参照作者说明。

[游戏预设来源](UEModManager/Services/GameConfigService.cs) · [引擎规则](UEModManager/Models/EngineProfile.cs) · [游戏图标来源](UEModManager/Assets/GameIcons/SOURCES.md)

## 一处管理

| 导入与整理 | 启用与方案 | 记录与恢复 |
| --- | --- | --- |
| 导入 ZIP、解压后的文件夹及引擎支持的文件 | 单个或批量启用、禁用 MOD | 查看安装记录与恢复结果 |
| 分类、搜索、拖拽归档 | 为不同玩法保存 MOD 组合 | 操作中断后的事务恢复 |
| 独立选择 MOD 仓库位置 | 按规则检查覆盖关系与冲突 | 导出诊断包，辅助反馈问题 |

冲突检查依据文件与加载顺序规则，不解析 PAK 内部资源；具体能力随引擎规则而异。

RAR / 7z 请先使用 WinRAR 或 7-Zip 解压，再导入解压后的文件夹或 MOD 文件。

## 界面预览

<table>
  <tr>
    <td width="53%" valign="top">
      <strong>安装向导</strong><br><br>
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/docs/assets/readme/installer.png" width="100%" alt="新版深色安装向导，显示运行时状态并提供帮助入口">
    </td>
    <td width="47%" valign="top">
      <strong>首次启动 · MOD 存储设置</strong><br><br>
      <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/website/assets/storage-setup.webp" width="100%" alt="选择 MOD 仓库所在磁盘，查看可用空间并确认存储位置">
    </td>
  </tr>
</table>

<details>
<summary><strong>本地数据、升级与隐私</strong></summary>

### 数据放在哪里？

| 内容 | 默认位置 |
| --- | --- |
| 游戏配置与索引 | `%LOCALAPPDATA%\UEModManager\` |
| 界面偏好与账号数据 | `%APPDATA%\UEModManager\` |
| MOD 仓库 | `%LOCALAPPDATA%\UEModManager\Repository\`，可自定义 |
| 事务备份 | `%LOCALAPPDATA%\UEModManager\Backups\Deployments\`，可自定义 |
| 日志 | `%LOCALAPPDATA%\UEModManager\Logs\` |

以设置中显示的实际位置为准。安装版和解压运行版均使用当前 Windows 用户的数据目录。升级前关闭管理器；普通卸载保留用户数据、仓库和备份。换电脑时请分别备份两个用户数据目录及实际使用的仓库、生成物和备份目录。

### 联网功能

支持通过邮箱登录账号，核心 MOD 管理也可离线使用。账号功能目前不提供 MOD 文件或方案的跨设备云同步。

同意参与“检查更新与匿名统计”后，程序发送随机设备编号、软件版本与 Windows 版本；登录时可能附带邮箱哈希。统计不上传邮箱明文、电脑名、文件路径、游戏目录或 MOD 清单。可在“设置 → 常规参数”中关闭，关闭后停止对应的更新检查和统计请求。

登录、注册、找回密码等账号操作会分别发送其所需的账号信息。完整说明见[官网隐私说明](https://www.modmanger.com/help#privacy)及[使用许可](Setup/LICENSE.txt)。

</details>

<details>
<summary><strong>从源码构建与参与开发</strong></summary>

需要 Windows、.NET 8 SDK；Workers 测试需要 Node.js 22 或以上。

```powershell
dotnet build UEModManager.sln --configuration Release
dotnet test UEModManager.sln --configuration Release
```

构建安装包需要 Inno Setup 6.7 或以上，可传入安装目录、`Compil32.exe` 或 `ISCC.exe`：

```powershell
./Build-Installer.ps1 -Configuration Release -InnoPath 'D:/安装/Compil32.exe'
```

每次使用全新的 publish 目录，打包前检查版本与文件内容。更多信息见[文档索引](docs/README.md)、[架构总览](docs/architecture/overview.md)和[更新日志](CHANGELOG.md)。

欢迎通过 [Issues](https://github.com/velist/UE-Game-ModManager/issues) 反馈问题或建议，通过 Pull Request 参与改进。反馈时请附版本、复现步骤和错误信息，发送诊断包前先检查包内内容。

</details>

## 捐赠支持

如果这个工具对你有帮助，欢迎通过微信或支付宝扫码支持后续开发与维护。感谢每一份支持。

| 微信支付 | 支付宝 |
| :---: | :---: |
| <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/捐赠支持/微信支付.jpg" alt="微信支付收款码" width="240"> | <img src="https://raw.githubusercontent.com/velist/UE-Game-ModManager/main/捐赠支持/支付宝.jpg" alt="支付宝收款码" width="240"> |

---

<p align="center">
  爱酱工作室 · <a href="https://www.modmanger.com">modmanger.com</a><br>
  <a href="Setup/LICENSE.txt">使用许可</a> · <a href="https://github.com/velist/UE-Game-ModManager/issues">问题反馈</a> · mr.xzuo@foxmail.com
</p>
