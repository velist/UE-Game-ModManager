using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Repository;

namespace UEModManager.Services
{
    /// <summary>
    /// 包仓库管理服务。
    /// 管理 Package 的全生命周期：注册、查询、更新、删除、引用计数。
    /// 数据索引存储在 Data/{gameName}_packages.json，文件存储在 ObjectStore。
    /// </summary>
    public class PackageRepository : IPackageQuery
    {
        private readonly ILogger<PackageRepository> _logger;
        private readonly ObjectStore _objectStore;
        private readonly string _dataDirectory;
        private string _currentGame = string.Empty;
        private List<Package> _packages = new();

        /// <summary>当包列表发生变化时触发。</summary>
        public event Action? PackagesChanged;

        public PackageRepository(ILogger<PackageRepository> logger, ObjectStore objectStore)
        {
            _logger = logger;
            _objectStore = objectStore;
            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        }

        /// <summary>ObjectStore 实例。</summary>
        public ObjectStore Store => _objectStore;

        // ─── 初始化 ───

        /// <summary>
        /// 设置当前游戏并加载包索引。
        /// </summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGame = gameName;
            _objectStore.EnsureInitialized();

            if (!Directory.Exists(_dataDirectory))
                Directory.CreateDirectory(_dataDirectory);

            _packages = await LoadIndexAsync();

            // 恢复预览图路径
            foreach (var pkg in _packages)
            {
                if (string.IsNullOrEmpty(pkg.PreviewImagePath) || !File.Exists(pkg.PreviewImagePath))
                {
                    var preview = _objectStore.GetPreviewImagePath(pkg.PackageKey);
                    if (preview != null)
                        pkg.PreviewImagePath = preview;
                }
            }

            _logger.LogInformation("已加载 {Game} 的包索引: {Count} 个", gameName, _packages.Count);
        }

        // ─── 查询 ───

        /// <summary>获取所有包。</summary>
        public IReadOnlyList<Package> GetAllPackages() => _packages.AsReadOnly();

        /// <summary>按 PackageKey 获取包。</summary>
        public Package? GetByKey(string packageKey)
            => _packages.FirstOrDefault(p => p.PackageKey.Equals(packageKey, StringComparison.OrdinalIgnoreCase));

        /// <summary>按 ID 获取包。</summary>
        public Package? GetById(Guid id) => _packages.FirstOrDefault(p => p.Id == id);

        /// <summary>按类型过滤。</summary>
        public IReadOnlyList<Package> GetByKind(PackageKind kind)
            => _packages.Where(p => p.Kind == kind).ToList().AsReadOnly();

        /// <summary>搜索包（名称/标签）。</summary>
        public IReadOnlyList<Package> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return _packages.AsReadOnly();
            var q = query.Trim().ToLower();
            return _packages.Where(p =>
                p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.PackageKey.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToList().AsReadOnly();
        }

        /// <summary>包是否存在。</summary>
        public bool Exists(string packageKey) => GetByKey(packageKey) != null;

        // ─── 注册/添加 ───

        /// <summary>
        /// 注册一个新包到仓库。
        /// 同时写 manifest 和索引。
        /// </summary>
        public async Task<Package> RegisterPackageAsync(Package package)
        {
            // 检查重复
            var existing = GetByKey(package.PackageKey);
            if (existing != null)
            {
                _logger.LogWarning("包已存在，更新: {Key}", package.PackageKey);
                return await UpdatePackageAsync(package);
            }

            _packages.Add(package);

            // 写 manifest
            await WriteManifestAsync(package);

            // 写索引
            await SaveIndexAsync();

            PackagesChanged?.Invoke();
            _logger.LogInformation("包已注册: {Key} ({Kind})", package.PackageKey, package.Kind);
            return package;
        }

        /// <summary>
        /// 批量注册包（数据迁移用）。
        /// </summary>
        public async Task RegisterPackagesAsync(IEnumerable<Package> packages)
        {
            foreach (var pkg in packages)
            {
                if (!Exists(pkg.PackageKey))
                    _packages.Add(pkg);
            }
            await SaveIndexAsync();
            PackagesChanged?.Invoke();
        }

        // ─── 更新 ───

        /// <summary>
        /// 更新包的元数据（名称、备注、标签、预览图）。
        /// </summary>
        public async Task<Package> UpdatePackageAsync(Package updated)
        {
            var existing = GetByKey(updated.PackageKey);
            if (existing == null)
                throw new InvalidOperationException($"包不存在: {updated.PackageKey}");

            existing.DisplayName = updated.DisplayName;
            existing.Note = updated.Note;
            existing.Tags = new List<string>(updated.Tags);
            existing.PreviewImagePath = updated.PreviewImagePath;
            existing.PluginTargetPath = updated.PluginTargetPath;
            existing.LastModified = DateTime.Now;

            // 更新 manifest
            await WriteManifestAsync(existing);
            await SaveIndexAsync();

            PackagesChanged?.Invoke();
            return existing;
        }

        /// <summary>
        /// 更新包的预览图。
        /// </summary>
        public async Task<string?> UpdatePreviewImageAsync(string packageKey, string imagePath)
        {
            var package = GetByKey(packageKey);
            if (package == null) return null;

            var storedPath = _objectStore.StorePreviewImage(packageKey, imagePath);
            if (storedPath != null)
            {
                package.PreviewImagePath = storedPath;
                package.LastModified = DateTime.Now;
                await SaveIndexAsync();
            }
            return storedPath;
        }

        // ─── 删除 ───

        /// <summary>
        /// 计算给定 packageKey 在所有 Profile 中的引用情况，并产生删除决策。
        /// 不做任何 IO；调用方根据返回的 Plan 决定是否调用 <see cref="DeletePackageAsync"/>。
        /// </summary>
        public PackageDeletionPlan PlanDeletion(
            string packageKey,
            IEnumerable<InstanceProfile> allProfiles)
        {
            var report = PackageReferenceCounter.Count(packageKey, allProfiles);
            return PackageDeletionPlanner.Plan(report);
        }

        /// <summary>
        /// 从仓库中删除包（索引 + 仓库文件）。
        ///
        /// ⚠ 必须先调用 <see cref="PlanDeletion"/> 检查引用情况！
        /// 默认拒绝删除仍被任何 Profile 引用的包；通过 <paramref name="force"/> 可强制覆盖，
        /// 但调用方需确保已先回滚相关 Profile 的部署文件。
        /// </summary>
        /// <param name="packageKey">要删除的包标识。</param>
        /// <param name="allProfiles">所有 Profile（用于引用计数）。传 null 表示跳过检查（仅供测试）。</param>
        /// <param name="force">true = 即使被引用也强制删除（必须先自行回滚部署）。</param>
        /// <returns>(成功否, 决策详情)。Decision=ActivelyDeployed 且 force=false 时返回 (false, plan)。</returns>
        public async Task<(bool Success, PackageDeletionPlan? Plan)> DeletePackageAsync(
            string packageKey,
            IEnumerable<InstanceProfile>? allProfiles,
            bool force = false)
        {
            var package = GetByKey(packageKey);
            if (package == null) return (false, null);

            PackageDeletionPlan? plan = null;
            if (allProfiles != null)
            {
                plan = PlanDeletion(packageKey, allProfiles);
                if (plan.RequiresUserConfirmation && !force)
                {
                    _logger.LogWarning(
                        "[PackageRepo] 拒绝删除 {Key}: {Decision} — {Explanation}",
                        packageKey, plan.Decision, plan.Explanation);
                    return (false, plan);
                }
            }

            _packages.Remove(package);
            _objectStore.DeletePackage(packageKey);
            await SaveIndexAsync();

            PackagesChanged?.Invoke();
            _logger.LogInformation(
                "[PackageRepo] 包已删除: {Key} (force={Force}, decision={Decision})",
                packageKey, force, plan?.Decision.ToString() ?? "Unchecked");
            return (true, plan);
        }

        /// <summary>
        /// [向后兼容] 旧签名 — 直接删除，不做引用检查。
        /// 新代码应改用带引用检查的重载。
        /// </summary>
        [Obsolete("此重载会跳过引用计数检查，可能导致游戏目录残留孤儿文件。请使用带 allProfiles 参数的重载。")]
        public async Task<bool> DeletePackageAsync(string packageKey)
        {
            var (success, _) = await DeletePackageAsync(packageKey, allProfiles: null, force: true);
            return success;
        }

        // ─── 仓库统计 ───

        /// <summary>仓库总占用大小。</summary>
        public long GetTotalSize() => _objectStore.GetTotalSize();

        /// <summary>包总数。</summary>
        public int GetTotalCount() => _packages.Count;

        /// <summary>
        /// 获取未被任何 Profile 引用的孤立包。
        /// </summary>
        public List<Package> GetOrphanPackages(IEnumerable<string> referencedKeys)
        {
            var refSet = new HashSet<string>(referencedKeys, StringComparer.OrdinalIgnoreCase);
            return _packages.Where(p => !refSet.Contains(p.PackageKey)).ToList();
        }

        /// <summary>
        /// 获取内容哈希相同的重复包组。
        /// </summary>
        public List<List<Package>> GetDuplicateGroups()
        {
            return _packages
                .Where(p => !string.IsNullOrEmpty(p.ContentHash))
                .GroupBy(p => p.ContentHash!)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();
        }

        /// <summary>
        /// 检查仓库完整性（manifest 存在且文件齐全）。
        /// </summary>
        public Task<List<(string packageKey, string issue)>> CheckIntegrityAsync()
        {
            var issues = new List<(string, string)>();
            foreach (var pkg in _packages)
            {
                if (!_objectStore.PackageExists(pkg.PackageKey))
                {
                    issues.Add((pkg.PackageKey, "manifest.json 缺失"));
                    continue;
                }

                var files = _objectStore.GetPackageFiles(pkg.PackageKey);
                var expectedCount = pkg.Artifacts.Count;
                if (files.Count < expectedCount)
                    issues.Add((pkg.PackageKey, $"文件缺失: 期望 {expectedCount}, 实际 {files.Count}"));
            }
            return Task.FromResult(issues);
        }

        // ─── 持久化 ───

        private string GetIndexPath() => Path.Combine(_dataDirectory, $"{_currentGame}_packages.json");

        private async Task<List<Package>> LoadIndexAsync()
        {
            try
            {
                var path = GetIndexPath();
                if (!File.Exists(path)) return new List<Package>();
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<Package>>(json) ?? new List<Package>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载包索引失败");
                return new List<Package>();
            }
        }

        private async Task SaveIndexAsync()
        {
            try
            {
                var path = GetIndexPath();
                var tempFile = path + ".tmp";
                var json = JsonSerializer.Serialize(_packages, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(tempFile, json);

                if (File.Exists(path))
                    File.Replace(tempFile, path, null);
                else
                    File.Move(tempFile, path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存包索引失败");
            }
        }

        private async Task WriteManifestAsync(Package package)
        {
            try
            {
                _objectStore.EnsureInitialized();
                var manifestPath = _objectStore.GetManifestPath(package.PackageKey);
                var dir = Path.GetDirectoryName(manifestPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var manifest = PackageManifest.FromPackage(package);
                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(manifestPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入 manifest 失败: {Key}", package.PackageKey);
            }
        }
    }
}
