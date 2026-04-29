using System;
using System.Collections.Generic;

namespace UEModManager.Models
{
    /// <summary>
    /// 包清单。
    /// 记录一个包的完整元数据和文件列表，用于仓库管理和数据恢复。
    /// 存储在仓库目录的 manifest.json 中。
    /// </summary>
    public class PackageManifest
    {
        /// <summary>清单格式版本。</summary>
        public int ManifestVersion { get; init; } = 1;

        /// <summary>包 ID。</summary>
        public Guid PackageId { get; init; }

        /// <summary>包标识符。</summary>
        public string PackageKey { get; init; } = default!;

        /// <summary>显示名称。</summary>
        public string DisplayName { get; set; } = default!;

        /// <summary>包类型。</summary>
        public PackageKind Kind { get; init; } = PackageKind.Mod;

        /// <summary>版本号。</summary>
        public string Version { get; init; } = "1.0.0";

        /// <summary>标签列表。</summary>
        public List<string> Tags { get; set; } = [];

        /// <summary>用户备注。</summary>
        public string? Note { get; set; }

        /// <summary>预览图文件名（相对于包目录）。</summary>
        public string? PreviewFileName { get; set; }

        /// <summary>内容哈希。</summary>
        public string? ContentHash { get; set; }

        /// <summary>文件总大小（字节）。</summary>
        public long TotalSize { get; init; }

        /// <summary>产物列表。</summary>
        public List<ManifestArtifactEntry> Artifacts { get; init; } = [];

        /// <summary>关联的游戏名称。</summary>
        public string HostGameName { get; init; } = default!;

        /// <summary>插件目标路径（Plugin 类型）。</summary>
        public string? PluginTargetPath { get; set; }

        /// <summary>导入来源路径。</summary>
        public string? ImportSourcePath { get; set; }

        /// <summary>导入时间。</summary>
        public DateTime ImportedAt { get; init; } = DateTime.Now;

        /// <summary>最后修改时间。</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// 从 Package 对象创建 Manifest。
        /// </summary>
        public static PackageManifest FromPackage(Package package)
        {
            var manifest = new PackageManifest
            {
                PackageId = package.Id,
                PackageKey = package.PackageKey,
                DisplayName = package.DisplayName,
                Kind = package.Kind,
                Tags = new List<string>(package.Tags),
                Note = package.Note,
                ContentHash = package.ContentHash,
                TotalSize = package.TotalSize,
                HostGameName = package.HostGameName,
                PluginTargetPath = package.PluginTargetPath,
                ImportSourcePath = package.ImportSourcePath,
                ImportedAt = package.ImportedAt,
                LastModified = package.LastModified,
            };

            foreach (var artifact in package.Artifacts)
            {
                manifest.Artifacts.Add(new ManifestArtifactEntry
                {
                    FileName = artifact.FileName,
                    RelativeSourcePath = artifact.RelativeSourcePath,
                    RelativeTargetPath = artifact.RelativeTargetPath,
                    FileSize = artifact.FileSize,
                    FileHash = artifact.FileHash,
                    ArtifactType = artifact.ArtifactType,
                });
            }

            return manifest;
        }

        /// <summary>
        /// 从 Manifest 恢复 Package 对象。
        /// </summary>
        public Package ToPackage()
        {
            var package = new Package
            {
                Id = PackageId,
                PackageKey = PackageKey,
                DisplayName = DisplayName,
                Kind = Kind,
                Version = Version,
                Tags = new List<string>(Tags),
                Note = Note,
                ContentHash = ContentHash,
                TotalSize = TotalSize,
                HostGameName = HostGameName,
                PluginTargetPath = PluginTargetPath,
                ImportSourcePath = ImportSourcePath,
                ImportedAt = ImportedAt,
                LastModified = LastModified,
            };

            foreach (var entry in Artifacts)
            {
                package.Artifacts.Add(new PackageArtifact
                {
                    PackageId = PackageId,
                    FileName = entry.FileName,
                    RelativeSourcePath = entry.RelativeSourcePath,
                    RelativeTargetPath = entry.RelativeTargetPath,
                    FileSize = entry.FileSize,
                    FileHash = entry.FileHash,
                    ArtifactType = entry.ArtifactType,
                });
            }

            // 预览图路径需要在仓库加载时重新设置
            if (!string.IsNullOrEmpty(PreviewFileName))
                package.PreviewImagePath = PreviewFileName; // 调用方负责拼接完整路径

            return package;
        }
    }

    /// <summary>
    /// Manifest 中的产物条目。
    /// </summary>
    public class ManifestArtifactEntry
    {
        /// <summary>文件名。</summary>
        public string FileName { get; init; } = default!;

        /// <summary>仓库内相对路径。</summary>
        public string RelativeSourcePath { get; init; } = default!;

        /// <summary>部署目标相对路径。</summary>
        public string RelativeTargetPath { get; init; } = default!;

        /// <summary>文件大小。</summary>
        public long FileSize { get; init; }

        /// <summary>文件哈希。</summary>
        public string? FileHash { get; init; }

        /// <summary>产物类型。</summary>
        public ArtifactType ArtifactType { get; init; }
    }
}
