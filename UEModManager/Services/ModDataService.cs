using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.Services
{
    /// <summary>
    /// MOD 元数据持久化服务。
    /// 替代旧版 UEModManager.Core.Services.ModService，使用统一的 Models.ModInfo。
    /// 数据存储在 Data/{gameName}_mods.json。
    /// </summary>
    public class ModDataService
    {
        private readonly ILogger<ModDataService> _logger;
        private readonly string _dataDirectory;
        private string _currentGame = string.Empty;
        private List<ModInfo> _cachedMods = new();

        public ModDataService(ILogger<ModDataService> logger)
        {
            _logger = logger;
            _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        }

        // ─── 游戏切换 ───

        /// <summary>
        /// 设置当前游戏并加载对应的 MOD 数据。
        /// </summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            _currentGame = gameName;

            if (!Directory.Exists(_dataDirectory))
                Directory.CreateDirectory(_dataDirectory);

            _cachedMods = await LoadFromDiskAsync();
            _logger.LogInformation("已加载 {Game} 的 MOD 数据: {Count} 个", gameName, _cachedMods.Count);
        }

        // ─── 读取 ───

        /// <summary>
        /// 获取当前游戏的所有 MOD 元数据。
        /// </summary>
        public Task<List<ModInfo>> LoadModsAsync()
        {
            return Task.FromResult(new List<ModInfo>(_cachedMods));
        }

        // ─── 写入 ───

        /// <summary>
        /// 保存所有 MOD 元数据到磁盘。
        /// </summary>
        public async Task SaveModsAsync(IEnumerable<ModInfo> mods)
        {
            _cachedMods = mods.Select(m => new ModInfo
            {
                Name = m.Name,
                RealName = m.RealName,
                Description = m.Description,
                Categories = m.Categories.ToList(),
                IsEnabled = m.IsEnabled,
                FileSize = m.FileSize,
                InstallDate = m.InstallDate,
                PreviewImagePath = m.PreviewImagePath,
                BackupStatus = m.BackupStatus
            }).ToList();

            await WriteToDiskAsync(_cachedMods);
        }

        /// <summary>
        /// 保存单个 MOD 元数据（更新或新增）。
        /// </summary>
        public async Task SaveModAsync(ModInfo mod)
        {
            var existing = _cachedMods.FirstOrDefault(m => m.Id == mod.Id || m.RealName == mod.RealName);
            if (existing != null)
            {
                existing.Name = mod.Name;
                existing.Description = mod.Description;
                existing.Categories = mod.Categories.ToList();
                existing.IsEnabled = mod.IsEnabled;
                existing.PreviewImagePath = mod.PreviewImagePath;
                existing.FileSize = mod.FileSize;
            }
            else
            {
                _cachedMods.Add(new ModInfo
                {
                    Name = mod.Name,
                    RealName = mod.RealName,
                    Description = mod.Description,
                    Categories = mod.Categories.ToList(),
                    IsEnabled = mod.IsEnabled,
                    FileSize = mod.FileSize,
                    InstallDate = mod.InstallDate,
                    PreviewImagePath = mod.PreviewImagePath,
                    BackupStatus = mod.BackupStatus
                });
            }

            await WriteToDiskAsync(_cachedMods);
        }

        /// <summary>
        /// 删除单个 MOD 元数据。
        /// </summary>
        public async Task RemoveModAsync(string modId)
        {
            var mod = _cachedMods.FirstOrDefault(m => m.Id == modId || m.RealName == modId || m.Name == modId);
            if (mod != null)
            {
                _cachedMods.Remove(mod);
                await WriteToDiskAsync(_cachedMods);
            }
        }

        // ─── 内部持久化 ───

        private string GetFilePath() => Path.Combine(_dataDirectory, $"{_currentGame}_mods.json");

        private async Task<List<ModInfo>> LoadFromDiskAsync()
        {
            try
            {
                var filePath = GetFilePath();
                if (!File.Exists(filePath))
                    return new List<ModInfo>();

                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<List<ModInfo>>(json) ?? new List<ModInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载 MOD 数据失败");
                return new List<ModInfo>();
            }
        }

        /// <summary>
        /// 使用临时文件 + 原子替换策略写入磁盘，确保数据完整性。
        /// </summary>
        private async Task WriteToDiskAsync(List<ModInfo> mods)
        {
            const int maxRetries = 3;
            var filePath = GetFilePath();

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var tempFile = filePath + ".tmp";
                    var json = JsonSerializer.Serialize(mods, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(tempFile, json);

                    if (File.Exists(filePath))
                        File.Replace(tempFile, filePath, null);
                    else
                        File.Move(tempFile, filePath);

                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    _logger.LogWarning(ex, "保存 MOD 数据重试 {Attempt}/{Max}", attempt + 1, maxRetries);
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "保存 MOD 数据失败");
                    throw;
                }
            }
        }
    }
}
