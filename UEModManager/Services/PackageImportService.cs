using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;
using UEModManager.Models;
using UEModManager.Services.Detection;
using UEModManager.Services.Import;
using UEModManager.Services.Security;
using IOPath = System.IO.Path;

namespace UEModManager.Services
{
    /// <summary>
    /// 包导入结果。
    /// </summary>
    public class PackageImportResult
    {
        public bool Success { get; init; }
        public Package? Package { get; init; }
        public string? ErrorMessage { get; init; }
        public List<PackageArtifact> Artifacts { get; init; } = [];
    }

    /// <summary>
    /// 包导入服务。v2.0 唯一的导入入口（v1.x 的 ModManagementService 已删除）。
    /// 负责：解压 → 类型识别 → 文件存储到仓库 → 生成 Package + Artifacts → 注册到 Repository。
    /// </summary>
    public class PackageImportService
    {
        private readonly ILogger<PackageImportService> _logger;
        private readonly PackageRepository _repository;
        private readonly ObjectStore _objectStore;
        private readonly GameConfigService _gameConfig;
        private readonly RepositoryReclaimService _reclaim;

        /// <summary>
        /// 导入完成时触发。
        /// </summary>
        public event Action<Package>? PackageImported;

        public PackageImportService(
            ILogger<PackageImportService> logger,
            PackageRepository repository,
            ObjectStore objectStore,
            GameConfigService gameConfig,
            RepositoryReclaimService reclaim)
        {
            _logger = logger;
            _repository = repository;
            _objectStore = objectStore;
            _gameConfig = gameConfig;
            _reclaim = reclaim;
        }

        /// <summary>
        /// 当前引擎配置。
        /// </summary>
        private EngineProfile EngineConfig => EngineProfile.Get(_gameConfig.CurrentEngineType);

        // ─── 主导入入口 ───

        /// <summary>
        /// 从文件路径导入包。支持压缩包和直接文件。
        /// 返回导入成功的包列表。
        /// </summary>
        public async Task<List<PackageImportResult>> ImportAsync(string[] filePaths, string? targetRootPath = null)
        {
            var results = new List<PackageImportResult>();
            foreach (var filePath in filePaths)
            {
                var result = await ImportSingleAsync(filePath, targetRootPath);
                results.AddRange(result);
            }
            return results;
        }

        /// <summary>
        /// 导入单个文件（可能产生多个包，如压缩包内含多组 MOD）。
        /// </summary>
        public async Task<List<PackageImportResult>> ImportSingleAsync(string filePath, string? targetRootPath = null)
        {
            var results = new List<PackageImportResult>();

            try
            {
                if (!File.Exists(filePath))
                {
                    results.Add(new PackageImportResult { Success = false, ErrorMessage = "文件不存在" });
                    return results;
                }

                var kind = ImportFileKindClassifier.Classify(filePath, EngineConfig.DirectImportExtensions);
                switch (kind)
                {
                    case ImportFileKind.Compressed:
                        results = await ImportCompressedAsync(filePath, targetRootPath);
                        break;
                    case ImportFileKind.DirectImport:
                        results.Add(await ImportDirectFileAsync(filePath, targetRootPath));
                        break;
                    default:
                        results.Add(await ImportAsPluginAsync(filePath, targetRootPath));
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入文件失败: {Path}", filePath);
                results.Add(new PackageImportResult { Success = false, ErrorMessage = ex.Message });
            }

            return results;
        }

        /// <summary>
        /// 导入插件文件。
        /// </summary>
        public async Task<PackageImportResult> ImportPluginAsync(string filePath, string pluginTargetPath)
        {
            // 供补偿清理使用：包键在下面才确定，失败时据此判断能否删除残留目录
            string? pluginName = null;
            var directoryPreexisted = false;

            try
            {
                if (!File.Exists(filePath) && !Directory.Exists(filePath))
                    return new PackageImportResult { Success = false, ErrorMessage = "路径不存在" };

                var isDirectory = Directory.Exists(filePath) && !File.Exists(filePath);
                var rawName = isDirectory
                    ? new DirectoryInfo(filePath).Name
                    : IOPath.GetFileNameWithoutExtension(filePath);

                // 唯一化（索引 + 磁盘目录一起查）
                pluginName = EnsureUniquePackageKey(rawName);
                directoryPreexisted = _objectStore.PackageDirectoryExists(pluginName);

                var gameName = _gameConfig.CurrentGameName ?? "Unknown";
                var package = new Package
                {
                    PackageKey = pluginName,
                    DisplayName = pluginName,
                    Kind = PackageKind.Plugin,
                    Tags = new List<string> { "插件" },
                    HostGameName = gameName,
                    TargetRootPath = NormalizeTargetRootPath(pluginTargetPath),
                    ImportSourcePath = filePath,
                };

                // 收集文件
                List<string> files;
                if (isDirectory)
                    files = Directory.EnumerateFiles(filePath, "*.*", SearchOption.AllDirectories).ToList();
                else
                    files = new List<string> { filePath };

                // 存储到仓库
                long totalSize = 0;
                foreach (var file in files)
                {
                    var relativeName = isDirectory
                        ? IOPath.GetRelativePath(filePath, file)
                        : IOPath.GetFileName(file);
                    string safeRelativeName;
                    try
                    {
                        safeRelativeName = PathSanitizer.SanitizeRelative(relativeName);
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogWarning(ex, "跳过非法导入相对路径: {Path}", relativeName);
                        continue;
                    }

                    var (relPath, hash, size) = await _objectStore.StoreFileAsync(pluginName, file, safeRelativeName);
                    totalSize += size;

                    package.Artifacts.Add(new PackageArtifact
                    {
                        PackageId = package.Id,
                        RelativeSourcePath = relPath,
                        RelativeTargetPath = safeRelativeName,
                        FileName = IOPath.GetFileName(file),
                        FileSize = size,
                        FileHash = hash,
                        ArtifactType = ArtifactType.PluginFile,
                    });
                }

                package.TotalSize = totalSize;
                package.ContentHash = await ObjectStore.ComputeContentHashAsync(files);

                await _repository.RegisterPackageAsync(package);
                PackageImported?.Invoke(package);

                _logger.LogInformation("插件已导入: {Name} ({Count} 个文件)", pluginName, files.Count);
                return new PackageImportResult { Success = true, Package = package };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入插件失败: {Path}", filePath);
                if (pluginName != null)
                    CleanupPartialImport(pluginName, directoryPreexisted);
                return new PackageImportResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        // ─── 内部导入逻辑 ───

        private Task<PackageImportResult> ImportDirectFileAsync(string filePath, string? targetRootPath = null)
        {
            var fileName = EnsureUniquePackageKey(IOPath.GetFileNameWithoutExtension(filePath));

            return ImportWithCompensationAsync(fileName, async () =>
            {
                var gameName = _gameConfig.CurrentGameName ?? "Unknown";
                var kind = DetectPackageKind(filePath);
                var package = new Package
                {
                    PackageKey = fileName,
                    DisplayName = fileName,
                    Kind = kind,
                    Tags = new List<string> { DetectCategory(fileName) },
                    HostGameName = gameName,
                    ImportSourcePath = filePath,
                    TargetRootPath = kind == PackageKind.Mod ? null : NormalizeTargetRootPath(targetRootPath),
                };

                var (relPath, hash, size) = await _objectStore.StoreFileAsync(fileName, filePath);
                package.TotalSize = size;
                package.ContentHash = hash;

                package.Artifacts.Add(new PackageArtifact
                {
                    PackageId = package.Id,
                    RelativeSourcePath = relPath,
                    RelativeTargetPath = IOPath.GetFileName(filePath),
                    FileName = IOPath.GetFileName(filePath),
                    FileSize = size,
                    FileHash = hash,
                    ArtifactType = KindToArtifactType(kind),
                });

                // 查找同目录预览图
                var previewDir = IOPath.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(previewDir))
                {
                    var preview = FindPreviewInDirectory(previewDir);
                    if (preview != null)
                    {
                        package.PreviewImagePath = TryStorePreviewImage(fileName, preview);
                    }
                }

                await _repository.RegisterPackageAsync(package);
                PackageImported?.Invoke(package);

                return new PackageImportResult { Success = true, Package = package };
            });
        }

        private async Task<List<PackageImportResult>> ImportCompressedAsync(string filePath, string? targetRootPath = null)
        {
            var results = new List<PackageImportResult>();
            _objectStore.EnsureInitialized();
            // 临时目录固定挂在仓库根下：解压产物的最终去处就是仓库（ObjectStore.StoreFileAsync），
            // 放在这里天然与目标同盘，不会出现"临时目录在 D 盘、成品往 E 盘搬"的跨盘复制；
            // 而仓库根本身是用户可改的（UiPreferences.LoadRepositoryRoot），
            // 想让几十 GB 的解压不落在系统盘，改仓库位置就够了——不需要另一套选盘策略。
            var tempRoot = IOPath.Combine(_objectStore.RepositoryRoot, ".import-tmp");
            var tempDir = IOPath.Combine(tempRoot, $"uemod_import_{Guid.NewGuid()}");
            var archiveName = IOPath.GetFileNameWithoutExtension(filePath);

            // 先回收上次没能清掉的解压残留：本方法的 finally 会删自己的临时目录，但进程被强杀
            // （任务管理器结束进程/断电）时不会执行，几十 GB 的解压产物就永久留在仓库根下。
            // 放在导入前顺手做，比另起一个用户看不懂的按钮更合理；判据见
            // RepositoryReclaimPlanner.PlanImportTemp，无歧义故不需要确认。
            _reclaim.ReclaimStaleImportTemp(tempRoot);

            try
            {
                Directory.CreateDirectory(tempDir);

                if (!ExtractCompressedFile(filePath, tempDir))
                {
                    results.Add(new PackageImportResult { Success = false, ErrorMessage = "解压失败" });
                    return results;
                }

                // 处理嵌套压缩包
                ProcessNestedArchives(tempDir);
                CleanupArchives(tempDir);

                // 收集 MOD 文件（惰性枚举 + 过滤，避免先把整棵解压树的路径物化一遍）
                var modFiles = Directory.EnumerateFiles(tempDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsModFile(f)).ToList();

                if (modFiles.Count == 0)
                {
                    // 可能是纯插件/配置文件
                    var allFiles = Directory.EnumerateFiles(tempDir, "*.*", SearchOption.AllDirectories).ToList();
                    if (allFiles.Count > 0)
                    {
                        var packageName = EnsureUniquePackageName(archiveName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        var result = await ImportFilesAsPackageAsync(packageName, allFiles, tempDir, filePath, targetRootPath);
                        results.Add(result);
                    }
                    else
                    {
                        results.Add(new PackageImportResult { Success = false, ErrorMessage = "压缩包中无可识别文件" });
                    }
                    return results;
                }

                var groups = ModFileGrouper.SplitByImportScope(modFiles, tempDir)
                    .SelectMany(scope => GroupModFilesByPrefix(scope)
                        .Where(g => g.Value.Count > 0)
                        .Select(g => new KeyValuePair<string, List<string>>(g.Key, g.Value)))
                    .ToList();

                int index = 1;
                var usedPackageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    var packageName = DetermineGroupName(group.Value) ?? group.Key ?? $"{archiveName}_{index}";
                    packageName = EnsureUniquePackageName(packageName, usedPackageNames);

                    var result = await ImportFilesAsPackageAsync(packageName, group.Value, tempDir, filePath, targetRootPath);
                    results.Add(result);
                    index++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入压缩包失败: {Path}", filePath);
                results.Add(new PackageImportResult { Success = false, ErrorMessage = ex.Message });
            }
            finally
            {
                // 删不掉不阻断导入结果的返回（成品已经在仓库里了），但必须留痕：
                // 此前这里是裸 catch{}，几十 GB 的解压产物默默留在磁盘上，日志里一个字都没有。
                // 删不掉的会由下一次导入前的 ReclaimStaleImportTemp 回收。
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除解压临时目录失败，将在下次导入时回收: {Dir}", tempDir);
                }
            }

            return results;
        }

        private Task<PackageImportResult> ImportFilesAsPackageAsync(
            string packageName, List<string> files, string tempDir, string sourcePath, string? targetRootPath = null)
        {
            return ImportWithCompensationAsync(packageName, async () =>
            {
                var gameName = _gameConfig.CurrentGameName ?? "Unknown";
                var kind = DetectPackageKindFromFiles(files);
                var package = new Package
                {
                    PackageKey = packageName,
                    DisplayName = packageName,
                    Kind = kind,
                    Tags = new List<string> { DetectCategory(packageName) },
                    HostGameName = gameName,
                    ImportSourcePath = sourcePath,
                    TargetRootPath = kind == PackageKind.Mod ? null : NormalizeTargetRootPath(targetRootPath),
                };

                long totalSize = 0;
                var hashFiles = new List<string>();

                foreach (var file in files)
                {
                    var fileName = IOPath.GetFileName(file);
                    var (relPath, hash, size) = await _objectStore.StoreFileAsync(packageName, file);
                    totalSize += size;
                    hashFiles.Add(file);

                    package.Artifacts.Add(new PackageArtifact
                    {
                        PackageId = package.Id,
                        RelativeSourcePath = relPath,
                        RelativeTargetPath = fileName,
                        FileName = fileName,
                        FileSize = size,
                        FileHash = hash,
                        ArtifactType = DetectArtifactType(file),
                    });
                }

                package.TotalSize = totalSize;
                if (hashFiles.Count > 0)
                    package.ContentHash = await ObjectStore.ComputeContentHashAsync(hashFiles);

                // 查找预览图
                var modFileDir = IOPath.GetDirectoryName(files[0]) ?? tempDir;
                var preview = FindPreviewInDirectory(modFileDir) ?? FindPreviewInDirectory(tempDir);
                if (preview != null)
                {
                    package.PreviewImagePath = TryStorePreviewImage(packageName, preview);
                }

                await _repository.RegisterPackageAsync(package);
                PackageImported?.Invoke(package);

                return new PackageImportResult { Success = true, Package = package };
            });
        }

        private Task<PackageImportResult> ImportAsPluginAsync(string filePath, string? targetRootPath = null)
        {
            var fileName = EnsureUniquePackageKey(IOPath.GetFileNameWithoutExtension(filePath));

            return ImportWithCompensationAsync(fileName, async () =>
            {
                var gameName = _gameConfig.CurrentGameName ?? "Unknown";
                var kind = DetectPackageKind(filePath);
                var package = new Package
                {
                    PackageKey = fileName,
                    DisplayName = fileName,
                    Kind = kind,
                    Tags = new List<string> { DetectCategory(fileName) },
                    HostGameName = gameName,
                    ImportSourcePath = filePath,
                    TargetRootPath = kind == PackageKind.Mod ? null : NormalizeTargetRootPath(targetRootPath),
                };

                var (relPath, hash, size) = await _objectStore.StoreFileAsync(fileName, filePath);
                package.TotalSize = size;
                package.ContentHash = hash;

                package.Artifacts.Add(new PackageArtifact
                {
                    PackageId = package.Id,
                    RelativeSourcePath = relPath,
                    RelativeTargetPath = IOPath.GetFileName(filePath),
                    FileName = IOPath.GetFileName(filePath),
                    FileSize = size,
                    FileHash = hash,
                    ArtifactType = DetectArtifactType(filePath),
                });

                await _repository.RegisterPackageAsync(package);
                PackageImported?.Invoke(package);

                return new PackageImportResult { Success = true, Package = package };
            });
        }

        private static string? NormalizeTargetRootPath(string? targetRootPath)
        {
            if (string.IsNullOrWhiteSpace(targetRootPath)) return null;
            return PathSanitizer.SanitizeRelative(targetRootPath);
        }

        // ─── 类型检测 ───

        /// <summary>
        /// 根据文件扩展名检测包类型。
        /// </summary>
        public static PackageKind DetectPackageKind(string filePath)
            => PackageKindDetector.DetectByExtension(filePath);

        /// <summary>
        /// 根据多个文件检测包类型。
        /// </summary>
        public static PackageKind DetectPackageKindFromFiles(IEnumerable<string> files)
            => PackageKindDetector.AggregateFromFiles(files);

        private ArtifactType DetectArtifactType(string filePath)
            => ArtifactTypeDetector.DetectForImport(filePath, EngineConfig.ModFileExtensions);

        private static ArtifactType KindToArtifactType(PackageKind kind)
            => PackageKindDetector.KindToArtifactType(kind);

        // ─── 分类检测 ───

        /// <summary>
        /// 根据名称智能推测分类。委托 Core 的 ModCategoryClassifier。
        /// </summary>
        public static string DetectCategory(string name)
            => ModCategoryClassifier.Classify(name);

        // ─── 解压缩（委托 ArchiveExtractor） ───
        private bool ExtractCompressedFile(string filePath, string extractPath)
            => ArchiveExtractor.ExtractCompressedFile(filePath, extractPath, _logger);
        private void ProcessNestedArchives(string directory)
            => ArchiveExtractor.ProcessNestedArchives(directory, _logger);
        private static void CleanupArchives(string directory)
            => ArchiveExtractor.CleanupArchives(directory);

        // ─── 文件分组（委托 Core ModFileGrouper） ───

        private Dictionary<string, List<string>> GroupModFilesByPrefix(List<string> modFiles)
        {
            var grouped = ModFileGrouper.GroupByBaseName(modFiles);

            // 剑星 CNS 模式：外观 .pak 与 CNS 配置 .json 文件名不同源
            // （DekCNS-Nier2B.json 配 Nier2B_P.pak），按基础名分组会拆成两个包，
            // 用户只启用其中一个则配置不生效。这里把它们合回一个包。
            if (_gameConfig.CurrentGameType == GameType.StellarBladeCNS)
                grouped = CnsGroupMerger.Merge(grouped);

            return grouped.ToDictionary(
                kv => kv.Key,
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        private string? DetermineGroupName(List<string> groupFiles)
            => ModFileGrouper.SelectGroupName(groupFiles, EngineConfig.GroupPriorityExtensions);

        private string EnsureUniquePackageName(string packageName, HashSet<string> usedPackageNames)
        {
            var uniqueName = packageName;
            var suffix = 1;
            while (IsPackageKeyTaken(uniqueName) || usedPackageNames.Contains(uniqueName))
            {
                suffix++;
                uniqueName = $"{packageName}_{suffix}";
            }

            usedPackageNames.Add(uniqueName);
            return uniqueName;
        }

        /// <summary>
        /// 包键是否已被占用：索引里有记录，**或**磁盘上已有同名目录。
        ///
        /// 只查索引不够：导入中途失败会留下"有 files/、无 manifest、无索引"的孤儿目录，
        /// 复用该键会让 StoreFileAsync 的 File.Copy(overwrite:true) 把残留文件混进新包。
        /// 另外仓库根是跨游戏共享的，而索引是按游戏分的，只查索引还会撞上别的游戏的包目录。
        /// </summary>
        private bool IsPackageKeyTaken(string packageKey)
            => _repository.Exists(packageKey) || _objectStore.PackageDirectoryExists(packageKey);

        /// <summary>用时间戳后缀避开已被占用的包键（同一秒内再撞则继续加序号）。</summary>
        private string EnsureUniquePackageKey(string packageKey)
        {
            if (!IsPackageKeyTaken(packageKey)) return packageKey;

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var candidate = $"{packageKey}_{stamp}";
            var suffix = 1;
            while (IsPackageKeyTaken(candidate))
                candidate = $"{packageKey}_{stamp}_{++suffix}";

            return candidate;
        }

        /// <summary>
        /// 包装一次"逐文件落盘 → 注册"的导入，失败时补偿删除本次创建的包目录。
        ///
        /// 不补偿的话，磁盘上会留下没有 manifest、没有索引记录的孤儿目录：
        /// ObjectStore 没有任何启动期 GC，CheckIntegrityAsync 又只从索引出发查，
        /// 这些目录既不会被发现也不会被回收，只会在下次同名导入时把残留文件混进新包。
        /// </summary>
        private async Task<PackageImportResult> ImportWithCompensationAsync(
            string packageKey, Func<Task<PackageImportResult>> importAsync)
        {
            var directoryPreexisted = _objectStore.PackageDirectoryExists(packageKey);

            try
            {
                return await importAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入失败: {Package}", packageKey);
                CleanupPartialImport(packageKey, directoryPreexisted);
                return new PackageImportResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>只删本次导入自己创建、且最终没能登记成功的包目录。</summary>
        private void CleanupPartialImport(string packageKey, bool directoryPreexisted)
        {
            if (_repository.Exists(packageKey))
            {
                // 已经进了索引，说明失败发生在注册之后（如事件回调）：删目录只会制造索引与磁盘不一致
                _logger.LogWarning("包 {Package} 已登记在索引中，跳过残留清理", packageKey);
                return;
            }

            if (directoryPreexisted)
            {
                // 目录在本次导入前就存在，可能是别的包/别的游戏的数据，不能替用户做主删除
                _logger.LogWarning("包目录 {Package} 在本次导入前已存在，跳过残留清理", packageKey);
                return;
            }

            if (_objectStore.DeletePackage(packageKey))
                _logger.LogInformation("已清理导入失败残留的包目录: {Package}", packageKey);
        }

        // ─── 辅助方法 ───

        private bool IsModFile(string filePath)
            => EngineConfig.ModFileExtensions.Contains(IOPath.GetExtension(filePath).ToLower());

        private static string? FindPreviewInDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return null;
            var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            return PreviewImageSelector.Select(files);
        }

        /// <summary>
        /// 存预览图，失败只记 Warning 并返回 null。
        ///
        /// <see cref="ObjectStore.StorePreviewImage"/> 现在会把写失败抛出来，但在导入路径上
        /// 不能让它中止整包导入：MOD 文件本体可能已经全部落盘，为了一张缩略图回滚
        /// 是拿主要功能给次要功能陪葬。包会以"没有预览图"的形态正常入库。
        /// </summary>
        private string? TryStorePreviewImage(string packageKey, string sourceImagePath)
        {
            try
            {
                return _objectStore.StorePreviewImage(packageKey, sourceImagePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "存储预览图失败，包将以无预览图的形态导入: {Package}", packageKey);
                return null;
            }
        }
    }
}
