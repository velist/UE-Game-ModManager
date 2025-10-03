#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace UEModManager.Core
{
    public class Mod : INotifyPropertyChanged
    {
        private bool _isSelected;
        
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Categories { get; set; } = new List<string>();
        
        public bool IsSelected 
        { 
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }
        
        public string RealName { get; set; } = string.Empty;
        public string PreviewImagePath { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        
        public virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    public class ModInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string PreviewImagePath { get; set; } = string.Empty;
        public List<string> Categories { get; set; } = new List<string>();
        public bool IsEnabled { get; set; }
        public long FileSize { get; set; }
        public DateTime InstallDate { get; set; } = DateTime.Now;
    }
    
    public class CategoryItem
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public string FullPath { get; set; } = string.Empty;
    }
    
    public class DragAdorner
    {
        public DragAdorner(object adornedElement)
        {
        }
        
        public DragAdorner(object adornedElement, object content, double opacity)
        {
        }
        
        public DragAdorner(object adornedElement, object content, object size)
        {
        }
        
        public void UpdatePosition(object position) { }
    }
    
    public class Category
    {
        public string Name { get; set; } = string.Empty;
    }
}

namespace UEModManager.Core.Services
{
    public class ModService
    {
        private string _currentGame = string.Empty;
        private string _dataDirectory = string.Empty;
        public List<ModInfo> Mods { get; set; } = new List<ModInfo>();
        
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGame = gameName;
            // 设置数据目录为程序安装目录下的Data文件夹
            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            
            // 确保数据目录存在
            if (!Directory.Exists(_dataDirectory))
            {
                Directory.CreateDirectory(_dataDirectory);
            }
            
            // 加载MOD数据
            await LoadModsAsync();
        }
        
        private string GetModFilePath()
        {
            return Path.Combine(_dataDirectory, $"{_currentGame}_mods.json");
        }
        
        private async Task LoadModsAsync()
        {
            try
            {
                var filePath = GetModFilePath();
                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath);
                    var savedMods = JsonSerializer.Deserialize<List<ModInfo>>(json);
                    if (savedMods != null)
                    {
                        Mods = savedMods;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 加载MOD数据失败: {ex.Message}");
            }
        }
        
        public async Task ForceWriteToDiskAsync()
        {
            await SafeWriteFileAsync(GetModFilePath(), Mods);
        }
        
        /// <summary>
        /// 同步保存MOD数据到磁盘
        /// </summary>
        public void ForceWriteToDiskSync()
        {
            SafeWriteFileSync(GetModFilePath(), Mods);
        }
        
        public async Task AddModAsync(string modName) 
        {
            var mod = new ModInfo { Name = modName, Id = modName };
            Mods.Add(mod);
            await ForceWriteToDiskAsync();
        }
        
        public async Task<ModInfo> AddModAsync(string modName, string installPath, string description) 
        { 
            var mod = new ModInfo { Name = modName, Id = modName, Description = description };
            Mods.Add(mod);
            await ForceWriteToDiskAsync();
            return mod;
        }
        
        public async Task UpdateModAsync(ModInfo mod)
        {
            var existingMod = Mods.FirstOrDefault(m => m.Id == mod.Id);
            if (existingMod != null)
            {
                existingMod.Name = mod.Name;
                existingMod.Description = mod.Description;
                existingMod.Categories = mod.Categories;
                existingMod.PreviewImagePath = mod.PreviewImagePath;
                existingMod.IsEnabled = mod.IsEnabled;
                await ForceWriteToDiskAsync();
            }
        }
        
        public async Task RemoveModAsync(ModInfo mod)
        {
            Mods.Remove(mod);
            await ForceWriteToDiskAsync();
        }
        
        public async Task RemoveModAsync(string modName)
        {
            var mod = Mods.FirstOrDefault(m => m.Name == modName);
            if (mod != null)
            {
                Mods.Remove(mod);
                await ForceWriteToDiskAsync();
            }
        }
        
        public async Task CleanupInvalidModsAsync() 
        { 
            // 清理无效的MOD
            await Task.Delay(1);
        }
        
        public async Task AddModAsync(ModInfo mod)
        {
            if (!Mods.Any(m => m.Id == mod.Id))
            {
                Mods.Add(mod);
                await ForceWriteToDiskAsync();
            }
        }
        
        public async Task AddModAsync(ModInfo mod, string category, string additionalInfo)
        {
            if (!string.IsNullOrEmpty(category))
            {
                mod.Categories = new List<string> { category };
            }
            if (!Mods.Any(m => m.Id == mod.Id))
            {
                Mods.Add(mod);
                await ForceWriteToDiskAsync();
            }
        }

        /// <summary>
        /// 安全的异步文件写入，使用临时文件和重试机制
        /// </summary>
        private async Task SafeWriteFileAsync(string filePath, List<ModInfo> mods)
        {
            const int maxRetries = 3;
            const int retryDelayMs = 100;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var tempFilePath = filePath + ".tmp";
                    var json = JsonSerializer.Serialize(mods, new JsonSerializerOptions { WriteIndented = true });

                    // 写入临时文件
                    await File.WriteAllTextAsync(tempFilePath, json);

                    // 原子性替换
                    if (File.Exists(filePath))
                    {
                        File.Replace(tempFilePath, filePath, null);
                    }
                    else
                    {
                        File.Move(tempFilePath, filePath);
                    }

                    Console.WriteLine("[DEBUG] MOD数据已保存到磁盘");
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    Console.WriteLine($"[WARNING] 保存MOD数据重试 {attempt + 1}/{maxRetries}: {ex.Message}");
                    await Task.Delay(retryDelayMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] 保存MOD数据失败: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 安全的同步文件写入，使用临时文件和重试机制
        /// </summary>
        private void SafeWriteFileSync(string filePath, List<ModInfo> mods)
        {
            const int maxRetries = 3;
            const int retryDelayMs = 100;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var tempFilePath = filePath + ".tmp";
                    var json = JsonSerializer.Serialize(mods, new JsonSerializerOptions { WriteIndented = true });

                    // 写入临时文件
                    File.WriteAllText(tempFilePath, json);

                    // 原子性替换
                    if (File.Exists(filePath))
                    {
                        File.Replace(tempFilePath, filePath, null);
                    }
                    else
                    {
                        File.Move(tempFilePath, filePath);
                    }

                    Console.WriteLine("[DEBUG] MOD数据已同步保存到磁盘");
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    Console.WriteLine($"[WARNING] 保存MOD数据重试 {attempt + 1}/{maxRetries}: {ex.Message}");
                    System.Threading.Thread.Sleep(retryDelayMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] 保存MOD数据失败: {ex.Message}");
                    throw;
                }
            }
        }
    }
    
    public class CategoryService
    {
        private string _currentGame = string.Empty;
        private string _dataDirectory = string.Empty;
        public List<CategoryItem> Categories { get; set; } = new List<CategoryItem>();
        
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGame = gameName;
            // 设置数据目录为程序安装目录下的Data文件夹
            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            
            // 确保数据目录存在
            if (!Directory.Exists(_dataDirectory))
            {
                Directory.CreateDirectory(_dataDirectory);
            }
            
            // 加载分类数据
            await LoadCategoriesAsync();
        }
        
        private string GetCategoryFilePath()
        {
            return Path.Combine(_dataDirectory, $"{_currentGame}_categories.json");
        }
        
        private async Task LoadCategoriesAsync()
        {
            try
            {
                var filePath = GetCategoryFilePath();
                bool loadedFromGameFile = false;
                
                // 首先尝试加载游戏特定的分类文件
                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath);
                    var savedCategories = JsonSerializer.Deserialize<List<CategoryItem>>(json);
                    if (savedCategories != null && savedCategories.Count > 3) // 有用户自定义分类
                    {
                        Categories = savedCategories;
                        loadedFromGameFile = true;
                        Console.WriteLine($"[DEBUG] 从游戏特定文件加载分类: {savedCategories.Count} 个分类");
                    }
                }
                
                // 如果游戏特定文件不存在或只有默认分类，尝试从其他游戏迁移分类
                if (!loadedFromGameFile)
                {
                    var migratedCategories = await TryMigrateCategoriesFromOtherGamesAsync();
                    if (migratedCategories != null && migratedCategories.Count > 3)
                    {
                        Categories = migratedCategories;
                        await SaveCategoriesAsync(); // 保存迁移的分类
                        Console.WriteLine($"[DEBUG] 从其他游戏迁移分类: {migratedCategories.Count} 个分类");
                    }
                    else
                    {
                        // 如果没有可迁移的分类，初始化默认分类
                        await InitializeDefaultCategoriesAsync();
                        Console.WriteLine($"[DEBUG] 初始化默认分类: 3 个分类");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 加载分类数据失败: {ex.Message}");
                await InitializeDefaultCategoriesAsync();
            }
        }
        
        private async Task SaveCategoriesAsync()
        {
            try
            {
                var filePath = GetCategoryFilePath();
                
                // 如果有现有分类数据且有用户自定义分类，先创建备份
                if (File.Exists(filePath) && Categories.Count > 3)
                {
                    try
                    {
                        var backupDir = Path.Combine(_dataDirectory, "Backups");
                        if (!Directory.Exists(backupDir))
                        {
                            Directory.CreateDirectory(backupDir);
                        }
                        
                        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                        var backupPath = Path.Combine(backupDir, $"{_currentGame}_categories_auto_backup_{timestamp}.json");
                        
                        // 复制现有文件作为备份
                        File.Copy(filePath, backupPath, true);
                        Console.WriteLine($"[DEBUG] 自动备份分类数据: {backupPath}");
                    }
                    catch (Exception backupEx)
                    {
                        Console.WriteLine($"[WARNING] 创建自动备份失败: {backupEx.Message}");
                    }
                }
                
                await SafeWriteCategoriesAsync(filePath, Categories);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 保存分类数据失败: {ex.Message}");
            }
        }
        
        public async Task<CategoryItem> AddCategoryAsync(string categoryName) 
        { 
            var newCategory = new CategoryItem { Name = categoryName, Count = 0, FullPath = categoryName };
            if (!Categories.Any(c => c.Name == categoryName))
            {
                Categories.Add(newCategory);
                await SaveCategoriesAsync();
            }
            return newCategory;
        }
        
        public async Task<CategoryItem> AddCategoryAsync(string categoryName, string parentCategory) 
        { 
            var fullPath = string.IsNullOrEmpty(parentCategory) ? categoryName : $"{parentCategory}/{categoryName}";
            var newCategory = new CategoryItem { Name = categoryName, Count = 0, FullPath = fullPath };
            if (!Categories.Any(c => c.FullPath == fullPath))
            {
                Categories.Add(newCategory);
                await SaveCategoriesAsync();
            }
            return newCategory;
        }
        
        public async Task<CategoryItem> AddCategoryAsync(string categoryName, CategoryItem parentCategory) 
        { 
            var fullPath = parentCategory != null ? $"{parentCategory.FullPath}/{categoryName}" : categoryName;
            var newCategory = new CategoryItem { Name = categoryName, Count = 0, FullPath = fullPath };
            if (!Categories.Any(c => c.FullPath == fullPath))
            {
                Categories.Add(newCategory);
                await SaveCategoriesAsync();
            }
            return newCategory;
        }
        
        public async Task RemoveCategoryAsync(string categoryName) 
        { 
            var category = Categories.FirstOrDefault(c => c.Name == categoryName);
            if (category != null)
            {
                Categories.Remove(category);
                await SaveCategoriesAsync();
            }
        }
        
        public async Task RemoveCategoryAsync(CategoryItem category) 
        { 
            if (category != null)
            {
                Categories.Remove(category);
                await SaveCategoriesAsync();
            }
        }
        
        public async Task<bool> RenameCategoryAsync(string oldName, string newName) 
        { 
            var category = Categories.FirstOrDefault(c => c.Name == oldName);
            if (category != null && !Categories.Any(c => c.Name == newName))
            {
                category.Name = newName;
                category.FullPath = newName;
                await SaveCategoriesAsync();
                return true;
            }
            return false;
        }
        
        public async Task<bool> RenameCategoryAsync(CategoryItem category, string newName) 
        { 
            if (category != null && !Categories.Any(c => c.Name == newName))
            {
                category.Name = newName;
                category.FullPath = newName;
                await SaveCategoriesAsync();
                return true;
            }
            return false;
        }
        
        private async Task<List<CategoryItem>?> TryMigrateCategoriesFromOtherGamesAsync()
        {
            try
            {
                var dataDirectory = _dataDirectory;
                if (!Directory.Exists(dataDirectory))
                    return null;
                    
                // 查找所有其他游戏的分类文件
                var categoryFiles = Directory.GetFiles(dataDirectory, "*_categories.json")
                    .Where(f => !Path.GetFileName(f).StartsWith(_currentGame))
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime) // 按修改时间排序，优先最新的
                    .ToList();
                    
                foreach (var categoryFile in categoryFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(categoryFile);
                        var categories = JsonSerializer.Deserialize<List<CategoryItem>>(json);
                        
                        if (categories != null && categories.Count > 3) // 有用户自定义分类
                        {
                            Console.WriteLine($"[DEBUG] 发现可迁移的分类文件: {Path.GetFileName(categoryFile)}, 包含 {categories.Count} 个分类");
                            return categories;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] 读取分类文件失败 {categoryFile}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] 分类迁移过程出错: {ex.Message}");
            }
            
            return null;
        }
        
        public async Task InitializeDefaultCategoriesAsync() 
        { 
            Categories.Clear();
            // 初始化默认分类
            Categories.Add(new CategoryItem { Name = "全部", Count = 0, FullPath = "全部" });
            Categories.Add(new CategoryItem { Name = "已启用", Count = 0, FullPath = "已启用" });
            Categories.Add(new CategoryItem { Name = "已禁用", Count = 0, FullPath = "已禁用" });
            
            await SaveCategoriesAsync();
        }
        
        // 添加分类导出功能
        public async Task<bool> ExportCategoriesAsync(string exportPath)
        {
            try
            {
                var json = JsonSerializer.Serialize(Categories, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(exportPath, json);
                Console.WriteLine($"[DEBUG] 分类导出成功: {exportPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 分类导出失败: {ex.Message}");
                return false;
            }
        }
        
        // 添加分类导入功能
        public async Task<bool> ImportCategoriesAsync(string importPath, bool replaceExisting = false)
        {
            try
            {
                if (!File.Exists(importPath))
                    return false;
                    
                var json = await File.ReadAllTextAsync(importPath);
                var importedCategories = JsonSerializer.Deserialize<List<CategoryItem>>(json);
                
                if (importedCategories == null)
                    return false;
                    
                if (replaceExisting)
                {
                    Categories = importedCategories;
                }
                else
                {
                    // 合并模式：只添加不存在的分类
                    foreach (var category in importedCategories)
                    {
                        if (!Categories.Any(c => c.Name == category.Name || c.FullPath == category.FullPath))
                        {
                            Categories.Add(category);
                        }
                    }
                }
                
                await SaveCategoriesAsync();
                Console.WriteLine($"[DEBUG] 分类导入成功: {importedCategories.Count} 个分类");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 分类导入失败: {ex.Message}");
                return false;
            }
        }
        
        // 添加备份功能
        public async Task<bool> CreateBackupAsync()
        {
            try
            {
                var backupDir = Path.Combine(_dataDirectory, "Backups");
                if (!Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                }
                
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var backupPath = Path.Combine(backupDir, $"{_currentGame}_categories_backup_{timestamp}.json");
                
                return await ExportCategoriesAsync(backupPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 创建分类备份失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 安全的分类数据写入，使用临时文件和重试机制
        /// </summary>
        private async Task SafeWriteCategoriesAsync(string filePath, List<CategoryItem> categories)
        {
            const int maxRetries = 3;
            const int retryDelayMs = 100;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var tempFilePath = filePath + ".tmp";
                    var json = JsonSerializer.Serialize(categories, new JsonSerializerOptions { WriteIndented = true });

                    // 写入临时文件
                    await File.WriteAllTextAsync(tempFilePath, json);

                    // 原子性替换
                    if (File.Exists(filePath))
                    {
                        File.Replace(tempFilePath, filePath, null);
                    }
                    else
                    {
                        File.Move(tempFilePath, filePath);
                    }

                    Console.WriteLine($"[DEBUG] 分类数据保存成功: {categories.Count} 个分类");
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    Console.WriteLine($"[WARNING] 保存分类数据重试 {attempt + 1}/{maxRetries}: {ex.Message}");
                    await Task.Delay(retryDelayMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] 保存分类数据失败: {ex.Message}");
                    throw;
                }
            }
        }
    }
}

namespace UEModManager.Core.Models
{
    public class CategoryItem
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public string FullPath { get; set; } = string.Empty;
    }
    
    public class GameProfile
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string ExecutableName { get; set; } = string.Empty;
    }
    
    public class UserPreferences
    {
        public string Language { get; set; } = string.Empty;
        public bool AutoBackup { get; set; }
        public string DefaultGamePath { get; set; } = string.Empty;
    }
    
    public class ModCategory
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<ModInfo> Mods { get; set; } = new List<ModInfo>();
    }
}
