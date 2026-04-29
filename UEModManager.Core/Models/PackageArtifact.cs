using System;

namespace UEModManager.Models
{
    /// <summary>
    /// 包内的单个文件产物。
    /// 记录包中每个文件的来源路径、目标路径、类型和部署策略。
    /// </summary>
    public class PackageArtifact
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>所属包 ID。</summary>
        public Guid PackageId { get; init; }

        /// <summary>
        /// 在仓库中的相对路径（相对于包仓库根目录）。
        /// 例："{packageKey}/mod_p.pak"
        /// </summary>
        public string RelativeSourcePath { get; init; } = default!;

        /// <summary>
        /// 部署到宿主时的相对目标路径（相对于游戏 MOD 目录或插件目录）。
        /// 例："mod_p.pak" 或 "subfolder/config.ini"
        /// </summary>
        public string RelativeTargetPath { get; init; } = default!;

        /// <summary>文件名。</summary>
        public string FileName { get; init; } = default!;

        /// <summary>文件大小（字节）。</summary>
        public long FileSize { get; init; }

        /// <summary>
        /// 文件内容哈希（SHA-256 前 16 字符）。
        /// 用于去重和完整性校验。
        /// </summary>
        public string? FileHash { get; init; }

        /// <summary>产物类型。</summary>
        public ArtifactType ArtifactType { get; init; } = ArtifactType.ModFile;
    }

    /// <summary>
    /// 产物类型枚举。
    /// </summary>
    public enum ArtifactType
    {
        /// <summary>MOD 文件（.pak/.ucas/.utoc 等）。</summary>
        ModFile,

        /// <summary>插件文件（.dll/.exe 等）。</summary>
        PluginFile,

        /// <summary>配置文件（.ini/.json/.cfg 等）。</summary>
        ConfigFile,

        /// <summary>预览图。</summary>
        PreviewImage,

        /// <summary>其他文件。</summary>
        Other
    }
}
