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
    /// 节省磁盘空间，但要求源和目标在同一个盘。
    ///
    /// <para>
    /// <b>建不了链接时会降级为复制，并把这件事上报出去</b>
    /// （<see cref="IDeploymentDegradationReporter"/>）。此前降级只写一条 <c>LogWarning</c>：
    /// 包仓库默认在 <c>%LOCALAPPDATA%</c>（C 盘）而游戏通常装在别的盘，于是默认配置下
    /// 用户在设置里选了"硬链接"、界面显示已选中、部署也成功，但每个文件都在实打实复制 ——
    /// 空间一点没省，界面上一个字都看不到。功能没坏，坏的是它没做用户以为它做的事。
    /// </para>
    /// </summary>
    public class HardLinkBackend : IDeploymentBackend, IDeploymentDegradationReporter
    {
        private readonly ILogger<HardLinkBackend> _logger;

        public HardLinkBackend(ILogger<HardLinkBackend> logger)
        {
            _logger = logger;
        }

        public DeploymentBackendType Type => DeploymentBackendType.HardLink;
        public string DisplayName => "硬链接";

        /// <summary>
        /// 每降级一个文件抛一次。<b>在部署线程上触发</b>（<see cref="DeployFileAsync"/> 跑在
        /// <c>Task.Run</c> 里），订阅方负责线程安全与聚合 —— 一次部署可能有上万个文件，
        /// 而它们的降级原因是同一个。
        /// </summary>
        public event Action<DeploymentDegradation>? Degraded;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public Task<bool> CanUseAsync()
        {
            // 只判得到"这是 Windows"这一层。硬链接还要求源和目标在同一个盘、且文件系统支持，
            // 而这两件事都取决于具体的文件路径，装配阶段根本拿不到。
            // 真正的判定只能等到 DeployFileAsync 里由 CreateHardLink 的结果说了算，
            // 失败即降级为复制并上报（这正是本后端要实现 IDeploymentDegradationReporter 的原因）。
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

                    // 能不能降级、以及降级的真实原因，判据在 Core 的 HardLinkFailureClassifier：
                    // 错误码分不出"跨盘"和"这个盘不支持硬链接"（真机上跨盘拿到的是 1 而不是 17），
                    // 而这两种情况给用户的解法完全不同，所以按源与目标的盘根来分。
                    var kind = HardLinkFailureClassifier.Classify(error, sourcePath, targetPath);
                    if (kind != null)
                    {
                        _logger.LogWarning("硬链接失败(原因={Kind}, Win32Error={Error})，降级为复制: {Path}",
                            kind, error, targetPath);

                        // 先上报再复制：复制本身还可能抛（目标被占用），
                        // 那时这条降级记录已经进了收集器，日志与事务里都留得下痕迹。
                        Degraded?.Invoke(new DeploymentDegradation(
                            kind.Value, sourcePath, targetPath, $"CreateHardLink 失败，Win32Error={error}"));

                        File.Copy(sourcePath, targetPath, overwrite: true);
                        return;
                    }

                    // 其余失败都是真出了问题（被占用、权限、路径过长、链接数超上限）。
                    // 悄悄复制一份会把一次真实故障伪装成部署成功，必须上抛走事务回滚。
                    throw new IOException($"创建硬链接失败 (Win32Error={error}): {sourcePath} → {targetPath}");
                }

                _logger.LogDebug("硬链接部署: {Source} → {Target}", sourcePath, targetPath);
            });
        }

        /// <summary>
        /// 移除已部署的硬链接。
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
                    _logger.LogDebug("移除硬链接: {Path}", targetPath);
                }
            });
        }
    }
}
