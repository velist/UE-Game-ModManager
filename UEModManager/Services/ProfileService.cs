using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Persistence;
using UEModManager.Services.Profile;

namespace UEModManager.Services
{
    /// <summary>
    /// 游戏方案管理服务。
    /// 负责 Profile 的 CRUD、持久化、切换和数据迁移。
    /// </summary>
    public class ProfileService : IProfileQuery
    {
        private readonly ILogger<ProfileService> _logger;
        private readonly string _dataDir;

        /// <summary>当前游戏名称。</summary>
        private string _currentGameName = string.Empty;

        /// <summary>当前游戏的所有 Profile。</summary>
        private List<InstanceProfile> _profiles = [];

        /// <summary>当前活跃的 Profile。</summary>
        public InstanceProfile? CurrentProfile { get; private set; }

        /// <summary>Profile 切换事件。</summary>
        public event Action<InstanceProfile?>? ProfileChanged;

        /// <summary>Profile 列表变更事件。</summary>
        public event Action? ProfileListChanged;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ProfileService(ILogger<ProfileService> logger)
        {
            _logger = logger;
            _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            Directory.CreateDirectory(_dataDir);
        }

        // ─── 初始化 ───

        /// <summary>
        /// 设置当前游戏并加载其 Profile 列表。
        /// 如果没有 Profile，自动创建"默认配置"。
        /// </summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return;

            _currentGameName = gameName;
            await LoadProfilesAsync();

            // 自动创建默认 Profile（首次使用或数据迁移）
            if (_profiles.Count == 0)
            {
                await CreateDefaultProfileAsync(gameName);
            }

            // 确保有一个活跃 Profile
            CurrentProfile = _profiles.FirstOrDefault(p => p.IsActive)
                          ?? _profiles.FirstOrDefault();

            if (CurrentProfile != null && !CurrentProfile.IsActive)
            {
                CurrentProfile.IsActive = true;
                await SaveProfilesAsync();
            }

            ProfileChanged?.Invoke(CurrentProfile);
        }

        // ─── CRUD ───

        /// <summary>
        /// 获取当前游戏的所有 Profile。
        /// </summary>
        public IReadOnlyList<InstanceProfile> GetProfiles() => _profiles.AsReadOnly();

        /// <summary>按 ID 查找方案（IProfileQuery 实现）。</summary>
        public InstanceProfile? FindProfile(Guid profileId)
            => _profiles.FirstOrDefault(p => p.Id == profileId);

        /// <summary>
        /// 创建新 Profile。
        /// </summary>
        public async Task<InstanceProfile> CreateProfileAsync(string name, string? description = null,
            string? iconName = null, string? iconColor = null)
        {
            var profile = new InstanceProfile
            {
                HostGameName = _currentGameName,
                Name = name,
                Description = description,
                IconName = iconName ?? "shield",
                IconColor = iconColor ?? "#06b6d4",
                IsActive = false
            };

            _profiles.Add(profile);
            await SaveProfilesAsync();
            ProfileListChanged?.Invoke();

            _logger.LogInformation("创建方案 '{Name}' (游戏: {Game})", name, _currentGameName);
            return profile;
        }

        /// <summary>
        /// 复制现有 Profile。
        /// </summary>
        public async Task<InstanceProfile> CloneProfileAsync(Guid sourceId, string newName)
        {
            var source = _profiles.FirstOrDefault(p => p.Id == sourceId)
                ?? throw new InvalidOperationException($"找不到方案: {sourceId}");

            var clone = new InstanceProfile
            {
                HostGameName = _currentGameName,
                Name = newName,
                Description = $"复制自「{source.Name}」",
                IconName = source.IconName,
                IconColor = source.IconColor,
                IsActive = false,
                BackendType = source.BackendType,
                Packages = source.Packages
                    .Select(p => new ProfilePackageEntry
                    {
                        PackageKey = p.PackageKey,
                        IsEnabled = p.IsEnabled,
                        Priority = p.Priority,
                        Kind = p.Kind,
                        PluginTargetPath = p.PluginTargetPath
                    })
                    .ToList()
            };

            _profiles.Add(clone);
            await SaveProfilesAsync();
            ProfileListChanged?.Invoke();

            _logger.LogInformation("复制方案 '{Source}' → '{New}'", source.Name, newName);
            return clone;
        }

        /// <summary>
        /// 删除 Profile。不允许删除最后一个。
        /// </summary>
        public async Task<bool> DeleteProfileAsync(Guid profileId)
        {
            if (_profiles.Count <= 1)
            {
                _logger.LogWarning("不能删除最后一个方案");
                return false;
            }

            var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile == null) return false;

            bool wasActive = profile.IsActive;
            _profiles.Remove(profile);

            // 如果删除的是活跃方案，切换到第一个
            if (wasActive && _profiles.Count > 0)
            {
                _profiles[0].IsActive = true;
                CurrentProfile = _profiles[0];
                ProfileChanged?.Invoke(CurrentProfile);
            }

            await SaveProfilesAsync();
            ProfileListChanged?.Invoke();

            _logger.LogInformation("删除方案 '{Name}'", profile.Name);
            return true;
        }

        /// <summary>
        /// 重命名 Profile。
        /// </summary>
        public async Task RenameProfileAsync(Guid profileId, string newName)
        {
            var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile == null) return;

            profile.Name = newName;
            profile.LastModified = DateTime.Now;
            await SaveProfilesAsync();
            ProfileListChanged?.Invoke();
        }

        /// <summary>
        /// 切换到指定 Profile。
        /// </summary>
        public async Task SwitchProfileAsync(Guid profileId)
        {
            var target = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (target == null) return;

            // 取消所有活跃状态
            foreach (var p in _profiles)
                p.IsActive = false;

            target.IsActive = true;
            CurrentProfile = target;

            await SaveProfilesAsync();
            ProfileChanged?.Invoke(CurrentProfile);

            _logger.LogInformation("切换到方案 '{Name}'", target.Name);
        }

        // ─── 包管理 ───

        /// <summary>
        /// 同步 MOD 列表到当前 Profile。
        /// 新扫描到的包自动加入；已删除的包自动移除。
        /// </summary>
        public async Task SyncPackagesAsync(IReadOnlyList<ModInfo> scannedMods)
        {
            if (CurrentProfile == null) return;

            var snapshot = scannedMods
                .Select(m => new LegacyModEntry(m.RealName, m.IsEnabled, m.IsPlugin,
                    string.IsNullOrEmpty(m.PluginTargetPath) ? null : m.PluginTargetPath))
                .ToList();

            var sync = ProfileSyncPlanner.ComputeSync(CurrentProfile, snapshot);

            CurrentProfile.Packages.Clear();
            CurrentProfile.Packages.AddRange(sync.Packages);
            CurrentProfile.LastModified = DateTime.Now;

            _logger.LogDebug("Profile 同步: +{Added} -{Removed} ~{Updated}",
                sync.Added, sync.Removed, sync.Updated);

            await SaveProfilesAsync();
        }

        /// <summary>
        /// 仅更新 Profile 内的 IsEnabled 标志位，不触发任何部署/回滚。
        ///
        /// ⚠ 危险 API ⚠ — 直接调用会导致元数据与游戏目录文件不一致。
        /// 正常入口请走 <see cref="ViewModels.MainViewModel.DeployToggleAsync"/>，
        /// 它会先生成部署计划、执行事务，仅在事务成功后才调用本方法同步元数据。
        ///
        /// 仅在以下场景允许直接调用：
        /// 1. ViewModel 在事务 Committed 后回写元数据（当前唯一合法用法）
        /// 2. 数据迁移/恢复脚本（明确知道游戏目录已对齐）
        /// 3. 单元测试
        /// </summary>
        public async Task SetPackageEnabledFlagAsync(string packageKey, bool enabled)
        {
            if (CurrentProfile == null) return;

            var entry = CurrentProfile.Packages.FirstOrDefault(p =>
                p.PackageKey.Equals(packageKey, StringComparison.OrdinalIgnoreCase));

            if (entry != null)
            {
                if (entry.IsEnabled == enabled) return;

                entry.IsEnabled = enabled;
                CurrentProfile.LastModified = DateTime.Now;
                await SaveProfilesAsync();
            }
        }

        public async Task AddPackagesToCurrentProfileAsync(IEnumerable<Package> packages)
        {
            if (CurrentProfile == null) return;

            var existing = new HashSet<string>(
                CurrentProfile.Packages.Select(p => p.PackageKey),
                StringComparer.OrdinalIgnoreCase);

            var priority = CurrentProfile.Packages.Count == 0
                ? 0
                : CurrentProfile.Packages.Max(p => p.Priority) + 1;
            var changed = false;

            foreach (var package in packages)
            {
                if (!existing.Add(package.PackageKey))
                    continue;

                CurrentProfile.Packages.Add(new ProfilePackageEntry
                {
                    PackageKey = package.PackageKey,
                    IsEnabled = false,
                    Priority = priority++,
                    Kind = package.Kind,
                    TargetRootPath = package.TargetRootPath
                });
                changed = true;
            }

            if (!changed) return;

            CurrentProfile.LastModified = DateTime.Now;
            await SaveProfilesAsync();
            ProfileChanged?.Invoke(CurrentProfile);
        }

        public async Task RemovePackageReferencesAsync(string packageKey)
        {
            var changed = false;
            foreach (var profile in _profiles)
            {
                var removed = profile.Packages.RemoveAll(p =>
                    p.PackageKey.Equals(packageKey, StringComparison.OrdinalIgnoreCase));
                if (removed <= 0) continue;

                profile.LastModified = DateTime.Now;
                changed = true;
            }

            if (!changed) return;

            await SaveProfilesAsync();
            ProfileListChanged?.Invoke();
            ProfileChanged?.Invoke(CurrentProfile);
        }

        /// <summary>
        /// [已废弃] 旧名 — 转发到 <see cref="SetPackageEnabledFlagAsync"/>。
        /// 保留供二进制兼容；新代码请用新名。
        /// </summary>
        [Obsolete("方法已重命名为 SetPackageEnabledFlagAsync 以明确仅元数据不部署的语义。请改用 DeployToggleAsync 或 SetPackageEnabledFlagAsync。")]
        public Task SetPackageEnabledAsync(string packageKey, bool enabled)
            => SetPackageEnabledFlagAsync(packageKey, enabled);

        // ─── 数据迁移 ───

        /// <summary>
        /// 从现有 MOD 列表创建默认 Profile（v1.8 → v2.0 数据迁移）。
        /// </summary>
        private async Task CreateDefaultProfileAsync(string gameName)
        {
            var profile = new InstanceProfile
            {
                HostGameName = gameName,
                Name = "默认 MOD 方案",
                Description = "自动创建的默认 MOD 配置方案",
                IconName = "shield",
                IconColor = "#06b6d4",
                IsActive = true
            };

            // 尝试从现有 MOD 数据迁移
            var modsFilePath = Path.Combine(_dataDir, $"{gameName}_mods.json");
            if (File.Exists(modsFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(modsFilePath);
                    var mods = JsonSerializer.Deserialize<List<ModInfo>>(json);
                    if (mods != null)
                    {
                        var legacy = mods
                            .Select(m => new LegacyModEntry(m.RealName, m.IsEnabled, m.IsPlugin,
                                string.IsNullOrEmpty(m.PluginTargetPath) ? null : m.PluginTargetPath))
                            .ToList();
                        profile.Packages.AddRange(LegacyProfileMigrator.BuildPackagesFromLegacyMods(legacy));
                        _logger.LogInformation("从 v1.8 数据迁移了 {Count} 个包到默认方案", mods.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取旧版 MOD 数据失败，创建空方案");
                }
            }

            _profiles.Add(profile);
            await SaveProfilesAsync();

            _logger.LogInformation("已创建默认方案 (游戏: {Game})", gameName);
        }

        // ─── 持久化 ───

        private string GetProfileFilePath() =>
            Path.Combine(_dataDir, $"{_currentGameName}_profiles.json");

        private async Task LoadProfilesAsync()
        {
            var filePath = GetProfileFilePath();
            if (!File.Exists(filePath))
            {
                _profiles = [];
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                _profiles = JsonSerializer.Deserialize<List<InstanceProfile>>(json, JsonOptions) ?? [];
                _logger.LogInformation("加载了 {Count} 个方案 (游戏: {Game})",
                    _profiles.Count, _currentGameName);
            }
            catch (Exception ex)
            {
                BackupCorruptProfileFile(filePath, ex);
                _profiles = [];
            }
        }

        private async Task SaveProfilesAsync()
        {
            try
            {
                var filePath = GetProfileFilePath();
                var json = JsonSerializer.Serialize(_profiles, JsonOptions);
                await AtomicFileWriter.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存方案失败");
                throw;
            }
        }

        private void BackupCorruptProfileFile(string filePath, Exception loadException)
        {
            try
            {
                var backupPath = $"{filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Copy(filePath, backupPath, overwrite: false);
                _logger.LogWarning(loadException, "加载方案失败，已备份损坏文件: {BackupPath}", backupPath);
            }
            catch (Exception backupException)
            {
                _logger.LogWarning(loadException, "加载方案失败，且损坏文件备份失败: {Path}", filePath);
                _logger.LogWarning(backupException, "损坏方案文件备份失败");
            }
        }

        // ─── 工具 ───
        // DeterminePackageKind 已下沉到 Core 的
        // UEModManager.Services.Profile.LegacyProfileMigrator.DetermineKind，
        // 通过 LegacyModEntry 投影解耦 WPF 类型。
    }
}
