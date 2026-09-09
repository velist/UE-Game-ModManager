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
        var effectiveOverrides = overrides.Where(pair => candidates.Any(e =>
            string.Equals(e.TargetAbsolutePath, pair.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.PackageKey, pair.Value, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var checkedCandidates = new List<ResolvedEntry>();
        foreach (var entry in candidates)
        {
            if (!File.Exists(entry.SourceAbsolutePath))
                throw new FileNotFoundException($"仓库源文件不存在: {entry.SourceAbsolutePath}", entry.SourceAbsolutePath);
            var priority = entry.Priority;
            if (effectiveOverrides.TryGetValue(entry.TargetAbsolutePath, out var winner))
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
