using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Conflict;

namespace UEModManager.Services
{
    /// <summary>
    /// v2.0 冲突分析器。
    /// 检测当前 Profile 中已启用包之间的冲突，生成胜者/败者链。
    ///
    /// 检测文件路径冲突：多个包部署到同一目标路径。
    /// </summary>
    public class ConflictAnalyzer
    {
        private readonly ILogger<ConflictAnalyzer> _logger;
        private readonly PackageRepository _packageRepository;
        private readonly ProfileService _profileService;
        private readonly GameConfigService _gameConfigService;

        /// <summary>最近一次分析结果。</summary>
        public ConflictAnalysisResult? LastResult { get; private set; }

        /// <summary>冲突分析完成事件。</summary>
        public event Action<ConflictAnalysisResult>? AnalysisCompleted;

        public ConflictAnalyzer(
            ILogger<ConflictAnalyzer> logger,
            PackageRepository packageRepository,
            ProfileService profileService,
            GameConfigService gameConfigService)
        {
            _logger = logger;
            _packageRepository = packageRepository;
            _profileService = profileService;
            _gameConfigService = gameConfigService;
            _profileService.ProfileChanged += _ => LastResult = null;
        }

        /// <summary>
        /// 游戏切换后清除分析快照。规则随 ProfileService 加载对应方案。
        /// </summary>
        public Task SetCurrentGameAsync(string gameName)
        {
            LastResult = null;
            return Task.CompletedTask;
        }

        /// <summary>
        /// 分析当前 Profile 的文件路径冲突。
        /// 轻量级检测，不使用 CUE4Parse。
        /// </summary>
        public Task<ConflictAnalysisResult> AnalyzeAsync()
        {
            var profile = _profileService.CurrentProfile;
            if (profile == null)
                throw new InvalidOperationException("没有活跃的 Profile");

            return AnalyzeProfileAsync(profile);
        }

        /// <summary>
        /// 分析指定 Profile 的冲突。
        /// 收集 packagesByKey 字典 + 路径，委托给 Core 的 ConflictDetector 完成纯函数求解。
        /// </summary>
        public Task<ConflictAnalysisResult> AnalyzeProfileAsync(InstanceProfile profile)
        {
            var overrides = GetOverrides(profile);
            return Task.Run(() =>
            {
                var modPath = _gameConfigService.CurrentModPath;
                var gamePath = _gameConfigService.CurrentGamePath;

                // 构建 packagesByKey 快照（让 Detector 不依赖具体 Repository）
                var packagesByKey = profile.Packages
                    .Where(p => p.IsEnabled)
                    .Select(p => _packageRepository.GetByKey(p.PackageKey))
                    .Where(p => p != null)
                    .ToDictionary(p => p!.PackageKey, p => p!, StringComparer.OrdinalIgnoreCase);

                var conflicts = ConflictDetector.DetectConflicts(
                    profile, packagesByKey, modPath, gamePath, overrides);

                var result = new ConflictAnalysisResult
                {
                    ProfileId = profile.Id,
                    HostGameName = profile.HostGameName,
                    Conflicts = conflicts,
                    ScannedPackages = packagesByKey.Count,
                    TotalArtifacts = packagesByKey.Values.Sum(p =>
                        p.Artifacts.Count(a => a.ArtifactType != ArtifactType.PreviewImage))
                };

                LastResult = result;
                _logger.LogInformation(
                    "冲突分析完成: {Packages} 个包, {Artifacts} 个文件, {Conflicts} 个冲突 ({Overrides} 个用户覆盖)",
                    result.ScannedPackages, result.TotalArtifacts,
                    result.TotalConflicts, result.UserOverrideCount);

                AnalysisCompleted?.Invoke(result);
                return result;
            });
        }

        // ─── 用户覆盖 ───

        /// <summary>
        /// 设置用户覆盖：指定某个目标路径的胜者包。
        /// </summary>
        public async Task SetOverrideAsync(string targetPath, string winnerPackageKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(winnerPackageKey);
            await UpdateOverrideAsync(targetPath, winnerPackageKey);
            _logger.LogInformation("冲突覆盖已设置: {Path} → {Winner}", targetPath, winnerPackageKey);
        }

        /// <summary>
        /// 移除用户覆盖。
        /// </summary>
        public async Task RemoveOverrideAsync(string targetPath)
        {
            await UpdateOverrideAsync(targetPath, null);
            _logger.LogInformation("冲突覆盖已移除: {Path}", targetPath);
        }

        /// <summary>
        /// 清除所有用户覆盖。
        /// </summary>
        public async Task ClearAllOverridesAsync()
        {
            var profile = _profileService.CurrentProfile
                ?? throw new InvalidOperationException("没有活跃的 Profile");
            await _profileService.ReplaceConflictOverridesAsync(profile.Id,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            LastResult = null;
            _logger.LogInformation("当前方案的冲突覆盖已清除");
        }

        /// <summary>
        /// 获取当前方案在本机游戏目录下的覆盖规则快照。
        /// </summary>
        public IReadOnlyDictionary<string, string> GetOverrides()
            => _profileService.CurrentProfile is { } profile
                ? GetOverrides(profile)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> GetOverrides(InstanceProfile profile)
            => ConflictOverridePaths.Resolve(profile.ConflictOverrides,
                _gameConfigService.CurrentModPath, _gameConfigService.CurrentGamePath);

        public IReadOnlyDictionary<string, string> GetPortableOverrides(InstanceProfile profile)
            => ConflictOverridePaths.MakePortable(profile.ConflictOverrides,
                _gameConfigService.CurrentModPath, _gameConfigService.CurrentGamePath);

        private async Task UpdateOverrideAsync(string targetPath, string? winnerPackageKey)
        {
            var profile = _profileService.CurrentProfile
                ?? throw new InvalidOperationException("没有活跃的 Profile");
            var modPath = _gameConfigService.CurrentModPath;
            var gamePath = _gameConfigService.CurrentGamePath;
            var portableKey = ConflictOverridePaths.ToPortableKey(targetPath, modPath, gamePath);
            await _profileService.UpdateConflictOverridesAsync(profile.Id, rules =>
            {
                // 顺带规范化旧绝对键，避免删除新键后旧别名的规则重新出现。
                var portable = ConflictOverridePaths.MakePortable(rules, modPath, gamePath);
                rules.Clear();
                foreach (var (key, value) in portable) rules[key] = value;
                if (winnerPackageKey == null) rules.Remove(portableKey);
                else rules[portableKey] = winnerPackageKey;
            });
            LastResult = null;
        }

        // ─── 查询 ───

        /// <summary>
        /// 获取指定包涉及的所有冲突。
        /// </summary>
        public List<ConflictRecord> GetConflictsForPackage(string packageKey)
        {
            if (LastResult == null) return [];
            return ConflictQueries.GetConflictsForPackage(LastResult.Conflicts, packageKey);
        }

        /// <summary>
        /// 获取指定包作为败者的冲突数。
        /// </summary>
        public int GetLossCount(string packageKey)
        {
            if (LastResult == null) return 0;
            return ConflictQueries.GetLossCount(LastResult.Conflicts, packageKey);
        }

        /// <summary>
        /// 获取指定包作为胜者的冲突数。
        /// </summary>
        public int GetWinCount(string packageKey)
        {
            if (LastResult == null) return 0;
            return ConflictQueries.GetWinCount(LastResult.Conflicts, packageKey);
        }

        // ─── 内部方法 ───
        // ComputeTargetPath / ComputeRelativePath / DetermineSeverity 已下沉到 Core
        // 的 Services.Conflict.ConflictDetector / ConflictResolver。

        // ─── 内部数据结构 ───
        // ConflictAnalysisResult 已迁到 Core 的 UEModManager.Services.Conflict 命名空间，
        // 见 UEModManager.Core/Services/Conflict/ConflictAnalysisResult.cs。
        // ArtifactOwner 已抽到同一命名空间下，让纯求解逻辑可独立单测。
    }
}
