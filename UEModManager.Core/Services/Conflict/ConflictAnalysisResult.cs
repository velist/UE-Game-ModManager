using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Conflict
{
    /// <summary>
    /// 冲突分析结果（Core 数据模型）。
    ///
    /// 由主项目 <c>ConflictAnalyzer.AnalyzeProfileAsync</c> 调用 <see cref="ConflictDetector"/>
    /// 后聚合产出，含统计计算属性。本身无 IO 依赖，可独立单测。
    /// </summary>
    public class ConflictAnalysisResult
    {
        /// <summary>关联的 Profile ID。</summary>
        public Guid ProfileId { get; init; }

        /// <summary>关联的游戏名称。</summary>
        public string HostGameName { get; init; } = default!;

        /// <summary>冲突记录列表。</summary>
        public List<ConflictRecord> Conflicts { get; init; } = [];

        /// <summary>扫描的包数量。</summary>
        public int ScannedPackages { get; init; }

        /// <summary>总文件数量。</summary>
        public int TotalArtifacts { get; init; }

        /// <summary>分析时间。</summary>
        public DateTime AnalyzedAt { get; init; } = DateTime.Now;

        // ─── 计算属性 ───

        /// <summary>总冲突数。</summary>
        public int TotalConflicts => Conflicts.Count;

        /// <summary>是否有冲突。</summary>
        public bool HasConflicts => Conflicts.Count > 0;

        /// <summary>按类型分组的冲突数。</summary>
        public Dictionary<ConflictType, int> ConflictsByType =>
            Conflicts.GroupBy(c => c.Type)
                .ToDictionary(g => g.Key, g => g.Count());

        /// <summary>按严重程度分组的冲突数。</summary>
        public Dictionary<ConflictSeverity, int> ConflictsBySeverity =>
            Conflicts.GroupBy(c => c.Severity)
                .ToDictionary(g => g.Key, g => g.Count());

        /// <summary>用户覆盖的冲突数。</summary>
        public int UserOverrideCount => Conflicts.Count(c => c.IsUserOverride);

        /// <summary>涉及冲突的包列表（去重）。</summary>
        public List<string> AffectedPackages =>
            Conflicts.SelectMany(c =>
                new[] { c.WinnerPackageKey }
                    .Concat(c.Losers.Select(l => l.PackageKey)))
                .Distinct()
                .ToList();
    }
}
