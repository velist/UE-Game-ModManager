using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UEModManager.Data;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// 本地缓存服务
    /// </summary>
    public class LocalCacheService
    {
        private readonly LocalDbContext _dbContext;
        private readonly ILogger<LocalCacheService> _logger;

        public LocalCacheService(LocalDbContext dbContext, ILogger<LocalCacheService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        #region MOD缓存管理

        /// <summary>
        /// 缓存MOD信息
        /// </summary>
        public async Task<bool> CacheModAsync(string modId, string modName, string gameName, 
            string? version = null, string? description = null, string? author = null, 
            string? downloadUrl = null, long fileSize = 0, string? filePath = null)
        {
            try
            {
                var existingCache = await _dbContext.ModCaches
                    .FirstOrDefaultAsync(m => m.ModId == modId);

                if (existingCache != null)
                {
                    // 更新现有缓存
                    existingCache.ModName = modName;
                    existingCache.GameName = gameName;
                    existingCache.Version = version;
                    existingCache.Description = description;
                    existingCache.Author = author;
                    existingCache.DownloadUrl = downloadUrl;
                    existingCache.FileSize = fileSize;
                    existingCache.FilePath = filePath;
                    existingCache.LastUpdated = DateTime.Now;
                }
                else
                {
                    // 创建新缓存条目
                    var newCache = new LocalModCache
                    {
                        ModId = modId,
                        ModName = modName,
                        GameName = gameName,
                        Version = version,
                        Description = description,
                        Author = author,
                        DownloadUrl = downloadUrl,
                        FileSize = fileSize,
                        FilePath = filePath,
                        CachedAt = DateTime.Now,
                        LastUpdated = DateTime.Now
                    };

                    _dbContext.ModCaches.Add(newCache);
                }

                await _dbContext.SaveChangesAsync();
                _logger.LogInformation($"MOD缓存已更新: {modName} ({modId})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"缓存MOD失败: {modName} ({modId})");
                return false;
            }
        }

        /// <summary>
        /// 获取缓存的MOD信息
        /// </summary>
        public async Task<LocalModCache?> GetCachedModAsync(string modId)
        {
            try
            {
                return await _dbContext.ModCaches
                    .FirstOrDefaultAsync(m => m.ModId == modId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取MOD缓存失败: {modId}");
                return null;
            }
        }

        /// <summary>
        /// 获取指定游戏的所有缓存MOD
        /// </summary>
        public async Task<List<LocalModCache>> GetCachedModsByGameAsync(string gameName)
        {
            try
            {
                return await _dbContext.ModCaches
                    .Where(m => m.GameName == gameName)
                    .OrderBy(m => m.ModName)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取游戏MOD缓存失败: {gameName}");
                return new List<LocalModCache>();
            }
        }

        /// <summary>
        /// 搜索缓存的MOD
        /// </summary>
        public async Task<List<LocalModCache>> SearchCachedModsAsync(string searchTerm, string? gameName = null)
        {
            try
            {
                var query = _dbContext.ModCaches.AsQueryable();

                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    query = query.Where(m => m.GameName == gameName);
                }

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    query = query.Where(m => 
                        m.ModName.Contains(searchTerm) ||
                        (m.Description != null && m.Description.Contains(searchTerm)) ||
                        (m.Author != null && m.Author.Contains(searchTerm)));
                }

                return await query
                    .OrderBy(m => m.ModName)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"搜索MOD缓存失败: {searchTerm}");
                return new List<LocalModCache>();
            }
        }

        /// <summary>
        /// 删除MOD缓存
        /// </summary>
        public async Task<bool> RemoveCachedModAsync(string modId)
        {
            try
            {
                var cache = await _dbContext.ModCaches
                    .FirstOrDefaultAsync(m => m.ModId == modId);

                if (cache != null)
                {
                    _dbContext.ModCaches.Remove(cache);
                    await _dbContext.SaveChangesAsync();
                    _logger.LogInformation($"MOD缓存已删除: {cache.ModName} ({modId})");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"删除MOD缓存失败: {modId}");
                return false;
            }
        }

        /// <summary>
        /// 清理过期缓存
        /// </summary>
        public async Task<int> CleanExpiredCacheAsync(TimeSpan maxAge)
        {
            try
            {
                var expireDate = DateTime.Now - maxAge;
                var expiredCaches = await _dbContext.ModCaches
                    .Where(m => m.LastUpdated < expireDate)
                    .ToListAsync();

                if (expiredCaches.Any())
                {
                    _dbContext.ModCaches.RemoveRange(expiredCaches);
                    await _dbContext.SaveChangesAsync();
                    _logger.LogInformation($"已清理 {expiredCaches.Count} 个过期MOD缓存");
                }

                return expiredCaches.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理过期缓存失败");
                return 0;
            }
        }

        #endregion

        #region 统计和维护

        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        public async Task<CacheStatistics> GetCacheStatisticsAsync()
        {
            try
            {
                var totalMods = await _dbContext.ModCaches.CountAsync();
                var totalSize = await _dbContext.ModCaches.SumAsync(m => m.FileSize);
                var gameGroups = await _dbContext.ModCaches
                    .GroupBy(m => m.GameName)
                    .Select(g => new { GameName = g.Key, Count = g.Count() })
                    .ToListAsync();

                var oldestCache = await _dbContext.ModCaches
                    .OrderBy(m => m.CachedAt)
                    .FirstOrDefaultAsync();

                var newestCache = await _dbContext.ModCaches
                    .OrderByDescending(m => m.CachedAt)
                    .FirstOrDefaultAsync();

                return new CacheStatistics
                {
                    TotalMods = totalMods,
                    TotalSizeBytes = totalSize,
                    GameCounts = gameGroups.ToDictionary(g => g.GameName, g => g.Count),
                    OldestCacheDate = oldestCache?.CachedAt,
                    NewestCacheDate = newestCache?.CachedAt
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缓存统计信息失败");
                return new CacheStatistics();
            }
        }

        /// <summary>
        /// 清理所有缓存
        /// </summary>
        public async Task<bool> ClearAllCacheAsync()
        {
            try
            {
                var allCaches = await _dbContext.ModCaches.ToListAsync();
                if (allCaches.Any())
                {
                    _dbContext.ModCaches.RemoveRange(allCaches);
                    await _dbContext.SaveChangesAsync();
                    _logger.LogInformation($"已清理所有MOD缓存 ({allCaches.Count} 项)");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理所有缓存失败");
                return false;
            }
        }

        #endregion
    }

    #region 缓存相关数据类

    /// <summary>
    /// 缓存统计信息
    /// </summary>
    public class CacheStatistics
    {
        public int TotalMods { get; set; }
        public long TotalSizeBytes { get; set; }
        public Dictionary<string, int> GameCounts { get; set; } = new();
        public DateTime? OldestCacheDate { get; set; }
        public DateTime? NewestCacheDate { get; set; }

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