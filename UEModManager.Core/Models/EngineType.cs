namespace UEModManager.Models
{
    /// <summary>
    /// 游戏引擎类型枚举。
    /// </summary>
    public enum EngineType
    {
        UnrealEngine,
        Unity,
        REEngine,
        Godot,
        Decima,
        /// <summary>
        /// 暴雪自研引擎（暗黑破坏神 4 等）。
        /// 民间俗称"暗黑 4 引擎"，特征：MPQ 打包 + JSON/XML 数据 + .stl/.meta/.d4a 等专属二进制 + Config.wtf 启动配置。
        /// </summary>
        Diablo4Engine,
        Unknown
    }
}
