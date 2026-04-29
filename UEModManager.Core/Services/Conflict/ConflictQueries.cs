using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Conflict
{
    /// <summary>
    /// 冲突列表的纯查询函数。
    ///
    /// 主项目 <c>ConflictAnalyzer</c> 暴露的实例查询（GetConflictsForPackage / GetLossCount /
    /// GetWinCount）的实现已下沉到此处，让查询逻辑可独立单测。
    /// 主项目实例方法保留为薄包装以维持公开 API 兼容。
    /// </summary>
    public static class ConflictQueries
    {
        /// <summary>获取指定包涉及的所有冲突（作为胜者或败者）。</summary>
        public static List<ConflictRecord> GetConflictsForPackage(
            IReadOnlyList<ConflictRecord> conflicts,
            string packageKey)
        {
            if (conflicts == null) throw new System.ArgumentNullException(nameof(conflicts));
            if (packageKey == null) throw new System.ArgumentNullException(nameof(packageKey));

            return conflicts
                .Where(c => c.WinnerPackageKey == packageKey
                            || c.Losers.Any(l => l.PackageKey == packageKey))
                .ToList();
        }

        /// <summary>获取指定包作为败者的冲突数。</summary>
        public static int GetLossCount(
            IReadOnlyList<ConflictRecord> conflicts,
            string packageKey)
        {
            if (conflicts == null) throw new System.ArgumentNullException(nameof(conflicts));
            if (packageKey == null) throw new System.ArgumentNullException(nameof(packageKey));

            return conflicts.Count(c =>
                c.Losers.Any(l => l.PackageKey == packageKey));
        }

        /// <summary>获取指定包作为胜者的冲突数。</summary>
        public static int GetWinCount(
            IReadOnlyList<ConflictRecord> conflicts,
            string packageKey)
        {
            if (conflicts == null) throw new System.ArgumentNullException(nameof(conflicts));
            if (packageKey == null) throw new System.ArgumentNullException(nameof(packageKey));

            return conflicts.Count(c => c.WinnerPackageKey == packageKey);
        }
    }
}
