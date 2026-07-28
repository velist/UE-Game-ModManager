using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
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
            : this(logger, AppPaths.DataDirectory)
        {
        }

        /// <summary>
        /// 指定数据目录的构造函数（测试用）。DI 走上面的单参数构造函数——
        /// 容器无法解析 string，不会误选此重载。与 <see cref="GameConfigService"/> /
        /// <see cref="OverwriteStore"/> 的处理一致：数据目录归口 AppPaths 之后，
        /// 测试若不注入位置就会写进开发者真实的 %LOCALAPPDATA%。
        /// </summary>
        public NewCategoryService(ILogger<NewCategoryService> logger, string dataDirectory)
        {
            _logger = logger;
            _dataDirectory = dataDirectory;
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
        //
        // 四个写操作都遵循同一条规则：先改内存、落盘失败就把内存改回去再上抛。
        // <see cref="Categories"/> 是直接绑到侧边栏 ListBox 上的 ObservableCollection，
        // 不回滚的话用户会同时看到"错误对话框"和"列表里躺着那个新分类"，
        // 下次启动它又不见了——比单纯报错更让人怀疑到底成没成。

        /// <summary>
        /// 添加新分类。落盘失败时抛出，并撤销本次内存变更。
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
            try
            {
                await SaveCategoriesAsync();
            }
            catch
            {
                Categories.Remove(category);
                throw;
            }
            CategoriesChanged?.Invoke();

            _logger.LogInformation("添加分类: {Name}", name);
            return category;
        }

        /// <summary>
        /// 删除分类。落盘失败时抛出，并把分类放回原位。
        /// </summary>
        public async Task RemoveCategoryAsync(CategoryItem category)
        {
            if (CategoryItem.SystemNames.Contains(category.Name))
            {
                _logger.LogWarning("无法删除系统分类: {Name}", category.Name);
                return;
            }

            var index = Categories.IndexOf(category);
            if (index < 0) return;

            Categories.RemoveAt(index);
            try
            {
                await SaveCategoriesAsync();
            }
            catch
            {
                Categories.Insert(index, category);
                throw;
            }
            CategoriesChanged?.Invoke();

            _logger.LogInformation("删除分类: {Name}", category.Name);
        }

        /// <summary>
        /// 重命名分类。落盘失败时抛出，并还原原名称。
        /// </summary>
        public async Task<bool> RenameCategoryAsync(CategoryItem category, string newName)
        {
            if (CategoryItem.SystemNames.Contains(category.Name))
                return false;
            if (Categories.Any(c => c.Name == newName))
                return false;

            var oldName = category.Name;
            var oldFullPath = category.FullPath;

            category.Name = newName;
            category.FullPath = newName;
            try
            {
                await SaveCategoriesAsync();
            }
            catch
            {
                category.Name = oldName;
                category.FullPath = oldFullPath;
                throw;
            }
            CategoriesChanged?.Invoke();

            _logger.LogInformation("重命名分类: -> {NewName}", newName);
            return true;
        }

        /// <summary>
        /// 调整分类排序。落盘失败时抛出，并把顺序移回去。
        /// </summary>
        public async Task ReorderCategoryAsync(CategoryItem category, int newIndex)
        {
            var idx = Categories.IndexOf(category);
            if (idx < 0 || idx == newIndex) return;

            Categories.Move(idx, newIndex);
            ResequenceSortOrders();

            try
            {
                await SaveCategoriesAsync();
            }
            catch
            {
                Categories.Move(newIndex, idx);
                ResequenceSortOrders();
                throw;
            }
            CategoriesChanged?.Invoke();
        }

        private void ResequenceSortOrders()
        {
            for (int i = 0; i < Categories.Count; i++)
                Categories[i].SortOrder = i;
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
                    await TrySaveDuringLoadAsync();
                    _logger.LogInformation("从其他游戏迁移了 {Count} 个分类", migrated.Count);
                    return;
                }

                // 初始化默认分类
                InitializeDefaults();
                await TrySaveDuringLoadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载分类数据失败");
                InitializeDefaults();
            }
        }

        /// <summary>
        /// 加载过程中的隐式落盘。写失败只记日志、不上抛。
        ///
        /// 与用户显式发起的增删改不同，这里没有任何 UI 可以承接错误：它发生在切换游戏
        /// 的加载路径上，抛出去只会让整个加载失败、界面连默认分类都拿不到。更糟的是
        /// 异常会被上面的 catch 接住并再跑一次 <see cref="InitializeDefaults"/>，
        /// 把刚从其他游戏迁移过来的分类当场清空。
        /// 内存里的分类此时是可用的，用户下一次真正的增删改会走 <see cref="SaveCategoriesAsync"/>，
        /// 那条路径会明确报错。
        /// </summary>
        private async Task TrySaveDuringLoadAsync()
        {
            try
            {
                await SaveCategoriesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载分类时的初始化落盘失败，本次仅使用内存中的分类");
            }
        }

        private void InitializeDefaults()
        {
            Categories.Clear();
            Categories.Add(new CategoryItem { Name = "全部", FullPath = "全部", SortOrder = 0 });
            Categories.Add(new CategoryItem { Name = "已启用", FullPath = "已启用", SortOrder = 1 });
            Categories.Add(new CategoryItem { Name = "已禁用", FullPath = "已禁用", SortOrder = 2 });
        }

        /// <summary>
        /// 落盘当前分类列表。
        ///
        /// 写失败必须上抛：数据目录搬到 %LOCALAPPDATA% 之后，"目标不可写"（磁盘满、
        /// 权限、杀软锁定、被同步盘占用）是真实会发生的。此前这里把异常吞掉，用户看到
        /// 分类添加成功、重启后凭空消失，且没有任何线索。与 ModDataService.WriteToDiskAsync /
        /// ProfileService.PersistAsync 保持同一种失败语义：记日志 + 上抛，
        /// 由调用链上的 SafeEvent.Run 弹给用户。
        /// </summary>
        private async Task SaveCategoriesAsync()
        {
            var filePath = GetFilePath();

            // 自动备份。备份是尽力而为：它只是给"分类被误删"多留一份后悔药，
            // 失败不该阻断保存本身——否则备份目录不可写会连带把正常保存也毙掉。
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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "分类自动备份失败，继续保存");
                }
            }

            try
            {
                await SafeWriteAsync(filePath, Categories.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存分类数据失败: {Path}", filePath);
                throw;
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
                    catch (Exception ex)
                    {
                        // 单个文件读不出来就换下一个；这里是纯读取的机会主义迁移，
                        // 失败不影响功能，但完全无声会让"为什么没迁移过来"无从排查。
                        _logger.LogWarning(ex, "读取候选分类文件失败，跳过: {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "枚举其他游戏的分类文件失败，跳过迁移");
            }

            return null;
        }

        private async Task SafeWriteAsync(string filePath, List<CategoryItem> categories)
        {
            const int maxRetries = 3;

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

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
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    // 只重试前 N-1 次；最后一次的异常直接上抛给 SaveCategoriesAsync。
                    _logger.LogWarning(ex, "保存分类数据重试 {Attempt}/{Max}", attempt + 1, maxRetries);
                    await Task.Delay(100);
                }
            }
        }
    }
}
