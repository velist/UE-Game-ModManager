using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Conflict
{
    /// <summary>
    /// 冲突检测器：完整路径求解的纯函数入口。
    ///
    /// 输入领域数据（Profile + Packages 字典 + 路径），输出已解决的 ConflictRecord 列表。
    /// 不依赖 IO、不依赖 Service、不依赖日志 — 完全可独立单测。
    ///
    /// 调用方（ConflictAnalyzer）负责：
    /// - 从 PackageRepository 拿 packagesByKey 字典
    /// - 从 GameConfigService 拿 modPath/gamePath
    /// - 持久化 userOverrides
    /// - 维护事件 / LastResult / 日志
    /// </summary>
    public static class ConflictDetector
    {
        /// <summary>
        /// 检测一个 Profile 的全部文件路径冲突。
        /// </summary>
        public static List<ConflictRecord> DetectConflicts(
            InstanceProfile profile,
            IReadOnlyDictionary<string, Package> packagesByKey,
            string modPath,
            string gamePath,
            IReadOnlyDictionary<string, string>? userOverrides = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (packagesByKey == null) throw new ArgumentNullException(nameof(packagesByKey));

            var targetMap = CollectOwners(profile, packagesByKey, modPath, gamePath);
            var conflicts = new List<ConflictRecord>();

            foreach (var (loadKey, owners) in targetMap)
            {
                if (owners.Count <= 1) continue;

                var resolution = ConflictResolver.Resolve(loadKey, owners, userOverrides);
                if (resolution == null) continue;

                conflicts.Add(new ConflictRecord
                {
                    TargetPath = loadKey,
                    Type = ConflictType.LoadOrder,
                    WinnerPackageKey = resolution.WinnerPackageKey,
                    WinnerDisplayName = resolution.WinnerDisplayName,
                    Losers = resolution.Losers.ToList(),
                    Reason = resolution.Reason,
                    Resolution = resolution.Method,
                    IsUserOverride = resolution.Method == ResolutionMethod.UserOverride,
                    Severity = resolution.Severity,
                    HostGameName = profile.HostGameName,
                    ProfileId = profile.Id
                });
            }

            return conflicts;
        }

        /// <summary>
        /// 收集每个目标路径的所有声称者。Profile 中已启用的包按优先级遍历，
        /// 跳过预览图 Artifact。返回字典 key=目标绝对路径。
        /// </summary>
        public static Dictionary<string, List<ArtifactOwner>> CollectOwners(
            InstanceProfile profile,
            IReadOnlyDictionary<string, Package> packagesByKey,
            string modPath,
            string gamePath)
        {
            var targetMap = new Dictionary<string, List<ArtifactOwner>>(
                StringComparer.OrdinalIgnoreCase);

            var enabledEntries = profile.Packages
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.Priority);

            foreach (var entry in enabledEntries)
            {
                if (!packagesByKey.TryGetValue(entry.PackageKey, out var package)) continue;

                foreach (var artifact in package.Artifacts
                    .Where(a => a.ArtifactType != ArtifactType.PreviewImage))
                {
                    // 关键变化：用"假设所有包共用同一部署根"的相对路径作 key，
                    // 而不是含 PackageKey 子目录的绝对路径。
                    // 这样多个包声明同名 RelativeTargetPath 才会被判为冲突候选。
                    // 真实部署仍走每包独立子目录（DeploymentPlanner.ComputeTargetPath）；
                    // 这里只是"如果引擎按加载顺序处理同名文件会发生什么"的静态分析。
                    var key = ComputeLoadConflictKey(artifact, package, entry, modPath, gamePath);

                    if (!targetMap.TryGetValue(key, out var owners))
                    {
                        owners = new List<ArtifactOwner>();
                        targetMap[key] = owners;
                    }

                    owners.Add(new ArtifactOwner(
                        PackageKey: entry.PackageKey,
                        DisplayName: package.DisplayName,
                        Priority: entry.Priority,
                        Kind: package.Kind,
                        ArtifactHash: artifact.FileHash,
                        FileSize: artifact.FileSize));
                }
            }

            return targetMap;
        }

        /// <summary>
        /// 根据包类型计算 Artifact 在游戏目录中的目标绝对路径。
        /// - Plugin → gamePath/PluginTargetPath/PackageKey/RelativeTargetPath
        /// - Mod/Config → modPath/PackageKey/RelativeTargetPath
        /// </summary>
        public static string ComputeTargetPath(
            PackageArtifact artifact,
            Package package,
            ProfilePackageEntry entry,
            string modPath,
            string gamePath)
        {
            if (package.Kind == PackageKind.Plugin)
            {
                var pluginPath = entry.PluginTargetPath ?? package.PluginTargetPath ?? "";
                return Path.Combine(gamePath, pluginPath, package.PackageKey, artifact.RelativeTargetPath);
            }
            return Path.Combine(modPath, package.PackageKey, artifact.RelativeTargetPath);
        }

        /// <summary>
        /// 计算"加载顺序冲突"的归一化 key。与 <see cref="ComputeTargetPath"/> 不同，
        /// 这里**不包含 PackageKey 子目录**，因此多个包声明相同 RelativeTargetPath 时会归为同一 key
        /// （即引擎按加载顺序会发生覆盖的潜在冲突）。
        ///
        /// 路径布局：
        /// - Plugin → gamePath/PluginTargetPath/RelativeTargetPath
        /// - Mod/Config → modPath/RelativeTargetPath
        /// </summary>
        public static string ComputeLoadConflictKey(
            PackageArtifact artifact,
            Package package,
            ProfilePackageEntry entry,
            string modPath,
            string gamePath)
        {
            if (package.Kind == PackageKind.Plugin)
            {
                var pluginPath = entry.PluginTargetPath ?? package.PluginTargetPath ?? "";
                return Path.Combine(gamePath, pluginPath, artifact.RelativeTargetPath);
            }
            return Path.Combine(modPath, artifact.RelativeTargetPath);
        }

        /// <summary>
        /// 把目标绝对路径还原为相对路径（基于 modPath 或 gamePath）。
        /// </summary>
        public static string ComputeRelativePath(string absolutePath, string modPath, string gamePath)
        {
            if (!string.IsNullOrEmpty(modPath)
                && absolutePath.StartsWith(modPath, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(modPath, absolutePath);

            if (!string.IsNullOrEmpty(gamePath)
                && absolutePath.StartsWith(gamePath, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(gamePath, absolutePath);

            return absolutePath;
        }
    }
}
