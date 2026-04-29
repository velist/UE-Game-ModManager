using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// MOD 列表/网格 ViewModel。
    /// 管理 MOD 的显示列表、搜索、排序、多选操作。
    /// </summary>
    public partial class ModListViewModel : ObservableObject
    {
        private readonly ModManagementService _modService;
        private readonly GameConfigService _gameConfig;
        private readonly ILogger _logger;
        private ObservableCollection<ModInfo> _source = new();

        /// <summary>
        /// 过滤后的 MOD 列表（绑定到 UI）。
        /// </summary>
        public ObservableCollection<ModInfo> Mods { get; } = new();

        [ObservableProperty]
        private ModInfo? _selectedMod;

        [ObservableProperty]
        private bool _isGridView = true;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private string _sortMode = "Name_Asc";

        [ObservableProperty]
        private bool _isSelectAll;

        /// <summary>
        /// MOD 被选中事件。
        /// </summary>
        public event Action<ModInfo?>? ModSelected;

        /// <summary>
        /// MOD 列表变更事件。
        /// </summary>
        public event Action? ModsChanged;

        private CategoryItem? _currentCategory;

        public ModListViewModel(ModManagementService modService, GameConfigService gameConfig, ILogger logger)
        {
            _modService = modService;
            _gameConfig = gameConfig;
            _logger = logger;
        }

        /// <summary>
        /// 设置数据源（AllMods）。
        /// </summary>
        public void SetSource(ObservableCollection<ModInfo> source)
        {
            _source = source;
            ApplyFilter(_currentCategory, SearchText);
        }

        partial void OnSelectedModChanged(ModInfo? value)
        {
            ModSelected?.Invoke(value);
        }

        partial void OnSearchTextChanged(string value)
        {
            ApplyFilter(_currentCategory, value);
        }

        partial void OnSortModeChanged(string value)
        {
            ApplySort(value);
        }

        // ─── 过滤与排序 ───

        /// <summary>
        /// 应用分类过滤和搜索过滤。
        /// </summary>
        public void ApplyFilter(CategoryItem? category, string? search)
        {
            _currentCategory = category;
            var filtered = _source.AsEnumerable();

            // 分类过滤
            if (category != null && category.Name != "全部")
            {
                if (category.Name == "已启用")
                    filtered = filtered.Where(m => m.IsEnabled);
                else if (category.Name == "已禁用")
                    filtered = filtered.Where(m => !m.IsEnabled);
                else
                    filtered = filtered.Where(m => m.Categories.Contains(category.Name));
            }

            // 搜索过滤
            if (!string.IsNullOrWhiteSpace(search))
            {
                var lower = search.ToLower();
                filtered = filtered.Where(m =>
                    m.Name.ToLower().Contains(lower) ||
                    m.RealName.ToLower().Contains(lower) ||
                    m.Description.ToLower().Contains(lower));
            }

            var result = ApplySortInternal(filtered, SortMode).ToList();

            Mods.Clear();
            foreach (var mod in result)
                Mods.Add(mod);
        }

        /// <summary>
        /// 应用排序。
        /// </summary>
        public void ApplySort(string sortMode)
        {
            var sorted = ApplySortInternal(Mods, sortMode).ToList();
            Mods.Clear();
            foreach (var mod in sorted)
                Mods.Add(mod);
        }

        private static IEnumerable<ModInfo> ApplySortInternal(IEnumerable<ModInfo> mods, string sortMode)
        {
            return sortMode switch
            {
                "Name_Asc" => mods.OrderBy(m => m.Name),
                "Name_Desc" => mods.OrderByDescending(m => m.Name),
                "Date_Asc" => mods.OrderBy(m => m.InstallDate),
                "Date_Desc" => mods.OrderByDescending(m => m.InstallDate),
                "Size_Asc" => mods.OrderBy(m => m.FileSize),
                "Size_Desc" => mods.OrderByDescending(m => m.FileSize),
                "Status" => mods.OrderByDescending(m => m.IsEnabled).ThenBy(m => m.Name),
                _ => mods.OrderBy(m => m.Name)
            };
        }

        // ─── MOD 操作命令 ───

        /// <summary>
        /// 切换 MOD 启用/禁用。
        /// </summary>
        [RelayCommand]
        public async Task ToggleModAsync(ModInfo? mod)
        {
            if (mod == null) return;

            bool success;
            if (mod.IsEnabled)
                success = await _modService.DisableModAsync(mod, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath);
            else
                success = await _modService.EnableModAsync(mod, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath);

            if (success)
                ModsChanged?.Invoke();
        }

        /// <summary>
        /// 删除 MOD。
        /// </summary>
        [RelayCommand]
        public async Task DeleteModAsync(ModInfo? mod)
        {
            if (mod == null) return;

            if (await _modService.DeleteModAsync(mod, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath))
            {
                Mods.Remove(mod);
                if (SelectedMod == mod)
                    SelectedMod = null;
                ModsChanged?.Invoke();
            }
        }

        /// <summary>
        /// 选中 MOD。
        /// </summary>
        [RelayCommand]
        public void SelectMod(ModInfo? mod)
        {
            SelectedMod = mod;
        }

        /// <summary>
        /// 切换网格/列表视图。
        /// </summary>
        [RelayCommand]
        public void ToggleView()
        {
            IsGridView = !IsGridView;
        }

        /// <summary>
        /// 全选/取消全选。
        /// </summary>
        [RelayCommand]
        public void SelectAll()
        {
            IsSelectAll = !IsSelectAll;
            foreach (var mod in Mods)
                mod.IsSelected = IsSelectAll;
        }

        /// <summary>
        /// 启用所有 MOD。
        /// </summary>
        [RelayCommand]
        public async Task EnableAllAsync()
        {
            await _modService.EnableAllAsync(Mods, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath);
            ModsChanged?.Invoke();
        }

        /// <summary>
        /// 禁用所有 MOD。
        /// </summary>
        [RelayCommand]
        public async Task DisableAllAsync()
        {
            await _modService.DisableAllAsync(Mods, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath);
            ModsChanged?.Invoke();
        }

        /// <summary>
        /// 删除选中的 MOD。
        /// </summary>
        [RelayCommand]
        public async Task DeleteSelectedAsync()
        {
            var selected = Mods.Where(m => m.IsSelected).ToList();
            if (selected.Count == 0) return;

            await _modService.DeleteModsAsync(selected, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath);

            foreach (var mod in selected)
                Mods.Remove(mod);

            ModsChanged?.Invoke();
        }
    }
}
