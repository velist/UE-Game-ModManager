using System.Collections.Generic;

namespace UEModManager.Models
{
    /// <summary>
    /// 应用配置模型。持久化到 config.json。
    /// </summary>
    public class AppConfig
    {
        /// <summary>
        /// 当前选中的游戏名称。
        /// </summary>
        public string? GameName { get; set; }

        /// <summary>
        /// 游戏安装路径。
        /// </summary>
        public string? GamePath { get; set; }

        /// <summary>
        /// MOD 文件夹路径 (~mods)。
        /// </summary>
        public string? ModPath { get; set; }

        /// <summary>
        /// 备份目录路径。
        /// </summary>
        public string? BackupPath { get; set; }

        /// <summary>
        /// 游戏可执行文件名。
        /// </summary>
        public string? ExecutableName { get; set; }

        /// <summary>
        /// 用户自定义添加的游戏名称列表。
        /// </summary>
        public List<string> CustomGames { get; set; } = new();

        /// <summary>
        /// 游戏图标路径映射（游戏名称 → 图标文件路径）。
        /// </summary>
        public Dictionary<string, string> GameIcons { get; set; } = new();

        /// <summary>
        /// 游戏引擎类型映射（游戏名称 → 引擎类型字符串）。
        /// 旧配置无此字段时默认空字典，向后兼容。
        /// </summary>
        public Dictionary<string, string> GameEngines { get; set; } = new();

        /// <summary>
        /// 每个游戏的默认插件路径映射（游戏名称 → 路径）。
        /// </summary>
        public Dictionary<string, string> PluginPaths { get; set; } = new();
    }
}
