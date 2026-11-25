using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Localization;
using UEModManager.Models;
using UEModManager.Services.Reconciliation;
using UEModManager.Services.Security;

namespace UEModManager.Services
{
    /// <summary>一次核对的结论，连同两侧的位置（界面写提示时要用）。</summary>
    public sealed record DiskCheck(DiskReconcileReport Report, string RepositoryRoot, string GamePath);

    /// <summary>从游戏目录找回一个包的结果。</summary>
    public sealed record RecoveryResult(string PackageKey, bool Success, int FilesRestored, string? Error);

    /// <summary>
    /// 把管理器记着的东西和磁盘对一遍（IO 适配器）。
    ///
    /// <para>
    /// 列表是照着包索引和方案画的，部署清单记着装进游戏目录的每一个文件——这三样都是"记着的"。
    /// 玩家在资源管理器里删掉文件夹、游戏更新或重装清掉 MOD 目录，它们都不会知道。
    /// 本服务负责去磁盘上看一眼：每个文件探一次"在不在、多大"，交给 Core 的
    /// <see cref="DiskReconciler"/> 下结论。判据全部在那里，这里不许再写一份。
    /// </para>
    ///
    /// <para>
    /// <b><see cref="CheckAsync"/> 只读。</b>它不写任何文件、不建任何目录——仓库根不在时
    /// 顺手把它建出来，"位置访问不到"就会变成"位置在、里面是空的"，后面的护栏全部失效。
    /// 会改东西的只有 <see cref="RecoverFromGameAsync"/> 与 <see cref="ReleaseDeployedFilesAsync"/>，
    /// 两者都只在玩家点了对应的按钮之后才会被调用。
    /// </para>
    /// </summary>
    public class DiskReconcileService
    {
        private readonly ILogger _logger;
        private readonly PackageRepository _repository;
        private readonly ProfileService _profiles;
        private readonly GameConfigService _gameConfig;
        private readonly DeploymentStateStore _stateStore;

        public DiskReconcileService(
            ILogger<DiskReconcileService> logger,
            PackageRepository repository,
            ProfileService profiles,
            GameConfigService gameConfig,
            DeploymentStateStore stateStore)
            : this((ILogger)logger, repository, profiles, gameConfig, stateStore)
        {
        }

        /// <summary>
        /// 借用别的服务的日志器（体检用）。DI 走上面那个构造函数——容器里没有非泛型的 ILogger，不会误选这个。
        /// </summary>
        internal DiskReconcileService(
            ILogger logger,
            PackageRepository repository,
            ProfileService profiles,
            GameConfigService gameConfig,
            DeploymentStateStore stateStore)
        {
            _logger = logger;
            _repository = repository;
            _profiles = profiles;
            _gameConfig = gameConfig;
            _stateStore = stateStore;
        }

        /// <summary>
        /// 核对当前游戏：仓库里的源文件、游戏目录里装好的文件。只读，在后台线程上跑。
        /// </summary>
        public Task<DiskCheck> CheckAsync() => Task.Run(CheckCoreAsync);

        private async Task<DiskCheck> CheckCoreAsync()
        {
            var store = _repository.Store;
            var repositoryRoot = store.RepositoryRoot;
            var gameName = _gameConfig.CurrentGameName;
            var gamePath = _gameConfig.CurrentGamePath;
            var modPath = _gameConfig.CurrentModPath;

            var packages = _repository.GetAllPackages().ToList();
            var enabled = _profiles.CurrentProfile?.Packages
                .Where(entry => entry.IsEnabled)
                .Select(entry => entry.PackageKey)
                .ToList() ?? [];

            IReadOnlyList<ManagedDeploymentFile> managed = [];
            if (!string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(gamePath)
                && !string.IsNullOrWhiteSpace(modPath))
            {
                try
                {
                    managed = (await _stateStore.ReadAsync(gameName, gamePath, modPath).ConfigureAwait(false)).Files;
                }
                catch (InvalidDataException ex)
                {
                    // 清单读不出来时部署本身会停下并报错；这里只是少核对一侧，不能连累仓库侧的检查
                    _logger.LogWarning(ex, "[Reconcile] 部署清单读取失败，本次不核对游戏目录");
                }
            }

            var session = new ProbeSession();
            var input = new DiskReconcileInput
            {
                Packages = packages,
                ManagedFiles = managed,
                EnabledPackageKeys = enabled,
                RepositoryRootReachable = session.IsDirectoryReadable(repositoryRoot),
                // 还没装过任何东西时（清单为空），游戏目录在不在都不影响结论
                GameRootReachable = managed.Count == 0 || session.IsDirectoryReadable(gamePath),
            };

            var probes = new DiskProbes(
                RepositoryFile: relativePath => session.ProbeRepositoryFile(repositoryRoot, relativePath),
                GameFile: session.ProbeFile,
                HasOtherRepositoryFiles: key => HasFilesInPackage(store, key));

            var report = DiskReconciler.Reconcile(input, probes);
            LogReport(report, repositoryRoot, gamePath);
            return new DiskCheck(report, repositoryRoot, gamePath);
        }

        /// <summary>
        /// 把游戏目录里装着的那一份复制回仓库，补齐一个包缺失的源文件。
        ///
        /// <para>
        /// 先把要用到的每个文件都重算一遍哈希、全部对得上才动手：核对时只看了大小，
        /// 而这一步是把文件认作"MOD 的原件"写回仓库，大小相同、内容不同的文件不能混进去。
        /// 任何一个对不上就一个都不复制。
        /// </para>
        /// </summary>
        public async Task<RecoveryResult> RecoverFromGameAsync(RepositoryFinding finding)
        {
            ArgumentNullException.ThrowIfNull(finding);
            if (!finding.CanRecoverEverything)
                return Failed(finding, UiText.Get("游戏目录里没有完整的一份可以找回"));

            try
            {
                var store = _repository.Store;
                store.ThrowIfRelocating("找回 MOD 文件");

                foreach (var file in finding.Recoverable)
                {
                    var info = new FileInfo(file.GameFilePath);
                    if (!info.Exists || info.Length != file.FileSize
                        || !string.Equals(
                            await ObjectStore.ComputeFileHashAsync(file.GameFilePath).ConfigureAwait(false),
                            file.FileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[Reconcile] 找回 {Key} 时文件对不上，放弃: {Path}",
                            finding.PackageKey, file.GameFilePath);
                        return Failed(finding, UiText.Get("游戏目录里的文件已经和原来的不一样了"));
                    }
                }

                var restored = 0;
                foreach (var file in finding.Recoverable)
                {
                    var target = PathSanitizer.SafeCombine(store.RepositoryRoot, file.RelativeSourcePath);
                    // 核对之后它自己回来了（玩家从回收站还原）：不覆盖
                    if (File.Exists(target)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // 先写到旁边的临时文件再改名：复制到一半被打断时，仓库里不会留下一个大小不对的"源文件"
                    var temp = target + ".uemm-recover-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.Copy(file.GameFilePath, temp, overwrite: false);
                        File.Move(temp, target, overwrite: false);
                        restored++;
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }

                // 整个包文件夹被删掉时 manifest 也没了，补上
                await _repository.RewriteManifestAsync(finding.PackageKey).ConfigureAwait(false);
                _logger.LogInformation("[Reconcile] 已从游戏目录找回 {Key}: {Count} 个文件", finding.PackageKey, restored);
                return new RecoveryResult(finding.PackageKey, true, restored, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or InvalidOperationException)
            {
                // 已经复制回去的文件留着：它们都是核对过哈希的原件，下次刷新可以接着找回剩下的
                _logger.LogError(ex, "[Reconcile] 从游戏目录找回 {Key} 失败", finding.PackageKey);
                return Failed(finding, ex.Message);
            }
        }

        /// <summary>
        /// 把这些包装在游戏目录里的文件交还给游戏：文件原样留下，以后不再由管理器管理。
        /// </summary>
        public Task<IReadOnlyList<ManagedDeploymentFile>> ReleaseDeployedFilesAsync(IEnumerable<string> packageKeys)
            => _stateStore.ReleasePackagesAsync(
                _gameConfig.CurrentGameName, _gameConfig.CurrentGamePath, _gameConfig.CurrentModPath, packageKeys);

        private static RecoveryResult Failed(RepositoryFinding finding, string reason)
            => new(finding.PackageKey, false, 0, reason);

        /// <summary>包的 files 目录下还有没有文件。探测不了时当成"有"——不知道就不能说它整个丢了。</summary>
        private static bool HasFilesInPackage(ObjectStore store, string packageKey)
        {
            try
            {
                var filesDirectory = store.GetPackageFilesDirectory(packageKey);
                return Directory.Exists(filesDirectory)
                       && Directory.EnumerateFiles(filesDirectory, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return true;
            }
        }

        private void LogReport(DiskReconcileReport report, string repositoryRoot, string gamePath)
        {
            if (!report.HasFindings && report.UncheckedPackageKeys.Count == 0)
            {
                _logger.LogInformation(
                    "[Reconcile] 核对完成，没有发现问题: {Packages} 个包 / {Sources} 个源文件 / {Managed} 个已安装文件",
                    report.CheckedPackageCount, report.CheckedSourceFileCount, report.CheckedManagedFileCount);
                return;
            }

            _logger.LogWarning(
                "[Reconcile] 核对完成: 仓库不可达={RepoDown}（{Repo}），游戏目录不可达={GameDown}（{Game}），"
                + "疑似位置问题={Suspected}，整包丢失 {Lost}，部分丢失 {Partial}，游戏目录丢失 {Missing}，未能核对 {Unchecked}",
                report.RepositoryRootUnreachable, repositoryRoot, report.GameRootUnreachable, gamePath,
                report.SuspectedRepositoryLocationProblem, report.LostPackages.Count, report.PartialPackages.Count,
                report.MissingDeployments.Count, report.UncheckedPackageKeys.Count);

            foreach (var finding in report.LostPackages)
                _logger.LogWarning("[Reconcile] 整包丢失: {Key}（启用={Enabled}，游戏里还有 {Present}/{Managed} 个文件，可找回={Recoverable}）",
                    finding.PackageKey, finding.IsEnabled, finding.DeployedFilesPresent.Count,
                    finding.ManagedFileCount, finding.CanRecoverEverything);
            foreach (var finding in report.PartialPackages)
                _logger.LogWarning("[Reconcile] 部分丢失: {Key} 缺 {Missing}/{Total}（可找回={Recoverable}）",
                    finding.PackageKey, finding.MissingFiles.Count, finding.TotalFiles, finding.CanRecoverEverything);
            foreach (var finding in report.MissingDeployments)
                _logger.LogWarning("[Reconcile] 游戏目录里丢失: {Key} 缺 {Missing}/{Managed}（启用={Enabled}，可重装={Reinstall}）",
                    finding.PackageKey, finding.MissingFiles.Count, finding.ManagedFileCount,
                    finding.IsEnabled, finding.CanReinstall);
        }

        private static bool IsProbeFailure(Exception ex)
            => ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Security.SecurityException;

        /// <summary>
        /// 一次核对里的全部磁盘探测。
        ///
        /// <para>
        /// "文件不存在"只有在<b>看得清它所在的目录</b>时才算数。<see cref="File.Exists(string)"/> 在目录没权限读、
        /// 所在的盘不在线时同样返回 false，而这两种情况文件多半还好好的。所以文件不在时再往上找最近一层
        /// 存在的目录、试着列一下：列不出来（或者一层都找不到）就报"探测不了"，由 Core 按"不下结论"处理。
        /// 同一个目录只试一次。
        /// </para>
        /// </summary>
        private sealed class ProbeSession
        {
            private readonly Dictionary<string, bool> _readable = new(StringComparer.OrdinalIgnoreCase);

            public FileProbe ProbeRepositoryFile(string repositoryRoot, string relativeSourcePath)
            {
                try
                {
                    return ProbeFile(PathSanitizer.SafeCombine(repositoryRoot, relativeSourcePath));
                }
                catch (Exception ex) when (IsProbeFailure(ex))
                {
                    // 越出仓库根的非法路径：这不是"文件丢了"，是登记信息本身坏了
                    return FileProbe.Unknown;
                }
            }

            public FileProbe ProbeFile(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) return FileProbe.Present(info.Length);
                    return ContainerIsReadable(info.FullName) ? FileProbe.Missing : FileProbe.Unknown;
                }
                catch (Exception ex) when (IsProbeFailure(ex))
                {
                    return FileProbe.Unknown;
                }
            }

            /// <summary>目录存在，而且列得出里面的东西。</summary>
            public bool IsDirectoryReadable(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory)) return false;
                if (_readable.TryGetValue(directory, out var known)) return known;

                bool readable;
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        readable = false;
                    }
                    else
                    {
                        using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                        entries.MoveNext();
                        readable = true;
                    }
                }
                catch (Exception ex) when (IsProbeFailure(ex))
                {
                    readable = false;
                }
                return _readable[directory] = readable;
            }

            private bool ContainerIsReadable(string fullPath)
            {
                var start = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(start)) return false;
                if (_readable.TryGetValue(start, out var known) && known) return true;

                for (var directory = start; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
                {
                    if (!Directory.Exists(directory)) continue;
                    return IsDirectoryReadable(directory);
                }
                return false; // 连盘符都不在
            }
        }
    }
}
