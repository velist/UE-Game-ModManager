using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using UEModManager.Models;
using UEModManager.Services.Persistence;

namespace UEModManager.Services
{
    /// <summary>
    /// 生成物仓库。管理部署过程和工具产生的非原始输入文件。
    /// 存储路径：%APPDATA%/UEModManager/Overwrites/{gameName}/
    /// 索引文件：Data/{gameName}_overwrites.json
    ///
    /// 并发模型与 <see cref="ProfileService"/> 一致：写操作由 <see cref="_gate"/> 串行化、
    /// 结构性修改走 swap-on-write、事件在出锁后触发。详见 ProfileService 的类注释。
    ///
    /// ⚠ 本服务是三者中唯一会外调其他服务的（<see cref="PromoteToPackageAsync"/> 调
    /// PackageImportService 与 PackageRepository）。那些外调**一律在锁外完成**，
    /// 锁内只做本服务自己的状态变更，因此不存在跨服务的锁序问题。
    /// </summary>
    public class OverwriteStore
    {
        private readonly ILogger<OverwriteStore> _logger;
        private readonly PackageRepository _packageRepo;
        private readonly PackageImportService _packageImport;
        private readonly string _dataDirectory;

        /// <summary>串行化"改内存 + 落盘"。SemaphoreSlim 不可重入，锁内不得调用也要抢锁的方法。</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        private string _overwriteRoot;
        private string _currentGame = "";

        /// <summary>生成物索引。**只整体替换，不原地增删**，使无锁读取方的枚举始终安全。</summary>
        private List<GeneratedArtifact> _artifacts = [];

        /// <summary>生成物列表发生变化时触发。</summary>
        public event Action? ArtifactsChanged;

        public OverwriteStore(
            ILogger<OverwriteStore> logger,
            PackageRepository packageRepo,
            PackageImportService packageImport)
            : this(logger, packageRepo, packageImport,
                Infrastructure.AppPaths.DataDirectory, Infrastructure.AppPaths.OverwritesRoot)
        {
        }

        /// <summary>
        /// 指定存储位置的构造函数（测试用）。DI 走上面的三参数构造函数——
        /// 容器无法解析 string，不会误选此重载。
        ///
        /// <para>
        /// 存在的理由：索引目录曾经隐式等于 <c>{进程目录}\Data</c>，测试正是靠"测试宿主的
        /// 进程目录天然隔离"才没有互相踩踏。归口到 <c>AppPaths</c> 后那份隔离就没了，
        /// 跑一次测试会写进开发者真实的 <c>%LOCALAPPDATA%</c>。
        /// </para>
        /// </summary>
        public OverwriteStore(
            ILogger<OverwriteStore> logger,
            PackageRepository packageRepo,
            PackageImportService packageImport,
            string dataDirectory,
            string overwriteRoot)
        {
            _logger = logger;
            _packageRepo = packageRepo;
            _packageImport = packageImport;

            _dataDirectory = dataDirectory;
            Infrastructure.AppPaths.TryEnsureDirectory(_dataDirectory);

            _overwriteRoot = overwriteRoot;
            Directory.CreateDirectory(_overwriteRoot);
        }

        /// <summary>Overwrite 存储根目录。</summary>
        public string OverwriteRoot => _overwriteRoot;

        // ─── 初始化 ───

        /// <summary>切换当前游戏并加载生成物索引。</summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            var gameDir = Path.Combine(_overwriteRoot, gameName);
            Directory.CreateDirectory(gameDir);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _currentGame = gameName;
                await LoadIndexLockedAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        // ─── 查询 ───

        /// <summary>获取所有生成物。</summary>
        public IReadOnlyList<GeneratedArtifact> GetAll() => _artifacts.AsReadOnly();

        /// <summary>按类型筛选。</summary>
        public IReadOnlyList<GeneratedArtifact> GetByType(GeneratedArtifactType type)
            => _artifacts.Where(a => a.Type == type).ToList();

        /// <summary>按状态筛选。</summary>
        public IReadOnlyList<GeneratedArtifact> GetByStatus(GeneratedArtifactStatus status)
            => _artifacts.Where(a => a.Status == status).ToList();

        /// <summary>
        /// 按来源包筛选。包 key 比较用 <see cref="StringComparison.OrdinalIgnoreCase"/>，
        /// 与 <c>PackageRepository.GetByKey</c> 保持一致——两处口径不同会让大小写不同的 key 查不到。
        /// </summary>
        public IReadOnlyList<GeneratedArtifact> GetBySourcePackage(string packageKey)
            => _artifacts.Where(a => IsSamePackage(a, packageKey)).ToList();

        /// <summary>包 key 归一化比较。null 的 SourcePackageKey 永不匹配。</summary>
        private static bool IsSamePackage(GeneratedArtifact artifact, string packageKey)
            => artifact.SourcePackageKey != null
               && string.Equals(artifact.SourcePackageKey, packageKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>获取活跃生成物数量。</summary>
        public int ActiveCount => _artifacts.Count(a => a.Status == GeneratedArtifactStatus.Active);

        /// <summary>获取总占用空间。</summary>
        public long TotalSize => _artifacts.Sum(a => a.FileSize);

        /// <summary>获取过期生成物占用空间。</summary>
        public long StaleSize => _artifacts
            .Where(a => a.Status == GeneratedArtifactStatus.Stale)
            .Sum(a => a.FileSize);

        // ─── 注册生成物 ───

        /// <summary>
        /// 注册一个新的生成物。文件必须已存在于 Overwrite 目录中。
        /// </summary>
        public async Task<GeneratedArtifact> RegisterAsync(
            string filePath,
            GeneratedArtifactType type,
            string displayName,
            string? sourcePackageKey = null,
            Guid? sourceProfileId = null,
            Guid? sourceTransactionId = null,
            string? sourceDescription = null,
            string? relativeTargetPath = null)
        {
            GeneratedArtifact artifact;

            // 整个方法在锁内：下面的"避重名"循环是 File.Exists → File.Copy 的 TOCTOU，
            // 两个并发 RegisterAsync 会挑中同一个 destPath 从而互相覆盖。
            // 代价是并发注册被串行化（含文件拷贝），但注册本来就发生在部署的顺序循环里。
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var gameDir = Path.Combine(_overwriteRoot, _currentGame);

                // 如果文件不在 Overwrite 目录中，复制进来
                string relativePath;
                if (!filePath.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(filePath);
                    var typeFolder = type.ToString().ToLowerInvariant();
                    var destDir = Path.Combine(gameDir, typeFolder);
                    Directory.CreateDirectory(destDir);
                    var destPath = Path.Combine(destDir, fileName);

                    // 避免重名
                    var counter = 1;
                    while (File.Exists(destPath))
                    {
                        var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
                        var ext = Path.GetExtension(fileName);
                        destPath = Path.Combine(destDir, $"{nameNoExt}_{counter++}{ext}");
                    }

                    File.Copy(filePath, destPath);
                    relativePath = Path.GetRelativePath(gameDir, destPath);
                }
                else
                {
                    relativePath = Path.GetRelativePath(gameDir, filePath);
                }

                var fullPath = Path.Combine(gameDir, relativePath);
                var fi = new FileInfo(fullPath);

                artifact = new GeneratedArtifact
                {
                    RelativePath = relativePath,
                    RelativeTargetPath = relativeTargetPath,
                    DisplayName = displayName,
                    Type = type,
                    Status = GeneratedArtifactStatus.Active,
                    SourcePackageKey = sourcePackageKey,
                    SourceProfileId = sourceProfileId,
                    SourceTransactionId = sourceTransactionId,
                    SourceDescription = sourceDescription,
                    FileSize = fi.Exists ? fi.Length : 0,
                    FileHash = fi.Exists ? await ComputeHashAsync(fullPath).ConfigureAwait(false) : null,
                    HostGameName = _currentGame
                };

                _artifacts = [.. _artifacts, artifact];
                await SaveIndexLockedAsync().ConfigureAwait(false);
                _logger.LogInformation("Registered generated artifact: {Name} ({Type})", displayName, type);
            }
            finally { _gate.Release(); }

            ArtifactsChanged?.Invoke();
            return artifact;
        }

        // ─── 删除 ───

        /// <summary>删除单个生成物（文件 + 索引）。</summary>
        public async Task DeleteAsync(Guid artifactId)
        {
            bool removed;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
                if (artifact == null) return;

                var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    _logger.LogInformation("Deleted overwrite file: {Path}", fullPath);
                }

                _artifacts = _artifacts.Where(a => a.Id != artifactId).ToList();
                await SaveIndexLockedAsync().ConfigureAwait(false);
                removed = true;
            }
            finally { _gate.Release(); }

            if (removed) ArtifactsChanged?.Invoke();
        }

        /// <summary>清理所有过期生成物。</summary>
        public async Task<int> CleanupStaleAsync()
        {
            int count;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stale = _artifacts.Where(a => a.Status == GeneratedArtifactStatus.Stale).ToList();
                count = stale.Count;
                if (count == 0) return 0;

                foreach (var artifact in stale)
                {
                    var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }

                _artifacts = _artifacts.Where(a => a.Status != GeneratedArtifactStatus.Stale).ToList();
                await SaveIndexLockedAsync().ConfigureAwait(false);
                _logger.LogInformation("Cleaned up {Count} stale artifacts", count);
            }
            finally { _gate.Release(); }

            ArtifactsChanged?.Invoke();
            return count;
        }

        /// <summary>清理某个包的所有生成物。包 key 比较忽略大小写，避免留下孤儿生成物文件。</summary>
        public async Task CleanupByPackageAsync(string packageKey)
        {
            bool changed;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var toRemove = _artifacts.Where(a => IsSamePackage(a, packageKey)).ToList();
                changed = toRemove.Count > 0;
                if (!changed) return;

                foreach (var artifact in toRemove)
                {
                    var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }

                _artifacts = _artifacts.Where(a => !IsSamePackage(a, packageKey)).ToList();
                await SaveIndexLockedAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }

            ArtifactsChanged?.Invoke();
        }

        /// <summary>清理某个 Profile 的所有生成物。</summary>
        public async Task CleanupByProfileAsync(Guid profileId)
        {
            bool changed;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var toRemove = _artifacts.Where(a => a.SourceProfileId == profileId).ToList();
                changed = toRemove.Count > 0;
                if (!changed) return;

                foreach (var artifact in toRemove)
                {
                    var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }

                _artifacts = _artifacts.Where(a => a.SourceProfileId != profileId).ToList();
                await SaveIndexLockedAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }

            ArtifactsChanged?.Invoke();
        }

        // ─── 晋升为正式 Package ───

        /// <summary>
        /// 将生成物晋升为正式 Package（复制到仓库 + 注册）。
        ///
        /// ⚠ 对 PackageImportService / PackageRepository 的调用**刻意放在锁外**：
        /// 它们各自有自己的锁，在本服务的锁内调用会形成跨服务的锁嵌套（锁序风险），
        /// 且导入是长耗时操作，持锁会把所有生成物读写一并卡住。
        /// 锁只用来包住最后那次"改本服务状态 + 落盘"。
        /// </summary>
        public async Task<Package?> PromoteToPackageAsync(Guid artifactId, string? packageDisplayName = null)
        {
            var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
            if (artifact == null) return null;

            var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("Cannot promote artifact {Id}: file not found at {Path}", artifactId, fullPath);
                return null;
            }

            // ── 锁外：跨服务调用 ──
            var results = await _packageImport.ImportAsync([fullPath]).ConfigureAwait(false);
            if (results.Count == 0 || !results[0].Success || results[0].Package == null)
            {
                _logger.LogWarning("Cannot promote artifact {Id}: import failed", artifactId);
                return null;
            }

            var pkg = results[0].Package;
            if (packageDisplayName != null)
            {
                pkg.DisplayName = packageDisplayName;
                await _packageRepo.UpdatePackageAsync(pkg).ConfigureAwait(false);
            }

            // ── 锁内：只改自己的状态 ──
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 重新查一次：跨服务调用期间该生成物可能已被并发删除
                var current = _artifacts.FirstOrDefault(a => a.Id == artifactId);
                if (current == null)
                {
                    _logger.LogWarning("生成物 {Id} 在晋升过程中已被删除，跳过状态回写", artifactId);
                    return pkg;
                }

                current.Status = GeneratedArtifactStatus.Promoted;
                current.LastModified = DateTime.Now;
                await SaveIndexLockedAsync().ConfigureAwait(false);
                _logger.LogInformation("Promoted artifact {Name} to package {Key}", current.DisplayName, pkg.PackageKey);
            }
            finally { _gate.Release(); }

            ArtifactsChanged?.Invoke();
            return pkg;
        }

        // ─── 状态管理 ───

        /// <summary>将生成物标记为过期。</summary>
        public async Task MarkStaleAsync(Guid artifactId)
        {
            bool changed;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
                if (artifact == null) return;

                artifact.Status = GeneratedArtifactStatus.Stale;
                artifact.LastModified = DateTime.Now;
                await SaveIndexLockedAsync().ConfigureAwait(false);
                changed = true;
            }
            finally { _gate.Release(); }

            if (changed) ArtifactsChanged?.Invoke();
        }

        /// <summary>将某个部署事务的所有生成物标记为过期。</summary>
        public async Task MarkTransactionStaleAsync(Guid transactionId)
        {
            bool changed;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var affected = _artifacts
                    .Where(a => a.SourceTransactionId == transactionId && a.Status == GeneratedArtifactStatus.Active)
                    .ToList();
                changed = affected.Count > 0;
                if (!changed) return;

                foreach (var a in affected)
                {
                    a.Status = GeneratedArtifactStatus.Stale;
                    a.LastModified = DateTime.Now;
                }
                await SaveIndexLockedAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }

            ArtifactsChanged?.Invoke();
        }

        // ─── 持久化 ───

        private string GetIndexPath() => Path.Combine(_dataDirectory, $"{_currentGame}_overwrites.json");

        /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
        private async Task LoadIndexLockedAsync()
        {
            var path = GetIndexPath();
            if (!File.Exists(path))
            {
                _artifacts = [];
                return;
            }

            // 索引损坏不能让整个初始化中断：LoadIndexAsync 由 SetCurrentGameAsync 调用，
            // 抛出会冒泡到游戏切换/启动初始化链路，直接卡死主流程。
            // 与 ProfileService / GameConfigService / UiPreferences 对齐：备份损坏文件 + 记日志 + 空集合继续。
            try
            {
                var json = await File.ReadAllTextAsync(path);
                _artifacts = JsonConvert.DeserializeObject<List<GeneratedArtifact>>(json) ?? [];
                _logger.LogInformation("Loaded {Count} overwrite artifacts for {Game}", _artifacts.Count, _currentGame);
            }
            catch (Exception ex)
            {
                BackupCorruptIndexFile(path, ex);
                _artifacts = [];
            }
        }

        private void BackupCorruptIndexFile(string filePath, Exception loadException)
        {
            try
            {
                var backupPath = $"{filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Copy(filePath, backupPath, overwrite: false);
                _logger.LogError(loadException, "加载生成物索引失败，已备份损坏文件: {BackupPath}", backupPath);
            }
            catch (Exception backupException)
            {
                _logger.LogError(loadException, "加载生成物索引失败，且损坏文件备份失败: {Path}", filePath);
                _logger.LogError(backupException, "损坏生成物索引备份失败");
            }
        }

        /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
        private async Task SaveIndexLockedAsync()
        {
            var path = GetIndexPath();
            var json = JsonConvert.SerializeObject(_artifacts, Formatting.Indented);
            await AtomicFileWriter.WriteAllTextAsync(path, json);
        }

        // ─── 哈希 ───

        private static async Task<string> ComputeHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            var hash = await SHA256.HashDataAsync(stream);
            return Convert.ToHexString(hash)[..16];
        }
    }
}
