using System;
using System.IO;
using System.Threading.Tasks;
using UEModManager.Models;
using UEModManager.Services.Backends;

namespace UEModManager.Backends.Sample
{
    /// <summary>
    /// 示例 Deployment Backend — 演示一种"镜像复制 + 来源记号"的部署后端。
    ///
    /// 与内置 <c>CopyBackend</c> 的区别：
    /// - 部署成功后在目标文件旁写一个 <c>{文件名}.uemm-source</c> 记号文件，
    ///   记录源路径，便于事后审计或第三方工具核对。
    /// - 移除时连同记号文件一起清理。
    ///
    /// 这是一个**可独立编译**的最小完整示例：
    /// - 仅依赖 UEModManager.Core（无 WPF / 主项目耦合）
    /// - 实现完整的 IDeploymentBackend 契约
    /// - 可作为第三方贡献者新增部署方式的起点
    ///
    /// 如何用：
    /// 1. 复制本项目到自己的 fork，重命名 namespace / 类名
    /// 2. 在 <see cref="DeployFileAsync"/> / <see cref="RemoveFileAsync"/> 实现自定义部署逻辑
    ///    （硬链接 / 符号链接 / VFS Mount / DLL 注入 / 镜像复制 等）
    /// 3. 在 <see cref="CanUseAsync"/> 检测当前环境是否能跑此后端
    /// 4. 把编译产物 dll 放到主程序对应目录，或合并回主项目并在 App.xaml.cs 注册
    ///
    /// 完整说明：docs/playbooks/writing-deployment-backend.md
    /// </summary>
    public class SampleMirrorBackend : IDeploymentBackend
    {
        private const string SourceMarkerSuffix = ".uemm-source";

        public DeploymentBackendType Type => DeploymentBackendType.Copy;

        public string DisplayName => "示例镜像复制后端（带来源记号）";

        public Task<bool> CanUseAsync()
        {
            // 此后端任何环境都能用（仅做文件复制 + 写一个文本文件）
            return Task.FromResult(true);
        }

        public async Task DeployFileAsync(string sourcePath, string targetPath)
        {
            if (string.IsNullOrEmpty(sourcePath)) throw new ArgumentException("源路径不能为空", nameof(sourcePath));
            if (string.IsNullOrEmpty(targetPath)) throw new ArgumentException("目标路径不能为空", nameof(targetPath));
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("源文件不存在", sourcePath);

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 1. 主体文件复制
            await CopyAsync(sourcePath, targetPath);

            // 2. 写来源记号
            var marker = targetPath + SourceMarkerSuffix;
            await File.WriteAllTextAsync(marker,
                $"deployed-by: UEModManager.SampleBackend{Environment.NewLine}" +
                $"source: {sourcePath}{Environment.NewLine}" +
                $"deployed-at: {DateTime.UtcNow:O}{Environment.NewLine}");
        }

        public Task RemoveFileAsync(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath)) return Task.CompletedTask;

            // 1. 删主体
            if (File.Exists(targetPath)) File.Delete(targetPath);

            // 2. 删记号
            var marker = targetPath + SourceMarkerSuffix;
            if (File.Exists(marker)) File.Delete(marker);

            return Task.CompletedTask;
        }

        private static async Task CopyAsync(string source, string target)
        {
            const int bufferSize = 81920;
            await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize, useAsync: true);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize, useAsync: true);
            await src.CopyToAsync(dst);
        }
    }
}
