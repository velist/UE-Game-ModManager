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
using UEModManager.Services.Conflict;
using UEModManager.Services.Detection;
using UEModManager.Services.Import;
using UEModManager.Services.Security;
using UEModManager.Localization;
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
    /// 仓库里已有内容完全相同的包。
    ///
    /// 用异常而不是返回值传递，是为了复用 <see cref="PackageImportService"/> 的
    /// 补偿清理：文件这时候已经落盘，只有补偿路径知道包目录是不是本次新建的。
    /// </summary>
    public sealed class DuplicateModContentException(string existingDisplayName, string message)
        : Exception(message)
    {
        public string ExistingDisplayName { get; } = existingDisplayName;
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
        private readonly AssetIndexService _assetIndexService;
        private readonly long _maximumExtractedBytes;

        /// <summary>
        /// 导入完成时触发。
        /// </summary>
        public event Action<Package>? PackageImported;

        public PackageImportService(
            ILogger<PackageImportService> logger,
            PackageRepository repository,
            ObjectStore objectStore,
            GameConfigService gameConfig,
            RepositoryReclaimService reclaim,
            AssetIndexService assetIndexService)
            : this(logger, repository, objectStore, gameConfig, reclaim, assetIndexService,
                ArchiveExtractor.MaxTotalExtractedBytes)
        {
        }

        internal PackageImportService(
            ILogger<PackageImportService> logger,
            PackageRepository repository,
            ObjectStore objectStore,
            GameConfigService gameConfig,
            RepositoryReclaimService reclaim,
            AssetIndexService assetIndexService,
            long maximumExtractedBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumExtractedBytes);
            _logger = logger;
            _repository = repository;
            _objectStore = objectStore;
            _gameConfig = gameConfig;
            _reclaim = reclaim;
            _assetIndexService = assetIndexService;
            _maximumExtractedBytes = maximumExtractedBytes;
        }

        /// <summary>
        /// 当前引擎配置。
        /// </summary>
        private EngineProfile EngineConfig => EngineProfile.Get(_gameConfig.CurrentEngineType);

        // ─── 主导入入口 ───

        /// <summary>
        /// 导入成功后立刻在后台建一次容器索引。
        ///
        /// 不预热的话，用户第一次打开冲突面板要现场读十几个 pak 的索引 ——
        /// 面板会先卡几秒再出内容。预热是 fire-and-forget：失败只记日志，
        /// 反正真分析时还会再读一次，不影响导入结果。
        /// </summary>
        private void WarmUpImported(IReadOnlyList<PackageImportResult> results)
        {
            foreach (var package in results.Where(r => r.Success && r.Package is not null)
                         .Select(r => r.Package!))
            {
                _ = _assetIndexService.WarmUpAsync(package);
            }
        }

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
            WarmUpImported(results);
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
                var isDirectory = Directory.Exists(filePath) && !File.Exists(filePath);
                if (!File.Exists(filePath) && !isDirectory)
                {
                    results.Add(new PackageImportResult { Success = false, ErrorMessage = "文件或文件夹不存在" });
                    return results;
                }

                // 文件夹不是压缩包也不是可直读文件：走目录导入，保留其内部目录结构。
                // 旧实现用 File.Exists 判定，文件夹一路被拒成"文件不存在"，
                // 而唯一支持目录的 ImportPluginAsync 没有任何调用方 —— 拖文件夹导入因此完全不可用。
                if (isDirectory)
                {
                    results = await ImportFolderAsync(filePath, targetRootPath);
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
                    // 目录导入保留目录结构，RelativeTargetPath 即相对游戏根的安装位置：
                    // 裸文件名（dwmapi.dll / version.dll）必须落在游戏根，不能再套包目录。
                    // 单文件导入没有方位信息，保持 false 靠包目录隔离避免同名互相覆盖。
                    PreservesInstallLayout = isDirectory,
                };

                // 收集文件。目录导入同样按黑名单过滤，避免把调试符号/日志一起搬进仓库。
                List<string> files;
                if (isDirectory)
                    files = Directory.EnumerateFiles(filePath, "*.*", SearchOption.AllDirectories)
                        .Where(ArchiveImportPolicy.ShouldImport)
                        .ToList();
                else
                    files = new List<string> { filePath };

                // 存储到仓库
                long totalSize = 0;
                var seenArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    // 保留目录结构，但剥掉文件夹自带的游戏根层级
                    // （b1/Binaries/Win64/ue4ss/... → ue4ss/...），
                    // 否则会部署成 <gamePath>/b1/Binaries/Win64/ue4ss/... 而游戏找不到。
                    var relativeName = isDirectory
                        ? ArchiveTargetPathResolver.StripLeadingGameAnchor(
                            IOPath.GetRelativePath(filePath, file)
                                .Replace(IOPath.AltDirectorySeparatorChar, IOPath.DirectorySeparatorChar))
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

                    if (string.IsNullOrWhiteSpace(safeRelativeName)) continue;

                    var (relPath, hash, size) = await _objectStore.StoreFileAsync(pluginName, file, safeRelativeName);
                    if (!seenArtifacts.Add(safeRelativeName + "\n" + hash)) continue;

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

                // 与压缩包导入同一道拦截：内容完全相同的 MOD 再导一次没有意义。
                // 直接拖一个 .pak 进来走的是这条路径，漏掉它就会出现
                // "红色经文金箍棒_2 与 红色经文金箍棒 内容完全相同"这种重复包。
                if (kind == PackageKind.Mod)
                {
                    var duplicate = FindDuplicateModByContent(package.Artifacts, fileName);
                    if (duplicate is not null)
                    {
                        throw new DuplicateModContentException(
                            duplicate.DisplayName,
                            UiText.Interpolate($"仓库已有相同内容的 MOD：{duplicate.DisplayName}"));
                    }
                }
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

                var extractionBudget = new ExtractionBudget(_maximumExtractedBytes);
                if (!ArchiveExtractor.ExtractCompressedFile(filePath, tempDir, _logger, extractionBudget))
                {
                    results.Add(new PackageImportResult
                    {
                        Success = false,
                        ErrorMessage = extractionBudget.IsExceeded ? "解压体积超过上限" : "解压失败"
                    });
                    return results;
                }

                // 处理嵌套压缩包
                var nested = ArchiveExtractor.ProcessNestedArchives(
                    tempDir, _logger, ArchiveExtractor.MaxNestingDepth, extractionBudget);
                if (nested.StoppedBySizeLimit)
                {
                    results.Add(new PackageImportResult { Success = false, ErrorMessage = "解压体积超过上限" });
                    return results;
                }
                CleanupArchives(tempDir);

                // 收集解压产物。这里不再按 EngineProfile 的扩展名白名单过滤——
                // 白名单只含 .pak/.ucas/.utoc/.json，会把插件型 MOD 的 .lua/.dll/.ini
                // 整包丢掉且不留日志（实测 1194 个文件丢 1174 个）。
                // 现改为黑名单：只排除调试符号、日志、系统垃圾与第三方可执行程序。
                var allFiles = Directory.EnumerateFiles(tempDir, "*.*", SearchOption.AllDirectories).ToList();
                var keptFiles = new List<string>();
                var droppedCount = 0;
                long droppedBytes = 0;
                foreach (var candidate in allFiles)
                {
                    if (ArchiveImportPolicy.ShouldImport(candidate))
                    {
                        keptFiles.Add(candidate);
                    }
                    else
                    {
                        droppedCount++;
                        try { droppedBytes += new FileInfo(candidate).Length; } catch (IOException) { }
                    }
                }

                if (droppedCount > 0)
                {
                    // 每次排除都留痕：旧实现静默丢弃，用户与日志都无从察觉。
                    _logger.LogInformation(
                        "导入 {Archive}: 保留 {Kept} 个文件，按策略排除 {Dropped} 个（{Bytes} 字节）",
                        archiveName, keptFiles.Count, droppedCount, droppedBytes);
                }

                if (keptFiles.Count == 0)
                {
                    results.Add(new PackageImportResult { Success = false, ErrorMessage = "压缩包中无可识别文件" });
                    return results;
                }

                await ImportCollectedFilesAsync(keptFiles, tempDir, archiveName, filePath, targetRootPath, results);
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
            string packageName, List<string> files, string tempDir, string sourcePath,
            string? targetRootPath = null, bool keepDirectoryStructure = false, PackageKind? kindOverride = null)
        {
            return ImportWithCompensationAsync(packageName, async () =>
            {
                var gameName = _gameConfig.CurrentGameName ?? "Unknown";
                var kind = kindOverride ?? DetectPackageKindFromFiles(files);
                var package = new Package
                {
                    PackageKey = packageName,
                    DisplayName = packageName,
                    Kind = kind,
                    Tags = new List<string> { DetectCategory(packageName) },
                    HostGameName = gameName,
                    ImportSourcePath = sourcePath,
                    TargetRootPath = kind == PackageKind.Mod ? null : NormalizeTargetRootPath(targetRootPath),
                    // 保留目录结构的导入（文件夹 / 压缩包整体导入的产物）：
                    // RelativeTargetPath 就是相对游戏根的安装位置，裸文件名即必须在游戏根。
                    // 部署时不得再套包目录，否则 UE4SS / CSharpLoader 的代理 DLL 会被关进子目录。
                    PreservesInstallLayout = keepDirectoryStructure,
                };

                long totalSize = 0;
                var hashFiles = new List<string>();
                // 同一个包内不允许出现重复的 (目标相对路径, 哈希)：
                // 压缩包里不同文件夹下的同名同内容文件（如两个 ue4ss 的 mods.json）
                // 会折叠成同一个目标路径，重复登记只会撑大索引并制造假冲突。
                var seenArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 共有包装目录必须在"剥游戏根锚点之前"算：剥完锚点后最短的路径可能只剩裸文件名
                // （b1/Binaries/Win64/version.dll -> version.dll），共有前缀就再也算不出来，
                // 同包里没命中锚点的文件（MOD名/说明与事项.txt）会带着 MOD 名目录落进游戏根，
                // 在游戏根下留下一个以 MOD 命名的杂项目录。
                var relativePaths = keepDirectoryStructure
                    ? files.Select(f => ArchiveTargetPathResolver.ResolveRelativePath(f, tempDir)).ToList()
                    : files.Select(IOPath.GetFileName).ToList();
                var commonPrefix = keepDirectoryStructure
                    ? ArchiveTargetPathResolver.StripCommonPrefix(relativePaths)
                    : string.Empty;

                foreach (var file in files)
                {
                    var relativePath = keepDirectoryStructure
                        ? ArchiveTargetPathResolver.Resolve(file, tempDir, true, commonPrefix)
                        : IOPath.GetFileName(file);
                    relativePath = ArchiveTargetPathResolver.NormalizeGameRelativePath(relativePath);
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;

                    var (relPath, hash, size) = await _objectStore.StoreFileAsync(packageName, file, relativePath);
                    if (!seenArtifacts.Add(relativePath + "\n" + hash)) continue;

                    totalSize += size;
                    hashFiles.Add(file);

                    package.Artifacts.Add(new PackageArtifact
                    {
                        PackageId = package.Id,
                        RelativeSourcePath = relPath,
                        RelativeTargetPath = relativePath,
                        FileName = IOPath.GetFileName(file),
                        FileSize = size,
                        FileHash = hash,
                        ArtifactType = DetectArtifactType(file),
                    });
                }

                package.TotalSize = totalSize;
                if (hashFiles.Count > 0)
                    package.ContentHash = await ObjectStore.ComputeContentHashAsync(hashFiles);

                // 内容完全相同的 MOD 再导一次没有意义：玩家的"装错了"表现是列表里
                // 出现两个一模一样的 MOD，之后还要靠冲突面板的"重复"提示去删。
                // 在入口拦住并说清是哪一个，比事后让他自己发现更好。
                var duplicate = kind == PackageKind.Mod
                    ? FindDuplicateModByContent(package.Artifacts, packageName)
                    : null;
                if (duplicate is not null)
                {
                    // 走异常而不是就地 return：包目录已经落盘了，必须由
                    // ImportWithCompensationAsync 的补偿清理收尾（它才知道目录
                    // 是不是本次新建的）。就地 return 会把残留目录留在仓库里。
                    throw new DuplicateModContentException(
                        duplicate.DisplayName,
                        UiText.Interpolate($"仓库已有相同内容的 MOD：{duplicate.DisplayName}"));
                }

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

        /// <summary>
        /// 目录导入的默认落点（相对游戏根）。
        ///
        /// 拖入文件夹时用户没有机会填安装目录。游戏根（<c>CurrentGamePath</c>）本身就已经是
        /// UE 的 <c>.../b1/Binaries/Win64</c>，所以这里必须是空串：
        /// 再填一次 <c>Binaries/Win64</c> 会部署成 <c>.../Win64/Binaries/Win64/...</c>，
        /// 与 UE4SS 约定目录错位，插件永远不生效。
        /// 文件夹里剥掉游戏根锚点后剩下的层级（<c>ue4ss/Mods/...</c>）就是最终安装位置。
        /// </summary>
        private const string DefaultPluginTargetRootPath = "";

        /// <summary>
        /// 目录导入：按**内容**决定是 MOD 包还是插件/配置包，而不是一律当成插件。
        ///
        /// 用户手动解压后把文件夹拖进来是最常见的用法（RAR 本身就要求先手动解压），
        /// 而旧实现把文件夹拒成"文件不存在"，唯一支持目录的 ImportPluginAsync 没有调用方。
        /// 这里复用压缩包导入的同一套分组逻辑：有 .pak 就按 pak 分组出多个 MOD 包
        /// （同名 pak + 配置归一组），没有 .pak 才整包当作插件/配置保留目录结构。
        /// </summary>
        private async Task<List<PackageImportResult>> ImportFolderAsync(string folderPath, string? targetRootPath)
        {
            var results = new List<PackageImportResult>();

            var keptFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(ArchiveImportPolicy.ShouldImport)
                .ToList();
            if (keptFiles.Count == 0)
            {
                results.Add(new PackageImportResult { Success = false, ErrorMessage = "文件夹中无可识别文件" });
                return results;
            }

            var archiveName = new DirectoryInfo(folderPath).Name;
            await ImportCollectedFilesAsync(
                keptFiles, folderPath, archiveName, folderPath,
                targetRootPath ?? DefaultPluginTargetRootPath, results);
            return results;
        }

        /// <summary>
        /// 压缩包与文件夹共用的"已筛选文件 → 包"分配逻辑。
        ///
        /// 以**顶层目录**为界把一个文件夹/压缩包切成多个互不相干的包：
        /// 一个顶层目录 = 一个 MOD。这是打包者表达"我这里面有几个 MOD"的唯一方式，
        /// 丢掉它就等于把整包当成一个 MOD。
        ///
        /// 旧实现把整包所有 <c>.pak</c> 丢进同一个分组池按基础名分组，再把全部非 MOD 载荷
        /// 折成一个以压缩包名命名的包。实测 <c>0916.zip</c>（7 个 MOD 文件夹、14 个 .pak、
        /// 1177 个插件载荷文件）因此变成单个名叫 <c>0916</c> 的包：界面里只有一个条目，
        /// 而冲突检测也随之失效 —— 所有文件同属一个 PackageKey，
        /// <c>ConflictDetector</c> 去重后只剩一个声称者，"两个包抢同一路径"永远不成立
        /// （实测该压缩包内有 583 处跨 MOD 重叠，全部漏报）。
        ///
        /// 每个单元内部再按 MOD 文件分组：单元里没有 .pak 时整单元做成一个保留目录结构的
        /// 插件/配置包；有 .pak 时按基础名分组成 MOD 包，剩下的非 MOD 载荷另成一个包。
        /// </summary>
        private async Task ImportCollectedFilesAsync(
            List<string> keptFiles, string rootDirectory, string archiveName, string sourcePath,
            string? targetRootPath, List<PackageImportResult> results)
        {
            var usedPackageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var units = ArchiveImportUnitResolver.Partition(keptFiles, rootDirectory);
            var multiUnit = units.Count > 1 || (units.Count == 1 && units[0].Name != ArchiveImportUnitResolver.RootUnitName);

            // 顶层第一段是 b1 / Binaries 这类引擎锚点，说明这不是"一个压缩包里装了多个 MOD"，
            // 而是"这一个 MOD 自带游戏目录层级"（用户单独拖入解压后的「超级识破」文件夹，
            // 或压缩包内容直接是 b1\Binaries\...）。退回单单元，用压缩包名 / 文件夹名命名，
            // 否则包名会变成 b1，第二个这样的 MOD 变成 b1_2，真名全丢。
            if (multiUnit && ArchiveImportUnitResolver.LooksLikeSingleModLayout(units))
            {
                _logger.LogWarning(
                    "导入 {Name}: 顶层目录 {Units} 命中安装锚点，按「整个输入就是一个 MOD」处理，" +
                    "包名改用压缩包/文件夹名；若这是把多个 MOD 打进了同一个压缩包，请重新打包",
                    archiveName,
                    string.Join(", ", units.Select(u => u.Name)));
                multiUnit = false;
            }

            if (multiUnit)
            {
                // 以压缩包名命名根单元，其余单元用自己的目录名。
                // 目录名天然互不相同，_2 这类后缀因此不再出现（旧实现里每个单元
                // 都拿同一批 pak 去抢同一个基础名，撞名后被迫加序号）。
                foreach (var unit in units)
                {
                    var unitName = string.IsNullOrEmpty(unit.Name) ? archiveName : unit.Name;
                    await ImportUnitAsync(unit.Files, rootDirectory, unitName, sourcePath,
                        targetRootPath, usedPackageNames, results);
                }
                return;
            }

            // 没有可用的顶层目录（压缩包根下直接平铺文件）：沿用单包行为。
            await ImportUnitAsync(keptFiles, rootDirectory, archiveName, sourcePath,
                targetRootPath, usedPackageNames, results);
        }

        /// <summary>
        /// 导入单个单元（一个 MOD 文件夹的全部文件）。
        /// </summary>
        private async Task ImportUnitAsync(
            IReadOnlyList<string> unitFiles, string rootDirectory, string unitName, string sourcePath,
            string? targetRootPath, HashSet<string> usedPackageNames, List<PackageImportResult> results)
        {
            // 单元根上的说明文件（readme*、说明*、*.txt）不部署：
            // 它们会被原样复制进游戏目录，在 Win64 根下留下"steam属性口令.txt"
            // 这类和游戏毫无关系的文件。
            //
            // 本方法的 unitFiles 是相对**解压根**的，而单元边界就是解压根下的第一段目录
            // （见 ArchiveImportUnitResolver），所以实测 0916.zip 里的
            // 「红色经文金箍棒/steam属性口令.txt」在这里看是低了一层。
            // "是否只有一个顶层目录"区分两种情况：单元被包了一层时（只有 MOD名/）
            // 那层是包装，说明文件其实就在单元根上；而 ue4ss/ 与 b1/ 并列时
            // 那些是真实安装位置，里面的 mods.txt 属于运行时，不能当说明丢掉。
            var topLevelDirectories = unitFiles
                .Select(f => Path.GetRelativePath(rootDirectory, f))
                .Where(p => p.Contains(Path.DirectorySeparatorChar))
                .Select(p => p[..p.IndexOf(Path.DirectorySeparatorChar)])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 只有一个顶层目录、且"低一层是单元根"还不足以判定它是包装：
            // 单元内容直接就是「ue4ss/Mods/…」（用户单独拖入解压后的 UE4SS MOD）时，
            // 那一层是真实安装位置，里面的 mods.txt 不能当说明丢掉。
            // 安装锚点名录是现成的判据 —— 与 ArchiveImportUnitResolver 判断
            // "这一层是包装还是安装位置"用的是同一套词汇。
            var wrapped = topLevelDirectories.Count == 1
                && !ArchiveTargetPathResolver.IsInstallAnchor(topLevelDirectories[0]);
            var attachments = unitFiles
                .Where(f => SharedComponentClassifier.IsAttachment(
                    Path.GetRelativePath(rootDirectory, f), wrapped))
                .ToList();
            if (attachments.Count > 0)
            {
                var dropped = attachments.Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } });
                _logger.LogInformation("导入 {Unit}: 跳过 {Count} 个说明文件（{Bytes} 字节）",
                    unitName, attachments.Count, dropped);
                unitFiles = unitFiles.Except(attachments, StringComparer.OrdinalIgnoreCase).ToList();
                if (unitFiles.Count == 0) return;
            }

            var modFiles = unitFiles.Where(IsBinaryModFile).ToList();

            if (modFiles.Count == 0)
            {
                // 纯插件/配置（UE4SS / CSharpLoader 型 MOD）：整单元一个包，保留目录结构，
                // 否则插件按约定找不到自己的文件。
                results.Add(await ImportFilesAsPackageAsync(
                    EnsureUniquePackageName(unitName, usedPackageNames), [.. unitFiles],
                    rootDirectory, sourcePath, targetRootPath,
                    keepDirectoryStructure: true, kindOverride: KindForPayload(unitFiles)));
                return;
            }

            var groups = ModFileGrouper.SplitByImportScope(modFiles, rootDirectory)
                .SelectMany(scope => GroupModFilesByPrefix(scope)
                    .Where(g => g.Value.Count > 0)
                    .Select(g => new KeyValuePair<string, List<string>>(g.Key, g.Value)))
                .ToList();

            var index = 1;
            foreach (var group in groups)
            {
                var packageName = DetermineGroupName(group.Value) ?? group.Key ?? $"{unitName}_{index}";
                packageName = EnsureUniquePackageName(packageName, usedPackageNames);

                results.Add(await ImportFilesAsPackageAsync(
                    packageName, group.Value, rootDirectory, sourcePath, targetRootPath));
                index++;
            }

            // 单元内没被任何 MOD 组认领的载荷不能丢：单独成一个保留目录结构的包。
            var claimed = groups.SelectMany(g => g.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unclaimed = unitFiles.Where(f => !modFiles.Contains(f) && !claimed.Contains(f)).ToList();
            if (unclaimed.Count > 0)
            {
                var packageName = EnsureUniquePackageName(unitName, usedPackageNames);
                _logger.LogInformation("导入 {Unit}: {Count} 个非 MOD 文件单独成包 {Package}",
                    unitName, unclaimed.Count, packageName);
                // 这一包里全是插件/配置载荷，显式定型：按扩展名聚合会因单个 .pak 被判成 Mod，
                // 从而落错部署根（MOD 根而不是游戏根）。
                results.Add(await ImportFilesAsPackageAsync(
                    packageName, unclaimed, rootDirectory, sourcePath, targetRootPath,
                    keepDirectoryStructure: true, kindOverride: KindForPayload(unclaimed)));
            }
        }

        /// <summary>
        /// 非 MOD 载荷的包类型：含 DLL/SO 就是插件，否则算配置。
        /// 不用 <see cref="DetectPackageKindFromFiles"/> —— 它见到单个 .pak 就整体判成 Mod，
        /// 会让这些"必须落在游戏根"的文件被部署到 MOD 根。
        /// </summary>
        private static PackageKind KindForPayload(IReadOnlyList<string> files)
            => files.Any(f => IOPath.GetExtension(f).ToLowerInvariant() is ".dll" or ".exe" or ".so")
                ? PackageKind.Plugin
                : PackageKind.Config;

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

        /// <summary>
        /// 用序号后缀避开已被占用的包键。
        ///
        /// 取 _2、_3… 而不是时间戳：用户看到的包名是"红色经文金箍棒_2"这种，
        /// 一眼能对上"这是同一款 MOD 的第二份"；时间戳后缀
        /// （…_20260922_143012）只会让人以为是另一个 MOD。
        /// </summary>
        private string EnsureUniquePackageKey(string packageKey)
        {
            if (!IsPackageKeyTaken(packageKey)) return packageKey;

            var suffix = 2;
            var candidate = $"{packageKey}_{suffix}";
            while (IsPackageKeyTaken(candidate))
                candidate = $"{packageKey}_{++suffix}";

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
                // 重复内容不算错误：这是导入入口的主动拦截，日志按 Information 记，
                // 失败消息原样带给用户（"仓库已有相同内容的 MOD：xxx"）。
                if (ex is DuplicateModContentException duplicate)
                {
                    _logger.LogInformation("导入 {Package}: 仓库已有相同内容的 {Existing}，取消导入",
                        packageKey, duplicate.ExistingDisplayName);
                }
                else
                {
                    _logger.LogError(ex, "导入失败: {Package}", packageKey);
                }
                CleanupPartialImport(packageKey, directoryPreexisted);
                return new PackageImportResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// 仓库里是否已有"内容完全相同"的包。
        ///
        /// 用**逐文件哈希的多重集**判定，不用 <see cref="Package.ContentHash"/>：
        /// 那个哈希把 <c>RelativeSourcePath</c> 也算进去，同一个 MOD 换个文件夹名导入
        /// 就会算出不同的值，拿它去重等于没去重。
        ///
        /// 只对**文件完全一致**的 MOD 判重：真·重复 MOD（红色经文金箍棒 / _2）文件数
        /// 相同、逐个哈希相同。凡是文件集合或数量不同的，一个都不拦 —— 两个各带一份
        /// 相同 dwmapi.dll 的插件 MOD 内容并不相同（判重在调用方已限定 Kind == Mod，
        /// 插件的代理 DLL 由共享组件裁决处理）；压缩包里夹了一篇导入说明
        /// （说明与事项.txt）的 MOD，除了那篇说明外完全一致，也不该被当成重复。
        /// </summary>
        private Package? FindDuplicateModByContent(IReadOnlyList<PackageArtifact> artifacts, string packageKey)
        {
            var own = artifacts.Select(a => a.FileHash).OrderBy(h => h, StringComparer.Ordinal).ToList();
            if (own.Count == 0) return null;

            foreach (var candidate in _repository.GetAllPackages())
            {
                if (string.Equals(candidate.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (candidate.PackageKey.StartsWith(packageKey + "_", StringComparison.OrdinalIgnoreCase)) continue;
                if (candidate.Artifacts.Count != own.Count) continue;

                var theirs = candidate.Artifacts.Select(a => a.FileHash)
                    .OrderBy(h => h, StringComparer.Ordinal).ToList();
                if (own.SequenceEqual(theirs, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("导入 {Package}: 与 {Existing} 内容完全相同", packageKey, candidate.PackageKey);
                    return candidate;
                }
            }

            return null;
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

        /// <summary>
        /// 是否为"二进制 MOD 载荷"：UE 只会加载这些扩展名，且 ~mods 不递归子目录。
        ///
        /// 刻意不用 <see cref="IsModFile"/>：引擎的 ModFileExtensions 含 <c>.json</c>，
        /// 用它做分流会把 UE4SS 的 mods.json 当 MOD 处理，并让同包的 .lua/.dll 变成
        /// 无人认领的孤儿文件而被静默丢弃。
        /// </summary>
        private static bool IsBinaryModFile(string filePath)
            => IOPath.GetExtension(filePath).ToLowerInvariant() is ".pak" or ".ucas" or ".utoc";

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
