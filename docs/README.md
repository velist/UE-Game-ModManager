# 开发文档

产品介绍与使用说明见[中文 README](../README.md)、[English README](../README.en.md)和[官网帮助](https://www.modmanger.com/help)。安装包请从[官网下载](https://www.modmanger.com/#download)。

## 项目与扩展

- [架构总览](architecture/overview.md)：项目分层、数据与部署边界、启动流程，以及新增游戏的位置。
- [编写部署后端](playbooks/writing-deployment-backend.md)：`IDeploymentBackend` 接口、注册方式和实现示例。
- [编写 Core 服务](playbooks/writing-core-service.md)：领域逻辑、输入输出与测试约定。
- [Package 仓库与 manifest 格式](playbooks/package-manifest-format.md)：包目录、元数据和外部工具兼容要求。
- [部署后端示例](../samples/UEModManager.SampleBackend/README.md)：仅引用 Core、可独立构建的示例项目。
- [账号与 Worker 协议](../cf-workers/modmanger-api/AUTH_PROTOCOL.md)：邮箱验证码、部署绑定与桌面客户端契约。
- [官网维护](../website/README.md)：本地预览、资源检查、SEO 与发布流程。

新增游戏通常只需维护游戏预设与引擎规则，具体入口见[架构总览](architecture/overview.md)。

## 本地构建

桌面项目需要 Windows 和 .NET 8 SDK。在仓库根目录运行：

```powershell
dotnet build UEModManager.sln --configuration Release
```

构建安装包另需 Inno Setup 6.7 或以上。脚本会查找默认安装目录，也可通过 `-InnoPath` 指定 Inno Setup 目录、`Compil32.exe` 或 `ISCC.exe`：

```powershell
./Build-Installer.ps1 -Configuration Release
```

安装包输出到 `installer_output/`。构建脚本使用全新的 publish 目录，并检查版本、凭据文件和用户数据，避免把本机状态打入安装包。

## 验证改动

桌面测试：

```powershell
dotnet test UEModManager.sln --configuration Release
```

Worker 测试需要 Node.js 22 或以上；桌面协议检查还需要 Windows 和 .NET 8 SDK：

```powershell
cd cf-workers/modmanger-api
npm ci
npm test
node test/desktop-contract.mjs
npm run check:bundle
```

官网检查在仓库根目录运行：

```powershell
node tools/website/check.mjs
```

持续集成配置见 [CI 工作流](../.github/workflows/ci.yml)，运行结果见 [GitHub Actions](https://github.com/velist/UE-Game-ModManager/actions)。测试中的外部服务使用替身，线上认证和邮件投递需在相应部署环境验证。

## 提交内容

仓库保留源码、测试、运行所需资源、构建工具与开发文档。构建产物、本机配置、环境变量、数据库、诊断日志和内部审计记录不提交；版本变化见[更新日志](../CHANGELOG.md)。
