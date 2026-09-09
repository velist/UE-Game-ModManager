# 2.1.0 Release 与安装向导更新

2026-09-09，源工作区 `D:/modmangerpd/codex-fix-build`，版本真源为 `UEModManager/Properties/VersionAssemblyInfo.cs`。沿用 2.1.0，没有提升版本号。

## 构建

使用 .NET SDK 8.0.416 与 Inno Setup 6.7.1。用户提供的 `D:/安装/Compil32.exe` 会自动解析到同目录 `ISCC.exe`，脚本也接受目录或 ISCC 路径。

```powershell
./Build-Installer.ps1 -Configuration Release -Version 2.1.0 `
  -InnoPath 'D:/安装/Compil32.exe' `
  -PublishDirectory 'installer_output/UEModManager_v2.1.0_win-x64'
```

显式 PublishDirectory 必须不存在，每次构建使用全新 publish 目录。版本未传入时从 AssemblyInformationalVersion 读取；打包前校验实际 FileVersion，并拒绝凭据和私人运行数据。目标为 win-x64，依赖 .NET 8 Desktop Runtime，不内置运行时。构建脚本保持 ASCII，兼容 Windows PowerShell 5.1。

## 安装界面与行为

`Setup/UEModManager.iss` 使用 Inno 6.7 原生暗色 Windows 11 样式。`InstallerUi.iss` 集中处理布局、运行时检测、帮助入口与启动选项。欢迎和完成页使用品牌侧图，移除旧 InfoBefore / InfoAfter 页面、三页教程、Quick Launch 选项及默认打开捐赠图片的行为。

三张 PNG 由 `Setup/New-InstallerArtwork.ps1` 基于已有品牌图标生成并纳入源码。生成器使用 Windows 自带 .NET Framework / System.Drawing，不需要额外图像依赖。

桌面快捷方式默认开启，自启动默认关闭；自定义选择在原生任务列表初始化后同步，并保存用于下次升级。静默安装沿用 Inno 的 /TASKS 语义。取消自启动会删除软件自身的 Run 值。AppId、最低权限安装和保留用户数据的卸载方式保持兼容。

更新 LICENSE 和随包说明中的旧版本、虚构 MOD 云同步、旧数据路径以及不准确的完整备份说明。官网及网盘内容未在本轮调整。

## Release 检查发现的修复

首次完整测试发现新版 RepositorySetupWindow 与既有主题守卫不一致。将存储设置窗口颜色集中为 CyberDarkTheme 资源，保持色值不变；守卫同时识别全局主题和窗口局部资源。同步主题资源快照，窗口业务测试显式加载同一主题字典。

## 验证证据

- `bin/release-20260909/build-release-final.log`：0 警告、0 错误。
- `bin/release-20260909/test-release-final.log`：Core 1,211 通过，应用 588 通过，手动快照生成器跳过 1 项。
- `bin/release-20260909/build-installer-final.log`：完整构建脚本成功；随后的 `compile-installer-final.log` 纳入文档末尾空行整理。
- `bin/release-20260909/installer-qa/`：使用独立 AppId、程序名、启动项和安装目录验证；额外显示准备页以核对界面选项，未在该页执行 UI 安装。
- `bin/release-20260909/installer-smoke-final.log`：通过命令行执行隔离安装、覆盖安装与卸载，20 项检查通过。验证全部发布文件哈希、版本、图标、字体许可、桌面快捷方式、自启动开启/关闭，以及保留非安装器创建的数据。
- `C:/Users/a/Documents/Codex/2026-09-09/modmanger-release/installer-welcome.png`：最终安装包实际欢迎页截图。

安装器的普通身份在最终欢迎页预览；实际安装测试使用隔离身份，避免覆盖当前用户的已有安装。没有运行用户数据清理脚本，也没有清除现有配置。

## 最终文件

| 文件 | 字节数 | SHA256 |
|---|---:|---|
| UEModManager_v2.1.0_Setup.exe | 46546344 | 01677eb0af8d660be9bb29b0570dbccc8583db77c820652f4e6bf122e1cb6c41 |
| UEModManager_v2.1.0_win-x64.zip | 56835137 | e3e318a1e927b369b3a4df46dfbbbbe24d1abae5804f201f39574c405778a1b9 |

位于 `installer_output/`。Release ZIP 含 137 个条目，主程序与保留的 Release 文件夹一致；包内不含私人配置、数据库、日志、密钥或 PDB。
