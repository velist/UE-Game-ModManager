namespace UEModManager.Models
{
    /// <summary>
    /// 游戏配置档案。
    /// </summary>
    public class GameProfile
    {
        /// <summary>
        /// 游戏名称。
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 游戏安装路径。
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 游戏可执行文件名。
        /// </summary>
        public string ExecutableName { get; set; } = string.Empty;

        /// <summary>
        /// MOD 文件夹路径。
        /// </summary>
        public string ModPath { get; set; } = string.Empty;

        /// <summary>
        /// 备份目录路径。
        /// </summary>
        public string BackupPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// 游戏类型枚举。
    /// </summary>
    public enum GameType
    {
        Other,
        StellarBlade,
        StellarBladeCNS,
        Expedition33,
        BlackMythWukong,
        WuchangFallenFeathers,
        Borderlands4,
        SlayTheSpire2,
        DeathStranding2
    }
}
