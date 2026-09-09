using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Categories;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// 分类导航 ViewModel。
    /// </summary>
    public partial class CategoryViewModel : ObservableObject
    {
        private readonly NewCategoryService _categoryService;

        /// <summary>
        /// 分类列表（绑定到 UI）。
        /// </summary>
        public ObservableCollection<CategoryItem> Categories => _categoryService.Categories;

        /// <summary>
        /// 可以作为"移动到分类"目标的分类（<see cref="Categories"/> 去掉三个系统分类）。
        ///
        /// 单独维护一个集合而不是让 View 自己过滤：右键子菜单是 ItemsSource 绑定的，
        /// 绑到 <see cref="Categories"/> 就会把"全部/已启用/已禁用"也列出来，
        /// 而这三个是按 IsEnabled 现算的筛选视图、根本不存储归属——
        /// 用户点进去会看到一次"成功"，刷新后 MOD 却回到原分类。
        ///
        /// 跟随 <see cref="Categories"/> 整体重建而不是增量同步：分类总量是几十条的量级，
        /// 重建的代价可以忽略，而增量同步要正确处理 Move/Replace/Reset 四种事件，
        /// 漏一种的表现就是菜单里多出一个已被删除的分类。
        /// </summary>
        public ObservableCollection<CategoryItem> AssignableCategories { get; } = new();

        [ObservableProperty]
        private CategoryItem? _selectedCategory;

        /// <summary>
        /// 分类选中事件。
        /// </summary>
        public event Action<CategoryItem?>? CategorySelected;

        public CategoryViewModel(NewCategoryService categoryService)
        {
            _categoryService = categoryService;

            Categories.CollectionChanged += (_, _) => RebuildAssignableCategories();
            RebuildAssignableCategories();
        }

        private void RebuildAssignableCategories()
        {
            AssignableCategories.Clear();
            foreach (var cat in Categories.Where(c => ModCategoryAssignment.IsAssignableTarget(c.Name)))
                AssignableCategories.Add(cat);
        }

        partial void OnSelectedCategoryChanged(CategoryItem? value)
        {
            CategorySelected?.Invoke(value);
        }

        /// <summary>
        /// 根据 MOD 列表更新所有分类的计数。
        /// </summary>
        public void UpdateCounts(IEnumerable<ModInfo> allMods)
        {
            _categoryService.UpdateCounts(allMods);
        }

        // ─── 命令 ───

        /// <summary>
        /// 添加新分类。
        /// </summary>
        [RelayCommand]
        public async Task AddCategoryAsync(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            await _categoryService.AddCategoryAsync(name.Trim());
        }

        /// <summary>
        /// 删除分类。
        /// </summary>
        [RelayCommand]
        public async Task DeleteCategoryAsync(CategoryItem? category)
        {
            if (category == null) return;

            if (CategoryItem.SystemNames.Contains(category.Name))
                return;

            await _categoryService.RemoveCategoryAsync(category);

            if (SelectedCategory == category)
                SelectedCategory = Categories.FirstOrDefault();
        }

        /// <summary>
        /// 重命名分类。
        /// </summary>
        [RelayCommand]
        public Task RenameCategoryAsync(CategoryItem? category)
        {
            if (category == null) return Task.CompletedTask;
            return Task.CompletedTask;
        }

        /// <summary>
        /// 执行重命名（由 View 调用，传入新名称）。
        /// </summary>
        public async Task DoRenameCategoryAsync(CategoryItem category, string newName)
        {
            await _categoryService.RenameCategoryAsync(category, newName);
        }

        /// <summary>
        /// 调整分类顺序（侧边栏拖拽排序）。
        ///
        /// 必须走服务而不是直接 <c>Categories.Move</c>：后者只改内存，重启后顺序原样弹回来。
        /// 落盘失败时服务会把顺序移回原位并上抛，由 View 的 SafeEvent.Run 弹给用户。
        /// </summary>
        public async Task ReorderCategoryAsync(CategoryItem category, int newIndex)
        {
            await _categoryService.ReorderCategoryAsync(category, newIndex);
        }
    }
}
