using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Config
{
    /// <summary>
    /// 配置合并的"纯函数"层。无 IO，无日志，无状态。
    ///
    /// 调用方负责：
    /// - 读取每个 ConfigMergeSource.SourceFilePath 文件内容（通常由 ConfigMergeEngine 完成）
    /// - 把 (PackageKey → 文件内容) 字典传入 Merge()
    ///
    /// 这里只负责：解析、合并、冲突检测、来源追踪。
    /// </summary>
    public static class ConfigMerger
    {
        /// <summary>按文件扩展名识别配置格式。</summary>
        public static ConfigFormat DetectFormat(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".ini" => ConfigFormat.Ini,
                ".json" => ConfigFormat.Json,
                ".yaml" or ".yml" => ConfigFormat.Yaml,
                ".toml" => ConfigFormat.Toml,
                ".cfg" => ConfigFormat.Cfg,
                _ => ConfigFormat.Unknown
            };
        }

        /// <summary>按内容探测格式（依赖每个 parser 的 CanParse）。</summary>
        public static ConfigFormat DetectFormatByContent(
            string content,
            IReadOnlyDictionary<ConfigFormat, IConfigParser> parsers)
        {
            foreach (var (format, parser) in parsers)
            {
                if (parser.CanParse(content)) return format;
            }
            return ConfigFormat.Unknown;
        }

        /// <summary>
        /// 执行合并（纯函数）。
        /// </summary>
        /// <param name="plan">合并计划。</param>
        /// <param name="sourceContents">每个 PackageKey 对应的已加载文件内容。
        /// 缺失的来源会被自动跳过（视为"提供方未提供"）。</param>
        /// <param name="parsers">解析器注册表（必须涵盖 plan.Format 对应的 parser，除非 plan 走整文件替换）。</param>
        /// <returns>合并结果。失败时 Success=false 且 ErrorMessage 非空。</returns>
        public static ConfigMergeResult Merge(
            ConfigMergePlan plan,
            IReadOnlyDictionary<string, string> sourceContents,
            IReadOnlyDictionary<ConfigFormat, IConfigParser> parsers)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (sourceContents == null) throw new ArgumentNullException(nameof(sourceContents));
            if (parsers == null) throw new ArgumentNullException(nameof(parsers));

            try
            {
                // 不支持的格式 / ReplaceFile 策略 → 整文件替换
                if (plan.Format is ConfigFormat.Unknown or ConfigFormat.Yaml or ConfigFormat.Toml
                    || !parsers.TryGetValue(plan.Format, out var parser)
                    || plan.Strategy == ConfigMergeStrategy.ReplaceFile)
                {
                    return HandleFileReplace(plan, sourceContents);
                }

                // 解析基础内容
                var baseEntries = !string.IsNullOrEmpty(plan.BaseContent)
                    ? parser.Parse(plan.BaseContent)
                    : new List<ConfigEntry>();

                // 高优先级在后（后写覆盖前写）
                var orderedSources = plan.Sources.OrderByDescending(s => s.Priority).ToList();

                var allSourceEntries = new List<(ConfigMergeSource source, List<ConfigEntry> entries)>();
                foreach (var source in orderedSources)
                {
                    if (!sourceContents.TryGetValue(source.PackageKey, out var content)) continue;
                    var entries = parser.Parse(content);
                    allSourceEntries.Add((source, entries));
                }

                return plan.Strategy switch
                {
                    ConfigMergeStrategy.MergeByKey => MergeByKey(plan, parser, baseEntries, allSourceEntries),
                    ConfigMergeStrategy.Patch => MergePatch(plan, parser, baseEntries, allSourceEntries),
                    ConfigMergeStrategy.Append => MergeAppend(plan, parser, baseEntries, allSourceEntries),
                    _ => HandleFileReplace(plan, sourceContents)
                };
            }
            catch (Exception ex)
            {
                return new ConfigMergeResult
                {
                    Success = false,
                    TargetRelativePath = plan.TargetRelativePath,
                    ErrorMessage = ex.Message
                };
            }
        }

        // ─── 合并策略实现 ───

        private static ConfigMergeResult MergeByKey(
            ConfigMergePlan plan,
            IConfigParser parser,
            List<ConfigEntry> baseEntries,
            List<(ConfigMergeSource source, List<ConfigEntry> entries)> allSourceEntries)
        {
            var mergedMap = new Dictionary<string, (ConfigEntry entry, ConfigMergeSource source)>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<ConfigKeyConflict>();
            var entrySourceMap = new List<ConfigEntrySource>();

            // 基础条目
            foreach (var entry in baseEntries)
            {
                if (!string.IsNullOrEmpty(entry.Key))
                    mergedMap[entry.FullKey] = (entry, new ConfigMergeSource { PackageKey = "_base", DisplayName = "原始文件" });
            }

            // 按优先级覆盖（高优先级在后）
            foreach (var (source, entries) in allSourceEntries)
            {
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;

                    if (mergedMap.TryGetValue(entry.FullKey, out var existing) &&
                        existing.source.PackageKey != "_base" &&
                        existing.source.PackageKey != source.PackageKey)
                    {
                        var conflict = conflicts.FirstOrDefault(c => c.FullKey == entry.FullKey);
                        if (conflict == null)
                        {
                            conflict = new ConfigKeyConflict
                            {
                                Section = entry.Section,
                                Key = entry.Key,
                                WinnerPackageKey = source.PackageKey,
                                WinnerValue = entry.Value,
                                Losers = [new ConfigKeyConflictLoser
                                {
                                    PackageKey = existing.source.PackageKey,
                                    DisplayName = existing.source.DisplayName,
                                    Value = existing.entry.Value,
                                    Priority = existing.source.Priority
                                }]
                            };
                            conflicts.Add(conflict);
                        }
                        else
                        {
                            conflict.Losers.Add(new ConfigKeyConflictLoser
                            {
                                PackageKey = existing.source.PackageKey,
                                DisplayName = existing.source.DisplayName,
                                Value = existing.entry.Value,
                                Priority = existing.source.Priority
                            });
                            conflict.WinnerPackageKey = source.PackageKey;
                            conflict.WinnerValue = entry.Value;
                        }
                    }

                    mergedMap[entry.FullKey] = (entry, source);
                }
            }

            var resultEntries = mergedMap.Values.Select(v => v.entry).ToList();
            var mergedContent = parser.Serialize(resultEntries);

            foreach (var (key, (entry, source)) in mergedMap)
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                entrySourceMap.Add(new ConfigEntrySource
                {
                    Section = entry.Section,
                    Key = entry.Key,
                    Value = entry.Value,
                    SourcePackageKey = source.PackageKey,
                    SourceDisplayName = source.DisplayName,
                    IsConflictResolution = conflicts.Any(c => c.FullKey == entry.FullKey)
                });
            }

            return new ConfigMergeResult
            {
                Success = true,
                MergedContent = mergedContent,
                TargetRelativePath = plan.TargetRelativePath,
                EntrySourceMap = entrySourceMap,
                Conflicts = conflicts
            };
        }

        private static ConfigMergeResult MergePatch(
            ConfigMergePlan plan,
            IConfigParser parser,
            List<ConfigEntry> baseEntries,
            List<(ConfigMergeSource source, List<ConfigEntry> entries)> allSourceEntries)
        {
            var mergedMap = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in baseEntries)
            {
                if (!string.IsNullOrEmpty(entry.Key))
                    mergedMap[entry.FullKey] = entry;
            }

            var entrySourceMap = new List<ConfigEntrySource>();

            foreach (var (source, entries) in allSourceEntries)
            {
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    mergedMap[entry.FullKey] = entry;
                    entrySourceMap.Add(new ConfigEntrySource
                    {
                        Section = entry.Section,
                        Key = entry.Key,
                        Value = entry.Value,
                        SourcePackageKey = source.PackageKey,
                        SourceDisplayName = source.DisplayName
                    });
                }
            }

            return new ConfigMergeResult
            {
                Success = true,
                MergedContent = parser.Serialize(mergedMap.Values.ToList()),
                TargetRelativePath = plan.TargetRelativePath,
                EntrySourceMap = entrySourceMap,
                Conflicts = []
            };
        }

        private static ConfigMergeResult MergeAppend(
            ConfigMergePlan plan,
            IConfigParser parser,
            List<ConfigEntry> baseEntries,
            List<(ConfigMergeSource source, List<ConfigEntry> entries)> allSourceEntries)
        {
            var allEntries = new List<ConfigEntry>(baseEntries);
            foreach (var (_, entries) in allSourceEntries)
            {
                allEntries.AddRange(entries);
            }

            return new ConfigMergeResult
            {
                Success = true,
                MergedContent = parser.Serialize(allEntries),
                TargetRelativePath = plan.TargetRelativePath,
                EntrySourceMap = [],
                Conflicts = []
            };
        }

        private static ConfigMergeResult HandleFileReplace(
            ConfigMergePlan plan,
            IReadOnlyDictionary<string, string> sourceContents)
        {
            // 整文件替换：取最高优先级（数字最小）来源
            var topSource = plan.Sources.OrderBy(s => s.Priority).FirstOrDefault();

            if (topSource == null)
            {
                return new ConfigMergeResult
                {
                    Success = true,
                    MergedContent = plan.BaseContent ?? "",
                    TargetRelativePath = plan.TargetRelativePath
                };
            }

            sourceContents.TryGetValue(topSource.PackageKey, out var content);

            return new ConfigMergeResult
            {
                Success = true,
                MergedContent = content ?? "",
                TargetRelativePath = plan.TargetRelativePath,
                EntrySourceMap =
                [
                    new ConfigEntrySource
                    {
                        Key = "(整文件替换)",
                        Value = topSource.SourceFilePath,
                        SourcePackageKey = topSource.PackageKey,
                        SourceDisplayName = topSource.DisplayName
                    }
                ]
            };
        }
    }
}
