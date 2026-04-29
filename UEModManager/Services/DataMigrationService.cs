using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Detection;
using UEModManager.Services.Migration;

namespace UEModManager.Services
{
    /// <summary>
    /// v1.8 → v2.0 数据迁移服务。
    /// 将旧版 ModInfo JSON + 文件系统数据迁移到 Package 格式 + ObjectStore 仓库。
    /// MigrationProgress / MigrationResult 已迁到 Core 的 UEModManager.Services.Migration 命名空间。
    /// </summary>
    public class DataMigrationService
    {
        private readonly ILogger<DataMigrationService> _logger;
        private readonly PackageRepository _repository;
        private readonly ObjectStore _objectStore;
        private readonly ModDataService _modDataService;
        private readonly GameConfigService _gameConfig;
        private readonly string _dataDirectory;

        /// <summary>迁移进度更新。</summary>
        public event Action<MigrationProgress>? ProgressChanged;

        public DataMigrationService(
            ILogger<DataMigrationService> logger,
            PackageRepository repository,
            ObjectStore objectStore,
            ModDataService modDataService,
            GameConfigService gameConfig)
        {
            _logger = logger;
            _repository = repository;
            _objectStore = objectStore;
            _modDataService = modDataService;
            _gameConfig = gameConfig;
            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        }

        /// <summary>
        /// 检查是否需要迁移（存在旧格式数据但没有新格式数据）。
        /// IO 部分（文件存在 + 反序列化）留主项目；决策逻辑下沉到 Core 的 MigrationDecision。
        /// </summary>
        public bool NeedsMigration(string gameName)
        {
            var oldPath = Path.Combine(_dataDirectory, $"{gameName}_mods.json");
            var newPath = Path.Combine(_dataDirectory, $"{gameName}_packages.json");

            var oldExists = File.Exists(oldPath);
            var newExists = File.Exists(newPath);

            if (!oldExists || !newExists)
                return MigrationDecision.NeedsMigration(oldExists, newExists, newPackagesCount: null);

            int? count;
            try
            {
                var json = File.ReadAllText(newPath);
                var packages = JsonSerializer.Deserialize<List<Package>>(json);
                count = packages?.Count;
            }
            catch
            {
                count = null; // 反序列化失败视为需要迁移
            }

            return MigrationDecision.NeedsMigration(oldExists, newExists, count);
        }

        /// <summary>
        /// 检查所有游戏是否需要迁移。
        /// </summary>
        public List<string> GetGamesNeedingMigration()
        {
            var games = new List<string>();
            if (!Directory.Exists(_dataDirectory)) return games;

            foreach (var file in Directory.GetFiles(_dataDirectory, "*_mods.json"))
            {
                var gameName = Path.GetFileNameWithoutExtension(file).Replace("_mods", "");
                if (NeedsMigration(gameName))
                    games.Add(gameName);
            }

            return games;
        }

        /// <summary>
        /// 执行完整数据迁移。
        /// 步骤：
        /// 1. 扫描旧数据
        /// 2. 创建默认方案（ProfileService 已处理）
        /// 3. 迁移 MOD 数据到包仓库
        /// 4. 生成 Manifest
        /// 5. 验证完整性
        /// </summary>
        public async Task<MigrationResult> MigrateAsync(string gameName)
        {
            int migrated = 0, skipped = 0;
            var warnings = new List<string>();

            try
            {
                _logger.LogInformation("开始数据迁移: {Game}", gameName);

                // Step 1: 扫描旧数据
                ReportProgress(MigrationStep.ScanOldData, $"读取 {gameName}_mods.json");
                var oldMods = await LoadOldModDataAsync(gameName);
                if (oldMods.Count == 0)
                {
                    return new MigrationResult
                    {
                        Success = true,
                        MigratedPackages = 0,
                        Warnings = new List<string> { "无旧数据需要迁移" }
                    };
                }

                // Step 2: 准备仓库
                ReportProgress(MigrationStep.PrepareRepository, "已从 ProfileService 处理");
                _objectStore.EnsureInitialized();

                // Step 3: 迁移到仓库
                ReportProgress(MigrationStep.MigrateToRepository, $"处理 {oldMods.Count} 个包");
                var modPath = _gameConfig.CurrentModPath;
                var backupPath = _gameConfig.CurrentBackupPath;

                foreach (var mod in oldMods)
                {
                    try
                    {
                        // 跳过已迁移
                        if (_repository.Exists(mod.RealName))
                        {
                            skipped++;
                            continue;
                        }

                        var package = PackageMappers.FromModInfo(mod, gameName);

                        // 尝试从备份目录复制文件到仓库
                        var backupDir = Path.Combine(backupPath, mod.RealName);
                        var modDir = Path.Combine(modPath, mod.RealName);
                        var sourceDir = Directory.Exists(backupDir) ? backupDir : (Directory.Exists(modDir) ? modDir : null);

                        if (sourceDir != null)
                        {
                            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories)
                                .Where(f => !Path.GetFileName(f).StartsWith("preview", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            long totalSize = 0;
                            foreach (var file in files)
                            {
                                var relativeName = Path.GetRelativePath(sourceDir, file);
                                var (relPath, hash, size) = await _objectStore.StoreFileAsync(mod.RealName, file, relativeName);
                                totalSize += size;

                                package.Artifacts.Add(new PackageArtifact
                                {
                                    PackageId = package.Id,
                                    RelativeSourcePath = relPath,
                                    RelativeTargetPath = relativeName,
                                    FileName = Path.GetFileName(file),
                                    FileSize = size,
                                    FileHash = hash,
                                    ArtifactType = ArtifactTypeDetector.DetectForMigration(file, mod.IsPlugin),
                                });
                            }

                            package.TotalSize = totalSize;
                            if (files.Count > 0)
                                package.ContentHash = await ObjectStore.ComputeFileHashAsync(files[0]);

                            // 迁移预览图
                            if (!string.IsNullOrEmpty(mod.PreviewImagePath) && File.Exists(mod.PreviewImagePath))
                            {
                                var storedPreview = _objectStore.StorePreviewImage(mod.RealName, mod.PreviewImagePath);
                                package.PreviewImagePath = storedPreview;
                            }
                            else
                            {
                                // 从备份目录查找预览图
                                var preview = Directory.GetFiles(sourceDir, "preview*", SearchOption.TopDirectoryOnly)
                                    .FirstOrDefault();
                                if (preview != null)
                                {
                                    var storedPreview = _objectStore.StorePreviewImage(mod.RealName, preview);
                                    package.PreviewImagePath = storedPreview;
                                }
                            }
                        }
                        else
                        {
                            // 没有找到源文件，仅保存元数据
                            warnings.Add($"'{mod.Name}' 的文件未找到（备份和 MOD 目录均不存在），仅迁移元数据");
                        }

                        await _repository.RegisterPackageAsync(package);
                        migrated++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "迁移 '{Name}' 失败", mod.Name);
                        warnings.Add($"'{mod.Name}' 迁移失败: {ex.Message}");
                        skipped++;
                    }
                }

                // Step 4: 生成 Manifest
                ReportProgress(MigrationStep.GenerateManifest, $"已完成 {migrated} 个");

                // Step 5: 验证
                ReportProgress(MigrationStep.VerifyIntegrity, "检查仓库一致性");
                var integrityIssues = await _repository.CheckIntegrityAsync();
                foreach (var issue in integrityIssues)
                    warnings.Add($"完整性警告: {issue.packageKey} - {issue.issue}");

                _logger.LogInformation("数据迁移完成: 迁移 {Migrated}, 跳过 {Skipped}, 警告 {Warnings}",
                    migrated, skipped, warnings.Count);

                return new MigrationResult
                {
                    Success = true,
                    MigratedPackages = migrated,
                    SkippedPackages = skipped,
                    Warnings = warnings
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据迁移失败: {Game}", gameName);
                return new MigrationResult
                {
                    Success = false,
                    MigratedPackages = migrated,
                    SkippedPackages = skipped,
                    Warnings = warnings,
                    ErrorMessage = ex.Message
                };
            }
        }

        // ─── 内部方法 ───

        private async Task<List<ModInfo>> LoadOldModDataAsync(string gameName)
        {
            try
            {
                var path = Path.Combine(_dataDirectory, $"{gameName}_mods.json");
                if (!File.Exists(path)) return new List<ModInfo>();
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<ModInfo>>(json) ?? new List<ModInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取旧 MOD 数据失败");
                return new List<ModInfo>();
            }
        }

        private static ArtifactType DetectArtifactType(string filePath, bool isPlugin)
            => ArtifactTypeDetector.DetectForMigration(filePath, isPlugin);

        private void ReportProgress(MigrationStep step, string detail)
        {
            var progress = MigrationProgressTracker.Build(step, detail);
            ProgressChanged?.Invoke(progress);
            _logger.LogDebug("迁移进度: [{Step}/{Total}] {Name} - {Detail}",
                progress.CurrentStep, progress.TotalSteps, progress.StepName, progress.Detail);
        }
    }
}
