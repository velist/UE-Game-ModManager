# 更新日志 / Changelog

本文档记录UE Mod Manager的所有版本更新内容。

---

## [v1.7.38] - 2025-10-07

### 🔧 重要修复 / Critical Fixes

#### Brevo SMTP认证失败问题
- **问题**: 邮件验证码发送失败，用户无法注册或使用邮箱登录
- **原因**: Brevo使用系统分配的专用SMTP登录账户，配置文件中的"apikey"只是占位符
- **解决**: 智能检测"apikey"占位符并自动替换为正确的SMTP账户 (984a39001@smtp-brevo.com)
- **影响**: 修复后邮件发送功能恢复正常，支持API和SMTP双通道fallback

#### 分类配置文件路径错误
- **问题**: 分类重命名、删除、拖拽排序功能失效
- **原因**: 配置文件路径错误导致无法正确保存分类数据
- **解决**: 修正配置文件路径逻辑
- **影响**: 分类管理功能完全恢复

### ✨ 新功能 / New Features

- **点击头像打开账户设置**: 统一用户体验，头像点击与账户设置按钮功能一致
- **移除重复菜单项**: 删除"更换头像"菜单项，避免功能重复

### ⚡ 性能优化 / Performance Improvements

- **API超时优化**: Brevo API超时从30秒减少到10秒，快速fallback到SMTP
- **SMTP参数验证**: 构造函数增加邮箱格式验证，提前发现配置错误
- **详细日志输出**: 增强SMTP认证调试日志，便于问题排查

### 🙏 鸣谢 / Thanks

- 新增捐赠感谢：**枪王**

### 📝 技术细节 / Technical Details

**修改文件**:
- `App.xaml.cs`: 智能SMTP_LOGIN修正逻辑
- `BrevoEmailService.cs`: 增强构造函数日志和参数验证
- `BrevoApiEmailService.cs`: 减少超时时间
- `MainWindow.xaml.cs`: 头像点击事件统一
- `installer_clean.iss`: 版本号更新到1.7.38

**新增文件**:
- `故障排查_Brevo_SMTP认证问题.md`: 详细的问题排查文档
- `UEModManager/Security/SecretFileProtector.cs`: 配置文件加密保护

---

## [v1.7.37] - 2025-10-04

### 🔧 修复 / Fixes

- 修复分类配置文件路径问题
- 修复MOD重命名、删除、排序功能
- 优化邮件发送服务稳定性

### ✨ 新功能 / New Features

- 为使用说明文档添加完整的中英文双语支持
- 增强网站SEO自动化
- 完善项目文档结构

### 🙏 鸣谢 / Thanks

- 捐赠感谢：胖虎、YUki、Tarnished、春告鳥、蘭、虎子、神秘不保底男、文铭、阪、林墨、Daisuke、虎子哥
- 特别感谢：爱酱游戏群全体群友

---

## [v1.7.36] - 2025-10-02

### ✨ 新功能 / New Features

- 集成Cloudflare Workers API网关
- 实现云端认证服务
- 添加离线模式支持
- 完善用户认证流程

### 🔧 优化 / Improvements

- 重构项目结构，清理冗余文件
- 优化数据库连接管理
- 增强错误处理机制

---

## [v1.7.35] - 2025-09-30

### 🎮 游戏支持 / Game Support

- 支持剑星/Stellar Blade MOD管理
- 支持黑神话悟空 MOD管理
- 支持明末无双 MOD管理

### ✨ 核心功能 / Core Features

- MOD自动备份与恢复
- MOD分类管理系统
- 批量操作支持
- 云端同步功能

---

## 版本说明 / Version Notes

### 版本号格式
- **主版本号**: 重大功能变更或架构升级
- **次版本号**: 新功能添加或重要修复
- **修订号**: Bug修复和小幅优化

### 更新频率
- **稳定版**: 每月1-2次重大更新
- **修复版**: 根据问题严重程度随时发布
- **测试版**: 内部测试，不公开发布

---

## 贡献者 / Contributors

感谢所有为本项目做出贡献的开发者和用户！

### 核心开发
- **主要维护者**: 爱酱工作室

### 技术支持
- AI助手 (Claude Code)

### 社区贡献
- 所有提交Bug报告的用户
- 所有提供功能建议的用户
- 所有捐赠支持的用户

---

## 支持与反馈 / Support

- **项目主页**: https://github.com/velist/UE-Game-ModManager
- **官方网站**: https://www.modmanger.com
- **问题反馈**: GitHub Issues
- **技术支持**: mr.xzuo@foxmail.com

---

**最后更新**: 2025-10-07
**当前版本**: v1.7.38
