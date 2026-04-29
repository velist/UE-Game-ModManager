using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// v2.0 冲突记录。
    /// 记录一个目标路径上的冲突：谁赢了、谁输了、为什么。
    /// 取代旧版 ConflictGroup 的简单列表模型。
    /// </summary>
    public class ConflictRecord
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>
        /// 冲突目标路径。
        /// 文件路径冲突：部署目标的相对路径。
        /// 资产冲突：UE 资产路径（如 /Game/Characters/Hero/Body）。
        /// 配置键冲突：配置文件路径 + 键名。
        /// </summary>
        public string TargetPath { get; init; } = default!;

        /// <summary>冲突类型。</summary>
        public ConflictType Type { get; init; }

        /// <summary>胜者包标识。</summary>
        public string WinnerPackageKey { get; init; } = default!;

        /// <summary>胜者包显示名称。</summary>
        public string WinnerDisplayName { get; init; } = default!;

        /// <summary>败者包列表（按优先级从高到低排列）。</summary>
        public List<ConflictLoser> Losers { get; init; } = [];

        /// <summary>
        /// 解决原因。
        /// 说明为什么这个包赢了。
        /// </summary>
        public string Reason { get; init; } = default!;

        /// <summary>
        /// 解决方式。
        /// </summary>
        public ResolutionMethod Resolution { get; init; }

        /// <summary>
        /// 是否为用户手动覆盖的结果。
        /// </summary>
        public bool IsUserOverride { get; set; }

        /// <summary>冲突严重程度。</summary>
        public ConflictSeverity Severity { get; init; } = ConflictSeverity.Warning;

        /// <summary>关联的游戏名称。</summary>
        public string HostGameName { get; init; } = default!;

        /// <summary>关联的 Profile ID。</summary>
        public Guid ProfileId { get; init; }

        /// <summary>检测时间。</summary>
        public DateTime DetectedAt { get; init; } = DateTime.Now;

        // ─── 计算属性 ───

        /// <summary>涉及的包总数（胜者 + 败者）。</summary>
        [JsonIgnore]
        public int InvolvedPackageCount => 1 + Losers.Count;
    }

    /// <summary>
    /// 冲突中的败者信息。
    /// </summary>
    public class ConflictLoser
    {
        /// <summary>败者包标识。</summary>
        public string PackageKey { get; init; } = default!;

        /// <summary>败者包显示名称。</summary>
        public string DisplayName { get; init; } = default!;

        /// <summary>该败者的优先级（数字越小越优先）。</summary>
        public int Priority { get; init; }
    }

    /// <summary>
    /// 冲突解决方式。
    /// </summary>
    public enum ResolutionMethod
    {
        /// <summary>按优先级自动解决（默认）。</summary>
        Priority,

        /// <summary>按加载顺序解决（后加载的覆盖先加载的）。</summary>
        LoadOrder,

        /// <summary>用户手动指定胜者。</summary>
        UserOverride
    }

    /// <summary>
    /// 冲突严重程度。
    /// </summary>
    public enum ConflictSeverity
    {
        /// <summary>信息性（不影响运行）。</summary>
        Info,

        /// <summary>警告（可能影响运行）。</summary>
        Warning,

        /// <summary>错误（会导致问题）。</summary>
        Error
    }
}
