using System.Collections.Generic;

namespace UEModManager.Models
{
    /// <summary>
    /// MOD 冲突检测结果。
    /// </summary>
    public class ConflictGroup
    {
        /// <summary>
        /// 冲突的文件路径。
        /// </summary>
        public string ConflictFile { get; set; } = string.Empty;

        /// <summary>
        /// 涉及冲突的 MOD 列表。
        /// </summary>
        public List<ConflictMod> Mods { get; set; } = new();
    }

    /// <summary>
    /// 冲突中的单个 MOD 信息。
    /// </summary>
    public class ConflictMod
    {
        /// <summary>
        /// MOD 名称。
        /// </summary>
        public string ModName { get; set; } = string.Empty;

        /// <summary>
        /// 该 MOD 是否已启用。
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// 用户是否选择保留此 MOD。
        /// </summary>
        public bool IsSelected { get; set; }
    }
}
