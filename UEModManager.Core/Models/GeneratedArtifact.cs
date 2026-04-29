using System;

namespace UEModManager.Models
{
    /// <summary>
    /// 运行时生成物记录。追踪部署过程或工具产生的非原始输入文件。
    /// </summary>
    public class GeneratedArtifact
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>生成物在 Overwrite 目录中的相对路径。</summary>
        public string RelativePath { get; init; } = "";

        /// <summary>部署到游戏目录时的相对目标路径。</summary>
        public string? RelativeTargetPath { get; init; }

        /// <summary>显示名称。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>生成物类型。</summary>
        public GeneratedArtifactType Type { get; init; } = GeneratedArtifactType.Other;

        /// <summary>生成物状态。</summary>
        public GeneratedArtifactStatus Status { get; set; } = GeneratedArtifactStatus.Active;

        /// <summary>来源包的 PackageKey（如果有）。</summary>
        public string? SourcePackageKey { get; init; }

        /// <summary>来源 Profile ID（如果有）。</summary>
        public Guid? SourceProfileId { get; init; }

        /// <summary>来源部署事务 ID（如果有）。</summary>
        public Guid? SourceTransactionId { get; init; }

        /// <summary>来源描述（人类可读）。</summary>
        public string? SourceDescription { get; init; }

        /// <summary>文件大小（字节）。</summary>
        public long FileSize { get; init; }

        /// <summary>文件哈希（SHA-256 前 16 字符）。</summary>
        public string? FileHash { get; init; }

        /// <summary>创建时间。</summary>
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>最后修改时间。</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>所属游戏名称。</summary>
        public string HostGameName { get; init; } = "";

        /// <summary>格式化文件大小。</summary>
        public string FormattedSize => FormatFileSize(FileSize);

        /// <summary>来源摘要文本（UI 用）。</summary>
        public string SourceSummary =>
            SourceDescription
            ?? (SourcePackageKey != null ? $"来自 {SourcePackageKey}" : "手动添加");

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
