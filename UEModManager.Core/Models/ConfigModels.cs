using System;
using System.Collections.Generic;

namespace UEModManager.Models
{
    /// <summary>配置文件格式。</summary>
    public enum ConfigFormat
    {
        /// <summary>INI 格式（Section/Key=Value）。</summary>
        Ini,
        /// <summary>JSON 格式。</summary>
        Json,
        /// <summary>YAML 格式。</summary>
        Yaml,
        /// <summary>TOML 格式。</summary>
        Toml,
        /// <summary>CFG 格式（Key=Value，无 Section）。</summary>
        Cfg,
        /// <summary>未知格式（整文件替换）。</summary>
        Unknown
    }

    /// <summary>配置合并策略。</summary>
    public enum ConfigMergeStrategy
    {
        /// <summary>整文件替换（默认，最简单）。</summary>
        ReplaceFile,
        /// <summary>按 Section/Key 合并（INI 专用）。</summary>
        MergeByKey,
        /// <summary>追加到末尾（不去重）。</summary>
        Append,
        /// <summary>Patch 模式（仅修改指定键，其余保留）。</summary>
        Patch
    }

    /// <summary>
    /// 配置键值对。表示配置文件中的一个键值条目。
    /// </summary>
    public class ConfigEntry
    {
        /// <summary>Section 名称（INI 专用，JSON 用路径表示，如 "Graphics.Resolution"）。</summary>
        public string Section { get; init; } = "";

        /// <summary>键名。</summary>
        public string Key { get; init; } = "";

        /// <summary>值。</summary>
        public string Value { get; set; } = "";

        /// <summary>注释（如果有）。</summary>
        public string? Comment { get; init; }

        /// <summary>完整键路径（Section.Key 或纯 Key）。</summary>
        public string FullKey => string.IsNullOrEmpty(Section) ? Key : $"{Section}.{Key}";
    }

    /// <summary>
    /// 配置补丁操作。描述对配置文件的单个修改。
    /// </summary>
    public class ConfigPatchOperation
    {
        /// <summary>操作类型。</summary>
        public ConfigPatchType Type { get; init; }

        /// <summary>目标 Section（可为空）。</summary>
        public string Section { get; init; } = "";

        /// <summary>目标键名。</summary>
        public string Key { get; init; } = "";

        /// <summary>新值（Add/Update 时使用）。</summary>
        public string? NewValue { get; init; }

        /// <summary>旧值（用于追踪和冲突检测）。</summary>
        public string? OldValue { get; init; }

        /// <summary>来源包 Key。</summary>
        public string SourcePackageKey { get; init; } = "";

        /// <summary>来源包显示名。</summary>
        public string SourceDisplayName { get; init; } = "";

        /// <summary>优先级（数字越小越优先）。</summary>
        public int Priority { get; init; }
    }

    /// <summary>配置补丁操作类型。</summary>
    public enum ConfigPatchType
    {
        /// <summary>新增键。</summary>
        Add,
        /// <summary>修改已有键。</summary>
        Update,
        /// <summary>删除键。</summary>
        Remove
    }

    /// <summary>
    /// 配置合并计划。描述对一个配置文件的完整合并方案。
    /// </summary>
    public class ConfigMergePlan
    {
        /// <summary>目标配置文件的相对路径。</summary>
        public string TargetRelativePath { get; init; } = "";

        /// <summary>配置格式。</summary>
        public ConfigFormat Format { get; init; }

        /// <summary>合并策略。</summary>
        public ConfigMergeStrategy Strategy { get; init; }

        /// <summary>基础配置内容（游戏原始文件或空）。</summary>
        public string? BaseContent { get; init; }

        /// <summary>按优先级排列的补丁操作列表。</summary>
        public List<ConfigPatchOperation> Patches { get; init; } = [];

        /// <summary>参与合并的包列表（按优先级排列）。</summary>
        public List<ConfigMergeSource> Sources { get; init; } = [];

        /// <summary>合并后是否产生冲突。</summary>
        public bool HasConflicts => Conflicts.Count > 0;

        /// <summary>键级冲突列表。</summary>
        public List<ConfigKeyConflict> Conflicts { get; init; } = [];
    }

    /// <summary>
    /// 配置合并来源。一个参与合并的包。
    /// </summary>
    public class ConfigMergeSource
    {
        /// <summary>包 Key。</summary>
        public string PackageKey { get; init; } = "";

        /// <summary>包显示名。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>该包提供的配置文件路径（仓库内）。</summary>
        public string SourceFilePath { get; init; } = "";

        /// <summary>优先级。</summary>
        public int Priority { get; init; }

        /// <summary>合并策略（该包指定或继承默认）。</summary>
        public ConfigMergeStrategy Strategy { get; init; } = ConfigMergeStrategy.MergeByKey;
    }

    /// <summary>
    /// 配置键冲突。多个包修改同一键时产生。
    /// </summary>
    public class ConfigKeyConflict
    {
        /// <summary>冲突所在 Section。</summary>
        public string Section { get; init; } = "";

        /// <summary>冲突键名。</summary>
        public string Key { get; init; } = "";

        /// <summary>完整键路径。</summary>
        public string FullKey => string.IsNullOrEmpty(Section) ? Key : $"{Section}.{Key}";

        /// <summary>胜者包 Key。</summary>
        public string WinnerPackageKey { get; set; } = "";

        /// <summary>胜者值。</summary>
        public string WinnerValue { get; set; } = "";

        /// <summary>败者列表（包Key → 值）。</summary>
        public List<ConfigKeyConflictLoser> Losers { get; init; } = [];
    }

    /// <summary>配置键冲突败者。</summary>
    public class ConfigKeyConflictLoser
    {
        public string PackageKey { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Value { get; init; } = "";
        public int Priority { get; init; }
    }

    /// <summary>
    /// 配置合并结果。
    /// </summary>
    public class ConfigMergeResult
    {
        /// <summary>是否成功。</summary>
        public bool Success { get; init; }

        /// <summary>合并后的完整内容。</summary>
        public string MergedContent { get; init; } = "";

        /// <summary>目标文件相对路径。</summary>
        public string TargetRelativePath { get; init; } = "";

        /// <summary>每个键的来源追踪。</summary>
        public List<ConfigEntrySource> EntrySourceMap { get; init; } = [];

        /// <summary>键级冲突列表。</summary>
        public List<ConfigKeyConflict> Conflicts { get; init; } = [];

        /// <summary>错误消息（失败时）。</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// 配置条目来源追踪。记录最终结果中每个键来自哪个包。
    /// </summary>
    public class ConfigEntrySource
    {
        /// <summary>Section。</summary>
        public string Section { get; init; } = "";

        /// <summary>键名。</summary>
        public string Key { get; init; } = "";

        /// <summary>最终值。</summary>
        public string Value { get; init; } = "";

        /// <summary>来源包 Key。</summary>
        public string SourcePackageKey { get; init; } = "";

        /// <summary>来源包显示名。</summary>
        public string SourceDisplayName { get; init; } = "";

        /// <summary>是否为冲突解决结果。</summary>
        public bool IsConflictResolution { get; init; }
    }
}
