using UEModManager.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// MOD 列表/网格 ViewModel。
    /// 管理 MOD 的显示列表、搜索、排序、多选操作。
    /// </summary>
    public partial class ModListViewModel : ObservableObject
    {
        private readonly ILogger _logger;
        private Func<ModInfo, Task<bool>>? _deleteModAsync;
        private Func<ModInfo, bool, Task<bool>>? _toggleModAsync;
        private Func<IReadOnlyList<ModInfo>, Task<bool>>? _deleteModsAsync;
        private Func<IReadOnlyList<ModInfo>, bool, Task<bool>>? _toggleModsAsync;
        private ObservableCollection<ModInfo> _source = new();

        /// <summary>
        /// 批量改动 <see cref="ModInfo.IsSelected"/> 期间置位。
        ///
        /// 每张卡片自己会发 PropertyChanged，<see cref="OnModPropertyChanged"/> 收到就把
        /// <see cref="UpdateSelectionState"/> 跑一遍，而后者会把 IsSelectAll 重算成
        /// "选中的确实全选了" —— 一个循环里只选到第 1 个时它就被改回 false，
        /// 剩下的卡片跟着读到一个已经翻回去的值，于是"点一次只多选一个"。
        /// 批量期间挡住重算，循环结束再统一算一次。
        /// </summary>
        private bool _suppressSelectionUpdate;

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
        [NotifyPropertyChangedFor(nameof(SelectAllText))]
        private bool _isSelectAll;

        /// <summary>
        /// "全选"按钮要显示哪一条文案：**未翻译的源串**（"全选" / "取消全选"）。
        ///
        /// 由 <see cref="IsSelectAll"/> 派生，让"现在点下去是全选还是取消"可见 ——
        /// 用户此前只能靠数卡片判断按钮处在哪个档位。
        ///
        /// 这里刻意不做翻译、把源串交给 XAML 的 <c>loc:Value</c>：那条路带语言订阅，
        /// 切换语言时会自己重取；在 VM 里翻好再绑，语言切了文案不会跟着变。
        /// 返回源串也让单测不受进程当前语言影响。
        /// </summary>
        public string SelectAllText => IsSelectAll ? "取消全选" : "全选";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedMods))]
        private int _selectedCount;

        public bool HasSelectedMods => SelectedCount > 0;

        /// <summary>
        /// MOD 被选中事件。
        /// </summary>
        public event Action<ModInfo?>? ModSelected;

        /// <summary>
        /// MOD 列表变更事件。
        /// </summary>
        public event Action? ModsChanged;

        private CategoryItem? _currentCategory;

        public ModListViewModel(ILogger logger)
        {
            _logger = logger;
        }

        public void ConfigureActions(
            Func<ModInfo, bool, Task<bool>> toggleModAsync,
            Func<ModInfo, Task<bool>> deleteModAsync,
            Func<IReadOnlyList<ModInfo>, bool, Task<bool>>? toggleModsAsync = null,
            Func<IReadOnlyList<ModInfo>, Task<bool>>? deleteModsAsync = null)
        {
            _toggleModAsync = toggleModAsync;
            _deleteModAsync = deleteModAsync;
            _toggleModsAsync = toggleModsAsync;
            _deleteModsAsync = deleteModsAsync;
        }

        /// <summary>
        /// 设置数据源（AllMods）。
        /// </summary>
        public void SetSource(ObservableCollection<ModInfo> source)
        {
            foreach (var mod in _source)
                mod.PropertyChanged -= OnModPropertyChanged;

            _source = source;
            foreach (var mod in _source)
                mod.PropertyChanged += OnModPropertyChanged;

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

        private void OnModPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressSelectionUpdate) return;
            if (e.PropertyName == nameof(ModInfo.IsSelected))
                UpdateSelectionState();
        }

        private void UpdateSelectionState()
        {
            SelectedCount = Mods.Count(m => m.IsSelected);
            IsSelectAll = Mods.Count > 0 && SelectedCount == Mods.Count;
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
                // 用 OrdinalIgnoreCase 比较，不再为每个 MOD 分配 3 个 ToLower() 字符串；
                // 顺带避开 ToLower() 的文化敏感问题（如土耳其语的 i/İ）。
                filtered = filtered.Where(m =>
                    m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    m.RealName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    m.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var result = ApplySortInternal(filtered, SortMode).ToList();

            Mods.Clear();
            foreach (var mod in result)
                Mods.Add(mod);
            UpdateSelectionState();
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
            UpdateSelectionState();
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
            if (_toggleModAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("切换 MOD"));

            var success = await _toggleModAsync(mod, !mod.IsEnabled);

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
            if (_deleteModAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("删除 MOD"));

            var success = await _deleteModAsync(mod);

            if (success)
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
            // 按当前**真实**选中情况决定，不信 IsSelectAll 字段：它是 UpdateSelectionState
            // 算出来的结果，与"用户想干什么"不是一回事（部分选中时它早就是 false 了）。
            var target = !(Mods.Count > 0 && Mods.All(m => m.IsSelected));

            _suppressSelectionUpdate = true;
            foreach (var mod in Mods)
                mod.IsSelected = target;
            _suppressSelectionUpdate = false;

            UpdateSelectionState();
        }

        /// <summary>
        /// 启用所有 MOD。
        /// </summary>
        [RelayCommand]
        public async Task EnableAllAsync()
        {
            if (_toggleModsAsync != null)
            {
                await _toggleModsAsync(Mods.Where(m => !m.IsEnabled).ToList(), true);
            }
            else if (_toggleModAsync != null)
            {
                foreach (var mod in Mods.Where(m => !m.IsEnabled).ToList())
                    await _toggleModAsync(mod, true);
            }
            else
            {
                throw DeploymentServiceNotInitialized(UiText.Get("批量启用 MOD"));
            }
            ModsChanged?.Invoke();
        }

        /// <summary>
        /// 禁用所有 MOD。
        /// </summary>
        [RelayCommand]
        public async Task DisableAllAsync()
        {
            if (_toggleModsAsync != null)
            {
                await _toggleModsAsync(Mods.Where(m => m.IsEnabled).ToList(), false);
            }
            else if (_toggleModAsync != null)
            {
                foreach (var mod in Mods.Where(m => m.IsEnabled).ToList())
                    await _toggleModAsync(mod, false);
            }
            else
            {
                throw DeploymentServiceNotInitialized(UiText.Get("批量禁用 MOD"));
            }
            ModsChanged?.Invoke();
        }

        public async Task EnableSelectedAsync()
        {
            await ToggleSelectedAsync(true);
        }

        public async Task DisableSelectedAsync()
        {
            await ToggleSelectedAsync(false);
        }

        private async Task ToggleSelectedAsync(bool enable)
        {
            var selected = Mods.Where(m => m.IsSelected && m.IsEnabled != enable).ToList();
            if (selected.Count == 0) return;

            var changed = false;
            if (_toggleModsAsync != null)
            {
                changed = await _toggleModsAsync(selected, enable);
            }
            else if (_toggleModAsync != null)
            {
                foreach (var mod in selected)
                    changed |= await _toggleModAsync(mod, enable);
            }
            else
            {
                throw DeploymentServiceNotInitialized(enable ? UiText.Get("启用选中 MOD") : UiText.Get("禁用选中 MOD"));
            }

            if (changed)
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

            var changed = false;
            if (_deleteModsAsync != null)
            {
                changed = await _deleteModsAsync(selected);
            }
            else if (_deleteModAsync != null)
            {
                foreach (var mod in selected)
                    changed |= await _deleteModAsync(mod);
            }
            else
            {
                throw DeploymentServiceNotInitialized(UiText.Get("删除选中 MOD"));
            }

            if (changed)
            {
                foreach (var mod in selected)
                    mod.IsSelected = false;
                ModsChanged?.Invoke();
            }
        }

        private InvalidOperationException DeploymentServiceNotInitialized(string operation)
        {
            _logger.LogError("部署服务未初始化，无法执行操作: {Operation}", operation);
            return new InvalidOperationException(UiText.Get("部署服务未初始化"));
        }
    }
}
