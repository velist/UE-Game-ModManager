using System;
using System.Collections.Generic;

namespace UEModManager.Services.Detection
{
    /// <summary>
    /// 按 MOD 名称推测分类标签（纯函数）。
    ///
    /// 主项目原本散落在 <c>ModManagementService.DetermineModType</c> 的中英文关键词分类规则，
    /// 下沉到 Core 让关键词表集中可见、可独立单测，并供 PackageImportService 等其他场景复用。
    ///
    /// 优先级：面部 &gt; 人物 &gt; 武器 &gt; 服装 &gt; 发型 &gt; 其他（兜底）。
    /// 早出现的关键词不会被后面覆盖，保留原 v1.8 行为。
    /// </summary>
    public static class ModCategoryClassifier
    {
        /// <summary>未匹配任何关键词时返回的默认分类。</summary>
        public const string DefaultCategory = "其他";

        private static readonly (string Category, string[] Keywords)[] _rules =
        [
            ("面部", ["face", "facial", "脸", "面部"]),
            ("人物", ["character", "body", "skin", "人物", "角色", "身体"]),
            ("武器", ["weapon", "sword", "gun", "武器", "剑", "刀"]),
            ("服装", ["outfit", "cloth", "suit", "服装", "衣服", "套装"]),
            ("发型", ["hair", "头发", "发型"]),
        ];

        /// <summary>所有可识别的分类标签（按优先级顺序）。</summary>
        public static IReadOnlyList<string> KnownCategories
        {
            get
            {
                var list = new List<string>(_rules.Length);
                foreach (var (cat, _) in _rules) list.Add(cat);
                return list;
            }
        }

        /// <summary>
        /// 按名称推测分类。匹配规则：小写化后命中关键词表中第一个组的任意 keyword 即返回该组分类。
        /// null / 空字符串 / 仅空白 → 返回 <see cref="DefaultCategory"/>。
        /// </summary>
        public static string Classify(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return DefaultCategory;
            var lower = name.ToLowerInvariant();

            foreach (var (category, keywords) in _rules)
                foreach (var kw in keywords)
                    if (lower.Contains(kw, StringComparison.Ordinal))
                        return category;

            return DefaultCategory;
        }
    }
}
