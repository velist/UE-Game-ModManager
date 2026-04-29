using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// 符号链接部署后端。
    /// 无跨卷限制，但 Windows 上需要管理员权限或开发者模式。
    /// </summary>
    public class SymlinkBackend : IDeploymentBackend
    {
        private readonly ILogger<SymlinkBackend> _logger;

        public SymlinkBackend(ILogger<SymlinkBackend> logger)
        {
            _logger = logger;
        }

        public DeploymentBackendType Type => DeploymentBackendType.Symlink;
        public string DisplayName => "符号链接";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

        private const int SYMBOLIC_LINK_FLAG_FILE = 0x0;
        private const int SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;

        public Task<bool> CanUseAsync()
        {
            return Task.Run(() =>
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return false;

                // 测试是否有创建符号链接的权限
                var testDir = Path.Combine(Path.GetTempPath(), $"uemod_symlink_test_{Guid.NewGuid()}");
                var testSource = Path.Combine(testDir, "source.txt");
                var testLink = Path.Combine(testDir, "link.txt");

                try
                {
                    Directory.CreateDirectory(testDir);
                    File.WriteAllText(testSource, "test");

                    var flags = SYMBOLIC_LINK_FLAG_FILE | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE;
                    var result = CreateSymbolicLink(testLink, testSource, flags);
                    return result;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
                }
            });
        }

        public Task DeployFileAsync(string sourcePath, string targetPath)
        {
            return Task.Run(() =>
            {
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (File.Exists(targetPath))
                    File.Delete(targetPath);

                var flags = SYMBOLIC_LINK_FLAG_FILE | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE;
                if (!CreateSymbolicLink(targetPath, sourcePath, flags))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogWarning("符号链接失败(错误={Error})，降级为复制: {Path}", error, targetPath);
                    File.Copy(sourcePath, targetPath, overwrite: true);
                    return;
                }

                _logger.LogDebug("符号链接部署: {Source} → {Target}", sourcePath, targetPath);
            });
        }

        public Task RemoveFileAsync(string targetPath)
        {
            return Task.Run(() =>
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    _logger.LogDebug("移除符号链接: {Path}", targetPath);
                }

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
