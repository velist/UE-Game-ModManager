using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// 统一分类模型。
    /// 合并了旧版 MainWindow.Category 和 Core.CategoryItem。
    /// </summary>
    public class CategoryItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string? _displayName;
        private string _fullPath = string.Empty;
        private int _count;
        private bool _isCustom;
        private bool _isHidden;
        private int _sortOrder;
        private bool _isActive;

        /// <summary>
        /// 分类名称（内部标识）。
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>
        /// 显示别名（用户自定义的显示名称）。
        /// </summary>
        public string? DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>
        /// 层级路径 "父分类/子分类"。
        /// </summary>
        public string FullPath
        {
            get => _fullPath;
            set
            {
                if (_fullPath != value)
                {
                    _fullPath = value;
                    OnPropertyChanged(nameof(FullPath));
                }
            }
        }

        /// <summary>
        /// 该分类下的 MOD 数量。
        /// </summary>
        [JsonIgnore]
        public int Count
        {
            get => _count;
            set
            {
                if (_count != value)
                {
                    _count = value;
                    OnPropertyChanged(nameof(Count));
                }
            }
        }

        /// <summary>
        /// 是否为用户自定义分类。
        /// </summary>
        public bool IsCustom
        {
            get => _isCustom;
            set
            {
                if (_isCustom != value)
                {
                    _isCustom = value;
                    OnPropertyChanged(nameof(IsCustom));
                }
            }
        }

        /// <summary>
        /// 是否在界面中隐藏。
        /// </summary>
        public bool IsHidden
        {
            get => _isHidden;
            set
            {
                if (_isHidden != value)
                {
                    _isHidden = value;
                    OnPropertyChanged(nameof(IsHidden));
                }
            }
        }

        /// <summary>
        /// 排序权重。
        /// </summary>
        public int SortOrder
        {
            get => _sortOrder;
            set
            {
                if (_sortOrder != value)
                {
                    _sortOrder = value;
                    OnPropertyChanged(nameof(SortOrder));
                }
            }
        }

        // ─── UI 绑定专用 ───

        /// <summary>
        /// 是否为当前选中分类（UI 绑定）。
        /// </summary>
        [JsonIgnore]
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

        /// <summary>
        /// 计算属性：优先显示 DisplayName，否则显示 Name。
        /// </summary>
        [JsonIgnore]
        public string DisplayText => DisplayName ?? Name;

        // ─── INotifyPropertyChanged ───

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // ─── 系统分类常量 ───

        /// <summary>
        /// 系统内置分类名称（不可删除/重命名）。
        /// </summary>
        public static readonly HashSet<string> SystemNames = new() { "全部", "已启用", "已禁用" };

        /// <summary>
        /// 默认分类排序顺序。
        /// </summary>
        public static readonly string[] DefaultOrder =
            { "面部", "人物", "武器", "服装", "发型", "修改", "其他", "未分类" };
    }
}
