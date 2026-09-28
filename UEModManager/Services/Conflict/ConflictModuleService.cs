using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Conflict.Model;

namespace UEModManager.Services.Conflict;

/// <summary>
/// 冲突检测模块的对外门面。
///
/// 取代旧的 <c>ConflictAnalyzer</c>：后者产出的是"哪个包在哪个路径上赢了"，
/// 而玩家要的是"哪几个 MOD 改到了同一批资源、我该留哪个"。
/// 覆盖规则的读写也集中在这里 —— 新模型下覆盖规则只由共享组件裁决写入，
/// 不再由用户逐条指定胜者。
/// </summary>
public sealed class ConflictModuleService
{
    private readonly ILogger<ConflictModuleService> _logger;
    private readonly PackageRepository _packageRepository;
    private readonly ProfileService _profileService;
    private readonly GameConfigService _gameConfigService;
    private readonly AssetIndexService _assetIndexService;

    private ConflictStrategy _strategy = ConflictStrategy.ContainerAssets;
    private IReadOnlyList<SharedComponentRule> _rules = [];

    /// <summary>最近一次分析结果。</summary>
    public ConflictReport? LastReport { get; private set; }

    /// <summary>报告更新事件。卡片角标与启动中心据此刷新。</summary>
    public event Action<ConflictReport>? ReportUpdated;

    public ConflictModuleService(
        ILogger<ConflictModuleService> logger,
        PackageRepository packageRepository,
        ProfileService profileService,
        GameConfigService gameConfigService,
        AssetIndexService assetIndexService)
    {
        _logger = logger;
        _packageRepository = packageRepository;
        _profileService = profileService;
        _gameConfigService = gameConfigService;
        _assetIndexService = assetIndexService;

        // 方案切换后旧报告立刻作废：成员、启用集合、覆盖规则都可能已经不同。
        _profileService.ProfileChanged += _ => LastReport = null;
    }

    /// <summary>游戏切换后清空快照，并按新引擎的策略重置。</summary>
    public async Task SetCurrentGameAsync(string gameName)
    {
        var profile = EngineProfile.Get(_gameConfigService.CurrentEngineType);
        _strategy = profile.ConflictStrategy;
        _rules = profile.SharedComponentRules;
        LastReport = null;
        await _assetIndexService.SetCurrentGameAsync(gameName);
    }

    /// <summary>
    /// 分析当前方案里已启用包的冲突。
    /// </summary>
    public async Task<ConflictReport> AnalyzeAsync()
    {
        var profile = _profileService.CurrentProfile
            ?? throw new InvalidOperationException("没有活跃的 Profile");

        var enabled = profile.Packages.Where(p => p.IsEnabled).ToList();
        var packages = enabled
            .Select(e => _packageRepository.GetByKey(e.PackageKey))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        var indexes = new List<ModAssetIndex>(packages.Count);
        foreach (var package in packages)
            indexes.Add(await _assetIndexService.GetIndexAsync(package, _strategy));

        var overlaps = AssetConflictDetector.FindOverlaps(indexes);
        await _assetIndexService.FillFingerprintsAsync(overlaps);
        var filled = _assetIndexService.ApplyFingerprints(indexes);

        var report = AssetConflictDetector.Build(
            filled, _rules, _assetIndexService.CollectProvenance(packages));

        LastReport = report;
        await PersistSharedComponentChoicesAsync(report);

        _logger.LogInformation(
            "冲突分析完成: {Mods} 个已启用包, {Assets} 个资产; {Groups} 组冲突, "
            + "{Dups} 处重复, {Shared} 个共享组件, {Hidden} 处内容相同已隐藏, {Unanalyzable} 个无法分析",
            report.Stats.EnabledMods, report.Stats.IndexedAssets,
            report.Groups.Count, report.Duplicates.Count, report.SharedComponents.Count,
            report.Stats.IdenticalOverlapsHidden, report.Unanalyzable.Count);

        ReportUpdated?.Invoke(report);
        return report;
    }

    /// <summary>
    /// 卡片角标用：该包出现在几个冲突组里。
    /// 组数（而不是资产数）才是用户能对照的东西 —— 面板上有几个框，卡片上就写几。
    /// </summary>
    public int ConflictCountFor(string packageKey)
    {
        if (LastReport is null || string.IsNullOrEmpty(packageKey)) return 0;
        return LastReport.Groups.Count(g => g.Members.Any(m =>
            string.Equals(m.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>卡片"重复"标签用。</summary>
    public bool IsDuplicate(string packageKey)
    {
        if (LastReport is null || string.IsNullOrEmpty(packageKey)) return false;
        return LastReport.Duplicates.Any(d =>
            string.Equals(d.Primary.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase)
            || d.Copies.Any(c => string.Equals(c.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 改用另一个包携带的共享组件版本：写入覆盖规则，让部署层落盘那一份。
    /// </summary>
    public async Task ApplySharedComponentChoiceAsync(string componentName, string packageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageKey);

        var component = LastReport?.SharedComponents.FirstOrDefault(c =>
            string.Equals(c.Name, componentName, StringComparison.Ordinal));
        if (component is null)
        {
            _logger.LogWarning("共享组件不存在: {Component}", componentName);
            return;
        }

        var provider = component.Providers.FirstOrDefault(p =>
            string.Equals(p.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            _logger.LogWarning("共享组件 {Component} 没有来自 {Package} 的版本", componentName, packageKey);
            return;
        }

        // 组件名下所有落在同一路径的文件都要改判 —— 只改其中一个会让
        // "UE4SS 运行时"里一半来自 A、一半来自 B，版本混搭比不换还糟。
        var targets = await CollectComponentPathsAsync(component);
        if (targets.Count == 0)
        {
            _logger.LogInformation("共享组件 {Component} 没有可写规则的目标路径", componentName);
            return;
        }

        await SetOverridesAsync(targets.ToDictionary(p => p, _ => packageKey, StringComparer.OrdinalIgnoreCase));
        _logger.LogInformation("共享组件 {Component} 改用 {Package}（{Count} 个文件）",
            componentName, packageKey, targets.Count);
    }

    // ─── 覆盖规则（原 ConflictAnalyzer 的职责，整体搬过来） ───

    /// <summary>一次性写入多条覆盖规则，只落盘一次方案文件。</summary>
    public async Task SetOverridesAsync(IReadOnlyDictionary<string, string> targetPathToWinner)
    {
        ArgumentNullException.ThrowIfNull(targetPathToWinner);
        if (targetPathToWinner.Count == 0) return;
        foreach (var (_, winner) in targetPathToWinner)
            ArgumentException.ThrowIfNullOrWhiteSpace(winner);

        await MutateOverridesAsync(rules =>
        {
            foreach (var (targetPath, winner) in targetPathToWinner)
                rules[ConflictOverridePaths.ToPortableKey(
                    targetPath, _gameConfigService.CurrentModPath, _gameConfigService.CurrentGamePath)] = winner;
        });
    }

    /// <summary>一次性移除多条覆盖规则，只落盘一次方案文件。</summary>
    public async Task RemoveOverridesAsync(IEnumerable<string> targetPaths)
    {
        ArgumentNullException.ThrowIfNull(targetPaths);

        var portableKeys = targetPaths
            .Select(p => ConflictOverridePaths.ToPortableKey(
                p, _gameConfigService.CurrentModPath, _gameConfigService.CurrentGamePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (portableKeys.Count == 0) return;

        await MutateOverridesAsync(rules =>
        {
            foreach (var key in portableKeys) rules.Remove(key);
        });
    }

    /// <summary>当前方案在本机游戏目录下的覆盖规则快照。</summary>
    public IReadOnlyDictionary<string, string> GetOverrides()
        => _profileService.CurrentProfile is { } profile
            ? ConflictOverridePaths.Resolve(profile.ConflictOverrides,
                _gameConfigService.CurrentModPath, _gameConfigService.CurrentGamePath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 方案的可移植覆盖规则快照（<c>@mod/</c> / <c>@game/</c> 前缀）。
    ///
    /// 导出 lock / 整合包时必须用这一份：<see cref="GetOverrides"/> 的结果里带着
    /// 本机安装盘符，写进 lock 文件后换台机器就失效。
    /// </summary>
    public IReadOnlyDictionary<string, string> GetPortableOverrides(InstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ConflictOverridePaths.MakePortable(
            profile.ConflictOverrides,
            _gameConfigService.CurrentModPath,
            _gameConfigService.CurrentGamePath);
    }

    /// <summary>设定或清除单条覆盖规则（<paramref name="winnerPackageKey"/> 为 null 即清除）。</summary>
    public async Task SetOverrideAsync(string targetPath, string? winnerPackageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var portableKey = ConflictOverridePaths.ToPortableKey(
            targetPath, _gameConfigService.CurrentModPath, _gameConfigService.CurrentGamePath);

        await MutateOverridesAsync(rules =>
        {
            if (winnerPackageKey is null) rules.Remove(portableKey);
            else rules[portableKey] = winnerPackageKey;
        });
    }

    /// <summary>
    /// 覆盖规则的唯一改写入口：规范化旧绝对键 → 应用 <paramref name="mutate"/> → 落盘一次。
    ///
    /// 无论改一条还是五百条，方案文件只写一遍。
    /// </summary>
    private async Task MutateOverridesAsync(Action<Dictionary<string, string>> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var profile = _profileService.CurrentProfile
            ?? throw new InvalidOperationException("没有活跃的 Profile");
        var modPath = _gameConfigService.CurrentModPath;
        var gamePath = _gameConfigService.CurrentGamePath;

        await _profileService.UpdateConflictOverridesAsync(profile.Id, rules =>
        {
            // 顺带规范化旧绝对键，避免删除新键后旧别名的规则重新出现。
            var portable = ConflictOverridePaths.MakePortable(rules, modPath, gamePath);
            rules.Clear();
            // 大小写不敏感的比较器与 ConflictOverridePaths 的语义保持一致：
            // 同一路径只有大小写不同时不该留下两条规则。
            foreach (var (key, value) in portable) rules[key] = value;
            mutate(rules);
        });
        LastReport = null;
    }

    /// <summary>
    /// 把共享组件的裁决写成覆盖规则。
    ///
    /// 各包自带的同名运行时文件在游戏目录里落在同一路径上，部署层靠覆盖规则
    /// 决定落盘哪一份。只写"内容不同"那些 —— 内容相同的写进去没有行为差异，
    /// 只会撑大方案文件。
    /// </summary>
    private async Task PersistSharedComponentChoicesAsync(ConflictReport report)
    {
        var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in report.SharedComponents)
        {
            if (component.Providers.All(p => p.IdenticalToChosen)) continue;

            foreach (var path in await CollectComponentPathsAsync(component))
                changed[path] = component.ChosenPackageKey;
        }

        if (changed.Count == 0) return;

        var current = GetOverrides();
        var effective = changed
            .Where(kv => !current.TryGetValue(kv.Key, out var existing)
                         || !string.Equals(existing, kv.Value, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (effective.Count == 0) return;

        await SetOverridesAsync(effective);
        _logger.LogInformation("共享组件裁决已写入覆盖规则: {Count} 条", effective.Count);
    }

    /// <summary>
    /// 找出某个共享组件在游戏目录下的目标路径：取该组件下所有 Provider 的
    /// 条目路径的并集 —— 各包带的具体文件集合可能不同（有人多带一个脚本），
    /// 但凡是落在同一路径上的都应该由同一个 Provider 提供。
    /// </summary>
    private async Task<List<string>> CollectComponentPathsAsync(SharedComponent component)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = _profileService.CurrentProfile;
        if (profile is null) return [];

        foreach (var provider in component.Providers)
        {
            var package = _packageRepository.GetByKey(provider.PackageKey);
            if (package is null) continue;

            var index = await _assetIndexService.GetIndexAsync(package, ConflictStrategy.FilePath);
            foreach (var entry in index.Entries)
            {
                if (string.Equals(
                    SharedComponentClassifier.Classify(entry.DisplayPath, _rules),
                    component.Name, StringComparison.Ordinal))
                    paths.Add(entry.DisplayPath);
            }
        }

        return paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
