using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    /// </summary>
    public class OverwriteStore
    {
        private readonly ILogger<OverwriteStore> _logger;
        private readonly PackageRepository _packageRepo;
        private readonly PackageImportService _packageImport;
        private readonly string _dataDirectory;
        private string _overwriteRoot;
        private string _currentGame = "";
        private List<GeneratedArtifact> _artifacts = [];

        /// <summary>生成物列表发生变化时触发。</summary>
        public event Action? ArtifactsChanged;

        public OverwriteStore(
            ILogger<OverwriteStore> logger,
            PackageRepository packageRepo,
            PackageImportService packageImport)
        {
            _logger = logger;
            _packageRepo = packageRepo;
            _packageImport = packageImport;

            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            Directory.CreateDirectory(_dataDirectory);

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _overwriteRoot = Path.Combine(appData, "UEModManager", "Overwrites");
            Directory.CreateDirectory(_overwriteRoot);
        }

        /// <summary>Overwrite 存储根目录。</summary>
        public string OverwriteRoot => _overwriteRoot;

        // ─── 初始化 ───

        /// <summary>切换当前游戏并加载生成物索引。</summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGame = gameName;
            var gameDir = Path.Combine(_overwriteRoot, gameName);
            Directory.CreateDirectory(gameDir);
            await LoadIndexAsync();
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

        /// <summary>按来源包筛选。</summary>
        public IReadOnlyList<GeneratedArtifact> GetBySourcePackage(string packageKey)
            => _artifacts.Where(a => a.SourcePackageKey == packageKey).ToList();

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

            var artifact = new GeneratedArtifact
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
                FileHash = fi.Exists ? await ComputeHashAsync(fullPath) : null,
                HostGameName = _currentGame
            };

            _artifacts.Add(artifact);
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();

            _logger.LogInformation("Registered generated artifact: {Name} ({Type})", displayName, type);
            return artifact;
        }

        // ─── 删除 ───

        /// <summary>删除单个生成物（文件 + 索引）。</summary>
        public async Task DeleteAsync(Guid artifactId)
        {
            var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
            if (artifact == null) return;

            var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogInformation("Deleted overwrite file: {Path}", fullPath);
            }

            _artifacts.Remove(artifact);
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();
        }

        /// <summary>清理所有过期生成物。</summary>
        public async Task<int> CleanupStaleAsync()
        {
            var stale = _artifacts.Where(a => a.Status == GeneratedArtifactStatus.Stale).ToList();
            foreach (var artifact in stale)
            {
                var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                _artifacts.Remove(artifact);
            }

            if (stale.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
                _logger.LogInformation("Cleaned up {Count} stale artifacts", stale.Count);
            }
            return stale.Count;
        }

        /// <summary>清理某个包的所有生成物。</summary>
        public async Task CleanupByPackageAsync(string packageKey)
        {
            var toRemove = _artifacts.Where(a => a.SourcePackageKey == packageKey).ToList();
            foreach (var artifact in toRemove)
            {
                var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                _artifacts.Remove(artifact);
            }

            if (toRemove.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
            }
        }

        /// <summary>清理某个 Profile 的所有生成物。</summary>
        public async Task CleanupByProfileAsync(Guid profileId)
        {
            var toRemove = _artifacts.Where(a => a.SourceProfileId == profileId).ToList();
            foreach (var artifact in toRemove)
            {
                var fullPath = Path.Combine(_overwriteRoot, _currentGame, artifact.RelativePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                _artifacts.Remove(artifact);
            }

            if (toRemove.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
            }
        }

        // ─── 晋升为正式 Package ───

        /// <summary>
        /// 将生成物晋升为正式 Package（复制到仓库 + 注册）。
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

            // 通过 PackageImportService 导入
            var results = await _packageImport.ImportAsync([fullPath]);
            if (results.Count == 0 || !results[0].Success || results[0].Package == null)
            {
                _logger.LogWarning("Cannot promote artifact {Id}: import failed", artifactId);
                return null;
            }

            // 更新显示名
            var pkg = results[0].Package;
            if (packageDisplayName != null)
            {
                pkg.DisplayName = packageDisplayName;
                await _packageRepo.UpdatePackageAsync(pkg);
            }

            // 标记为已晋升
            artifact.Status = GeneratedArtifactStatus.Promoted;
            artifact.LastModified = DateTime.Now;
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();

            _logger.LogInformation("Promoted artifact {Name} to package {Key}", artifact.DisplayName, pkg.PackageKey);
            return pkg;
        }

        // ─── 状态管理 ───

        /// <summary>将生成物标记为过期。</summary>
        public async Task MarkStaleAsync(Guid artifactId)
        {
            var artifact = _artifacts.FirstOrDefault(a => a.Id == artifactId);
            if (artifact == null) return;
            artifact.Status = GeneratedArtifactStatus.Stale;
            artifact.LastModified = DateTime.Now;
            await SaveIndexAsync();
            ArtifactsChanged?.Invoke();
        }

        /// <summary>将某个部署事务的所有生成物标记为过期。</summary>
        public async Task MarkTransactionStaleAsync(Guid transactionId)
        {
            var affected = _artifacts
                .Where(a => a.SourceTransactionId == transactionId && a.Status == GeneratedArtifactStatus.Active)
                .ToList();
            foreach (var a in affected)
            {
                a.Status = GeneratedArtifactStatus.Stale;
                a.LastModified = DateTime.Now;
            }
            if (affected.Count > 0)
            {
                await SaveIndexAsync();
                ArtifactsChanged?.Invoke();
            }
        }

        // ─── 持久化 ───

        private string GetIndexPath() => Path.Combine(_dataDirectory, $"{_currentGame}_overwrites.json");

        private async Task LoadIndexAsync()
        {
            var path = GetIndexPath();
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                _artifacts = JsonConvert.DeserializeObject<List<GeneratedArtifact>>(json) ?? [];
                _logger.LogInformation("Loaded {Count} overwrite artifacts for {Game}", _artifacts.Count, _currentGame);
            }
            else
            {
                _artifacts = [];
            }
        }

        private async Task SaveIndexAsync()
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
