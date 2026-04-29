using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using UEModManager.Models;
using UEModManager.Services.Detection;
using UEModManager.Services.Import;
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
    /// 包导入服务。
    /// 从 ModManagementService 的导入逻辑抽取而来，整合 ObjectStore 和 PackageRepository。
    /// 负责：解压 → 类型识别 → 文件存储到仓库 → 生成 Package + Artifacts → 注册到 Repository。
    /// </summary>
    public class PackageImportService
    {
        private readonly ILogger<PackageImportService> _logger;
        private readonly PackageRepository _repository;
        private readonly ObjectStore _objectStore;
        private readonly GameConfigService _gameConfig;

        /// <summary>
        /// 导入完成时触发。
        /// </summary>
        public event Action<Package>? PackageImported;

        public PackageImportService(
            ILogger<PackageImportService> logger,
            PackageRepository repository,
            ObjectStore objectStore,
            GameConfigService gameConfig)
        {
            _logger = logger;
            _repository = repository;
            _objectStore = objectStore;
            _gameConfig = gameConfig;
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
        public async Task<List<PackageImportResult>> ImportAsync(string[] filePaths)
        {
            var results = new List<PackageImportResult>();
            foreach (var filePath in filePaths)
            {
                var result = await ImportSingleAsync(filePath);
                results.AddRange(result);
            }
            return results;
        }

        /// <summary>
        /// 导入单个文件（可能产生多个包，如压缩包内含多组 MOD）。
        /// </summary>
        public async Task<List<PackageImportResult>> ImportSingleAsync(string filePath)
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
                        results = await ImportCompressedAsync(filePath);
                        break;
                    case ImportFileKind.DirectImport:
                        results.Add(await ImportDirectFileAsync(filePath));
                        break;
                    default:
                        results.Add(await ImportAsPluginAsync(filePath));
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
            try
            {
                if (!File.Exists(filePath) && !Directory.Exists(filePath))
                    return new PackageImportResult { Success = false, ErrorMessage = "路径不存在" };

                var isDirectory = Directory.Exists(filePath) && !File.Exists(filePath);
                var pluginName = isDirectory
                    ? new DirectoryInfo(filePath).Name
                    : IOPath.GetFileNameWithoutExtension(filePath);

                // 唯一化
                if (_repository.Exists(pluginName))
                    pluginName = $"{pluginName}_{DateTime.Now:yyyyMMdd_HHmmss}";

                var gameName = _gameConfig.CurrentGameName ?? "Unknown";
                var package = new Package
                {
                    PackageKey = pluginName,
                    DisplayName = pluginName,
                    Kind = PackageKind.Plugin,
                    Tags = new List<string> { "插件" },
                    HostGameName = gameName,
                    PluginTargetPath = pluginTargetPath,
                    ImportSourcePath = filePath,
                };

                // 收集文件
                List<string> files;
                if (isDirectory)
                    files = Directory.GetFiles(filePath, "*.*", SearchOption.AllDirectories).ToList();
                else
                    files = new List<string> { filePath };

                // 存储到仓库
                long totalSize = 0;
                foreach (var file in files)
                {
                    var relativeName = isDirectory
                        ? IOPath.GetRelativePath(filePath, file)
                        : IOPath.GetFileName(file);

                    var (relPath, hash, size) = await _objectStore.StoreFileAsync(pluginName, file, relativeName);
                    totalSize += size;

                    package.Artifacts.Add(new PackageArtifact
                    {
                        PackageId = package.Id,
                        RelativeSourcePath = relPath,
                        RelativeTargetPath = relativeName,
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
                return new PackageImportResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        // ─── 内部导入逻辑 ───

        private async Task<PackageImportResult> ImportDirectFileAsync(string filePath)
        {
            var fileName = IOPath.GetFileNameWithoutExtension(filePath);

            if (_repository.Exists(fileName))
                fileName = $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}";

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
                    var storedPreview = _objectStore.StorePreviewImage(fileName, preview);
                    package.PreviewImagePath = storedPreview;
                }
            }

            await _repository.RegisterPackageAsync(package);
            PackageImported?.Invoke(package);

            return new PackageImportResult { Success = true, Package = package };
        }

        private async Task<List<PackageImportResult>> ImportCompressedAsync(string filePath)
        {
            var results = new List<PackageImportResult>();
            var tempDir = IOPath.Combine(IOPath.GetTempPath(), $"uemod_import_{Guid.NewGuid()}");
            var archiveName = IOPath.GetFileNameWithoutExtension(filePath);

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

                // 收集 MOD 文件
                var extractedDirs = Directory.GetDirectories(tempDir, "*_extracted", SearchOption.AllDirectories);
                List<string> modFiles;

                if (extractedDirs.Length > 0)
                {
                    modFiles = extractedDirs
                        .SelectMany(d => Directory.GetFiles(d, "*.*", SearchOption.AllDirectories))
                        .Where(f => IsModFile(f)).ToList();
                }
                else
                {
                    modFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                        .Where(f => IsModFile(f)).ToList();
                }

                if (modFiles.Count == 0)
                {
                    // 可能是纯插件/配置文件
                    var allFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);
                    if (allFiles.Length > 0)
                    {
                        var result = await ImportFilesAsPackageAsync(archiveName, allFiles.ToList(), tempDir, filePath);
                        results.Add(result);
                    }
                    else
                    {
                        results.Add(new PackageImportResult { Success = false, ErrorMessage = "压缩包中无可识别文件" });
                    }
                    return results;
                }

                // 按前缀分组
                var groups = GroupModFilesByPrefix(modFiles);

                int index = 1;
                foreach (var group in groups.Where(g => g.Value.Count > 0))
                {
                    var packageName = DetermineGroupName(group.Value) ?? group.Key ?? $"{archiveName}_{index}";
                    if (_repository.Exists(packageName))
                        packageName = $"{packageName}_{DateTime.Now:yyyyMMdd_HHmmss}";

                    var result = await ImportFilesAsPackageAsync(packageName, group.Value, tempDir, filePath);
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
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }

            return results;
        }

        private async Task<PackageImportResult> ImportFilesAsPackageAsync(
            string packageName, List<string> files, string tempDir, string sourcePath)
        {
            try
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
                    var storedPreview = _objectStore.StorePreviewImage(packageName, preview);
                    package.PreviewImagePath = storedPreview;
                }

                await _repository.RegisterPackageAsync(package);
                PackageImported?.Invoke(package);

                return new PackageImportResult { Success = true, Package = package };
            }
            catch (Exception ex)
            {
                return new PackageImportResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<PackageImportResult> ImportAsPluginAsync(string filePath)
        {
            var fileName = IOPath.GetFileNameWithoutExtension(filePath);
            if (_repository.Exists(fileName))
                fileName = $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}";

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

        // ─── 解压缩（复用 ModManagementService 逻辑） ───

        private bool ExtractCompressedFile(string filePath, string extractPath)
        {
            try
            {
                var ext = IOPath.GetExtension(filePath).ToLower();
                if (ext == ".zip")
                {
                    ZipFile.ExtractToDirectory(filePath, extractPath, true);
                    return true;
                }

                using var archive = ArchiveFactory.Open(filePath);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    entry.WriteToFile(IOPath.Combine(extractPath, entry.Key ?? ""), new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解压文件失败: {Path}", filePath);
                return false;
            }
        }

        private void ProcessNestedArchives(string directory)
        {
            var archives = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(CompressedArchive.IsCompressed)
                .ToList();

            foreach (var archive in archives)
            {
                var extractDir = archive + "_extracted";
                try
                {
                    Directory.CreateDirectory(extractDir);
                    ExtractCompressedFile(archive, extractDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "处理嵌套压缩包失败: {Path}", archive);
                }
            }
        }

        private static void CleanupArchives(string directory)
        {
            foreach (var archive in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(CompressedArchive.IsCompressed))
            {
                try { File.Delete(archive); } catch { }
            }
        }

        // ─── 文件分组（委托 Core ModFileGrouper） ───

        private static Dictionary<string, List<string>> GroupModFilesByPrefix(List<string> modFiles)
        {
            var grouped = ModFileGrouper.GroupByBaseName(modFiles);
            return grouped.ToDictionary(
                kv => kv.Key,
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        private string? DetermineGroupName(List<string> groupFiles)
            => ModFileGrouper.SelectGroupName(groupFiles, EngineConfig.GroupPriorityExtensions);

        // ─── 辅助方法 ───

        private bool IsModFile(string filePath)
            => EngineConfig.ModFileExtensions.Contains(IOPath.GetExtension(filePath).ToLower());

        private static string? FindPreviewInDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return null;
            var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            return PreviewImageSelector.Select(files);
        }
    }
}
