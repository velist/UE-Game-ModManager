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
    /// 分类导航 ViewModel。
    /// </summary>
    public partial class CategoryViewModel : ObservableObject
    {
        private readonly NewCategoryService _categoryService;
        private readonly ILogger _logger;

        /// <summary>
        /// 分类列表（绑定到 UI）。
        /// </summary>
        public ObservableCollection<CategoryItem> Categories => _categoryService.Categories;

        [ObservableProperty]
        private CategoryItem? _selectedCategory;

        /// <summary>
        /// 分类选中事件。
        /// </summary>
        public event Action<CategoryItem?>? CategorySelected;

        public CategoryViewModel(NewCategoryService categoryService, ILogger logger)
        {
            _categoryService = categoryService;
            _logger = logger;
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
    }
}
