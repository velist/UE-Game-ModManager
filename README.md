# 爱酱MOD管理器

<div align="center">

**多引擎游戏 MOD 管理工具 — 本地优先，离线可用**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey.svg)](https://www.microsoft.com/windows)
[![Version](https://img.shields.io/badge/version-2.0.4--beta-orange.svg)](https://github.com/velist/UE-Game-ModManager/releases)

</div>

---

## 📖 简介

爱酱MOD管理器是一款面向多引擎游戏的 MOD/插件管理工具，提供简单易用的界面和强大的功能，让您轻松管理游戏 MOD —— 不限于虚幻引擎，也不只服务 MOD 文件。

### ✨ 核心功能

- 🎮 **多游戏支持** — 剑星、黑神话悟空、明末无双、无主之地 4 等多款游戏
- ☁️ **云端同步** — 登录后 MOD 配置云端保存，多设备无缝同步
- 💾 **本地离线** — 无需网络也能完整使用，核心功能不依赖云端
- 🔄 **事务回滚** — 部署计划 + 执行事务 + 异常恢复，出错可回滚
- 📦 **批量操作** — 支持 MOD 批量启用 / 禁用 / 删除
- 🗂️ **分类管理** — 自定义分类 + 拖拽归类，让 MOD 井井有条
- 🔍 **智能搜索** — 快速找到想要的 MOD
- 📋 **诊断导出** — 一键导出日志和数据快照，方便反馈问题

---

## 🎯 支持的游戏

| 游戏名称 | 英文名称 | 状态 |
|---------|---------|------|
| 剑星 | Stellar Blade | ✅ 完整支持 |
| 黑神话悟空 | Black Myth: Wukong | ✅ 完整支持 |
| 明末：渡鸦之乱 | Wuchang: Fallen Feathers | ✅ 完整支持 |
| 无主之地 4 | Borderlands 4 | ✅ 完整支持 |
| 巢栖之地 | Enshrouded | ⚠️ 实验性支持 |

持续扩展更多游戏适配中。

---

## 📥 安装

### 系统要求

- **操作系统：** Windows 10 / Windows 11
- **.NET 运行时：** .NET 8.0 Desktop Runtime
- **磁盘空间：** 至少 100 MB

### 安装步骤

1. **下载安装包**
   — 从 [下载页面](https://www.modmanger.com/) 获取最新版本

2. **运行安装程序**
   — 双击安装包，按向导完成安装

3. **首次运行**
   — 启动程序 → 选择游戏目录 → 开始管理 MOD！

---

## 🚀 快速开始

### 1. 选择游戏

首次启动时，从游戏列表中选择您要管理的游戏。如果未在列表中，可点击"自定义游戏"手动添加。

### 2. 扫描 MOD

程序会自动扫描游戏目录下的 MOD 文件，扫描完成后所有 MOD 显示在列表中。

### 3. 管理 MOD

- **启用 MOD：** 勾选 MOD 名称前的复选框
- **禁用 MOD：** 取消勾选
- **删除 MOD：** 右键点击 → 删除
- **查看详情：** 点击 MOD 名称查看详细信息

### 4. 备份与恢复

- **自动备份：** 启用 MOD 时自动备份原文件
- **手动备份：** 点击"备份所有文件"
- **恢复备份：** 右键点击 MOD → 恢复备份

---

## 💡 高级功能

### 云端同步

1. **注册账号** → 点击右上角"未登录" → 选择"注册" → 输入邮箱和密码 → 验证邮箱
2. **登录账号** → 输入邮箱和密码，勾选"记住我"可自动登录
3. **同步数据** → 登录后 MOD 配置自动云端保存，多设备同步

### 分类管理

1. **创建分类** → 右键左侧分类列表空白处 → "新建分类" → 输入名称
2. **移动 MOD** → 右键 MOD → "移动到分类" → 选择目标分类

### 批量操作

- **全选 MOD：** 点击列表顶部"全选"按钮
- **批量启用/禁用：** 选中多个 MOD → 工具栏"批量启用"/"批量禁用"
- **批量删除：** 选中多个 MOD → "批量删除"

### 剑星专属功能

- **CNS 模式分组** — 自动识别并分组 CNS 格式的 MOD
- **pak 签名修复** — 修复被签名检查拦截的 MOD
- **模组优先级调整** — 调整 MOD 加载顺序

---

## 💝 开发支持

如果觉得好用，可以请我喝一杯咖啡支持开发哦~

| 微信支付 | 支付宝 |
| :---: | :---: |
| <img src="https://github.com/velist/UE-Game-ModManager/blob/main/%E6%8D%90%E8%B5%A0%E6%94%AF%E6%8C%81/%E5%BE%AE%E4%BF%A1%E6%94%AF%E4%BB%98.jpg" alt="微信支付" width="200"> | <img src="https://github.com/velist/UE-Game-ModManager/blob/main/%E6%8D%90%E8%B5%A0%E6%94%AF%E6%8C%81/%E6%94%AF%E4%BB%98%E5%AE%9D.jpg" alt="支付宝" width="200"> |

---

## ❓ 常见问题

### Q: 安装 MOD 后游戏崩溃怎么办？

**A:** 尝试：
1. 禁用最近安装的 MOD
2. 恢复备份文件
3. 检查 MOD 是否与游戏版本兼容
4. 查看 MOD 是否与其他 MOD 冲突

### Q: MOD 没有生效？

**A:** 检查：
1. MOD 已启用（勾选框已勾选）
2. 游戏路径设置正确
3. 重启游戏
4. 查看 MOD 安装说明

### Q: 如何卸载程序？

**A:** Windows "设置" → "应用" → "UE Mod Manager" → "卸载"

> ⚠️ 卸载前建议先恢复所有 MOD 备份

### Q: 忘记密码怎么办？

**A:** 登录窗口 → "忘记密码" → 输入注册邮箱 → 查收邮件 → 点击链接重置

### Q: 支持 Steam Deck 吗？

**A:** 目前仅支持 Windows 平台，Linux / Steam Deck 支持正在开发中。

### Q: 数据存储在哪里？

**A:**
- 本地数据库：`%APPDATA%\UEModManager\local.db`
- 配置文件：`%APPDATA%\UEModManager\auth_config.json`
- 备份文件：`<程序目录>\Backups\`

---

## 🛡️ 安全与隐私

- ✅ 所有密码使用加密存储，我们无法查看您的密码
- ✅ 邮箱仅用于账号认证和找回密码
- ✅ 不收集任何游戏数据或个人文件
- ✅ 本地数据库仅存储在您的设备上
- ✅ 云端仅保存 MOD 配置信息（不包含 MOD 文件）

### 开源透明

本项目完全开源，您可以：
- 查看所有源代码
- 审查数据处理流程
- 提交改进建议
- 自行编译使用

---

## 🤝 参与贡献

欢迎各种形式的贡献！

### 报告问题

发现 Bug？请通过 [GitHub Issues](https://github.com/velist/UE-Game-ModManager/issues) 报告。

**报告时请包含：** 问题描述 / 复现步骤 / 操作系统版本 / 程序版本 / 日志文件（如有）

### 功能建议

有好的想法？通过 Issues 告诉我们！

### 代码贡献

1. Fork 本仓库
2. 创建功能分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 提交 Pull Request

详细开发指南请查看 [CLAUDE.md](CLAUDE.md)

---

## 📞 联系我们

- **技术支持：** [GitHub Issues](https://github.com/velist/UE-Game-ModManager/issues)
- **电子邮件：** mr.xzuo@foxmail.com
- **项目主页：** [GitHub](https://github.com/velist/UE-Game-ModManager)

**如果这个项目对您有帮助，请给我们一个 ⭐ Star！**

---

<div align="center">

Made with ❤️ by 爱酱工作室

[主页](https://github.com/velist/UE-Game-ModManager) ·
[文档](CLAUDE.md) ·
[问题反馈](https://github.com/velist/UE-Game-ModManager/issues) ·
[更新日志](CHANGELOG.md)

</div>