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

                // 先复制到同目录的临时文件，再用 Move 换上去。
                // 不能直接 File.Copy(overwrite: true)：Win32 的 CREATE_ALWAYS 会原地截断并改写
                // 既有文件；若目标是此前硬链接模式留下的链接，写入就穿过去改掉了仓库里的
                // MOD 源文件（之后该包哈希对不上，再也部署不了）。Move 替换的是目录项，
                // 不碰旧数据；同目录同卷，目标位置要么是旧文件、要么是完整的新文件。
                var tempPath = targetPath + ".uemm-" + System.Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(sourcePath, tempPath, overwrite: false);
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); }
                        catch (IOException ex) { _logger.LogWarning(ex, "清理复制临时文件失败: {Path}", tempPath); }
                        catch (System.UnauthorizedAccessException ex) { _logger.LogWarning(ex, "清理复制临时文件失败: {Path}", tempPath); }
                    }
                }
                _logger.LogDebug("复制部署: {Source} → {Target}", sourcePath, targetPath);
            });
        }

        /// <summary>
        /// 移除已部署的文件。
        /// 空目录清理由 DeploymentService 统一负责——后端只拿到 targetPath，
        /// 没有部署根，无法判断向上删到哪一层才算越界。
        /// </summary>
        public Task RemoveFileAsync(string targetPath)
        {
            return Task.Run(() =>
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    _logger.LogDebug("移除文件: {Path}", targetPath);
                }
            });
        }
    }
}
