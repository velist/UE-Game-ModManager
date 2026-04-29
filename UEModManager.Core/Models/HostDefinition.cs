using System;

namespace UEModManager.Models
{
    /// <summary>
    /// 宿主定义。从 GameProfile 演进而来的统一宿主模型。
    /// 每个 HostDefinition 对应一个游戏/宿主实例，含引擎类型和适配器标识。
    /// </summary>
    public class HostDefinition
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>宿主键名（唯一，如 "stellar-blade"）。</summary>
        public string Key { get; init; } = "";

        /// <summary>显示名称（如 "剑星"）。</summary>
        public string Name { get; init; } = "";

        /// <summary>引擎类型。</summary>
        public EngineType Engine { get; init; } = EngineType.Unknown;

        /// <summary>游戏安装根目录。</summary>
        public string RootPath { get; set; } = "";

        /// <summary>可执行文件路径（相对或绝对）。</summary>
        public string ExecutablePath { get; set; } = "";

        /// <summary>适配器键名（对应 IHostAdapter.AdapterKey）。</summary>
        public string AdapterKey { get; init; } = "generic-overlay";

        /// <summary>MOD 目录路径（绝对或相对于 RootPath）。</summary>
        public string ModPath { get; set; } = "";

        /// <summary>备份目录路径。</summary>
        public string BackupPath { get; set; } = "";

        /// <summary>旧版游戏名称（兼容 GameType/GameProfile）。</summary>
        public string? LegacyGameName { get; init; }
    }
}
