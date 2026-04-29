using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 分类管理服务（重写版）。
    /// 替代旧版 UEModManager.Core.Services.CategoryService，使用统一的 Models.CategoryItem。
    /// 数据存储在 Data/{gameName}_categories.json。
    /// </summary>
    public class NewCategoryService
    {
        private readonly ILogger<NewCategoryService> _logger;
        private readonly string _dataDirectory;
        private string _currentGame = string.Empty;

        /// <summary>
        /// 当前游戏的分类列表。
        /// </summary>
        public ObservableCollection<CategoryItem> Categories { get; } = new();

        /// <summary>
        /// 分类变更事件。
        /// </summary>
        public event Action? CategoriesChanged;

        public NewCategoryService(ILogger<NewCategoryService> logger)
        {
            _logger = logger;
            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        }

        // ─── 游戏切换 ───

        /// <summary>
        /// 切换当前游戏并加载对应的分类数据。
        /// </summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGame = gameName;
            if (!Directory.Exists(_dataDirectory))
                Directory.CreateDirectory(_dataDirectory);

            await LoadCategoriesAsync();
        }

        // ─── CRUD 操作 ───

        /// <summary>
        /// 添加新分类。
        /// </summary>
        public async Task<CategoryItem> AddCategoryAsync(string name, string? parentPath = null)
        {
            var fullPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

            if (Categories.Any(c => c.FullPath == fullPath))
                return Categories.First(c => c.FullPath == fullPath);

            var category = new CategoryItem
            {
                Name = name,
                FullPath = fullPath,
                IsCustom = true,
                SortOrder = Categories.Count
            };

            Categories.Add(category);
            await SaveCategoriesAsync();
            CategoriesChanged?.Invoke();

            _logger.LogInformation("添加分类: {Name}", name);
            return category;
        }

        /// <summary>
        /// 删除分类。
        /// </summary>
        public async Task RemoveCategoryAsync(CategoryItem category)
        {
            if (CategoryItem.SystemNames.Contains(category.Name))
            {
                _logger.LogWarning("无法删除系统分类: {Name}", category.Name);
                return;
            }

            Categories.Remove(category);
            await SaveCategoriesAsync();
            CategoriesChanged?.Invoke();

            _logger.LogInformation("删除分类: {Name}", category.Name);
        }

        /// <summary>
        /// 重命名分类。
        /// </summary>
        public async Task<bool> RenameCategoryAsync(CategoryItem category, string newName)
        {
            if (CategoryItem.SystemNames.Contains(category.Name))
                return false;
            if (Categories.Any(c => c.Name == newName))
                return false;

            category.Name = newName;
            category.FullPath = newName;
            await SaveCategoriesAsync();
            CategoriesChanged?.Invoke();

            _logger.LogInformation("重命名分类: -> {NewName}", newName);
            return true;
        }

        /// <summary>
        /// 调整分类排序。
        /// </summary>
        public async Task ReorderCategoryAsync(CategoryItem category, int newIndex)
        {
            var idx = Categories.IndexOf(category);
            if (idx < 0 || idx == newIndex) return;

            Categories.Move(idx, newIndex);

            for (int i = 0; i < Categories.Count; i++)
                Categories[i].SortOrder = i;

            await SaveCategoriesAsync();
            CategoriesChanged?.Invoke();
        }

        // ─── 计数更新 ───

        /// <summary>
        /// 根据 MOD 列表更新所有分类的 Count。
        /// </summary>
        public void UpdateCounts(IEnumerable<ModInfo> allMods)
        {
            var modList = allMods.ToList();
            foreach (var cat in Categories)
            {
                cat.Count = cat.Name switch
                {
                    "全部" => modList.Count,
                    "已启用" => modList.Count(m => m.IsEnabled),
                    "已禁用" => modList.Count(m => !m.IsEnabled),
                    _ => modList.Count(m => m.Categories.Contains(cat.Name))
                };
            }
        }

        /// <summary>
        /// 根据分类筛选 MOD。
        /// </summary>
        public IEnumerable<ModInfo> FilterMods(IEnumerable<ModInfo> mods, CategoryItem? category)
        {
            if (category == null || category.Name == "全部")
                return mods;
            if (category.Name == "已启用")
                return mods.Where(m => m.IsEnabled);
            if (category.Name == "已禁用")
                return mods.Where(m => !m.IsEnabled);
            return mods.Where(m => m.Categories.Contains(category.Name));
        }

        // ─── 分类显示配置 ───

        /// <summary>
        /// 保存分类显示配置（DisplayName、IsHidden、SortOrder 等）。
        /// </summary>
        public async Task SaveDisplayConfigAsync()
        {
            await SaveCategoriesAsync();
        }

        // ─── 内部方法 ───

        private string GetFilePath() => Path.Combine(_dataDirectory, $"{_currentGame}_categories.json");

        private async Task LoadCategoriesAsync()
        {
            Categories.Clear();

            try
            {
                var filePath = GetFilePath();

                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath);
                    var saved = JsonSerializer.Deserialize<List<CategoryItem>>(json);
                    if (saved != null && saved.Count > 0)
                    {
                        foreach (var cat in saved)
                            Categories.Add(cat);
                        _logger.LogInformation("加载了 {Count} 个分类", saved.Count);
                        return;
                    }
                }

                // 尝试从其他游戏迁移
                var migrated = await TryMigrateFromOtherGamesAsync();
                if (migrated != null && migrated.Count > 3)
                {
                    foreach (var cat in migrated)
                        Categories.Add(cat);
                    await SaveCategoriesAsync();
                    _logger.LogInformation("从其他游戏迁移了 {Count} 个分类", migrated.Count);
                    return;
                }

                // 初始化默认分类
                InitializeDefaults();
                await SaveCategoriesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载分类数据失败");
                InitializeDefaults();
            }
        }

        private void InitializeDefaults()
        {
            Categories.Clear();
            Categories.Add(new CategoryItem { Name = "全部", FullPath = "全部", SortOrder = 0 });
            Categories.Add(new CategoryItem { Name = "已启用", FullPath = "已启用", SortOrder = 1 });
            Categories.Add(new CategoryItem { Name = "已禁用", FullPath = "已禁用", SortOrder = 2 });
        }

        private async Task SaveCategoriesAsync()
        {
            try
            {
                var filePath = GetFilePath();

                // 自动备份
                if (File.Exists(filePath) && Categories.Count > 3)
                {
                    try
                    {
                        var backupDir = Path.Combine(_dataDirectory, "Backups");
                        if (!Directory.Exists(backupDir))
                            Directory.CreateDirectory(backupDir);
                        var backupPath = Path.Combine(backupDir,
                            $"{_currentGame}_categories_backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json");
                        File.Copy(filePath, backupPath, true);
                    }
                    catch { /* 备份失败不影响保存 */ }
                }

                await SafeWriteAsync(filePath, Categories.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存分类数据失败");
            }
        }

        private async Task<List<CategoryItem>?> TryMigrateFromOtherGamesAsync()
        {
            try
            {
                if (!Directory.Exists(_dataDirectory))
                    return null;

                var files = Directory.GetFiles(_dataDirectory, "*_categories.json")
                    .Where(f => !Path.GetFileName(f).StartsWith(_currentGame))
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime);

                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var cats = JsonSerializer.Deserialize<List<CategoryItem>>(json);
                        if (cats != null && cats.Count > 3)
                            return cats;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        private static async Task SafeWriteAsync(string filePath, List<CategoryItem> categories)
        {
            const int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var tempFile = filePath + ".tmp";
                    var json = JsonSerializer.Serialize(categories, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(tempFile, json);

                    if (File.Exists(filePath))
                        File.Replace(tempFile, filePath, null);
                    else
                        File.Move(tempFile, filePath);

                    return;
                }
                catch when (attempt < maxRetries - 1)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
