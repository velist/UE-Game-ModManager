using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Localization;
using UEModManager.Models;
using UEModManager.Services.Persistence;
using UEModManager.Services.Profile;

namespace UEModManager.Services
{
    /// <summary>
    /// 游戏方案管理服务。
    /// 负责 Profile 的 CRUD、持久化、切换和数据迁移。
    ///
    /// ── 并发模型 ──
    /// UI 允许并发触发（导入 / 部署 / 切换方案可能同时进行），因此这里有三条硬规则：
    ///
    /// 1. **所有"改内存 + 落盘"的复合操作由 <see cref="_gate"/> 串行化。**
    ///    只锁落盘是不够的——那样两个线程仍可能同时改 <c>_profiles</c>，
    ///    枚举方一侧照样抛 InvalidOperationException。
    ///
    /// 2. **结构性修改一律"改副本再整体换引用"（swap-on-write）。**
    ///    读取方（<see cref="GetProfiles"/> 等）不加锁，只读一次字段引用再枚举，
    ///    因此永远不会枚举到一个正在被增删的列表。这既消除了枚举异常，
    ///    也避免了读取方去抢写锁——见规则 3 为什么这一点是必须的。
    ///
    /// 3. **事件一律在释放锁之后触发。**
    ///    <c>ProfileListChanged</c> 的订阅方（MainViewModel / MainWindow）内部是
    ///    <c>Dispatcher.Invoke</c>（**阻塞**调用），且回调里又会读 <see cref="GetProfiles"/>。
    ///    若持锁触发事件，后台线程会持锁等 UI 线程，而 UI 线程一旦需要本服务就会等锁 —— 直接死锁。
    ///    所以锁内只决定"要不要通知"，通知动作出锁后再做。
    /// </summary>
    public class ProfileService : IProfileQuery
    {
        private readonly ILogger<ProfileService> _logger;
        private readonly string _dataDir;

        /// <summary>串行化"改内存 + 落盘"。注意 SemaphoreSlim 不可重入，锁内不得调用任何也要抢锁的方法。</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>批处理嵌套深度（受 <see cref="_gate"/> 保护）。&gt;0 时写操作只标脏不落盘。</summary>
        private int _batchDepth;

        /// <summary>批处理期间是否累计了未落盘的修改（受 <see cref="_gate"/> 保护）。</summary>
        private bool _pendingSave;

        /// <summary>
        /// 实际落盘次数。仅供测试断言"批量期间只写一次"，以及排查写放大时看一眼。
        /// </summary>
        internal int PersistCount { get; private set; }

        /// <summary>当前游戏名称。</summary>
        private string _currentGameName = string.Empty;

        /// <summary>
        /// 当前游戏的所有 Profile。
        /// **只整体替换，不原地增删**（swap-on-write），使无锁读取方的枚举始终安全。
        /// </summary>
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
            : this(logger, AppPaths.DataDirectory)
        {
        }

        /// <summary>
        /// 指定数据目录的构造函数（测试用）。DI 走上面的单参数构造函数——
        /// 容器无法解析 string，不会误选此重载。与 <see cref="NewCategoryService"/> /
        /// <see cref="GameConfigService"/> / <see cref="OverwriteStore"/> 的处理一致：
        /// 数据目录归口 AppPaths 之后，测试若不注入位置就会写进开发者真实的 %LOCALAPPDATA%。
        ///
        /// <para>
        /// 本类此前漏了这个重载，三个 ProfileService 测试类因此一直在往真实
        /// <c>%LOCALAPPDATA%\UEModManager\Data</c> 里写 <c>UEMM*_profiles.json</c>，
        /// 而它们的 Dispose 删的是早已不再使用的"测试宿主进程目录\Data"——
        /// 于是每跑一次测试就在开发者的真实数据目录里多留一批孤儿文件，永不清理。
        /// </para>
        /// </summary>
        public ProfileService(ILogger<ProfileService> logger, string dataDirectory)
        {
            _logger = logger;
            _dataDir = dataDirectory;
            AppPaths.TryEnsureDirectory(_dataDir);
        }

        // ─── 批处理 ───

        /// <summary>
        /// 开启批处理作用域：作用域内的写操作只改内存、标脏，不落盘；
        /// 作用域全部结束（支持嵌套）时统一落盘一次。
        ///
        /// 用于"全部启用/禁用"这类批量操作——原先每个 MOD 都会触发一次完整的
        /// profiles JSON 序列化 + 原子写，100 个 MOD 就是 100 次全量写盘。
        ///
        /// ⚠ 作用域**不持锁**：它只是在锁内把计数 +1 就立刻放锁。
        /// 这样作用域存续期间的每个写操作仍各自正常抢锁，不会自锁。
        /// </summary>
        public async Task<IAsyncDisposable> BeginBatchAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _batchDepth++; }
            finally { _gate.Release(); }
            return new BatchScope(this);
        }

        private async Task EndBatchAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_batchDepth > 0) _batchDepth--;
                if (_batchDepth == 0 && _pendingSave)
                {
                    _pendingSave = false;
                    await PersistAsync().ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
        }

        /// <summary>批处理作用域句柄。重复 Dispose 是安全的。</summary>
        private sealed class BatchScope(ProfileService owner) : IAsyncDisposable
        {
            private bool _disposed;

            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;
                await owner.EndBatchAsync().ConfigureAwait(false);
            }
        }

        // ─── 初始化 ───

        /// <summary>
        /// 设置当前游戏并加载其 Profile 列表。
        /// 如果没有 Profile，自动创建"默认配置"。
        /// </summary>
        public async Task SetCurrentGameAsync(string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return;

            InstanceProfile? current;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _currentGameName = gameName;
                await LoadProfilesLockedAsync().ConfigureAwait(false);

                // 自动创建默认 Profile（首次使用或数据迁移）
                if (_profiles.Count == 0)
                {
                    await CreateDefaultProfileLockedAsync(gameName).ConfigureAwait(false);
                }

                // 确保有一个活跃 Profile
                CurrentProfile = _profiles.FirstOrDefault(p => p.IsActive)
                              ?? _profiles.FirstOrDefault();

                if (CurrentProfile != null && !CurrentProfile.IsActive)
                {
                    CurrentProfile.IsActive = true;
                    await SaveOrDeferLockedAsync().ConfigureAwait(false);
                }

                current = CurrentProfile;
            }
            finally { _gate.Release(); }

            ProfileChanged?.Invoke(current);
        }

        // ─── CRUD ───

        /// <summary>
        /// 获取当前游戏的所有 Profile。
        /// 不加锁：读一次字段引用即可，写入方永远换新列表而不原地增删（见类注释规则 2）。
        /// </summary>
        public IReadOnlyList<InstanceProfile> GetProfiles() => _profiles.AsReadOnly();

        /// <summary>按 ID 查找方案（IProfileQuery 实现）。</summary>
        public InstanceProfile? FindProfile(Guid profileId)
            => _profiles.FirstOrDefault(p => p.Id == profileId);

        /// <summary>
        /// 创建新 Profile。
        /// </summary>
        public async Task<InstanceProfile> CreateProfileAsync(string name, string? description = null,
            string? iconName = null, string? iconColor = null,
            IReadOnlyList<ProfilePackageEntry>? packages = null,
            IReadOnlyDictionary<string, string>? conflictOverrides = null,
            DeploymentBackendType backendType = DeploymentBackendType.Copy)
        {
            InstanceProfile profile;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 在锁内构造：HostGameName 是 init-only，且 _currentGameName 本身要在锁内读
                profile = new InstanceProfile
                {
                    HostGameName = _currentGameName,
                    Name = name,
                    Description = description,
                    IconName = iconName ?? "shield",
                    IconColor = iconColor ?? "#06b6d4",
                    IsActive = false,
                    BackendType = backendType,
                    Packages = packages?.Select(CloneEntry).ToList() ?? [],
                    ConflictOverrides = conflictOverrides == null
                        ? new(StringComparer.OrdinalIgnoreCase)
                        : new(conflictOverrides, StringComparer.OrdinalIgnoreCase)
                };

                _profiles = [.. _profiles, profile];
                await SaveOrDeferLockedAsync().ConfigureAwait(false);
                _logger.LogInformation("创建方案 '{Name}' (游戏: {Game})", name, _currentGameName);
            }
            finally { _gate.Release(); }

            ProfileListChanged?.Invoke();
            return profile;
        }

        /// <summary>
        /// 复制现有 Profile。
        /// </summary>
        public async Task<InstanceProfile> CloneProfileAsync(Guid sourceId, string newName)
        {
            InstanceProfile clone;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var source = _profiles.FirstOrDefault(p => p.Id == sourceId)
                    ?? throw new InvalidOperationException($"找不到方案: {sourceId}");

                clone = new InstanceProfile
                {
                    HostGameName = _currentGameName,
                    Name = newName,
                    Description = $"复制自「{source.Name}」",
                    IconName = source.IconName,
                    IconColor = source.IconColor,
                    IsActive = false,
                    BackendType = source.BackendType,
                    ConflictOverrides = new(source.ConflictOverrides, StringComparer.OrdinalIgnoreCase),
                    Packages = source.Packages
                        .Select(p => new ProfilePackageEntry
                        {
                            PackageKey = p.PackageKey,
                            IsEnabled = p.IsEnabled,
                            Priority = p.Priority,
                            Kind = p.Kind,
                            // 直接拷规范字段 TargetRootPath，而非旧别名 PluginTargetPath。
                            // 两者当前指向同一个后备字段（InstanceProfile.cs 的 PluginTargetPath
                            // 是 TargetRootPath 的 get/set 转发），所以行为不变；但一旦将来
                            // 别名被拆开或移除，写规范字段才不会静默丢掉 entry 级覆盖。
                            TargetRootPath = p.TargetRootPath
                        })
                        .ToList()
                };

                _profiles = [.. _profiles, clone];
                await SaveOrDeferLockedAsync().ConfigureAwait(false);
                _logger.LogInformation("复制方案 '{Source}' → '{New}'", source.Name, newName);
            }
            finally { _gate.Release(); }

            ProfileListChanged?.Invoke();
            return clone;
        }

        /// <summary>
        /// 删除 Profile。不允许删除最后一个。
        /// </summary>
        public async Task<bool> DeleteProfileAsync(Guid profileId)
        {
            bool activeChanged;
            InstanceProfile? current;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_profiles.Count <= 1)
                {
                    _logger.LogWarning("不能删除最后一个方案");
                    return false;
                }

                var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
                if (profile == null) return false;

                bool wasActive = profile.IsActive;
                _profiles = _profiles.Where(p => p.Id != profileId).ToList();

                // 如果删除的是活跃方案，切换到第一个
                activeChanged = wasActive && _profiles.Count > 0;
                if (activeChanged)
                {
                    _profiles[0].IsActive = true;
                    CurrentProfile = _profiles[0];
                }

                await SaveOrDeferLockedAsync().ConfigureAwait(false);
                _logger.LogInformation("删除方案 '{Name}'", profile.Name);
                current = CurrentProfile;
            }
            finally { _gate.Release(); }

            if (activeChanged) ProfileChanged?.Invoke(current);
            ProfileListChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// 重命名 Profile。
        /// </summary>
        public async Task RenameProfileAsync(Guid profileId, string newName)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
                if (profile == null) return;

                profile.Name = newName;
                profile.LastModified = DateTime.Now;
                await SaveOrDeferLockedAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }

            ProfileListChanged?.Invoke();
        }

        /// <summary>
        /// 切换到指定 Profile。
        /// </summary>
        public async Task SwitchProfileAsync(Guid profileId)
        {
            InstanceProfile? current;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var target = _profiles.FirstOrDefault(p => p.Id == profileId);
                if (target == null) return;

                // 取消所有活跃状态
                foreach (var p in _profiles)
                    p.IsActive = false;

                target.IsActive = true;
                CurrentProfile = target;
                current = target;

                await SaveOrDeferLockedAsync().ConfigureAwait(false);
                _logger.LogInformation("切换到方案 '{Name}'", target.Name);
            }
            finally { _gate.Release(); }

            ProfileChanged?.Invoke(current);
        }

        // ─── 包管理 ───

        /// <summary>
        /// 同步 MOD 列表到当前 Profile。
        /// 新扫描到的包自动加入；已删除的包自动移除。
        /// </summary>
        public async Task SyncPackagesAsync(IReadOnlyList<ModInfo> scannedMods)
        {
            var snapshot = scannedMods
                .Select(m => new LegacyModEntry(m.RealName, m.IsEnabled, m.IsPlugin,
                    string.IsNullOrEmpty(m.PluginTargetPath) ? null : m.PluginTargetPath))
                .ToList();

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CurrentProfile == null) return;

                var sync = ProfileSyncPlanner.ComputeSync(CurrentProfile, snapshot);

                // 整体换列表而不是 Clear + AddRange：后者会让正在枚举 Packages 的
                // 读取方（如 InstanceProfile.EnabledCount）抛 InvalidOperationException。
                CurrentProfile.Packages = [.. sync.Packages];
                CurrentProfile.LastModified = DateTime.Now;

                _logger.LogDebug("Profile 同步: +{Added} -{Removed} ~{Updated}",
                    sync.Added, sync.Removed, sync.Updated);

                await SaveOrDeferLockedAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
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
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CurrentProfile == null) return;

                var entry = CurrentProfile.Packages.FirstOrDefault(p =>
                    p.PackageKey.Equals(packageKey, StringComparison.OrdinalIgnoreCase));

                if (entry == null) return;
                if (entry.IsEnabled == enabled) return;

                entry.IsEnabled = enabled;
                CurrentProfile.LastModified = DateTime.Now;
                await SaveOrDeferLockedAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task AddPackagesToCurrentProfileAsync(IEnumerable<Package> packages)
        {
            var incoming = packages.ToList();
            InstanceProfile? current;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CurrentProfile == null) return;

                var existing = new HashSet<string>(
                    CurrentProfile.Packages.Select(p => p.PackageKey),
                    StringComparer.OrdinalIgnoreCase);

                var priority = CurrentProfile.Packages.Count == 0
                    ? 0
                    : CurrentProfile.Packages.Max(p => p.Priority) + 1;

                var appended = new List<ProfilePackageEntry>();
                foreach (var package in incoming)
                {
                    if (!existing.Add(package.PackageKey))
                        continue;

                    appended.Add(new ProfilePackageEntry
                    {
                        PackageKey = package.PackageKey,
                        IsEnabled = false,
                        Priority = priority++,
                        Kind = package.Kind,
                        TargetRootPath = package.TargetRootPath
                    });
                }

                if (appended.Count == 0) return;

                // swap-on-write：换新列表而不是原地 Add
                CurrentProfile.Packages = [.. CurrentProfile.Packages, .. appended];
                CurrentProfile.LastModified = DateTime.Now;
                await SaveOrDeferLockedAsync().ConfigureAwait(false);
                current = CurrentProfile;
            }
            finally { _gate.Release(); }

            ProfileChanged?.Invoke(current);
        }

        public async Task RemovePackageReferencesAsync(string packageKey)
        {
            InstanceProfile? current;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var changed = false;
                foreach (var profile in _profiles)
                {
                    var remaining = profile.Packages
                        .Where(p => !p.PackageKey.Equals(packageKey, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (remaining.Count == profile.Packages.Count) continue;

                    // swap-on-write：换新列表而不是原地 RemoveAll
                    profile.Packages = remaining;
                    profile.LastModified = DateTime.Now;
                    changed = true;
                }

                if (!changed) return;

                await SaveOrDeferLockedAsync().ConfigureAwait(false);
                current = CurrentProfile;
            }
            finally { _gate.Release(); }

            ProfileListChanged?.Invoke();
            ProfileChanged?.Invoke(current);
        }

        /// <summary>
        /// [已废弃] 旧名 — 转发到 <see cref="SetPackageEnabledFlagAsync"/>。
        /// 保留供二进制兼容；新代码请用新名。
        /// </summary>
        [Obsolete("方法已重命名为 SetPackageEnabledFlagAsync 以明确仅元数据不部署的语义。请改用 DeployToggleAsync 或 SetPackageEnabledFlagAsync。")]
        public Task SetPackageEnabledAsync(string packageKey, bool enabled)
            => SetPackageEnabledFlagAsync(packageKey, enabled);

        // ─── 数据迁移 ───

        /// <summary>只修改指定方案的覆盖规则；写入失败恢复内存，避免显示已保存的假象。</summary>
        public async Task SetConflictOverrideAsync(Guid profileId, string targetPath, string? winnerPackageKey)
            => await UpdateConflictOverridesAsync(profileId, rules =>
            {
                if (winnerPackageKey == null) rules.Remove(targetPath);
                else rules[targetPath] = winnerPackageKey;
            });

        internal async Task UpdateConflictOverridesAsync(Guid profileId, Action<Dictionary<string, string>> update)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var profile = _profiles.FirstOrDefault(p => p.Id == profileId)
                    ?? throw new InvalidOperationException($"找不到方案: {profileId}");
                var oldOverrides = profile.ConflictOverrides;
                var oldModified = profile.LastModified;
                var updated = new Dictionary<string, string>(oldOverrides, StringComparer.OrdinalIgnoreCase);
                update(updated);
                profile.ConflictOverrides = updated;
                profile.LastModified = DateTime.Now;
                try { await SaveOrDeferLockedAsync().ConfigureAwait(false); }
                catch
                {
                    profile.ConflictOverrides = oldOverrides;
                    profile.LastModified = oldModified;
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        public async Task ReplaceConflictOverridesAsync(Guid profileId, IReadOnlyDictionary<string, string> overrides)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var profile = _profiles.FirstOrDefault(p => p.Id == profileId)
                    ?? throw new InvalidOperationException($"找不到方案: {profileId}");
                var oldOverrides = profile.ConflictOverrides;
                var oldModified = profile.LastModified;
                profile.ConflictOverrides = new(overrides, StringComparer.OrdinalIgnoreCase);
                profile.LastModified = DateTime.Now;
                try { await SaveOrDeferLockedAsync().ConfigureAwait(false); }
                catch
                {
                    profile.ConflictOverrides = oldOverrides;
                    profile.LastModified = oldModified;
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        private static ProfilePackageEntry CloneEntry(ProfilePackageEntry entry) => new()
        {
            PackageKey = entry.PackageKey,
            IsEnabled = entry.IsEnabled,
            Priority = entry.Priority,
            Kind = entry.Kind,
            TargetRootPath = entry.TargetRootPath
        };

        private async Task<Dictionary<string, string>> ReadLegacyOverridesAsync()
        {
            var legacyPath = Path.Combine(_dataDir, $"{_currentGameName}_conflict_overrides.json");
            if (!File.Exists(legacyPath)) return new(StringComparer.OrdinalIgnoreCase);
            var json = await File.ReadAllTextAsync(legacyPath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? throw new InvalidDataException($"旧版冲突覆盖规则无法读取: {legacyPath}");
        }

        /// <summary>
        /// 从现有 MOD 列表创建默认 Profile（v1.8 → v2.0 数据迁移）。
        /// 调用方必须已持有 <see cref="_gate"/>。
        /// </summary>
        private async Task CreateDefaultProfileLockedAsync(string gameName)
        {
            var profile = new InstanceProfile
            {
                HostGameName = gameName,
                Name = UiText.Get("默认 MOD 方案"),
                Description = UiText.Get("自动创建的默认 MOD 配置方案"),
                IconName = "shield",
                IconColor = "#06b6d4",
                IsActive = true,
                ConflictOverrides = await ReadLegacyOverridesAsync().ConfigureAwait(false)
            };

            // 尝试从现有 MOD 数据迁移
            var modsFilePath = Path.Combine(_dataDir, $"{gameName}_mods.json");
            if (File.Exists(modsFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(modsFilePath).ConfigureAwait(false);
                    var mods = JsonSerializer.Deserialize<List<ModInfo>>(json);
                    if (mods != null)
                    {
                        var legacy = mods
                            .Select(m => new LegacyModEntry(m.RealName, m.IsEnabled, m.IsPlugin,
                                string.IsNullOrEmpty(m.PluginTargetPath) ? null : m.PluginTargetPath))
                            .ToList();
                        profile.Packages = [.. LegacyProfileMigrator.BuildPackagesFromLegacyMods(legacy)];
                        _logger.LogInformation("从 v1.8 数据迁移了 {Count} 个包到默认方案", mods.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取旧版 MOD 数据失败，创建空方案");
                }
            }

            _profiles = [.. _profiles, profile];
            await SaveOrDeferLockedAsync().ConfigureAwait(false);

            _logger.LogInformation("已创建默认方案 (游戏: {Game})", gameName);
        }

        // ─── 持久化 ───

        private string GetProfileFilePath() =>
            Path.Combine(_dataDir, $"{_currentGameName}_profiles.json");

        /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
        private async Task LoadProfilesLockedAsync()
        {
            var filePath = GetProfileFilePath();
            if (!File.Exists(filePath))
            {
                _profiles = [];
                return;
            }

            List<Guid> needsMigration;
            try
            {
                var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                _profiles = JsonSerializer.Deserialize<List<InstanceProfile>>(json, JsonOptions) ?? [];
                using var document = JsonDocument.Parse(json);
                needsMigration = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().Zip(_profiles)
                        .Where(pair => !pair.First.TryGetProperty("conflictOverrides", out var value)
                            || value.ValueKind == JsonValueKind.Null)
                        .Select(pair => pair.Second.Id).ToList()
                    : [];
                _logger.LogInformation("加载了 {Count} 个方案 (游戏: {Game})",
                    _profiles.Count, _currentGameName);
            }
            catch (Exception ex)
            {
                BackupCorruptProfileFile(filePath, ex);
                _profiles = [];
                return;
            }

            if (needsMigration.Count == 0) return;
            // 每个旧方案此前都使用游戏级规则，所以各自复制一份。新方案有明确空字段，不继承旧文件。
            // 旧文件保留；只有原子保存成功后，磁盘上的字段存在性才成为迁移完成标记。
            var legacy = await ReadLegacyOverridesAsync().ConfigureAwait(false);
            var migrating = _profiles.Where(p => needsMigration.Contains(p.Id)).ToList();
            var previous = migrating.ToDictionary(p => p.Id, p => p.ConflictOverrides);
            foreach (var profile in migrating) profile.ConflictOverrides = legacy;
            try { await PersistAsync().ConfigureAwait(false); }
            catch
            {
                foreach (var profile in migrating) profile.ConflictOverrides = previous[profile.Id];
                throw;
            }
        }

        /// <summary>
        /// 落盘，或在批处理作用域内仅标脏。调用方必须已持有 <see cref="_gate"/>。
        /// </summary>
        private async Task SaveOrDeferLockedAsync()
        {
            if (_batchDepth > 0)
            {
                _pendingSave = true;
                return;
            }
            await PersistAsync().ConfigureAwait(false);
        }

        /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
        private async Task PersistAsync()
        {
            try
            {
                var filePath = GetProfileFilePath();
                var json = JsonSerializer.Serialize(_profiles, JsonOptions);
                await AtomicFileWriter.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
                PersistCount++;
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
