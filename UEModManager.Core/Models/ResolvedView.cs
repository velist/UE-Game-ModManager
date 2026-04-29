using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UEModManager.Models
{
    /// <summary>
    /// 最终视图条目来源层类型。
    /// </summary>
    public enum ResolvedEntrySource
    {
        /// <summary>宿主基础层（游戏原始文件）。</summary>
        HostBase,
        /// <summary>包文件层（MOD/插件）。</summary>
        Package,
        /// <summary>配置合并层（键级合并产物）。</summary>
        ConfigMerge,
        /// <summary>生成物层（部署快照/工具输出）。</summary>
        Generated,
        /// <summary>用户覆盖层（手动放入的修复文件）。</summary>
        UserOverride
    }

    /// <summary>
    /// 最终视图中的单个条目。
    /// 描述"最终会在游戏目录中生效的一个文件"。
    /// </summary>
    public class ResolvedEntry
    {
        /// <summary>目标相对路径（游戏目录内）。</summary>
        public string TargetRelativePath { get; init; } = "";

        /// <summary>源文件绝对路径（仓库/Overwrite 中）。</summary>
        public string SourceAbsolutePath { get; init; } = "";

        /// <summary>来源层类型。</summary>
        public ResolvedEntrySource Source { get; init; }

        /// <summary>来源包 Key（Package/ConfigMerge 层）。</summary>
        public string? PackageKey { get; init; }

        /// <summary>来源包显示名。</summary>
        public string? PackageDisplayName { get; init; }

        /// <summary>包类型。</summary>
        public PackageKind? PackageKind { get; init; }

        /// <summary>文件大小。</summary>
        public long FileSize { get; init; }

        /// <summary>文件哈希（SHA-256 前 16 字符）。</summary>
        public string? FileHash { get; init; }

        /// <summary>是否为冲突解决结果（胜者文件）。</summary>
        public bool IsConflictWinner { get; init; }

        /// <summary>被此条目覆盖的败者包 Key 列表。</summary>
        public List<string> OverriddenPackageKeys { get; init; } = [];

        /// <summary>优先级（来源排序用）。</summary>
        public int Priority { get; init; }

        /// <summary>产物类型（如果来自 PackageArtifact）。</summary>
        public ArtifactType? ArtifactType { get; init; }
    }

    /// <summary>
    /// 最终视图。
    /// 系统核心对象 — 代表"当前 Profile 在当前 Host 上最终会生效的完整文件集合"。
    /// 由 ResolvedViewBuilder 构建，可序列化、可哈希、可用于部署比较。
    /// </summary>
    public class ResolvedView
    {
        /// <summary>宿主游戏名称。</summary>
        public string HostGameName { get; init; } = "";

        /// <summary>Profile ID。</summary>
        public Guid ProfileId { get; init; }

        /// <summary>Profile 名称。</summary>
        public string ProfileName { get; init; } = "";

        /// <summary>所有生效文件条目。</summary>
        public List<ResolvedEntry> Entries { get; init; } = [];

        /// <summary>冲突记录列表。</summary>
        public List<ConflictRecord> Conflicts { get; init; } = [];

        /// <summary>配置合并结果列表。</summary>
        public List<ConfigMergeResult> ConfigMergeResults { get; init; } = [];

        /// <summary>构建时间。</summary>
        public DateTime BuiltAt { get; init; } = DateTime.Now;

        /// <summary>视图哈希（用于判断部署是否过期）。</summary>
        public string ViewHash { get; init; } = "";

        // ─── 统计属性 ───

        /// <summary>总文件数。</summary>
        public int TotalEntries => Entries.Count;

        /// <summary>包来源文件数。</summary>
        public int PackageEntries => Entries.Count(e => e.Source == ResolvedEntrySource.Package);

        /// <summary>配置合并文件数。</summary>
        public int ConfigMergeEntries => Entries.Count(e => e.Source == ResolvedEntrySource.ConfigMerge);

        /// <summary>生成物文件数。</summary>
        public int GeneratedEntries => Entries.Count(e => e.Source == ResolvedEntrySource.Generated);

        /// <summary>冲突数。</summary>
        public int ConflictCount => Conflicts.Count;

        /// <summary>总文件大小。</summary>
        public long TotalSize => Entries.Sum(e => e.FileSize);

        /// <summary>涉及的包数量。</summary>
        public int AffectedPackageCount => Entries
            .Where(e => e.PackageKey != null)
            .Select(e => e.PackageKey)
            .Distinct()
            .Count();

        /// <summary>
        /// 判断视图是否与另一视图相同（通过哈希比较）。
        /// </summary>
        public bool IsIdenticalTo(ResolvedView other) => ViewHash == other.ViewHash;

        /// <summary>
        /// 计算视图哈希。基于所有条目的路径+哈希排序后计算 SHA-256。
        /// </summary>
        public static string ComputeViewHash(List<ResolvedEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries.OrderBy(e => e.TargetRelativePath, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(entry.TargetRelativePath.ToLowerInvariant());
                sb.Append(':');
                sb.Append(entry.FileHash ?? "null");
                sb.Append(':');
                sb.Append(entry.PackageKey ?? "none");
                sb.Append('\n');
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes)[..16];
        }
    }
}
