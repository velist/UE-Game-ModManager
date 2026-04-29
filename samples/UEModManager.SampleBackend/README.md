# UEModManager.SampleBackend

UEModManager 自定义 `IDeploymentBackend` 的最小可独立编译示例。

## 用途

第三方贡献者新增一种"部署方式"（如 VFS / Junction / Mirror / DLL 注入）时，可以从这个项目复制起步：

1. 复制本目录到自己的 fork。
2. 重命名 csproj / namespace / 类名。
3. 在 `DeployFileAsync` / `RemoveFileAsync` 实现自定义逻辑。
4. 在 `CanUseAsync` 检测当前环境是否支持该后端。
5. 把编译产物（一个独立 dll）放到主程序对应目录，或合并回主项目并在 `App.xaml.cs` 注册。

## 编译

```bash
dotnet build samples/UEModManager.SampleBackend/UEModManager.SampleBackend.csproj
```

仅依赖 `UEModManager.Core`，无 WPF / Windows / 主项目耦合。

## 此 Sample 实现的语义

`SampleMirrorBackend` 演示一种"镜像复制 + 来源记号"模式：
- 主体文件普通复制。
- 在目标文件旁额外写一个 `{文件名}.uemm-source` 记号文件，
  记录源路径和部署时间，便于事后审计或第三方工具核对。
- 移除时连同记号文件一起清理。

实际项目中，把这套骨架替换为真正的部署语义即可（例如：
- 用 `Path.Combine` 算硬链接的目标
- 调用 `mklink /J` 建 junction
- 通过 P/Invoke 挂载 WinFsp
- 等等）。

## 相关文档

- [完整 Backend 写法说明](../../docs/playbooks/writing-deployment-backend.md)
- [架构总览](../../docs/architecture/overview.md)
- [SampleAdapter 示例](../UEModManager.SampleAdapter/) — 同模式的"自定义 Host Adapter"示例
