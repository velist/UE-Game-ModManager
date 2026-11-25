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
using UEModManager.Services.InstallLayout;
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
        // 部署清单的身份仍按「游戏路径 + MOD 路径」算（configuredGamePath）；
        // 插件 / 配置包的落盘基准是游戏本体所在目录（gamePath），两者只在启动壳与本体分开的游戏上不同。
        var configuredGamePath = DeploymentStateStore.NormalizeRoot(_gameConfig.CurrentGamePath);
        var gamePath = DeploymentStateStore.NormalizeRoot(_gameConfig.CurrentPluginRootPath);
        if (string.IsNullOrEmpty(modPath)) throw new InvalidOperationException("游戏 MOD 路径未配置");
        if (!string.Equals(profile.HostGameName, _gameConfig.CurrentGameName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("方案所属游戏与当前游戏不匹配");

        // 部署前把包内路径归位一遍（规则与导入时同一套）：老版本入库的包带着
        // PBZ\platform\steam\ue4ss\… 这类多余层级，不必重新导入也能落到游戏会读的位置。
        var (rules, packages) = NormalizeForProfile(profile);
        // 装不到有效位置的包（多选一的配置方案）不落盘：铺进游戏目录只会留下一堆游戏不读的文件，
        // 还让界面显示「部署成功」。原因由体检与导入结果告诉玩家。
        foreach (var issue in PlacementIssues(profile, packages, rules)
                     .Where(issue => issue.Kind == PlacementIssueKind.ChoiceVariants))
        {
            _logger.LogWarning("包 {Key} 是多选一的配置方案（{Variants}），无法自动判断该装哪一套，本次不部署",
                issue.PackageKey, string.Join(" / ", issue.Variants));
            packages[issue.PackageKey] = InstallLayoutNormalizer.WithoutArtifacts(packages[issue.PackageKey]);
        }
        var overrides = ConflictOverridePaths.Resolve(profile.ConflictOverrides, modPath, gamePath);
        // Candidate collection MUST precede winner selection. Equal logical MOD names still have distinct destinations.
        var candidates = ResolvedViewLayerBuilder.BuildDeploymentCandidates(profile, packages, _packageRepo.Store, modPath, gamePath);
        // 缺启用标记的脚本 MOD 文件夹各补一个空的 enabled.txt（归在所属的包名下，停用时一起撤掉）。
        // 包里自己带了同名文件的不会走到这里；万一别的包占了这个位置，下面按目标路径去重时包文件优先。
        var enableMarkers = EnableMarkerTargets(profile, packages, rules, gamePath);
        if (enableMarkers.Count > 0)
        {
            var emptyFile = await EnsureEmptyMarkerFileAsync();
            candidates.AddRange(enableMarkers.Select(marker => new ResolvedEntry
            {
                DeploymentRootPath = Path.GetFullPath(gamePath),
                TargetAbsolutePath = marker.TargetPath,
                TargetRelativePath = Path.GetRelativePath(gamePath, marker.TargetPath),
                SourceAbsolutePath = emptyFile,
                Source = ResolvedEntrySource.Generated,
                PackageKey = marker.PackageKey,
                PackageDisplayName = packages[marker.PackageKey].DisplayName,
                PackageKind = packages[marker.PackageKey].Kind,
                // 排在所有包文件之后：同一位置有真文件时用真文件
                Priority = int.MaxValue,
                ArtifactType = ArtifactType.Other,
            }));
        }
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
            GameRootPath = configuredGamePath, PluginRootPath = gamePath, ModRootPath = modPath, Entries = files,
            ConfigMergeResults = configResults, Conflicts = conflicts, ViewHash = ResolvedView.ComputeViewHash(files)
        };
        _logger.LogInformation("Resolved view: {Files} files, {Merges} configuration merges, hash={Hash}", files.Count, configResults.Count, view.ViewHash);
        return view;
    }

    /// <summary>
    /// 当前方案的安装落位概况：每个文件按现行规则该装在哪、哪些包装不到有效位置。
    /// 只算路径，不读也不哈希任何文件（体检在启动时跑，不能为此去读几百 MB 的 pak）。
    /// </summary>
    public InstallPlacementSnapshot DescribePlacement()
    {
        var profile = _profileService.CurrentProfile;
        var modPath = DeploymentStateStore.NormalizeRoot(_gameConfig.CurrentModPath);
        var pluginRoot = DeploymentStateStore.NormalizeRoot(_gameConfig.CurrentPluginRootPath);
        if (profile == null || string.IsNullOrEmpty(modPath) || string.IsNullOrEmpty(pluginRoot)
            || !string.Equals(profile.HostGameName, _gameConfig.CurrentGameName, StringComparison.OrdinalIgnoreCase))
            return new InstallPlacementSnapshot([], []);

        var (rules, packages) = NormalizeForProfile(profile);
        var issues = PlacementIssues(profile, packages, rules);
        foreach (var issue in issues.Where(issue => issue.Kind == PlacementIssueKind.ChoiceVariants))
            packages[issue.PackageKey] = InstallLayoutNormalizer.WithoutArtifacts(packages[issue.PackageKey]);

        // 方案里引用了仓库里已经没有的包时，候选构建会抛；体检只描述现状，跳过它们。
        var known = new InstanceProfile
        {
            Id = profile.Id, HostGameName = profile.HostGameName, Name = profile.Name,
            Packages = profile.Packages.Where(p => packages.ContainsKey(p.PackageKey)).ToList()
        };
        var expected = ResolvedViewLayerBuilder
            .BuildDeploymentCandidates(known, packages, _packageRepo.Store, modPath, pluginRoot)
            .Select(e => new OwnedTarget(e.TargetAbsolutePath, e.PackageKey ?? string.Empty))
            .Concat(EnableMarkerTargets(known, packages, rules, pluginRoot))
            .ToList();
        return new InstallPlacementSnapshot(expected, issues);
    }

    /// <summary>
    /// 按方案把全部包归位，并给出这次用的归位规则。
    ///
    /// 规则里的脚本 MOD 目录跟着 UE4SS 的目录结构走。结构先看磁盘，再把「方案里启用了、这次部署才会落盘」的
    /// 加载器本体算进去：否则一键安装 / 拖入旧版 UE4SS 的那一次部署里，脚本 MOD 会按「没装」的默认结构落位，
    /// 和刚装上的 UE4SS 读的目录对不上。部署与体检（<see cref="DescribePlacement"/>）走的是同一个结果。
    /// </summary>
    private (InstallLayoutRules Rules, Dictionary<string, Package> Packages) NormalizeForProfile(InstanceProfile profile)
    {
        var all = _packageRepo.GetAllPackages().ToDictionary(p => p.PackageKey, StringComparer.OrdinalIgnoreCase);
        var diskRules = _gameConfig.CurrentInstallLayoutRules;

        var pending = new List<string>();
        foreach (var entry in profile.Packages.Where(e => e.IsEnabled))
        {
            if (!all.TryGetValue(entry.PackageKey, out var package) || package.Kind == PackageKind.Mod) continue;
            if (!string.IsNullOrWhiteSpace(entry.TargetRootPath ?? package.TargetRootPath)) continue;
            var paths = InstallLayoutNormalizer.Normalize(
                package.Artifacts.Select(a => a.RelativeTargetPath ?? string.Empty).ToList(), diskRules);
            if (InstallLayoutNormalizer.IsLoaderRuntime(paths, diskRules)) pending.AddRange(paths);
        }

        var rules = pending.Count == 0 ? diskRules : _gameConfig.ResolveInstallLayoutRules(pending);
        return (rules, InstallLayoutNormalizer.NormalizePackages(all, profile, rules));
    }

    /// <summary>
    /// 方案里启用的脚本 MOD 包各自要补的启用标记（<c>enabled.txt</c>）的落盘位置。
    /// 判定在 Core 的 <see cref="InstallLayoutNormalizer.MissingEnableMarkers"/>。
    /// </summary>
    private static List<OwnedTarget> EnableMarkerTargets(
        InstanceProfile profile, IReadOnlyDictionary<string, Package> normalizedPackages, InstallLayoutRules rules, string pluginRoot)
    {
        var result = new List<OwnedTarget>();
        if (string.IsNullOrEmpty(rules.ScriptModEnableMarker) || string.IsNullOrEmpty(pluginRoot)) return result;

        foreach (var entry in profile.Packages.Where(e => e.IsEnabled))
        {
            if (!normalizedPackages.TryGetValue(entry.PackageKey, out var package) || package.Kind == PackageKind.Mod) continue;
            // 玩家自己指定了安装子目录的包：位置是玩家定的，不是脚本 MOD 目录，不往里加东西
            if (!string.IsNullOrWhiteSpace(entry.TargetRootPath ?? package.TargetRootPath)) continue;

            var paths = package.Artifacts
                .Where(a => a.ArtifactType != ArtifactType.PreviewImage)
                .Select(a => a.RelativeTargetPath ?? string.Empty);
            result.AddRange(InstallLayoutNormalizer.MissingEnableMarkers(paths, rules)
                .Select(marker => new OwnedTarget(Path.GetFullPath(PathSanitizer.SafeCombine(pluginRoot, marker)), package.PackageKey)));
        }
        return result;
    }

    /// <summary>启用标记的内容：一个空文件（UE4SS 只看它在不在）。放在生成物目录里，所有包共用一份。</summary>
    private async Task<string> EnsureEmptyMarkerFileAsync()
    {
        var directory = Path.Combine(_overwriteStore.OverwriteRoot, "Resolved", "Markers");
        var path = Path.Combine(directory, "empty.txt");
        if (File.Exists(path) && new FileInfo(path).Length == 0) return path;
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(path, []);
        return path;
    }

    /// <summary>方案里已启用的包中，装不到游戏会读取的位置的那些（供部署与体检共用）。</summary>
    public static List<PlacementIssue> PlacementIssues(
        InstanceProfile profile, IReadOnlyDictionary<string, Package> normalizedPackages, InstallLayoutRules rules)
        => profile.Packages
            .Where(entry => entry.IsEnabled && string.IsNullOrWhiteSpace(entry.TargetRootPath))
            .Select(entry => normalizedPackages.TryGetValue(entry.PackageKey, out var package)
                ? InstallPlacementAnalyzer.Analyze(package, rules) : null)
            .Where(issue => issue != null)
            .Select(issue => issue!)
            .ToList();

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

/// <summary>当前方案的安装落位概况。</summary>
/// <param name="ExpectedTargets">每个文件按现行规则该装到的位置。</param>
/// <param name="Issues">装不到游戏会读取的位置的包。</param>
public sealed record InstallPlacementSnapshot(
    IReadOnlyList<OwnedTarget> ExpectedTargets, IReadOnlyList<PlacementIssue> Issues);
