using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.DeploymentPlanning;
using UEModManager.Services.Security;

namespace UEModManager.Services
{
    /// <summary>
    /// 部署计划生成器。
    /// 根据当前 Profile 的期望状态和游戏目录的实际状态，
    /// 生成 Add/Remove/Replace 操作列表。
    ///
    /// 纯逻辑（差异比较 / 路径计算 / Toggle 构造）下沉到
    /// <see cref="DeploymentDiffComputer"/> / <see cref="DeploymentTargetPathBuilder"/> /
    /// <see cref="TogglePlanBuilder"/>。本类负责 IO + 编排。
    /// </summary>
    public class DeploymentPlanner
    {
        private readonly ILogger<DeploymentPlanner> _logger;
        private readonly PackageRepository _packageRepository;
        private readonly ObjectStore _objectStore;
        private readonly ProfileService _profileService;
        private readonly GameConfigService _gameConfigService;

        public DeploymentPlanner(
            ILogger<DeploymentPlanner> logger,
            PackageRepository packageRepository,
            ObjectStore objectStore,
            ProfileService profileService,
            GameConfigService gameConfigService)
        {
            _logger = logger;
            _packageRepository = packageRepository;
            _objectStore = objectStore;
            _profileService = profileService;
            _gameConfigService = gameConfigService;
        }

        /// <summary>
        /// 为当前活跃 Profile 生成完整部署计划。
        /// 比较 Profile 期望状态与游戏目录实际状态，输出差异操作。
        /// </summary>
        public async Task<DeploymentPlan> CreatePlanAsync()
        {
            var profile = _profileService.CurrentProfile;
            if (profile == null)
                throw new InvalidOperationException("没有活跃的 Profile");

            return await CreatePlanForProfileAsync(profile);
        }

        /// <summary>
        /// 为指定 Profile 生成部署计划。
        /// </summary>
        public Task<DeploymentPlan> CreatePlanForProfileAsync(InstanceProfile profile)
        {
            return Task.Run(() =>
            {
                var modPath = _gameConfigService.CurrentModPath;
                var gamePath = _gameConfigService.CurrentGamePath;

                if (string.IsNullOrEmpty(modPath))
                    throw new InvalidOperationException("游戏 MOD 路径未配置");

                // 1. 收集期望状态：Profile 中所有已启用包的 Artifact → 目标路径
                var desiredFiles = BuildDesiredFileMap(profile, modPath, gamePath);

                // 2. 收集实际状态：游戏 MOD 目录中已存在的文件
                var actualFiles = ScanDeployedFiles(modPath, gamePath, profile);

                // 3. 比较差异（纯函数，下沉到 Core 的 DeploymentDiffComputer）
                var operations = DeploymentDiffComputer.ComputeDiff(desiredFiles, actualFiles);

                var plan = new DeploymentPlan
                {
                    ProfileId = profile.Id,
                    HostGameName = profile.HostGameName,
                    Operations = operations,
                    BackendType = UiPreferences.LoadDeployBackend()
                };

                _logger.LogInformation(
                    "部署计划已生成: +{Add} -{Remove} ~{Replace} (共 {Total} 个操作)",
                    plan.AddCount, plan.RemoveCount, plan.ReplaceCount, plan.TotalCount);

                return plan;
            });
        }

        /// <summary>
        /// 为单个包的启用/禁用生成精简部署计划。
        /// </summary>
        public Task<DeploymentPlan> CreateTogglePlanAsync(
            string packageKey, bool enable)
        {
            return Task.Run(() =>
            {
                var profile = _profileService.CurrentProfile;
                if (profile == null)
                    throw new InvalidOperationException("没有活跃的 Profile");

                var package = _packageRepository.GetByKey(packageKey);
                if (package == null)
                    throw new InvalidOperationException($"包 '{packageKey}' 不存在");

                var modPath = _gameConfigService.CurrentModPath;
                var gamePath = _gameConfigService.CurrentGamePath;
                var entry = profile.Packages.FirstOrDefault(p => p.PackageKey == packageKey);

                var operations = TogglePlanBuilder.BuildToggleOperations(
                    package, entry, modPath, gamePath,
                    _objectStore.RepositoryRoot,
                    enable,
                    File.Exists);

                return new DeploymentPlan
                {
                    ProfileId = profile.Id,
                    HostGameName = profile.HostGameName,
                    Operations = operations,
                    BackendType = UiPreferences.LoadDeployBackend()
                };
            });
        }

        // ─── IO 辅助（保留主项目，因为依赖 PackageRepository 和文件系统） ───

        private Dictionary<string, DesiredFile> BuildDesiredFileMap(
            InstanceProfile profile, string modPath, string gamePath)
        {
            var map = new Dictionary<string, DesiredFile>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in profile.Packages.Where(p => p.IsEnabled))
            {
                var package = _packageRepository.GetByKey(entry.PackageKey);
                if (package == null)
                {
                    _logger.LogWarning("Profile 引用的包不存在: {Key}", entry.PackageKey);
                    continue;
                }

                foreach (var artifact in package.Artifacts.Where(a => a.ArtifactType != ArtifactType.PreviewImage))
                {
                    var sourcePath = Path.Combine(_objectStore.RepositoryRoot, artifact.RelativeSourcePath);
                    if (!File.Exists(sourcePath))
                    {
                        _logger.LogWarning("仓库文件不存在: {Path}", sourcePath);
                        continue;
                    }

                    var targetPath = DeploymentTargetPathBuilder.ComputeTargetPath(
                        artifact, package, entry, modPath, gamePath);

                    map[targetPath] = new DesiredFile(
                        PackageKey: entry.PackageKey,
                        PackageDisplayName: package.DisplayName,
                        SourcePath: sourcePath,
                        TargetPath: targetPath,
                        RelativeTargetPath: DeploymentTargetPathBuilder.ComputeRelativeTargetPath(
                            artifact, package, entry, modPath, gamePath, targetPath),
                        FileHash: artifact.FileHash,
                        FileSize: artifact.FileSize,
                        Kind: package.Kind);
                }
            }

            return map;
        }

        private Dictionary<string, DeployedFile> ScanDeployedFiles(
            string modPath, string gamePath, InstanceProfile profile)
        {
            var map = new Dictionary<string, DeployedFile>(StringComparer.OrdinalIgnoreCase);

            // 扫描 MOD 目录
            if (Directory.Exists(modPath))
            {
                foreach (var dir in Directory.GetDirectories(modPath))
                {
                    var dirName = new DirectoryInfo(dir).Name;
                    var entry = profile.Packages.FirstOrDefault(p => p.PackageKey == dirName);
                    var package = entry != null ? _packageRepository.GetByKey(entry.PackageKey) : null;

                    foreach (var file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        // 跳过预览图
                        if (Path.GetFileName(file).StartsWith("preview", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var relativePath = Path.GetRelativePath(modPath, file);
                        map[file] = new DeployedFile(
                            PackageKey: dirName,
                            PackageDisplayName: package?.DisplayName ?? dirName,
                            RelativePath: relativePath,
                            Hash: null, // 延迟计算
                            FileSize: new FileInfo(file).Length,
                            Kind: package?.Kind ?? PackageKind.Mod,
                            BelongsToKnownPackage: entry != null && !entry.IsEnabled);
                    }
                }
            }

            // 扫描非 MOD 目标目录
            foreach (var entry in profile.Packages.Where(p => p.Kind != PackageKind.Mod))
            {
                var package = _packageRepository.GetByKey(entry.PackageKey);
                var targetRootPath = entry.TargetRootPath ?? package?.TargetRootPath;
                if (string.IsNullOrEmpty(targetRootPath) || string.IsNullOrEmpty(gamePath))
                    continue;

                targetRootPath = PathSanitizer.SanitizeRelative(targetRootPath);
                var packageKey = PathSanitizer.SanitizeRelative(entry.PackageKey);
                var packageDir = Path.Combine(gamePath, targetRootPath, packageKey);
                if (!Directory.Exists(packageDir))
                    continue;

                foreach (var file in Directory.GetFiles(packageDir, "*.*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(
                        Path.Combine(gamePath, targetRootPath), file);

                    map[file] = new DeployedFile(
                        PackageKey: entry.PackageKey,
                        PackageDisplayName: package?.DisplayName ?? entry.PackageKey,
                        RelativePath: relativePath,
                        Hash: null,
                        FileSize: new FileInfo(file).Length,
                        Kind: entry.Kind,
                        BelongsToKnownPackage: !entry.IsEnabled);
                }
            }

            return map;
        }
    }
}
