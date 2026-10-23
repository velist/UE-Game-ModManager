using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.DeploymentPlanning;

namespace UEModManager.Services;

/// <summary>Plans against one resolved view and durable, installation-specific file ownership.</summary>
public class DeploymentPlanner
{
    private readonly ILogger<DeploymentPlanner> _logger;
    private readonly PackageRepository _packageRepository;
    private readonly ObjectStore _objectStore;
    private readonly ProfileService _profileService;
    private readonly GameConfigService _gameConfigService;
    private readonly ResolvedViewBuilder _viewBuilder;
    private readonly DeploymentStateStore _stateStore;

    public DeploymentPlanner(ILogger<DeploymentPlanner> logger, PackageRepository packageRepository,
        ObjectStore objectStore, ProfileService profileService, GameConfigService gameConfigService,
        ResolvedViewBuilder viewBuilder, DeploymentStateStore stateStore)
    {
        _logger = logger;
        _packageRepository = packageRepository;
        _objectStore = objectStore;
        _profileService = profileService;
        _gameConfigService = gameConfigService;
        _viewBuilder = viewBuilder;
        _stateStore = stateStore;
    }

    public Task<DeploymentPlan> CreatePlanAsync(bool releaseExternallyModified = false)
        => CreatePlanForProfileAsync(_profileService.CurrentProfile ?? throw new InvalidOperationException("没有活跃的 Profile"),
            releaseExternallyModified);

    public async Task<DeploymentPlan> CreatePlanForProfileAsync(InstanceProfile profile, bool releaseExternallyModified = false)
        => await CreatePlanForViewAsync(await _viewBuilder.BuildForProfileAsync(profile), releaseExternallyModified);

    /// <param name="releaseExternallyModified">
    /// 要移除的托管文件已被外部改过（典型是游戏更新换掉了它）时：false 抛
    /// <see cref="ExternallyModifiedFilesException"/>；true 则"交给游戏"——文件原样留下、
    /// 移出托管清单，不删除也不还原更新前的原版文件（那会覆盖掉游戏更新）。
    /// </param>
    public async Task<DeploymentPlan> CreatePlanForViewAsync(ResolvedView view, bool releaseExternallyModified = false)
    {
        if (view.ProfileId == Guid.Empty) throw new InvalidOperationException("没有活跃的 Profile");
        var modPath = DeploymentStateStore.NormalizeRoot(_gameConfigService.CurrentModPath);
        var gamePath = DeploymentStateStore.NormalizeRoot(_gameConfigService.CurrentGamePath);
        if (string.IsNullOrEmpty(modPath)) throw new InvalidOperationException("游戏 MOD 路径未配置");
        if (!string.Equals(modPath, view.ModRootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(gamePath, view.GameRootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("游戏目录已变化，请重新构建部署视图。");

        var stored = await _stateStore.ReadAsync(view.HostGameName, gamePath, modPath);
        var known = stored.Files.ToDictionary(f => f.TargetPath, StringComparer.OrdinalIgnoreCase);
        // Upgrade only files with committed transaction provenance. Matching a package path/content is not ownership.
        if (stored.Revision == Guid.Empty)
            await AdoptLegacyFilesAsync(stored, known);
        var before = CopyState(stored, stored.Revision, known.Values.ToList());

        var desired = new Dictionary<string, DesiredFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in view.Entries)
        {
            DeploymentStateStore.ValidateTarget(before, entry.TargetAbsolutePath);
            if (string.IsNullOrEmpty(entry.FileHash) || !File.Exists(entry.SourceAbsolutePath))
                throw new InvalidDataException($"最终视图的源文件不可用: {entry.SourceAbsolutePath}");
            desired.Add(entry.TargetAbsolutePath, new DesiredFile(
                entry.PackageKey ?? "generated", entry.PackageDisplayName ?? "生成配置",
                entry.SourceAbsolutePath, entry.TargetAbsolutePath, entry.TargetRelativePath,
                entry.FileHash, entry.FileSize, entry.PackageKind ?? PackageKind.Config));
        }

        var nextFiles = desired.Values.Select(want => new ManagedDeploymentFile
        {
            TargetPath = want.TargetPath, RelativeTargetPath = want.RelativeTargetPath,
            PackageKey = want.PackageKey, PackageDisplayName = want.PackageDisplayName, Kind = want.Kind,
            FileHash = want.FileHash!, FileSize = want.FileSize,
            OriginalFilePath = known.TryGetValue(want.TargetPath, out var prior) ? prior.OriginalFilePath : null
        }).ToList();

        var actual = new Dictionary<string, DeployedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in known.Keys.Concat(desired.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeploymentStateStore.ValidateTarget(before, path);
            if (!File.Exists(path)) continue;
            known.TryGetValue(path, out var owner);
            desired.TryGetValue(path, out var wanted);
            actual[path] = new DeployedFile(owner?.PackageKey ?? wanted?.PackageKey,
                owner?.PackageDisplayName ?? wanted?.PackageDisplayName,
                owner?.RelativeTargetPath ?? wanted!.RelativeTargetPath,
                await ObjectStore.ComputeFileHashAsync(path), new FileInfo(path).Length,
                owner?.Kind ?? wanted!.Kind, owner != null);
        }

        var modified = new List<string>();
        foreach (var owner in known.Values.Where(f => !desired.ContainsKey(f.TargetPath)))
        {
            if (actual.TryGetValue(owner.TargetPath, out var file)
                && !string.Equals(file.Hash, owner.FileHash, StringComparison.OrdinalIgnoreCase))
            {
                if (!releaseExternallyModified)
                {
                    modified.Add(owner.TargetPath);
                    continue;
                }
                // 交给游戏：既不在 desired 也不在 actual 里，diff 不会为它生成任何操作；
                // 它也不在 nextFiles 里，提交后就不再属于托管文件
                actual.Remove(owner.TargetPath);
                _logger.LogInformation("已部署文件被外部修改，交给游戏不再托管: {Path}", owner.TargetPath);
                continue;
            }
            if (owner.OriginalFilePath == null) continue;
            if (!File.Exists(owner.OriginalFilePath))
                throw new FileNotFoundException("原始文件备份缺失，已停止移除配置以保护游戏文件。", owner.OriginalFilePath);
            desired[owner.TargetPath] = new DesiredFile(owner.PackageKey, owner.PackageDisplayName,
                owner.OriginalFilePath, owner.TargetPath, owner.RelativeTargetPath,
                await ObjectStore.ComputeFileHashAsync(owner.OriginalFilePath), new FileInfo(owner.OriginalFilePath).Length, owner.Kind);
        }

        if (modified.Count > 0)
            throw new ExternallyModifiedFilesException(modified);

        var operations = DeploymentDiffComputer.ComputeDiff(desired, actual);
        foreach (var operation in operations)
        {
            operation.ExpectedTargetExists = actual.TryGetValue(operation.TargetPath, out var file);
            operation.ExpectedTargetHash = file?.Hash;
        }
        var changed = operations.Count > 0 || !SameFiles(before.Files, nextFiles)
            || (stored.Revision == Guid.Empty && nextFiles.Count > 0);
        var after = CopyState(before, changed ? Guid.NewGuid() : before.Revision, nextFiles);
        var plan = new DeploymentPlan
        {
            ProfileId = view.ProfileId, HostGameName = view.HostGameName, Operations = operations,
            BackendType = UiPreferences.LoadDeployBackend(), StateBefore = before, StateAfter = after
        };
        _logger.LogInformation("部署计划已生成: +{Add} -{Remove} ~{Replace}; managed={Managed}",
            plan.AddCount, plan.RemoveCount, plan.ReplaceCount, nextFiles.Count);
        return plan;
    }

    public Task<DeploymentPlan> CreateTogglePlanAsync(string packageKey, bool enable, bool releaseExternallyModified = false)
    {
        var profile = _profileService.CurrentProfile ?? throw new InvalidOperationException("没有活跃的 Profile");
        var package = _packageRepository.GetByKey(packageKey) ?? throw new InvalidOperationException($"包 '{packageKey}' 不存在");
        // Toggle is a proposed Profile snapshot, not an independent file shortcut. Other config contributors and
        // UserFix layers must remain part of the same resolution/ownership transaction.
        var snapshot = new InstanceProfile
        {
            Id = profile.Id, HostGameName = profile.HostGameName, Name = profile.Name,
            BackendType = profile.BackendType,
            ConflictOverrides = new Dictionary<string, string>(profile.ConflictOverrides, StringComparer.OrdinalIgnoreCase),
            Packages = profile.Packages.Select(p => new ProfilePackageEntry
            {
                PackageKey = p.PackageKey, Priority = p.Priority, Kind = p.Kind, TargetRootPath = p.TargetRootPath,
                IsEnabled = string.Equals(p.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase) ? enable : p.IsEnabled
            }).ToList()
        };
        if (!snapshot.Packages.Any(p => string.Equals(p.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase)))
            snapshot.Packages.Add(new ProfilePackageEntry
            {
                PackageKey = package.PackageKey, IsEnabled = enable, Kind = package.Kind,
                TargetRootPath = package.TargetRootPath, Priority = snapshot.Packages.Count
            });
        return CreatePlanForProfileAsync(snapshot, releaseExternallyModified);
    }

    private async Task AdoptLegacyFilesAsync(DeploymentState state, Dictionary<string, ManagedDeploymentFile> known)
    {
        var candidates = new Dictionary<string, ManagedDeploymentFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var transaction in await _stateStore.ReadLegacyTransactionsAsync(state))
        {
            if (transaction.Status == DeploymentStatus.RolledBack) continue;
            foreach (var operation in transaction.ExecutedOperations.Concat(transaction.PlannedOperations).DistinctBy(o => o.Id))
            {
                if (!DeploymentStateStore.IsInside(state.GameRootPath, operation.TargetPath)
                    && !DeploymentStateStore.IsInside(state.ModRootPath, operation.TargetPath)) continue;
                var target = Path.GetFullPath(operation.TargetPath);
                candidates.TryGetValue(target, out var previous);
                candidates.Remove(target);
                // Failed/partial/unknown/new-format transactions invalidate old ownership evidence for affected paths.
                if (transaction.Status != DeploymentStatus.Committed || transaction.StateBefore != null || transaction.StateAfter != null
                    || operation.Type == DeploymentOperationType.Remove) continue;
                if (string.IsNullOrWhiteSpace(operation.SourcePath) || string.IsNullOrWhiteSpace(operation.PackageKey)) continue;
                string packageRoot;
                try { packageRoot = _objectStore.GetPackageDirectory(operation.PackageKey); }
                catch (ArgumentException) { continue; }
                if (!DeploymentStateStore.IsInside(packageRoot, operation.SourcePath)) continue;
                DeploymentStateStore.ValidateTarget(state, target);
                var hash = operation.FileHash;
                if (string.IsNullOrEmpty(hash) && File.Exists(operation.SourcePath))
                    hash = await ObjectStore.ComputeFileHashAsync(operation.SourcePath);
                if (string.IsNullOrEmpty(hash)) continue;
                var original = previous?.OriginalFilePath;
                if (operation.Type == DeploymentOperationType.Replace && previous == null)
                {
                    original = await _stateStore.PreserveLegacyOriginalAsync(state, operation.BackupPath);
                    if (original == null) continue; // Cannot safely undo an overwrite whose original was lost.
                }
                var root = DeploymentStateStore.IsInside(state.ModRootPath, target) ? state.ModRootPath : state.GameRootPath;
                candidates[target] = new ManagedDeploymentFile
                {
                    TargetPath = target, RelativeTargetPath = Path.GetRelativePath(root, target),
                    PackageKey = operation.PackageKey, PackageDisplayName = operation.PackageDisplayName,
                    Kind = operation.PackageKind, FileHash = hash, FileSize = operation.FileSize, OriginalFilePath = original
                };
            }
        }
        foreach (var file in candidates.Values)
        {
            if (!known.ContainsKey(file.TargetPath) && File.Exists(file.TargetPath)
                && string.Equals(await ObjectStore.ComputeFileHashAsync(file.TargetPath), file.FileHash, StringComparison.OrdinalIgnoreCase))
                known[file.TargetPath] = file;
        }
    }

    private static DeploymentState CopyState(DeploymentState state, Guid revision, List<ManagedDeploymentFile> files)
        => new()
        {
            HostGameName = state.HostGameName, GameRootPath = state.GameRootPath, ModRootPath = state.ModRootPath,
            Revision = revision, Files = files
        };

    private static bool SameFiles(List<ManagedDeploymentFile> before, List<ManagedDeploymentFile> after)
    {
        if (before.Count != after.Count) return false;
        var map = before.ToDictionary(f => f.TargetPath, StringComparer.OrdinalIgnoreCase);
        return after.All(f => map.TryGetValue(f.TargetPath, out var other)
            && f.PackageKey == other.PackageKey && f.PackageDisplayName == other.PackageDisplayName
            && f.RelativeTargetPath == other.RelativeTargetPath && f.Kind == other.Kind
            && f.FileHash == other.FileHash && f.FileSize == other.FileSize && f.OriginalFilePath == other.OriginalFilePath);
    }
}

/// <summary>
/// 要移除的托管文件已被外部改过（多半是游戏更新换掉了它），为了安全没有动它们。
/// 调用方应询问玩家是否"交给游戏"，同意后以 releaseExternallyModified=true 重新规划。
/// </summary>
public sealed class ExternallyModifiedFilesException : IOException
{
    public ExternallyModifiedFilesException(IReadOnlyList<string> paths)
        : base(UEModManager.Localization.UiText.Interpolate(
            $"游戏更新后改动了 {paths.Count} 个已安装的文件，为了安全没有删除它们：{Path.GetFileName(paths[0])}"))
    {
        Paths = paths;
    }

    /// <summary>被外部修改的目标文件（绝对路径）。</summary>
    public IReadOnlyList<string> Paths { get; }
}
