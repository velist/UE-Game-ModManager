using System;

namespace UEModManager.Services.Detection
{
    /// <summary>
    /// 游戏名规范化（纯函数）。
    ///
    /// 主项目原本散落在 <c>GameConfigService.NormalizeGameName</c> 的"全角→半角 + 去除分隔符"规则
    /// 下沉到 Core，让规范化规则可独立单测，并供其他场景复用（如 lockfile 比对、UI 输入清洗）。
    /// </summary>
    public static class GameNameNormalizer
    {
        /// <summary>
        /// 规范化游戏名：去首尾空白；全角括号/冒号→半角；去掉间隔符 ·。
        /// null / 空白 → 返回空字符串。
        /// </summary>
        public static string Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            return name.Trim()
                .Replace("（", "(")
                .Replace("）", ")")
                .Replace("：", ":")
                .Replace("·", string.Empty);
        }
    }
}
