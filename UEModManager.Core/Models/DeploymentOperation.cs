using System;

namespace UEModManager.Models
{
    /// <summary>
    /// 单个部署操作。
    /// 描述一个文件从仓库到游戏目录的部署动作。
    /// </summary>
    public class DeploymentOperation
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>操作类型。</summary>
        public DeploymentOperationType Type { get; init; }

        /// <summary>所属包标识。</summary>
        public string PackageKey { get; init; } = default!;

        /// <summary>包显示名称（用于 UI 展示）。</summary>
        public string PackageDisplayName { get; init; } = default!;

        /// <summary>
        /// 源文件绝对路径（仓库中的文件）。
        /// Add/Replace 操作必填，Remove 操作为 null。
        /// </summary>
        public string? SourcePath { get; init; }

        /// <summary>
        /// 目标文件绝对路径（游戏目录中的文件）。
        /// </summary>
        public string TargetPath { get; init; } = default!;

        /// <summary>
        /// 目标文件的相对路径（相对于游戏 MOD/插件目录）。
        /// 用于 UI 展示。
        /// </summary>
        public string RelativeTargetPath { get; init; } = default!;

        /// <summary>源文件哈希（用于完整性校验）。</summary>
        public string? FileHash { get; init; }

        /// <summary>文件大小（字节）。</summary>
        public long FileSize { get; init; }

        /// <summary>包类型（MOD/Plugin/Config）。</summary>
        public PackageKind PackageKind { get; init; }

        /// <summary>操作是否已执行。</summary>
        public bool IsExecuted { get; set; }

        /// <summary>
        /// 备份路径（执行前原文件的备份位置）。
        /// Remove/Replace 操作时由 DeploymentService 填写。
        /// </summary>
        public string? BackupPath { get; set; }
    }
}
