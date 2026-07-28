using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Persistence;
using UEModManager.Services.Repository;

namespace UEModManager.Services
{
    /// <summary>
    /// 包仓库管理服务。
    /// 管理 Package 的全生命周期：注册、查询、更新、删除、引用计数。
    /// 数据索引存储在 Data/{gameName}_packages.json，文件存储在 ObjectStore。
    ///
    /// 并发模型与 <see cref="ProfileService"/> 一致：写操作由 <see cref="_gate"/> 串行化、
    /// 结构性修改走 swap-on-write、事件在出锁后触发。详见 ProfileService 的类注释。
    /// </summary>
    public class PackageRepository : IPackageQuery
    {
        private readonly ILogger<PackageRepository> _logger;
        private readonly ObjectStore _objectStore;
        private readonly string _dataDirectory;
        private string _currentGame = string.Empty;

        /// <summary>串行化"改内存 + 落盘"。SemaphoreSlim 不可重入，锁内只能调 *Locked 方法。</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>包索引。**只整体替换，不原地增删**，使无锁读取方的枚举始终安全。</summary>
        private List<Package> _packages = new();

        /// <summary>当包列表发生变化时触发。</summary>
        public event Action? PackagesChanged;

        public PackageRepository(ILogger<PackageRepository> logger, ObjectStore objectStore)
            : this(logger, objectStore, Infrastructure.AppPaths.DataDirectory)
        {
        }

        /// <summary>
        /// 指定索引目录的构造函数（测试用）。DI 走上面的双参数构造函数——
        /// 容器无法解析 string，不会误选此重载。理由同 <see cref="OverwriteStore"/>：
        /// 索引目录归口 AppPaths 之后，测试若不注入位置就会写进开发者真实的 %LOCALAPPDATA%。
        /// </summary>
        public PackageRepository(ILogger<PackageRepository> logger, ObjectStore objectStore, string dataDirectory)
        {
            _logger = logger;
            _objectStore = objectStore;
            _dataDirectory = dataDirectory;
        }

        /// <summary>ObjectStore 实例。</summary>
        public ObjectStore Store => _objectStore;

        // ─── 初始化 ───

        /// <summary>
        /// 设置当前游戏并加载包索引。
        /// </summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            _objectStore.EnsureInitialized();

            if (!Directory.Exists(_dataDirectory))
                Directory.CreateDirectory(_dataDirectory);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _currentGame = gameName;
                var loaded = await LoadIndexAsync().ConfigureAwait(false);

                // 恢复预览图路径
                foreach (var pkg in loaded)
                {
                    if (string.IsNullOrEmpty(pkg.PreviewImagePath) || !File.Exists(pkg.PreviewImagePath))
                    {
                        var preview = _objectStore.GetPreviewImagePath(pkg.PackageKey);
                        if (preview != null)
                            pkg.PreviewImagePath = preview;
                    }
                }

                _packages = loaded;
                _logger.LogInformation("已加载 {Game} 的包索引: {Count} 个", gameName, loaded.Count);
            }
            finally { _gate.Release(); }
        }

        // ─── 查询 ───

        /// <summary>获取所有包。</summary>
        public IReadOnlyList<Package> GetAllPackages() => _packages.AsReadOnly();

        /// <summary>按 PackageKey 获取包。</summary>
        public Package? GetByKey(string packageKey)
            => FindByKey(packageKey);

        /// <summary>
        /// 无锁按 key 查找。读一次字段引用再查（写入方走 swap-on-write，不会原地增删）。
        /// 锁内代码也用它——它不抢锁，因此不会自锁。
        /// </summary>
        private Package? FindByKey(string packageKey)
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
            Package result;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 检查重复。注意：不能在锁内调 UpdatePackageAsync（它自己也要抢锁），
                // 走 *Locked 版本。
                var existing = FindByKey(package.PackageKey);
                if (existing != null)
                {
                    _logger.LogWarning("包已存在，更新: {Key}", package.PackageKey);
                    result = await UpdatePackageLockedAsync(existing, package).ConfigureAwait(false);
                }
                else
                {
                    _packages = [.. _packages, package];
                    await WriteManifestAsync(package).ConfigureAwait(false);
                    await SaveIndexAsync().ConfigureAwait(false);
                    _logger.LogInformation("包已注册: {Key} ({Kind})", package.PackageKey, package.Kind);
                    result = package;
                }
            }
            finally { _gate.Release(); }

            PackagesChanged?.Invoke();
            return result;
        }

        /// <summary>
        /// 批量注册包（数据迁移用）。
        /// </summary>
        public async Task RegisterPackagesAsync(IEnumerable<Package> packages)
        {
            var incoming = packages.ToList();

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var appended = new List<Package>();
                var known = new HashSet<string>(
                    _packages.Select(p => p.PackageKey), StringComparer.OrdinalIgnoreCase);

                foreach (var pkg in incoming)
                {
                    if (known.Add(pkg.PackageKey))
                        appended.Add(pkg);
                }

                if (appended.Count > 0)
                    _packages = [.. _packages, .. appended];

                await SaveIndexAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }

            PackagesChanged?.Invoke();
        }

        // ─── 更新 ───

        /// <summary>
        /// 更新包的元数据（名称、备注、标签、预览图）。
        /// </summary>
        public async Task<Package> UpdatePackageAsync(Package updated)
        {
            Package existing;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var found = FindByKey(updated.PackageKey)
                    ?? throw new InvalidOperationException($"包不存在: {updated.PackageKey}");
                existing = await UpdatePackageLockedAsync(found, updated).ConfigureAwait(false);
            }
            finally { _gate.Release(); }

            PackagesChanged?.Invoke();
            return existing;
        }

        /// <summary>把 <paramref name="updated"/> 的元数据合并进 <paramref name="existing"/>。调用方必须已持有 <see cref="_gate"/>。</summary>
        private async Task<Package> UpdatePackageLockedAsync(Package existing, Package updated)
        {
            existing.DisplayName = updated.DisplayName;
            existing.Note = updated.Note;
            existing.Tags = new List<string>(updated.Tags);
            existing.PreviewImagePath = updated.PreviewImagePath;
            existing.PluginTargetPath = updated.PluginTargetPath;
            existing.LastModified = DateTime.Now;

            await WriteManifestAsync(existing).ConfigureAwait(false);
            await SaveIndexAsync().ConfigureAwait(false);
            return existing;
        }

        /// <summary>
        /// 更新包的预览图。返回落盘后的路径；包不在索引中时返回 null。
        /// 预览图或索引写失败会上抛——这是用户显式发起的操作，必须看得见失败。
        /// </summary>
        public async Task<string?> UpdatePreviewImageAsync(string packageKey, string imagePath)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var package = FindByKey(packageKey);
                if (package == null) return null;

                var storedPath = _objectStore.StorePreviewImage(packageKey, imagePath);
                package.PreviewImagePath = storedPath;
                package.LastModified = DateTime.Now;
                await SaveIndexAsync().ConfigureAwait(false);
                return storedPath;
            }
            finally { _gate.Release(); }
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
        /// <exception cref="IOException">仓库文件删不掉（被占用/权限不足）。此时索引未被改动。</exception>
        public async Task<(bool Success, PackageDeletionPlan? Plan)> DeletePackageAsync(
            string packageKey,
            IEnumerable<InstanceProfile>? allProfiles,
            bool force = false)
        {
            // 引用计数在锁外算：它只读传入的 profiles，不碰本服务状态，
            // 而且 allProfiles 可能是 ProfileService 的实时视图，锁内枚举没有必要。
            var profileSnapshot = allProfiles?.ToList();

            PackageDeletionPlan? plan = null;
            bool deleted;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var package = FindByKey(packageKey);
                if (package == null) return (false, null);

                if (profileSnapshot != null)
                {
                    plan = PlanDeletion(packageKey, profileSnapshot);
                    if (plan.RequiresUserConfirmation && !force)
                    {
                        _logger.LogWarning(
                            "[PackageRepo] 拒绝删除 {Key}: {Decision} — {Explanation}",
                            packageKey, plan.Decision, plan.Explanation);
                        return (false, plan);
                    }
                }

                // 先删仓库文件、再动索引。反过来的话文件删不掉（被游戏占用/权限不足）
                // 就会留下"索引里没有、磁盘上还在几十 GB"的孤儿目录，而且用户重试也删不掉了
                // ——包已经不在索引里，界面上根本找不到它。
                if (!_objectStore.DeletePackage(packageKey))
                    throw new IOException(
                        $"无法删除包「{packageKey}」的仓库文件，文件可能被游戏占用或权限不足。索引未改动，可稍后重试。");

                _packages = _packages.Where(p => !ReferenceEquals(p, package)).ToList();
                await SaveIndexAsync().ConfigureAwait(false);

                _logger.LogInformation(
                    "[PackageRepo] 包已删除: {Key} (force={Force}, decision={Decision})",
                    packageKey, force, plan?.Decision.ToString() ?? "Unchecked");
                deleted = true;
            }
            finally { _gate.Release(); }

            if (deleted) PackagesChanged?.Invoke();
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
        /// 检查仓库完整性（manifest 存在且文件齐全，并反向检出未登记的残留目录）。
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

            issues.AddRange(FindOrphanPackageDirectories());
            return Task.FromResult(issues);
        }

        /// <summary>
        /// 反向检查：磁盘上有目录、却没有 manifest.json 的包。
        ///
        /// 只从索引出发的正向检查永远看不到这类目录，而它们正是导入中途失败的残留
        /// （先逐个文件落盘，最后才写 manifest + 索引）。
        /// 只报"无 manifest"的目录：仓库根跨游戏共享，有 manifest 但不在当前游戏索引里的，
        /// 通常是别的游戏的包，不是问题。
        /// </summary>
        private List<(string packageKey, string issue)> FindOrphanPackageDirectories()
        {
            var orphans = new List<(string, string)>();
            var indexed = new HashSet<string>(
                _packages.Select(p => p.PackageKey), StringComparer.OrdinalIgnoreCase);

            foreach (var key in _objectStore.EnumeratePackageKeys())
            {
                if (indexed.Contains(key) || _objectStore.PackageExists(key)) continue;
                orphans.Add((key, "导入残留目录（无 manifest.json，索引中也无记录）"));
            }

            if (orphans.Count > 0)
                _logger.LogWarning("发现 {Count} 个导入残留目录", orphans.Count);

            return orphans;
        }

        // ─── 持久化 ───

        private string GetIndexPath() => Path.Combine(_dataDirectory, $"{_currentGame}_packages.json");

        private async Task<List<Package>> LoadIndexAsync()
        {
            var path = GetIndexPath();
            try
            {
                if (!File.Exists(path)) return new List<Package>();
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<Package>>(json) ?? new List<Package>();
            }
            catch (Exception ex)
            {
                // 读失败回落空列表：包索引读不出来不该让整个初始化中断，
                // 否则用户连界面都进不去，比暂时看不到 MOD 列表更糟。
                // 但空列表接下来会被任意一次保存全量覆盖，原索引就此消失——
                // 故先备份原文件，与 ProfileService.BackupCorruptProfileFile /
                // GameConfigService.BackupBrokenConfigFile 的处理一致。
                _logger.LogError(ex, "加载包索引失败: {Path}", path);
                BackupUnusableIndexFile(path, ex);
                return new List<Package>();
            }
        }

        /// <summary>
        /// 备份读不出来的包索引，命名与 ProfileService / GameConfigService 对齐。
        /// </summary>
        private void BackupUnusableIndexFile(string path, Exception cause)
        {
            if (!File.Exists(path)) return;

            try
            {
                var backupPath = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Copy(path, backupPath, overwrite: false);
                _logger.LogWarning(cause, "已备份无法读取的包索引: {BackupPath}", backupPath);
            }
            catch (Exception backupException)
            {
                // 备份失败无处可退，至少留痕：此刻原文件仍在原地，直到下一次保存才会被覆盖。
                _logger.LogError(backupException, "备份无法读取的包索引失败: {Path}", path);
            }
        }

        /// <summary>
        /// 落盘包索引。
        ///
        /// 写失败必须上抛：索引丢了等于所有 MOD 的登记信息丢了（仓库里的文件还在，
        /// 界面上却什么都不剩）。此前这里把异常吞掉，用户导入/删除后看到界面正常刷新，
        /// 重启后包凭空消失。语义与 ModDataService / ProfileService 统一为 log + throw，
        /// 由调用链上的 SafeEvent.Run 或 MainViewModel 的 OperationResult 呈现。
        ///
        /// 失败时**不**回滚内存里的 <c>_packages</c>：仓库文件已经真实写进去了，
        /// 从内存抹掉只会让用户在本次会话里既看不到也删不掉它。保留内存状态 + 明确报错，
        /// 用户修好目录后下一次操作仍会把完整索引写下去。
        /// </summary>
        private async Task SaveIndexAsync()
        {
            var path = GetIndexPath();
            try
            {
                var json = JsonSerializer.Serialize(_packages, new JsonSerializerOptions { WriteIndented = true });
                await AtomicFileWriter.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存包索引失败: {Path}", path);
                throw;
            }
        }

        /// <summary>
        /// 写包的 manifest.json。写失败上抛，理由同 <see cref="SaveIndexAsync"/>：
        /// manifest 是仓库的自描述来源，缺了它 <see cref="CheckIntegrityAsync"/>
        /// 会把这个包判成"导入残留目录"。
        /// </summary>
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
                await AtomicFileWriter.WriteAllTextAsync(manifestPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入 manifest 失败: {Key}", package.PackageKey);
                throw;
            }
        }
    }
}
