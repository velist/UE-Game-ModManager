using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// 硬链接部署后端。
    /// 节省磁盘空间，但要求源和目标在同一卷。
    /// </summary>
    public class HardLinkBackend : IDeploymentBackend
    {
        private readonly ILogger<HardLinkBackend> _logger;

        public HardLinkBackend(ILogger<HardLinkBackend> logger)
        {
            _logger = logger;
        }

        public DeploymentBackendType Type => DeploymentBackendType.HardLink;
        public string DisplayName => "硬链接";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public Task<bool> CanUseAsync()
        {
            // 硬链接在 Windows NTFS 上可用
            return Task.FromResult(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        }

        public Task DeployFileAsync(string sourcePath, string targetPath)
        {
            return Task.Run(() =>
            {
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                // 如果目标已存在，先删除
                if (File.Exists(targetPath))
                    File.Delete(targetPath);

                if (!CreateHardLink(targetPath, sourcePath, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    // 错误码 1 = 跨卷，17 = 已存在 → 降级为复制
                    if (error is 1 or 17)
                    {
                        _logger.LogWarning("硬链接失败(错误={Error})，降级为复制: {Path}", error, targetPath);
                        File.Copy(sourcePath, targetPath, overwrite: true);
                        return;
                    }
                    throw new IOException($"创建硬链接失败 (Win32Error={error}): {sourcePath} → {targetPath}");
                }

                _logger.LogDebug("硬链接部署: {Source} → {Target}", sourcePath, targetPath);
            });
        }

        public Task RemoveFileAsync(string targetPath)
        {
            return Task.Run(() =>
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    _logger.LogDebug("移除硬链接: {Path}", targetPath);
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
