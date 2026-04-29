using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// 文件复制部署后端。
    /// 最安全的后端，通过文件复制将包部署到游戏目录。
    /// 无跨卷限制，无权限要求。
    /// </summary>
    public class CopyBackend : IDeploymentBackend
    {
        private readonly ILogger<CopyBackend> _logger;

        public CopyBackend(ILogger<CopyBackend> logger)
        {
            _logger = logger;
        }

        public DeploymentBackendType Type => DeploymentBackendType.Copy;
        public string DisplayName => "文件复制";

        public Task<bool> CanUseAsync() => Task.FromResult(true);

        public Task DeployFileAsync(string sourcePath, string targetPath)
        {
            return Task.Run(() =>
            {
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                File.Copy(sourcePath, targetPath, overwrite: true);
                _logger.LogDebug("复制部署: {Source} → {Target}", sourcePath, targetPath);
            });
        }

        public Task RemoveFileAsync(string targetPath)
        {
            return Task.Run(() =>
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    _logger.LogDebug("移除文件: {Path}", targetPath);
                }

                // 清理空目录
                var dir = Path.GetDirectoryName(targetPath);
                CleanEmptyDirectories(dir);
            });
        }

        private static void CleanEmptyDirectories(string? directory)
        {
            while (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                if (Directory.GetFileSystemEntries(directory).Length > 0)
                    break;
                try
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
