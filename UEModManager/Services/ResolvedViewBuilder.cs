using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;
using UEModManager.Services.ResolvedViews;
using UEModManager.Services.Security;

namespace UEModManager.Services;

/// <summary>Builds the exact deployable file view, including merged configuration and user fixes.</summary>
public class ResolvedViewBuilder
{
    private readonly ILogger<ResolvedViewBuilder> _logger;
    private readonly PackageRepository _packageRepo;
    private readonly ProfileService _profileService;
    private readonly ConfigMergeEngine _configMergeEngine;
    private readonly OverwriteStore _overwriteStore;
    private readonly GameConfigService _gameConfig;

    public ResolvedViewBuilder(ILogger<ResolvedViewBuilder> logger, PackageRepository packageRepo,
        ProfileService profileService, ConfigMergeEngine configMergeEngine, OverwriteStore overwriteStore,
        GameConfigService gameConfig)
    {
        _logger = logger;
        _packageRepo = packageRepo;
        _profileService = profileService;
        _configMergeEngine = configMergeEngine;
        _overwriteStore = overwriteStore;
        _gameConfig = gameConfig;
    }

    public Task<ResolvedView> BuildAsync()
    {
        var profile = _profileService.CurrentProfile;
        return profile == null
            ? Task.FromResult(new ResolvedView { ViewHash = ResolvedView.ComputeViewHash([]) })
            : BuildForProfileAsync(profile);
    }

    public async Task<ResolvedView> BuildForProfileAsync(InstanceProfile profile)
    {
        var modPath = DeploymentStateStore.NormalizeRoot(_gameConfig.CurrentModPath);
        var gamePath = DeploymentStateStore.NormalizeRoot(_gameConfig.CurrentGamePath);
        if (string.IsNullOrEmpty(modPath)) throw new InvalidOperationException("游戏 MOD 路径未配置");
        if (!string.Equals(profile.HostGameName, _gameConfig.CurrentGameName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("方案所属游戏与当前游戏不匹配");

        var packages = _packageRepo.GetAllPackages().ToDictionary(p => p.PackageKey, StringComparer.OrdinalIgnoreCase);
        var overrides = ConflictOverridePaths.Resolve(profile.ConflictOverrides, modPath, gamePath);
        // Candidate collection MUST precede winner selection. Equal logical MOD names still have distinct destinations.
        var candidates = ResolvedViewLayerBuilder.BuildDeploymentCandidates(profile, packages, _packageRepo.Store, modPath, gamePath);
        // 覆盖规则的键可能是"加载顺序路径"（MOD 的 @mod/RelativeTargetPath，不含 PackageKey 子目录，
        // 与 ConflictDetector.ComputeLoadConflictKey 同源），也可能是实际部署路径或 @game/ 相对路径。
        // 早先只用实际部署路径比对，而 MOD 的实际路径多一层 PackageKey，
        // 于是用户在冲突面板里选出的胜者永远匹配不上任何候选 —— 规则存进了 Profile，磁盘上什么都没变。
        var effectiveOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in overrides)
        {
            var match = candidates.FirstOrDefault(e =>
                string.Equals(e.PackageKey, winner, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(e.TargetAbsolutePath, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.LoadConflictKey, key, StringComparison.OrdinalIgnoreCase)));
            if (match != null && !effectiveOverrides.ContainsKey(key))
                effectiveOverrides[key] = winner;
        }

        // 被用户明确判负的包，不得再落盘：否则同名文件在不同包目录里同时存在，
        // "选择胜者"只是面板上的装饰，引擎仍按自己的加载顺序决定谁生效。
        //
        // 只在"败者与胜者落在不同目标路径"时丢弃。目标路径相同的配置文件必须全部保留，
        // 键级合并（ConfigMergeEngine）依赖所有候选才能把 OnlyA / OnlyB 合成一份；
        // 提前丢掉败者会连带丢掉它独有的配置键。同路径竞争由下面的 GroupBy 去重按优先级裁决。
        var overriddenLosers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.Where(e => e.LoadConflictKey != null).GroupBy(e => e.LoadConflictKey!, StringComparer.OrdinalIgnoreCase))
        {
            if (!effectiveOverrides.TryGetValue(group.Key, out var winnerKey)) continue;
            var winnerTarget = group.FirstOrDefault(e => string.Equals(e.PackageKey, winnerKey, StringComparison.OrdinalIgnoreCase))?.TargetAbsolutePath;
            if (winnerTarget == null) continue;
            foreach (var loser in group.Where(e => !string.Equals(e.PackageKey, winnerKey, StringComparison.OrdinalIgnoreCase)
                                                   && !string.Equals(e.TargetAbsolutePath, winnerTarget, StringComparison.OrdinalIgnoreCase)))
                overriddenLosers.Add(loser.TargetAbsolutePath);
        }
        var checkedCandidates = new List<ResolvedEntry>();
        foreach (var entry in candidates)
        {
            if (overriddenLosers.Contains(entry.TargetAbsolutePath)) continue;
            if (!File.Exists(entry.SourceAbsolutePath))
                throw new FileNotFoundException($"仓库源文件不存在: {entry.SourceAbsolutePath}", entry.SourceAbsolutePath);
            var priority = entry.Priority;
            if (entry.LoadConflictKey != null && effectiveOverrides.TryGetValue(entry.LoadConflictKey, out var winner))
                priority = string.Equals(winner, entry.PackageKey, StringComparison.OrdinalIgnoreCase)
                    ? int.MinValue : Math.Max(entry.Priority, int.MinValue + 1);
            else if (effectiveOverrides.TryGetValue(entry.TargetAbsolutePath, out winner))
                priority = string.Equals(winner, entry.PackageKey, StringComparison.OrdinalIgnoreCase)
                    ? int.MinValue : Math.Max(entry.Priority, int.MinValue + 1);
            checkedCandidates.Add(await WithContentAsync(entry, entry.SourceAbsolutePath, entry.Source, priority));
        }

        var entries = checkedCandidates.GroupBy(e => e.TargetAbsolutePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Priority).First(),
                StringComparer.OrdinalIgnoreCase);
        var conflicts = ConflictDetector.DetectConflicts(profile, packages, modPath, gamePath, overrides);
        var configResults = new List<ConfigMergeResult>();

        foreach (var plan in ResolvedViewLayerBuilder.BuildConfigMergePlans(checkedCandidates))
        {
            var result = await _configMergeEngine.MergeAsync(plan);
            if (!result.Success)
                throw new InvalidDataException($"配置合并失败: {plan.TargetRelativePath}: {result.ErrorMessage}");
            configResults.Add(result);
            var target = checkedCandidates.First(e => e.TargetRelativePath == plan.TargetRelativePath
                && plan.Sources.Any(s => s.SourceFilePath == e.SourceAbsolutePath)).TargetAbsolutePath;
            var winner = entries[target];
            var source = await SaveMergedContentAsync(profile, gamePath, modPath, plan.TargetRelativePath, result.MergedContent);
            entries[target] = await WithContentAsync(winner, source, ResolvedEntrySource.ConfigMerge, winner.Priority);
            conflicts.AddRange(ResolvedViewLayerBuilder.TranslateConfigKeyConflicts(
                plan.TargetRelativePath, result.Conflicts, profile.HostGameName, profile.Id));
        }

        // User fixes have game-relative destinations. Profile-specific fixes never leak into another Profile.
        var fixes = _overwriteStore.GetByStatus(GeneratedArtifactStatus.Active)
            .Where(a => a.Type == GeneratedArtifactType.UserFix && !string.IsNullOrWhiteSpace(a.RelativeTargetPath)
                && string.Equals(a.HostGameName, profile.HostGameName, StringComparison.OrdinalIgnoreCase)
                && (a.SourceProfileId == null || a.SourceProfileId == profile.Id))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id);
        foreach (var fix in fixes)
        {
            var source = PathSanitizer.SafeCombine(
                PathSanitizer.SafeCombine(_overwriteStore.OverwriteRoot, PathSanitizer.SanitizeSegment(profile.HostGameName)), fix.RelativePath);
            if (!File.Exists(source)) throw new FileNotFoundException($"用户修复文件不存在: {source}", source);
            if (string.IsNullOrEmpty(gamePath)) throw new InvalidOperationException("用户修复的游戏目标目录未配置");
            var target = PathSanitizer.SafeCombine(gamePath, fix.RelativeTargetPath);
            entries[target] = new ResolvedEntry
            {
                TargetAbsolutePath = target, DeploymentRootPath = gamePath,
                TargetRelativePath = Path.GetRelativePath(gamePath, target),
                SourceAbsolutePath = source, Source = ResolvedEntrySource.UserOverride,
                PackageKey = fix.SourcePackageKey ?? $"userfix-{fix.Id:N}", PackageDisplayName = fix.DisplayName,
                PackageKind = PackageKind.Config, FileSize = new FileInfo(source).Length,
                FileHash = await ObjectStore.ComputeFileHashAsync(source), Priority = int.MinValue
            };
        }

        var files = entries.Values.OrderBy(e => e.TargetAbsolutePath, StringComparer.OrdinalIgnoreCase).ToList();
        var view = new ResolvedView
        {
            HostGameName = profile.HostGameName, ProfileId = profile.Id, ProfileName = profile.Name,
            GameRootPath = gamePath, ModRootPath = modPath, Entries = files,
            ConfigMergeResults = configResults, Conflicts = conflicts, ViewHash = ResolvedView.ComputeViewHash(files)
        };
        _logger.LogInformation("Resolved view: {Files} files, {Merges} configuration merges, hash={Hash}", files.Count, configResults.Count, view.ViewHash);
        return view;
    }

    public async Task<bool> IsViewStaleAsync(ResolvedView? currentView)
        => currentView == null || !currentView.IsIdenticalTo(await BuildAsync());

    private static async Task<ResolvedEntry> WithContentAsync(ResolvedEntry entry, string source, ResolvedEntrySource sourceType, int priority)
        => new()
        {
            TargetAbsolutePath = entry.TargetAbsolutePath, TargetRelativePath = entry.TargetRelativePath,
            DeploymentRootPath = entry.DeploymentRootPath, SourceAbsolutePath = source, Source = sourceType,
            PackageKey = entry.PackageKey, PackageDisplayName = entry.PackageDisplayName, PackageKind = entry.PackageKind,
            FileSize = new FileInfo(source).Length, FileHash = await ObjectStore.ComputeFileHashAsync(source),
            Priority = priority, ArtifactType = entry.ArtifactType,
            IsConflictWinner = entry.IsConflictWinner, OverriddenPackageKeys = entry.OverriddenPackageKeys
        };

    private async Task<string> SaveMergedContentAsync(InstanceProfile profile, string gameRoot, string modRoot, string target, string content)
    {
        // Content-addressed plan inputs: later view builds cannot change an earlier plan's merge output.
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{profile.HostGameName}\n{gameRoot}\n{modRoot}")));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var directory = Path.Combine(_overwriteStore.OverwriteRoot, "Resolved", scope, profile.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, hash + Path.GetExtension(target));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }
}
