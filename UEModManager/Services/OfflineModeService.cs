using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UEModManager.Data;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 离线模式管理服务
    /// </summary>
    public class OfflineModeService
    {
        private readonly LocalDbContext _dbContext;
        private readonly LocalCacheService _cacheService;
        private readonly ILogger<OfflineModeService> _logger;
        private bool _isOfflineMode = false;

        public OfflineModeService(LocalDbContext dbContext, LocalCacheService cacheService, ILogger<OfflineModeService> logger)
        {
            _dbContext = dbContext;
            _cacheService = cacheService;
            _logger = logger;
        }

        #region 离线模式控制

        /// <summary>
        /// 离线模式状态
        /// </summary>
        public bool IsOfflineMode 
        { 
            get => _isOfflineMode; 
            private set
            {
                if (_isOfflineMode != value)
                {
                    _isOfflineMode = value;
                    OfflineModeChanged?.Invoke(this, new OfflineModeEventArgs(_isOfflineMode));
                }
            }
        }

        /// <summary>
        /// 离线模式状态变更事件
        /// </summary>
        public event EventHandler<OfflineModeEventArgs>? OfflineModeChanged;

        /// <summary>
        /// 启用离线模式
        /// </summary>
        public async Task<bool> EnableOfflineModeAsync()
        {
            try
            {
                // 确保数据库可用
                await _dbContext.EnsureDatabaseCreatedAsync();

                // 更新配置
                await SetConfigurationAsync("OfflineModeEnabled", "true");
                
                IsOfflineMode = true;
                _logger.LogInformation("离线模式已启用");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启用离线模式失败");
                return false;
            }
        }

        /// <summary>
        /// 禁用离线模式
        /// </summary>
        public async Task<bool> DisableOfflineModeAsync()
        {
            try
            {
                await SetConfigurationAsync("OfflineModeEnabled", "false");
                
                IsOfflineMode = false;
                _logger.LogInformation("离线模式已禁用");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "禁用离线模式失败");
                return false;
            }
        }

        /// <summary>
        /// 初始化离线模式状态
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                var config = await GetConfigurationAsync("OfflineModeEnabled");
                _isOfflineMode = config?.Value == "true";
                
                _logger.LogInformation($"离线模式初始化完成，当前状态: {(_isOfflineMode ? "启用" : "禁用")}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "离线模式初始化失败");
                _isOfflineMode = false;
            }
        }

        #endregion

        #region 数据同步管理

        /// <summary>
        /// 获取待同步的MOD数据
        /// </summary>
        public async Task<List<LocalModCache>> GetPendingSyncDataAsync()
        {
            try
            {
                return await _dbContext.ModCaches
                    .Where(m => m.LastUpdated > DateTime.Now.AddDays(-7)) // 近7天更新的数据
                    .OrderByDescending(m => m.LastUpdated)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取待同步数据失败");
                return new List<LocalModCache>();
            }
        }

        /// <summary>
        /// 预加载常用MOD数据
        /// </summary>
        public async Task<bool> PreloadCommonModsAsync(string gameName, int maxCount = 50)
        {
            try
            {
                // 这里模拟从服务器获取热门MOD数据并缓存到本地
                // 实际实现时应该从真实的API获取数据
                
                var commonMods = new[]
                {
                    new { ModId = "mod001", Name = "常用MOD 1", Author = "作者1", Description = "这是一个常用的MOD" },
                    new { ModId = "mod002", Name = "常用MOD 2", Author = "作者2", Description = "这是另一个常用的MOD" },
                    new { ModId = "mod003", Name = "常用MOD 3", Author = "作者3", Description = "这是第三个常用的MOD" },
                };

                int cachedCount = 0;
                foreach (var mod in commonMods.Take(maxCount))
                {
                    var success = await _cacheService.CacheModAsync(
                        mod.ModId, 
                        mod.Name, 
                        gameName,
                        "1.0.0", 
                        mod.Description, 
                        mod.Author
                    );
                    
                    if (success) cachedCount++;
                }

                _logger.LogInformation($"预加载完成，成功缓存 {cachedCount} 个常用MOD");
                return cachedCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "预加载常用MOD失败");
                return false;
            }
        }

        /// <summary>
        /// 清理过期的离线数据
        /// </summary>
        public async Task<int> CleanExpiredOfflineDataAsync(int maxDays = 30)
        {
            try
            {
                var cleanedCount = await _cacheService.CleanExpiredCacheAsync(TimeSpan.FromDays(maxDays));
                
                // 同时清理过期的用户会话
                var expiredSessions = await _dbContext.UserSessions
                    .Where(s => s.ExpiresAt < DateTime.Now.AddDays(-maxDays))
                    .ToListAsync();

                if (expiredSessions.Any())
                {
                    _dbContext.UserSessions.RemoveRange(expiredSessions);
                    await _dbContext.SaveChangesAsync();
                    cleanedCount += expiredSessions.Count;
                }

                _logger.LogInformation($"清理过期离线数据完成，共清理 {cleanedCount} 项");
                return cleanedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理过期离线数据失败");
                return 0;
            }
        }

        #endregion

        #region 离线文件管理

        /// <summary>
        /// 检查本地文件完整性
        /// </summary>
        public async Task<OfflineFileStatus> CheckLocalFileIntegrityAsync()
        {
            var status = new OfflineFileStatus();
            
            try
            {
                var cachedMods = await _dbContext.ModCaches.ToListAsync();
                
                foreach (var mod in cachedMods)
                {
                    if (!string.IsNullOrEmpty(mod.FilePath) && File.Exists(mod.FilePath))
                    {
                        var fileInfo = new FileInfo(mod.FilePath);
                        if (fileInfo.Length == mod.FileSize)
                        {
                            status.ValidFiles++;
                        }
                        else
                        {
                            status.CorruptedFiles++;
                            status.CorruptedFilesList.Add(mod.FilePath);
                        }
                    }
                    else if (!string.IsNullOrEmpty(mod.FilePath))
                    {
                        status.MissingFiles++;
                        status.MissingFilesList.Add(mod.FilePath);
                    }
                    
                    status.TotalFiles++;
                }

                _logger.LogInformation($"文件完整性检查完成: 总计 {status.TotalFiles}, 有效 {status.ValidFiles}, 损坏 {status.CorruptedFiles}, 丢失 {status.MissingFiles}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查文件完整性失败");
            }

            return status;
        }

        /// <summary>
        /// 获取离线存储使用情况
        /// </summary>
        public async Task<OfflineStorageInfo> GetOfflineStorageInfoAsync()
        {
            try
            {
                var cacheStats = await _cacheService.GetCacheStatisticsAsync();
                var userCount = await _dbContext.Users.CountAsync();
                var sessionCount = await _dbContext.UserSessions.CountAsync();
                var configCount = await _dbContext.Configurations.CountAsync();

                var dbPath = GetDatabasePath();
                var dbSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;

                return new OfflineStorageInfo
                {
                    DatabaseSizeBytes = dbSize,
                    CachedModsCount = cacheStats.TotalMods,
                    CachedFilesSizeBytes = cacheStats.TotalSizeBytes,
                    UserCount = userCount,
                    SessionCount = sessionCount,
                    ConfigCount = configCount,
                    TotalSizeBytes = dbSize + cacheStats.TotalSizeBytes
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取离线存储信息失败");
                return new OfflineStorageInfo();
            }
        }

        #endregion

        #region 配置管理

        private async Task<AppConfiguration?> GetConfigurationAsync(string key)
        {
            return await _dbContext.Configurations
                .FirstOrDefaultAsync(c => c.Key == key);
        }

        private async Task SetConfigurationAsync(string key, string value, string? description = null)
        {
            var config = await GetConfigurationAsync(key);
            if (config != null)
            {
                config.Value = value;
                config.UpdatedAt = DateTime.Now;
            }
            else
            {
                config = new AppConfiguration
                {
                    Key = key,
                    Value = value,
                    Description = description ?? $"配置项: {key}",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                _dbContext.Configurations.Add(config);
            }

            await _dbContext.SaveChangesAsync();
        }

        private static string GetDatabasePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appDataPath, "UEModManager", "local.db");
        }

        #endregion
    }

    #region 事件和数据类

    /// <summary>
    /// 离线模式事件参数
    /// </summary>
    public class OfflineModeEventArgs : EventArgs
    {
        public bool IsOfflineMode { get; }

        public OfflineModeEventArgs(bool isOfflineMode)
        {
            IsOfflineMode = isOfflineMode;
        }
    }

    /// <summary>
    /// 离线文件状态
    /// </summary>
    public class OfflineFileStatus
    {
        public int TotalFiles { get; set; }
        public int ValidFiles { get; set; }
        public int CorruptedFiles { get; set; }
        public int MissingFiles { get; set; }
        public List<string> CorruptedFilesList { get; set; } = new();
        public List<string> MissingFilesList { get; set; } = new();

        public double IntegrityPercentage => TotalFiles > 0 ? (double)ValidFiles / TotalFiles * 100 : 0;
    }

    /// <summary>
    /// 离线存储信息
    /// </summary>
    public class OfflineStorageInfo
    {
        public long DatabaseSizeBytes { get; set; }
        public int CachedModsCount { get; set; }
        public long CachedFilesSizeBytes { get; set; }
        public int UserCount { get; set; }
        public int SessionCount { get; set; }
        public int ConfigCount { get; set; }
        public long TotalSizeBytes { get; set; }

        public string DatabaseSizeFormatted => FormatBytes(DatabaseSizeBytes);
        public string CachedFilesSizeFormatted => FormatBytes(CachedFilesSizeBytes);
        public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    #endregion
}