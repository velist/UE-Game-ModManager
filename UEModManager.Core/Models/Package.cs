using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// 统一包模型。
    /// 从 ModInfo 演进而来，是 v2.0 的核心领域对象。
    /// 一个 Package 代表用户导入的一个 MOD/插件/配置文件集合。
    /// </summary>
    public class Package : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;
        private string? _note;
        private string? _previewImagePath;
        private List<string> _tags = new() { "未分类" };

        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>
        /// 包标识符（= 文件夹名 / RealName）。
        /// 在一个 Host 内唯一，用于所有文件操作和 Profile 引用。
        /// </summary>
        public string PackageKey { get; init; } = default!;

        /// <summary>用户可修改的显示名称。</summary>
        public string DisplayName
        {
            get => _displayName;
            set => SetField(ref _displayName, value);
        }

        /// <summary>版本号。</summary>
        public string Version { get; init; } = "1.0.0";

        /// <summary>用户备注。</summary>
        public string? Note
        {
            get => _note;
            set => SetField(ref _note, value);
        }

        /// <summary>预览图路径。</summary>
        public string? PreviewImagePath
        {
            get => _previewImagePath;
            set => SetField(ref _previewImagePath, value);
        }

        /// <summary>包类型（MOD/Plugin/Config）。</summary>
        public PackageKind Kind { get; init; } = PackageKind.Mod;

        /// <summary>
        /// 标签/分类列表。
        /// 从 ModInfo.Categories 演进，支持多标签。
        /// </summary>
        public List<string> Tags
        {
            get => _tags;
            set
            {
                _tags = value ?? new List<string> { "未分类" };
                OnPropertyChanged(nameof(Tags));
                OnPropertyChanged(nameof(PrimaryTag));
            }
        }

        /// <summary>主标签（Tags 的第一个元素）。</summary>
        [JsonIgnore]
        public string PrimaryTag => _tags.Count > 0 ? _tags[0] : "未分类";

        /// <summary>包内文件产物列表。</summary>
        public List<PackageArtifact> Artifacts { get; set; } = [];

        /// <summary>
        /// 内容哈希（基于所有 Artifact 文件内容计算）。
        /// 用于去重：两个内容相同的包会有相同的 ContentHash。
        /// 元数据（名称、备注、预览图）不参与计算。
        /// </summary>
        public string? ContentHash { get; set; }

        /// <summary>文件总大小（字节）。</summary>
        public long TotalSize { get; set; }

        /// <summary>导入来源路径（记录最初从哪里导入的）。</summary>
        public string? ImportSourcePath { get; set; }

        /// <summary>导入时间。</summary>
        public DateTime ImportedAt { get; init; } = DateTime.Now;

        /// <summary>最后修改时间。</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// 关联的游戏名称。
        /// </summary>
        public string HostGameName { get; init; } = default!;

        /// <summary>
        /// 插件目标路径（仅 Plugin 类型使用，相对于游戏根目录）。
        /// </summary>
        public string? PluginTargetPath { get; set; }

        // ─── 计算属性（UI 用） ───

        /// <summary>格式化的文件大小。</summary>
        [JsonIgnore]
        public string FormattedSize => FormatFileSize(TotalSize);

        /// <summary>格式化的导入日期。</summary>
        [JsonIgnore]
        public string FormattedImportDate => ImportedAt.ToString("yyyy-MM-dd");

        /// <summary>Artifact 文件数量。</summary>
        [JsonIgnore]
        public int FileCount => Artifacts.Count;

        // ─── INotifyPropertyChanged ───

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName!);
            return true;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int idx = 0;
            double size = bytes;
            while (size >= 1024 && idx < units.Length - 1) { size /= 1024; idx++; }
            return $"{size:F1} {units[idx]}";
        }
    }
}
