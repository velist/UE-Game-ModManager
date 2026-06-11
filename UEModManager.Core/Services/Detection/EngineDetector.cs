using System;
using UEModManager.Models;

namespace UEModManager.Services.Detection
{
    /// <summary>
    /// 引擎特征探测（纯函数 + 探测委托注入）。
    ///
    /// 主项目 <c>GameConfigService.AutoDetectEngine</c> 把"目录/文件命中规则"和"Directory.Exists / GetFiles"
    /// 写在一起，无法独立单测。本类只接受 3 个布尔探测器委托，决策树完全脱离 IO；
    /// 主项目按当前 gamePath 提供 lambda，Core 测试用闭包模拟命中结果。
    /// </summary>
    public static class EngineDetector
    {
        /// <summary>
        /// 按特征命中顺序判定引擎类型。优先级：UE &gt; Unity &gt; REEngine &gt; Godot &gt; Decima &gt; Unknown。
        /// </summary>
        /// <param name="directoryExists">
        /// 给定相对路径（如 "Content/Paks"），返回该子目录是否存在。
        /// </param>
        /// <param name="hasFileMatching">
        /// 给定文件 glob（如 "*.uproject"），返回顶级目录是否有匹配文件。
        /// </param>
        /// <param name="hasDirectoryMatching">
        /// 给定目录 glob（如 "*_Data"），返回顶级目录是否有匹配子目录。
        /// </param>
        public static EngineType Detect(
            Func<string, bool> directoryExists,
            Func<string, bool> hasFileMatching,
            Func<string, bool> hasDirectoryMatching)
        {
            if (directoryExists == null) throw new ArgumentNullException(nameof(directoryExists));
            if (hasFileMatching == null) throw new ArgumentNullException(nameof(hasFileMatching));
            if (hasDirectoryMatching == null) throw new ArgumentNullException(nameof(hasDirectoryMatching));

            // UE：Content/Paks 或 Engine 子目录 或 *.uproject 文件
            if (directoryExists("Content/Paks")
                || directoryExists("Engine")
                || hasFileMatching("*.uproject"))
                return EngineType.UnrealEngine;

            // Unity：UnityPlayer.dll 文件 或 *_Data 子目录
            if (hasFileMatching("UnityPlayer.dll") || hasDirectoryMatching("*_Data"))
                return EngineType.Unity;

            // RE Engine：natives 目录 或 re_chunk_*.pak 文件
            if (directoryExists("natives") || hasFileMatching("re_chunk_*.pak"))
                return EngineType.REEngine;

            // Godot：*.pck 文件
            if (hasFileMatching("*.pck"))
                return EngineType.Godot;

            // Decima：*.core 文件、initial.dat 文件、Prefetch 目录
            if (hasFileMatching("*.core")
                || hasFileMatching("initial.dat")
                || directoryExists("Prefetch"))
                return EngineType.Decima;

            // Diablo 4（暴雪自研）：*.mpq 包、Diablo IV.exe、Config.wtf 反和谐配置、WTF 启动配置目录
            if (hasFileMatching("*.mpq")
                || hasFileMatching("Diablo IV.exe")
                || hasFileMatching("Config.wtf")
                || directoryExists("WTF"))
                return EngineType.Diablo4Engine;

            return EngineType.Unknown;
        }
    }
}
