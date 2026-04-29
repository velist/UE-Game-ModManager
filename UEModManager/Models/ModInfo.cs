using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace UEModManager.Models
{
    /// <summary>
    /// 统一 MOD 数据模型。
    /// 合并了旧版 MainWindow.Mod (UI绑定) 和 Core.ModInfo (持久化) 两个类。
    /// </summary>
    public class ModInfo : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _realName = string.Empty;
        private string _description = string.Empty;
        private List<string> _categories = new List<string> { "未分类" };
        private bool _isEnabled;
        private string _backupStatus = "未知";
        private bool _isSelected;
        private string _previewImagePath = string.Empty;
        private ImageSource? _previewImageSource;
        private long _fileSize;

        /// <summary>
        /// MOD 标识符 (= RealName/文件夹名)。用于持久化查找。
        /// </summary>
        public string Id
        {
            get => string.IsNullOrEmpty(_realName) ? _name : _realName;
            set => RealName = value;
        }

        /// <summary>
        /// 显示名称（用户可自定义的友好名称）。
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>
        /// 真实文件夹名（不可变，用于所有文件操作）。
        /// </summary>
        public string RealName
        {
            get => _realName;
            set => SetField(ref _realName, value);
        }

        /// <summary>
        /// MOD 描述。
        /// </summary>
        public string Description
        {
            get => _description;
            set => SetField(ref _description, value);
        }

        /// <summary>
        /// MOD 所属分类列表。默认为 ["未分类"]。
        /// </summary>
        public List<string> Categories
        {
            get => _categories;
            set
            {
                _categories = value ?? new List<string> { "未分类" };
                OnPropertyChanged(nameof(Categories));
                OnPropertyChanged(nameof(PrimaryCategory));
            }
        }

        /// <summary>
        /// 主分类（Categories 的第一个元素）。
        /// </summary>
        [JsonIgnore]
        public string PrimaryCategory => _categories.Count > 0 ? _categories[0] : "未分类";

        /// <summary>
        /// 是否启用。替代旧版的 Status 字符串 ("已启用"/"已禁用")。
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }

        /// <summary>
        /// 备份状态："正常" | "备份失败" | "未知"。
        /// </summary>
        public string BackupStatus
        {
            get => _backupStatus;
            set => SetField(ref _backupStatus, value);
        }

        /// <summary>
        /// 文件大小（字节）。
        /// </summary>
        public long FileSize
        {
            get => _fileSize;
            set
            {
                if (SetField(ref _fileSize, value))
                    OnPropertyChanged(nameof(FormattedSize));
            }
        }

        /// <summary>
        /// 安装/导入日期。
        /// </summary>
        public DateTime InstallDate { get; set; } = DateTime.Now;

        /// <summary>
        /// 是否为插件（非标准 MOD，任意格式文件放到指定目录）。
        /// </summary>
        public bool IsPlugin { get; set; }

        /// <summary>
        /// v2.0: 根据文件扩展名和 IsPlugin 推断包类型。
        /// 用于卡片类型色标绑定。
        /// </summary>
        [JsonIgnore]
        public PackageKind DetectedKind
        {
            get
            {
                if (IsPlugin) return PackageKind.Plugin;
                var ext = System.IO.Path.GetExtension(RealName ?? "").ToLowerInvariant();
                return ext switch
                {
                    ".dll" => PackageKind.Plugin,
                    ".ini" or ".json" or ".cfg" or ".yaml" or ".xml" => PackageKind.Config,
                    _ => PackageKind.Mod
                };
            }
        }

        /// <summary>
        /// 插件的目标安装路径（相对于游戏根目录）。仅 IsPlugin=true 时有效。
        /// </summary>
        public string PluginTargetPath { get; set; } = string.Empty;

        /// <summary>
        /// 预览图路径。
        /// </summary>
        public string PreviewImagePath
        {
            get => _previewImagePath;
            set => SetField(ref _previewImagePath, value);
        }

        // ─── 以下为 UI 绑定专用，不参与 JSON 序列化 ───

        /// <summary>
        /// 多选状态（UI 绑定）。
        /// </summary>
        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        /// <summary>
        /// 预览图源（UI 绑定，WPF ImageSource）。
        /// </summary>
        [JsonIgnore]
        public ImageSource? PreviewImage
        {
            get => _previewImageSource;
            set => SetField(ref _previewImageSource, value);
        }

        /// <summary>
        /// 格式化的文件大小（计算属性）。
        /// </summary>
        [JsonIgnore]
        public string FormattedSize => FormatFileSize(_fileSize);

        /// <summary>
        /// 格式化的安装日期。
        /// </summary>
        [JsonIgnore]
        public string FormattedInstallDate => InstallDate.ToString("yyyy-MM-dd");

        // ─── INotifyPropertyChanged ───

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName!);
            return true;
        }

        // ─── 工具方法 ───

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            int unitIndex = 0;
            double size = bytes;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:F1} {units[unitIndex]}";
        }
    }
}
