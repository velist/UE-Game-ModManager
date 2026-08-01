using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 剑星（Stellar Blade）CNS 模式的分组合并（纯函数）。
    ///
    /// <para><b>要解决的问题：</b>CNS 模式下一个 MOD 由两个文件组成——外观本体 <c>.pak</c>
    /// 和给 CNS 框架读的配置 <c>.json</c>，而两者的文件名并不同源，例如
    /// <c>DekCNS-Nier2B.json</c> 配 <c>Nier2B_P.pak</c>。
    /// <see cref="ModFileGrouper.GroupByBaseName"/> 只按"去掉 UE patch 后缀后的基础名"分组，
    /// 会把这两个文件判成两个包：用户导入后看到两条记录，只启用其中一个则配置不生效。</para>
    ///
    /// <para><b>匹配方式：</b>把两侧文件名切成关键词后互相打分，取分数最高且过阈值的一组合并。
    /// 这是模糊匹配，不是精确规则——CNS 社区的命名没有统一约定，只能靠关键词重合度猜。</para>
    ///
    /// <para><b>来历：</b>该逻辑原本在 v1.8 的 <c>ModManagementService</c>（该类已在 v2.0
    /// 作为死链路整体删除）里，v2.0 导入链路重写时被整条遗漏，属于静默功能回退
    /// （用户表现为"CNS MOD 导进来变成两个"）。
    /// 迁到 Core 并补测试，就是为了让这份领域知识不再依赖"有人记得它存在"。</para>
    /// </summary>
    public static class CnsGroupMerger
    {
        /// <summary>关键词完全相同的得分。</summary>
        private const int ExactKeywordScore = 3;

        /// <summary>关键词互为子串的得分。</summary>
        private const int PartialKeywordScore = 2;

        /// <summary>合并所需的最低总分，低于此分视为"只是碰巧有共同字样"。</summary>
        private const int MinimumMatchScore = 2;

        /// <summary>太短的片段（如 v2、ab）没有区分度，不参与打分。</summary>
        private const int MinimumKeywordLength = 3;

        private const string ConfigExtension = ".json";
        private const string ContentExtension = ".pak";

        private static readonly char[] KeywordSeparators = { '-', '_', '.' };

        /// <summary>
        /// 把 CNS 配置组（含 <c>.json</c>）与其对应的内容组（含 <c>.pak</c>）合并。
        /// </summary>
        /// <param name="groups">
        /// <see cref="ModFileGrouper.GroupByBaseName"/> 的分组结果：组桶名 → 组内文件。
        /// </param>
        /// <returns>
        /// 合并后的新字典。配置组保留其组桶名，内容组的文件被并入其中、该组被移除；
        /// 每组的文件列表都是新列表，**不会**改动入参。
        /// </returns>
        public static IReadOnlyDictionary<string, List<string>> Merge(
            IReadOnlyDictionary<string, List<string>> groups)
        {
            if (groups == null) throw new ArgumentNullException(nameof(groups));

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
                result[group.Key] = new List<string>(group.Value);

            // 一个组一旦参与过合并就不再被匹配：避免一个 .pak 被两个 .json 同时认领
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                if (processed.Contains(group.Key)) continue;

                var configFile = group.Value.FirstOrDefault(f => HasExtension(f, ConfigExtension));
                if (configFile == null) continue;

                var configName = Path.GetFileNameWithoutExtension(configFile);
                if (!LooksLikeCnsConfigName(configName)) continue;

                var configKeywords = ExtractKeywords(configName);
                var bestKey = FindBestContentGroup(groups, group.Key, configKeywords, processed);
                if (bestKey == null) continue;

                result[group.Key].AddRange(groups[bestKey]);
                result.Remove(bestKey);
                processed.Add(bestKey);
                processed.Add(group.Key);
            }

            return result;
        }

        /// <summary>
        /// 文件名是否像 CNS 配置。CNS 配置惯例带 <c>DekCNS-</c> 前缀或 <c>.dekcns</c> 中缀，
        /// 二者都含 <c>cns</c>，故只判断这一个子串。
        /// </summary>
        public static bool LooksLikeCnsConfigName(string? fileNameWithoutExtension)
            => !string.IsNullOrEmpty(fileNameWithoutExtension)
               && fileNameWithoutExtension.Contains("cns", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 把文件名切成用于匹配的关键词：小写化 → 去掉 CNS 命名装饰 → 按 <c>- _ .</c> 切分 →
        /// 丢掉长度小于 <see cref="MinimumKeywordLength"/> 的片段。
        /// </summary>
        /// <remarks>
        /// 这里的去装饰是**全串替换**而不是"去后缀"：<c>my_part</c> 会被削成 <c>myart</c>。
        /// 这是 v1.8 的原始行为，而 CNS 匹配整体是模糊打分，改动它会静默改变既有 MOD 的匹配结果，
        /// 因此原样保留并用测试钉住。
        /// </remarks>
        public static IReadOnlyList<string> ExtractKeywords(string? fileNameWithoutExtension)
        {
            if (string.IsNullOrEmpty(fileNameWithoutExtension)) return Array.Empty<string>();

            var normalized = fileNameWithoutExtension.ToLowerInvariant()
                .Replace("dekcns-", string.Empty)
                .Replace(".dekcns", string.Empty)
                .Replace("_p", string.Empty)
                .Replace("-p", string.Empty);

            return normalized
                .Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.Length >= MinimumKeywordLength)
                .ToList();
        }

        /// <summary>
        /// 两组关键词的匹配得分：完全相同 +3，互为子串 +2，逐对累加。
        /// </summary>
        public static int ScoreKeywordMatch(
            IReadOnlyList<string> configKeywords,
            IReadOnlyList<string> contentKeywords)
        {
            if (configKeywords == null || contentKeywords == null) return 0;

            var score = 0;
            foreach (var config in configKeywords)
            {
                foreach (var content in contentKeywords)
                {
                    if (config == content)
                        score += ExactKeywordScore;
                    else if (config.Contains(content, StringComparison.Ordinal)
                             || content.Contains(config, StringComparison.Ordinal))
                        score += PartialKeywordScore;
                }
            }

            return score;
        }

        private static string? FindBestContentGroup(
            IReadOnlyDictionary<string, List<string>> groups,
            string configGroupKey,
            IReadOnlyList<string> configKeywords,
            HashSet<string> processed)
        {
            string? bestKey = null;
            var bestScore = 0;

            foreach (var candidate in groups)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(candidate.Key, configGroupKey)) continue;
                if (processed.Contains(candidate.Key)) continue;

                var contentFile = candidate.Value.FirstOrDefault(f => HasExtension(f, ContentExtension));
                if (contentFile == null) continue;

                var score = ScoreKeywordMatch(
                    configKeywords,
                    ExtractKeywords(Path.GetFileNameWithoutExtension(contentFile)));

                if (score > bestScore && score >= MinimumMatchScore)
                {
                    bestScore = score;
                    bestKey = candidate.Key;
                }
            }

            return bestKey;
        }

        private static bool HasExtension(string filePath, string extension)
            => Path.GetExtension(filePath).Equals(extension, StringComparison.OrdinalIgnoreCase);
    }
}
