using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;
using UEModManager.Services.Config;

namespace UEModManager.Services.ResolvedViews
{
    /// <summary>
    /// 最终视图各层的纯函数构造器。
    ///
    /// 主项目的 ResolvedViewBuilder 编排此处的纯函数 + IO（读 ConfigMergeEngine 合并结果、
    /// 读 OverwriteStore 生成物等），最后聚合成 <see cref="ResolvedView"/>。
    /// </summary>
    public static class ResolvedViewLayerBuilder
    {
        /// <summary>
        /// Layer 1: Package 文件层 + 冲突解决。
        ///
        /// 给定 profile（含已启用包条目）+ 包字典 + 对象仓库查询接口，
        /// 输出该层产生的 ResolvedEntry 列表和路径冲突记录。
        ///
        /// 冲突识别 key：使用 <see cref="PackageArtifact.RelativeTargetPath"/>（不含 PackageKey），
        /// 与 <see cref="Conflict.ConflictDetector"/> 的 LoadConflictKey 语义一致。
        /// </summary>
        public static (List<ResolvedEntry> Entries, List<ConflictRecord> Conflicts) BuildPackageLayer(
            InstanceProfile profile,
            IReadOnlyDictionary<string, Package> packagesByKey,
            IObjectStoreQuery objectStore)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (packagesByKey == null) throw new ArgumentNullException(nameof(packagesByKey));
            if (objectStore == null) throw new ArgumentNullException(nameof(objectStore));

            var entries = new List<ResolvedEntry>();
            var conflicts = new List<ConflictRecord>();

            // 路径 → (条目, 优先级) 候选，用于检测冲突
            var pathMap = new Dictionary<string, List<(ResolvedEntry entry, int priority)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var profileEntry in profile.Packages
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.Priority))
            {
                if (!packagesByKey.TryGetValue(profileEntry.PackageKey, out var package))
                    continue;

                foreach (var artifact in package.Artifacts)
                {
                    if (artifact.ArtifactType == ArtifactType.PreviewImage) continue;

                    var entry = new ResolvedEntry
                    {
                        TargetRelativePath = artifact.RelativeTargetPath,
                        SourceAbsolutePath = Path.Combine(
                            objectStore.GetPackageFilesDirectory(package.PackageKey),
                            artifact.RelativeSourcePath),
                        Source = ResolvedEntrySource.Package,
                        PackageKey = package.PackageKey,
                        PackageDisplayName = package.DisplayName,
                        PackageKind = package.Kind,
                        FileSize = artifact.FileSize,
                        FileHash = artifact.FileHash,
                        Priority = profileEntry.Priority,
                        ArtifactType = artifact.ArtifactType,
                    };

                    if (!pathMap.TryGetValue(artifact.RelativeTargetPath, out var list))
                    {
                        list = [];
                        pathMap[artifact.RelativeTargetPath] = list;
                    }
                    list.Add((entry, profileEntry.Priority));
                }
            }

            // 冲突解决
            foreach (var (path, candidates) in pathMap)
            {
                if (candidates.Count == 1)
                {
                    entries.Add(candidates[0].entry);
                    continue;
                }

                var sorted = candidates.OrderBy(c => c.priority).ToList();
                var winner = sorted[0];
                var losers = sorted.Skip(1).ToList();

                entries.Add(new ResolvedEntry
                {
                    TargetRelativePath = winner.entry.TargetRelativePath,
                    SourceAbsolutePath = winner.entry.SourceAbsolutePath,
                    Source = winner.entry.Source,
                    PackageKey = winner.entry.PackageKey,
                    PackageDisplayName = winner.entry.PackageDisplayName,
                    PackageKind = winner.entry.PackageKind,
                    FileSize = winner.entry.FileSize,
                    FileHash = winner.entry.FileHash,
                    Priority = winner.priority,
                    ArtifactType = winner.entry.ArtifactType,
                    IsConflictWinner = true,
                    OverriddenPackageKeys = losers.Select(l => l.entry.PackageKey ?? "").ToList(),
                });

                conflicts.Add(new ConflictRecord
                {
                    TargetPath = path,
                    Type = ConflictType.LoadOrder,  // 与 ConflictDetector 修复后语义一致
                    WinnerPackageKey = winner.entry.PackageKey ?? "",
                    WinnerDisplayName = winner.entry.PackageDisplayName ?? "",
                    Losers = losers.Select(l => new ConflictLoser
                    {
                        PackageKey = l.entry.PackageKey ?? "",
                        DisplayName = l.entry.PackageDisplayName ?? "",
                        Priority = l.priority,
                    }).ToList(),
                    Reason = "优先级求解",
                    Resolution = ResolutionMethod.Priority,
                    Severity = ConflictSeverity.Warning,
                    HostGameName = profile.HostGameName,
                    ProfileId = profile.Id,
                });
            }

            return (entries, conflicts);
        }

        /// <summary>
        /// Layer 2 (前半)：从 Package 层结果中识别需要键级合并的配置文件，构造 <see cref="ConfigMergePlan"/> 列表。
        ///
        /// 选取规则：
        /// 1. <see cref="ResolvedEntry.ArtifactType"/> == <see cref="ArtifactType.ConfigFile"/>。
        /// 2. 同一 <see cref="ResolvedEntry.TargetRelativePath"/> 上有 ≥ 2 个候选条目（即多个包贡献同一配置文件）。
        /// 3. 文件扩展名能被 <see cref="ConfigMerger.DetectFormat"/> 识别（Ini/Json/Cfg/...，非 Unknown）。
        ///
        /// 注意：BuildPackageLayer 的胜者求解会让单一目标路径只保留 1 个 entry。
        /// 因此这里需要的是"包层冲突解决前"的候选——调用方通常会传入"未经胜者求解的全量条目"，
        /// 或显式记录每个目标路径的全部候选。当前实现接受 entries 列表并按 TargetRelativePath 聚合，
        /// 调用方有责任传入合适的输入（详见 ResolvedViewBuilder 调用点）。
        ///
        /// 返回的每个 plan 的 Sources 已按 Priority 升序排列；BaseContent 留给主项目从游戏目录读入。
        /// </summary>
        /// <param name="configCandidateEntries">候选配置文件条目（同一路径可重复出现，每次代表一个包的贡献）。</param>
        /// <returns>需要键级合并的 plan 列表。空集表示没有需要合并的文件。</returns>
        public static List<ConfigMergePlan> BuildConfigMergePlans(
            IReadOnlyList<ResolvedEntry> configCandidateEntries)
        {
            if (configCandidateEntries == null) throw new ArgumentNullException(nameof(configCandidateEntries));

            var plans = new List<ConfigMergePlan>();

            var groups = configCandidateEntries
                .Where(e => e.ArtifactType == ArtifactType.ConfigFile)
                .GroupBy(e => e.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var group in groups)
            {
                var format = ConfigMerger.DetectFormat(group.Key);
                if (format == ConfigFormat.Unknown) continue;

                var plan = new ConfigMergePlan
                {
                    TargetRelativePath = group.Key,
                    Format = format,
                    Strategy = ConfigMergeStrategy.MergeByKey,
                    Sources = group
                        .OrderBy(e => e.Priority)
                        .Select(e => new ConfigMergeSource
                        {
                            PackageKey = e.PackageKey ?? "",
                            DisplayName = e.PackageDisplayName ?? "",
                            SourceFilePath = e.SourceAbsolutePath,
                            Priority = e.Priority,
                        })
                        .ToList(),
                };

                plans.Add(plan);
            }

            return plans;
        }

        /// <summary>
        /// Layer 2 (后半)：把 <see cref="ConfigMerger"/> 返回的 <see cref="ConfigKeyConflict"/> 列表
        /// 翻译为 <see cref="ConflictRecord"/>（Type = <see cref="ConflictType.ConfigKey"/>）。
        ///
        /// 一个键级冲突 → 一条 ConflictRecord；TargetPath 编码为 <c>{filePath} [{Section}.{Key}]</c>。
        /// 严重程度统一为 Info（仅是覆盖，不导致功能异常）。
        /// </summary>
        public static List<ConflictRecord> TranslateConfigKeyConflicts(
            string targetRelativePath,
            IReadOnlyList<ConfigKeyConflict> keyConflicts,
            string hostGameName,
            Guid profileId)
        {
            if (targetRelativePath == null) throw new ArgumentNullException(nameof(targetRelativePath));
            if (keyConflicts == null) throw new ArgumentNullException(nameof(keyConflicts));

            var records = new List<ConflictRecord>(keyConflicts.Count);
            foreach (var kc in keyConflicts)
            {
                records.Add(new ConflictRecord
                {
                    TargetPath = $"{targetRelativePath} [{kc.FullKey}]",
                    Type = ConflictType.ConfigKey,
                    WinnerPackageKey = kc.WinnerPackageKey,
                    WinnerDisplayName = kc.WinnerPackageKey,
                    Losers = kc.Losers.Select(l => new ConflictLoser
                    {
                        PackageKey = l.PackageKey,
                        DisplayName = l.DisplayName,
                        Priority = l.Priority,
                    }).ToList(),
                    Reason = $"配置键 {kc.FullKey} 被多个包修改",
                    Resolution = ResolutionMethod.Priority,
                    Severity = ConflictSeverity.Info,
                    HostGameName = hostGameName ?? "",
                    ProfileId = profileId,
                });
            }
            return records;
        }

        /// <summary>
        /// Layer 3：把生成物 <see cref="GeneratedArtifact"/> 映射为 <see cref="ResolvedEntry"/>。
        ///
        /// 仅当 <see cref="GeneratedArtifact.RelativeTargetPath"/> 非空时才返回条目；
        /// 否则返回 null（生成物缺少部署目标路径，无法纳入最终视图）。
        ///
        /// 调用方负责：① 过滤 Active + UserFix 状态；② 计算 sourceAbsolutePath；③ 验证文件存在。
        /// 这里只做纯映射。
        /// </summary>
        public static ResolvedEntry? BuildOverwriteEntry(
            GeneratedArtifact overwrite,
            string sourceAbsolutePath)
        {
            if (overwrite == null) throw new ArgumentNullException(nameof(overwrite));
            if (sourceAbsolutePath == null) throw new ArgumentNullException(nameof(sourceAbsolutePath));

            if (string.IsNullOrEmpty(overwrite.RelativeTargetPath)) return null;

            return new ResolvedEntry
            {
                TargetRelativePath = overwrite.RelativeTargetPath,
                SourceAbsolutePath = sourceAbsolutePath,
                Source = ResolvedEntrySource.UserOverride,
                FileSize = overwrite.FileSize,
                FileHash = overwrite.FileHash,
                Priority = -1, // 用户覆盖最高优先级
            };
        }
    }
}
