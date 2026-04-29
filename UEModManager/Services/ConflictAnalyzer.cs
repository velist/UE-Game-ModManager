using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// 冲突检测分两层：
    /// 1. 文件路径冲突（轻量）— 多个包部署到同一目标路径
    /// 2. UE 资产冲突（重量）— CUE4Parse pak 内容检测（委托给 ModConflictService）
    /// </summary>
    public class ConflictAnalyzer
    {
        private readonly ILogger<ConflictAnalyzer> _logger;
        private readonly PackageRepository _packageRepository;
        private readonly ProfileService _profileService;
        private readonly GameConfigService _gameConfigService;
        private readonly ObjectStore _objectStore;

        private string _currentGameName = string.Empty;

        // 用户覆盖规则：TargetPath → 指定的胜者 PackageKey
        private readonly Dictionary<string, string> _userOverrides = new(StringComparer.OrdinalIgnoreCase);

        // 持久化路径
        private string OverridesFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data",
                $"{_currentGameName}_conflict_overrides.json");

        /// <summary>最近一次分析结果。</summary>
        public ConflictAnalysisResult? LastResult { get; private set; }

        /// <summary>冲突分析完成事件。</summary>
        public event Action<ConflictAnalysisResult>? AnalysisCompleted;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ConflictAnalyzer(
            ILogger<ConflictAnalyzer> logger,
            PackageRepository packageRepository,
            ProfileService profileService,
            GameConfigService gameConfigService,
            ObjectStore objectStore)
        {
            _logger = logger;
            _packageRepository = packageRepository;
            _profileService = profileService;
            _gameConfigService = gameConfigService;
            _objectStore = objectStore;
        }

        /// <summary>
        /// 设置当前游戏并加载用户覆盖规则。
        /// </summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGameName = gameName;
            await LoadOverridesAsync();
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
                    profile, packagesByKey, modPath, gamePath, _userOverrides);

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
            _userOverrides[targetPath] = winnerPackageKey;
            await SaveOverridesAsync();
            _logger.LogInformation("冲突覆盖已设置: {Path} → {Winner}", targetPath, winnerPackageKey);
        }

        /// <summary>
        /// 移除用户覆盖。
        /// </summary>
        public async Task RemoveOverrideAsync(string targetPath)
        {
            _userOverrides.Remove(targetPath);
            await SaveOverridesAsync();
            _logger.LogInformation("冲突覆盖已移除: {Path}", targetPath);
        }

        /// <summary>
        /// 清除所有用户覆盖。
        /// </summary>
        public async Task ClearAllOverridesAsync()
        {
            _userOverrides.Clear();
            await SaveOverridesAsync();
            _logger.LogInformation("所有冲突覆盖已清除");
        }

        /// <summary>
        /// 获取所有用户覆盖规则。
        /// </summary>
        public IReadOnlyDictionary<string, string> GetOverrides()
            => _userOverrides;

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

        private async Task LoadOverridesAsync()
        {
            _userOverrides.Clear();
            try
            {
                if (File.Exists(OverridesFilePath))
                {
                    var json = await File.ReadAllTextAsync(OverridesFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                            _userOverrides[kv.Key] = kv.Value;
                    }
                    _logger.LogDebug("加载了 {Count} 个冲突覆盖规则", _userOverrides.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载冲突覆盖规则失败");
            }
        }

        private async Task SaveOverridesAsync()
        {
            try
            {
                var dir = Path.GetDirectoryName(OverridesFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_userOverrides, JsonOptions);
                await File.WriteAllTextAsync(OverridesFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存冲突覆盖规则失败");
            }
        }

        // ─── 内部数据结构 ───
        // ConflictAnalysisResult 已迁到 Core 的 UEModManager.Services.Conflict 命名空间，
        // 见 UEModManager.Core/Services/Conflict/ConflictAnalysisResult.cs。
        // ArtifactOwner 已抽到同一命名空间下，让纯求解逻辑可独立单测。
    }
}
