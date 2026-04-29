using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Config;
using UEModManager.Services.ResolvedViews;

namespace UEModManager.Services
{
    /// <summary>
    /// 最终视图构建器。
    /// 从 Profile 期望状态构建"最终会在游戏目录中生效的完整文件集合"。
    ///
    /// 构建流程：
    /// 1. 读取 Profile 中所有已启用包
    /// 2. 收集所有包的 Artifact（按优先级排列）
    /// 3. 解决文件路径冲突（高优先级覆盖低优先级）
    /// 4. 合并配置文件（键级合并）
    /// 5. 纳入生成物层
    /// 6. 计算 ViewHash
    /// </summary>
    public class ResolvedViewBuilder
    {
        private readonly ILogger<ResolvedViewBuilder> _logger;
        private readonly PackageRepository _packageRepo;
        private readonly ProfileService _profileService;
        private readonly ConflictAnalyzer _conflictAnalyzer;
        private readonly ConfigMergeEngine _configMergeEngine;
        private readonly OverwriteStore _overwriteStore;

        public ResolvedViewBuilder(
            ILogger<ResolvedViewBuilder> logger,
            PackageRepository packageRepo,
            ProfileService profileService,
            ConflictAnalyzer conflictAnalyzer,
            ConfigMergeEngine configMergeEngine,
            OverwriteStore overwriteStore)
        {
            _logger = logger;
            _packageRepo = packageRepo;
            _profileService = profileService;
            _conflictAnalyzer = conflictAnalyzer;
            _configMergeEngine = configMergeEngine;
            _overwriteStore = overwriteStore;
        }

        /// <summary>
        /// 为当前活跃 Profile 构建最终视图。
        /// </summary>
        public async Task<ResolvedView> BuildAsync()
        {
            var profile = _profileService.CurrentProfile;
            if (profile == null)
            {
                _logger.LogWarning("No active profile, returning empty view");
                return new ResolvedView
                {
                    ViewHash = ResolvedView.ComputeViewHash([])
                };
            }

            return await BuildForProfileAsync(profile);
        }

        /// <summary>
        /// 为指定 Profile 构建最终视图。
        /// </summary>
        public async Task<ResolvedView> BuildForProfileAsync(InstanceProfile profile)
        {
            _logger.LogInformation("Building resolved view for profile {Name} ({Id})",
                profile.Name, profile.Id);

            var configResults = new List<ConfigMergeResult>();

            // ─── Layer 1: Package 文件层 + 冲突解决（纯函数） ───

            var packagesByKey = profile.Packages
                .Where(p => p.IsEnabled)
                .Select(p => _packageRepo.GetByKey(p.PackageKey))
                .Where(p => p != null)
                .ToDictionary(p => p!.PackageKey, p => p!, StringComparer.OrdinalIgnoreCase);

            var (entries, conflicts) = ResolvedViewLayerBuilder.BuildPackageLayer(
                profile, packagesByKey, _packageRepo.Store);

            // ─── Layer 2: 配置合并层（plan 构造 / 冲突翻译为纯函数；MergeAsync 仍是 IO） ───

            var configPlans = ResolvedViewLayerBuilder.BuildConfigMergePlans(entries);
            foreach (var plan in configPlans)
            {
                var result = await _configMergeEngine.MergeAsync(plan);
                if (!result.Success) continue;

                configResults.Add(result);
                conflicts.AddRange(ResolvedViewLayerBuilder.TranslateConfigKeyConflicts(
                    plan.TargetRelativePath, result.Conflicts, profile.HostGameName, profile.Id));
            }

            // ─── Layer 3: 生成物层（活跃的用户修复，纯映射 + IO 存在性检查） ───

            var activeOverwrites = _overwriteStore.GetByStatus(GeneratedArtifactStatus.Active)
                .Where(a => a.Type == GeneratedArtifactType.UserFix && a.RelativeTargetPath != null);

            foreach (var overwrite in activeOverwrites)
            {
                var fullPath = Path.Combine(_overwriteStore.OverwriteRoot, profile.HostGameName, overwrite.RelativePath);
                if (!File.Exists(fullPath)) continue;

                var entry = ResolvedViewLayerBuilder.BuildOverwriteEntry(overwrite, fullPath);
                if (entry != null) entries.Add(entry);
            }

            // ─── 构建视图 ───

            var viewHash = ResolvedView.ComputeViewHash(entries);

            var view = new ResolvedView
            {
                HostGameName = profile.HostGameName,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Entries = entries,
                Conflicts = conflicts,
                ConfigMergeResults = configResults,
                ViewHash = viewHash
            };

            _logger.LogInformation(
                "Resolved view built: {Entries} entries, {Conflicts} conflicts, {Configs} config merges, hash={Hash}",
                view.TotalEntries, view.ConflictCount, configResults.Count, viewHash);

            return view;
        }

        /// <summary>
        /// 快速检查当前视图是否过期（通过比较哈希）。
        /// </summary>
        public async Task<bool> IsViewStaleAsync(ResolvedView? currentView)
        {
            if (currentView == null) return true;
            var fresh = await BuildAsync();
            return !currentView.IsIdenticalTo(fresh);
        }

        // ─── 内部方法 ───
        // GetArtifactFullPath 已下沉到 Core 的 ResolvedViewLayerBuilder，
        // 通过 IObjectStoreQuery.GetPackageFilesDirectory 计算源路径。
    }
}
