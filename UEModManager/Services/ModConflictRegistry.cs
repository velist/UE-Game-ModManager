using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services
{
    // 冲突计数全局注册表（RealName -> 冲突数）
    public static class ModConflictRegistry
    {
        private static readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

        public static void Clear() => _counts.Clear();

        public static void SetCounts(IEnumerable<ModConflictSummary> summaries)
        {
            if (summaries == null) return;
            Clear();
            foreach (var s in summaries)
            {
                // 优先使用 OriginalName 作为key（真实目录名），以便与主界面卡片匹配；
                // 若为空则回退到 ModName。
                var key = !string.IsNullOrEmpty(s.OriginalName) ? s.OriginalName : s.ModName;
                if (!string.IsNullOrEmpty(key))
                {
                    _counts[key] = s.ConflictCount;
                }
            }
        }

        public static int Lookup(string? realName)
        {
            if (string.IsNullOrEmpty(realName)) return 0;
            return _counts.TryGetValue(realName, out var v) ? v : 0;
        }
    }
}
