using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Converters;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Conflict;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel — 协调所有子 ViewModel。
    /// </summary>
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly ModManagementService _modService;
        private readonly GameConfigService _gameConfig;
        private readonly ModDataService _modData;
        private readonly NewCategoryService _categoryService;
        private readonly ProfileService _profileService;
        private readonly PackageRepository _packageRepository;
        private readonly PackageImportService _packageImportService;
        private readonly DataMigrationService _dataMigrationService;
        private readonly DeploymentPlanner _deploymentPlanner;
        private readonly DeploymentService _deploymentService;
        private readonly ConflictAnalyzer _conflictAnalyzer;
        private readonly OverwriteStore _overwriteStore;
        private readonly Services.Config.ConfigMergeEngine _configMergeEngine;
        private readonly ResolvedViewBuilder _resolvedViewBuilder;
        private readonly LaunchOrchestrator _launchOrchestrator;
        private readonly ILogger<MainViewModel> _logger;

        // ─── 服务访问器（供 code-behind 使用） ───

        public ModManagementService ModService => _modService;
        public GameConfigService GameConfig => _gameConfig;
        public ModDataService ModData => _modData;
        public NewCategoryService CategoryService => _categoryService;
        public ProfileService ProfileService => _profileService;
        public PackageRepository PackageRepo => _packageRepository;
        public PackageImportService PackageImport => _packageImportService;
        public DataMigrationService DataMigration => _dataMigrationService;
        public DeploymentPlanner DeployPlanner => _deploymentPlanner;
        public DeploymentService DeployService => _deploymentService;
        public ConflictAnalyzer ConflictAnalysis => _conflictAnalyzer;
        public OverwriteStore OverwriteStore => _overwriteStore;
        public Services.Config.ConfigMergeEngine ConfigMerge => _configMergeEngine;
        public ResolvedViewBuilder ViewBuilder => _resolvedViewBuilder;
        public LaunchOrchestrator Launcher => _launchOrchestrator;

        // ─── 子 ViewModel ───

        public ModListViewModel ModList { get; }
        public ModDetailViewModel ModDetail { get; }
        public CategoryViewModel Categories { get; }

        // ─── 状态属性 ───

        [ObservableProperty]
        private bool _isDetailPanelOpen;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _loadingMessage = string.Empty;

        /// <summary>
        /// 加载遮罩的引用计数深度。
        ///
        /// 原先每个操作各自 <c>IsLoading = true</c> / <c>finally { IsLoading = false; }</c>，
        /// 批量操作（"全部启用" N 个 MOD）会让遮罩翻转 N 次，视觉上就是闪烁 N 下；
        /// 嵌套调用（DeletePackageModCoreAsync 内部又调 DeployToggleAsync）还会让内层的
        /// finally 提前把外层的遮罩关掉。改成引用计数后，只有最外层结束时才真正收起遮罩。
        ///
        /// 只在 UI 线程上访问：ViewModel 的 await 都不带 ConfigureAwait(false)，
        /// 续体回到 WPF 的同步上下文，因此这里不需要 Interlocked。
        /// </summary>
        private int _loadingDepth;

        /// <summary>
        /// 进入一次加载状态，返回的句柄 Dispose 时退出。支持嵌套。
        /// </summary>
        private IDisposable BeginLoading(string message)
        {
            var previousMessage = LoadingMessage;
            _loadingDepth++;
            IsLoading = true;
            LoadingMessage = message;
            return new LoadingScope(this, previousMessage);
        }

        private sealed class LoadingScope(MainViewModel owner, string previousMessage) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (--owner._loadingDepth <= 0)
                {
                    owner._loadingDepth = 0;
                    owner.IsLoading = false;
                    owner.LoadingMessage = string.Empty;
                }
                else
                {
                    // 还有外层在跑，把提示词还原成外层的
                    owner.LoadingMessage = previousMessage;
                }
            }
        }

        [ObservableProperty]
        private string _currentGameName = string.Empty;

        [ObservableProperty]
        private string _userName = "未登录";

        [ObservableProperty]
        private string _userAvatar = string.Empty;

        [ObservableProperty]
        private bool _isLoggedIn;

        [ObservableProperty]
        private string _statusBarText = string.Empty;

        [ObservableProperty]
        private string _currentProfileName = "默认 MOD 方案";

        [ObservableProperty]
        private string _currentProfileSummary = string.Empty;

        /// <summary>
        /// 所有 MOD（未过滤的完整列表）。
        /// </summary>
        public ObservableCollection<ModInfo> AllMods { get; } = new();

        /// <summary>
        /// 当前游戏的所有 Profile 列表。
        /// </summary>
        public ObservableCollection<InstanceProfile> Profiles { get; } = new();

        public MainViewModel(
            ModManagementService modService,
            GameConfigService gameConfig,
            ModDataService modData,
            NewCategoryService categoryService,
            ProfileService profileService,
            PackageRepository packageRepository,
            PackageImportService packageImportService,
            DataMigrationService dataMigrationService,
            DeploymentPlanner deploymentPlanner,
            DeploymentService deploymentService,
            ConflictAnalyzer conflictAnalyzer,
            OverwriteStore overwriteStore,
            Services.Config.ConfigMergeEngine configMergeEngine,
            ResolvedViewBuilder resolvedViewBuilder,
            LaunchOrchestrator launchOrchestrator,
            ILogger<MainViewModel> logger)
        {
            _modService = modService;
            _gameConfig = gameConfig;
            _modData = modData;
            _categoryService = categoryService;
            _profileService = profileService;
            _packageRepository = packageRepository;
            _packageImportService = packageImportService;
            _dataMigrationService = dataMigrationService;
            _deploymentPlanner = deploymentPlanner;
            _deploymentService = deploymentService;
            _conflictAnalyzer = conflictAnalyzer;
            _overwriteStore = overwriteStore;
            _configMergeEngine = configMergeEngine;
            _resolvedViewBuilder = resolvedViewBuilder;
            _launchOrchestrator = launchOrchestrator;
            _logger = logger;

            ModList = new ModListViewModel(modService, gameConfig, logger);
            // ModListViewModel / ModDetailViewModel 的回调契约仍是 Task<bool>，
            // 这里把 OperationResult 降级成 bool 适配。失败原因由 View 层
            // （MainWindow 的 *FromUiAsync）直接从 OperationResult 取，不经过这条通道。
            ModList.ConfigureActions(
                async (mod, enable) => (await ToggleModAsync(mod, enable)).Success,
                async mod => (await DeletePackageModAsync(mod)).Success,
                async (mods, enable) => (await ToggleModsAsync(mods, enable)).Success,
                async mods => (await DeletePackageModsAsync(mods)).Success);
            ModDetail = new ModDetailViewModel(modService, gameConfig, modData, logger);
            ModDetail.ConfigureActions(
                async (mod, enable) => (await ToggleModAsync(mod, enable)).Success,
                async (mod, path) => (await ChangePreviewAsync(mod, path)).Success,
                async mod => (await DeletePackageModAsync(mod)).Success);
            Categories = new CategoryViewModel(categoryService, logger);

            // 连接子 ViewModel 事件
            ModList.ModSelected += mod =>
            {
                ModDetail.CurrentMod = mod;
                if (mod != null)
                    IsDetailPanelOpen = true;
            };

            ModList.ModsChanged += () =>
            {
                Categories.UpdateCounts(AllMods);
                UpdateStatusBar();
            };

            Categories.CategorySelected += category =>
            {
                ModList.ApplyFilter(category, ModList.SearchText);
            };

            // 监听 Profile 切换
            _profileService.ProfileChanged += OnProfileChanged;
            _profileService.ProfileListChanged += OnProfileListChanged;
        }

        // ─── Profile 事件处理 ───

        private void OnProfileChanged(InstanceProfile? profile)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentProfileName = profile?.Name ?? "未选择";
                UpdateProfileSummary();
            });
        }

        private void OnProfileListChanged()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Profiles.Clear();
                foreach (var p in _profileService.GetProfiles())
                    Profiles.Add(p);
            });
        }

        private void UpdateProfileSummary()
        {
            var profile = _profileService.CurrentProfile;
            if (profile == null)
            {
                CurrentProfileSummary = string.Empty;
                return;
            }
            CurrentProfileSummary = $"{profile.EnabledCount}/{profile.TotalCount} 已启用";
        }

        // ─── 初始化 ───

        /// <summary>
        /// 初始化：加载配置、加载方案、扫描 MOD。
        /// </summary>
        [RelayCommand]
        public async Task InitializeAsync()
        {
            using var loading = BeginLoading("加载配置...");
            try
            {

                await _gameConfig.LoadConfigAsync();
                CurrentGameName = _gameConfig.CurrentGameName;
                _modService.CurrentGameType = _gameConfig.CurrentGameType;
                _modService.CurrentEngineType = _gameConfig.CurrentEngineType;

                if (!string.IsNullOrEmpty(CurrentGameName))
                {
                    // 加载 Profile
                    LoadingMessage = "加载方案...";
                    await _profileService.SetCurrentGameAsync(CurrentGameName);
                    OnProfileListChanged();
                    OnProfileChanged(_profileService.CurrentProfile);

                    // v2.0: 初始化包仓库
                    LoadingMessage = "加载包仓库...";
                    await _packageRepository.SetCurrentGameAsync(CurrentGameName);

                    // v2.0: 初始化冲突分析器
                    await _conflictAnalyzer.SetCurrentGameAsync(CurrentGameName);
                    await _overwriteStore.SetCurrentGameAsync(CurrentGameName);

                    // v2.0: 检查并执行数据迁移
                    if (_dataMigrationService.NeedsMigration(CurrentGameName))
                    {
                        LoadingMessage = "迁移旧数据到 v2.0 格式...";
                        _logger.LogInformation("检测到需要数据迁移: {Game}", CurrentGameName);
                        var result = await _dataMigrationService.MigrateAsync(CurrentGameName);
                        if (result.Success)
                            _logger.LogInformation("数据迁移完成: 迁移 {Count} 个包", result.MigratedPackages);
                        else
                            _logger.LogWarning("数据迁移部分失败: {Error}", result.ErrorMessage);
                    }

                    await RefreshFromRepositoryAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化失败");
            }
        }

        /// <summary>
        /// 刷新 MOD 列表。v2.0 的主数据源是 PackageRepository/Profile，不再用目录扫描覆盖方案状态。
        /// </summary>
        [RelayCommand]
        public Task RefreshModsAsync() => RefreshFromRepositoryAsync();

        // ─── 游戏操作 ───

        /// <summary>
        /// 切换游戏。
        /// </summary>
        [RelayCommand]
        public async Task SwitchGameAsync(string gameName)
        {
            CurrentGameName = gameName;
            _modService.CurrentGameType = _gameConfig.CurrentGameType;
            _modService.CurrentEngineType = _gameConfig.CurrentEngineType;

            // 切换游戏时清空图片缓存
            AsyncImageConverter.ClearCache();

            // 加载目标游戏的 Profile
            await _profileService.SetCurrentGameAsync(gameName);
            OnProfileListChanged();
            OnProfileChanged(_profileService.CurrentProfile);

            // v2.0: 加载包仓库
            await _packageRepository.SetCurrentGameAsync(gameName);

            // v2.0: 冲突分析器
            await _conflictAnalyzer.SetCurrentGameAsync(gameName);
            await _overwriteStore.SetCurrentGameAsync(gameName);

            // v2.0: 数据迁移（如需要）
            if (_dataMigrationService.NeedsMigration(gameName))
            {
                _logger.LogInformation("切换游戏时检测到需要数据迁移: {Game}", gameName);
                await _dataMigrationService.MigrateAsync(gameName);
            }

            await RefreshFromRepositoryAsync();
        }

        /// <summary>
        /// 启动游戏。
        /// </summary>
        [RelayCommand]
        public void LaunchGame()
        {
            _gameConfig.LaunchGame();
        }

        public async Task RefreshFromRepositoryAsync()
        {
            using var loading = BeginLoading("刷新仓库...");
            try
            {

                var profile = _profileService.CurrentProfile;
                var profileEntries = profile?.Packages.ToDictionary(p => p.PackageKey, StringComparer.OrdinalIgnoreCase)
                                     ?? new Dictionary<string, ProfilePackageEntry>(StringComparer.OrdinalIgnoreCase);
                var packages = _packageRepository.GetAllPackages();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    AllMods.Clear();
                    foreach (var package in packages)
                    {
                        profileEntries.TryGetValue(package.PackageKey, out var entry);
                        AllMods.Add(new ModInfo
                        {
                            Name = package.DisplayName,
                            RealName = package.PackageKey,
                            Description = package.Note ?? string.Empty,
                            Categories = package.Tags.Count > 0 ? new List<string>(package.Tags) : new List<string> { "未分类" },
                            IsEnabled = entry?.IsEnabled ?? false,
                            IsPlugin = package.Kind == PackageKind.Plugin,
                            PluginTargetPath = package.PluginTargetPath ?? string.Empty,
                            PreviewImagePath = package.PreviewImagePath ?? string.Empty,
                            FileSize = package.TotalSize,
                            InstallDate = package.ImportedAt
                        });
                    }

                    ModList.SetSource(AllMods);
                    Categories.UpdateCounts(AllMods);
                });

                UpdateProfileSummary();
                await _modData.SaveModsAsync(AllMods);
                UpdateStatusBar();
                _logger.LogInformation("仓库刷新完成: {Count} 个", packages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新仓库失败");
            }
        }

        // ─── MOD 操作 ───

        /// <summary>
        /// 导入 MOD。
        ///
        /// 失败以异常形式上抛，由调用它的 SafeEvent.Run 弹出——命令方法不能返回
        /// OperationResult（[RelayCommand] 只认 Task），而把失败静静咽下去正是要修的问题。
        /// </summary>
        [RelayCommand]
        public async Task ImportModAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = _gameConfig.CurrentEngineProfile.FileDialogFilter,
                Multiselect = true
            };

            if (dialog.ShowDialog() != true) return;

            var result = await ImportModsAsync(dialog.FileNames);
            if (!result.Success && !result.IsCancelled)
                throw new InvalidOperationException(result.Error ?? OperationResult.DefaultError);
        }

        /// <summary>
        /// 切换详情面板。
        /// </summary>
        [RelayCommand]
        public void ToggleDetailPanel()
        {
            IsDetailPanelOpen = !IsDetailPanelOpen;
        }

        /// <summary>
        /// 通过文件路径导入 MOD。
        ///
        /// 返回 OperationResult 而不是 void：<see cref="PackageImportService.ImportAsync"/>
        /// 是"每个文件各自 try"的结构，失败不会抛，只在结果里留一条 ErrorMessage。
        /// 此前这里只统计成功个数、把失败原因整个丢掉，于是"仓库目录不可写"表现为
        /// 列表里凭空少了几个 MOD 且没有任何解释。
        /// </summary>
        public async Task<OperationResult> ImportModsAsync(string[] filePaths)
        {
            using var loading = BeginLoading("导入MOD...");
            try
            {
                var results = await _packageImportService.ImportAsync(filePaths);
                var importedPackages = results
                    .Where(r => r.Success && r.Package != null)
                    .Select(r => r.Package!)
                    .ToList();

                var outcomes = results
                    .Select(r => r.Success ? OperationResult.Ok() : OperationResult.Fail(r.ErrorMessage))
                    .ToList();

                await _profileService.AddPackagesToCurrentProfileAsync(importedPackages);
                if (UiPreferences.LoadAutoDeploy())
                {
                    // 自动部署同样是 N 次循环，套批处理避免 N 次全量写盘
                    await using (await _profileService.BeginBatchAsync())
                    {
                        foreach (var package in importedPackages)
                            outcomes.Add(await DeployToggleAsync(package.PackageKey, true));
                    }
                }

                await RefreshFromRepositoryAsync();
                _logger.LogInformation("导入了 {Count} 个包", importedPackages.Count);
                return OperationResult.Aggregate(outcomes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入 MOD 失败");
                return OperationResult.Fail(ex.Message);
            }
        }

        // ─── 部署操作（v2.0 Phase 3） ───

        /// <summary>
        /// 通过部署层切换 MOD 启用/禁用状态。
        /// 生成精简部署计划并执行。
        /// </summary>
        public async Task<OperationResult> DeployToggleAsync(string packageKey, bool enable)
        {
            using var loading = BeginLoading(enable ? "启用中..." : "禁用中...");
            try
            {

                var plan = await _deploymentPlanner.CreateTogglePlanAsync(packageKey, enable);
                if (!plan.HasChanges)
                {
                    await _profileService.SetPackageEnabledFlagAsync(packageKey, enable);
                    _logger.LogInformation("无需部署变更，已同步状态: {Key} (enable={Enable})", packageKey, enable);
                    return OperationResult.Ok();
                }

                var transaction = await _deploymentService.ExecuteAsync(plan);
                if (transaction.Status == DeploymentStatus.Committed)
                {
                    await _profileService.SetPackageEnabledFlagAsync(packageKey, enable);
                    _logger.LogInformation("部署成功: {Key} → {State}", packageKey, enable ? "启用" : "禁用");
                    return OperationResult.Ok();
                }

                _logger.LogWarning("部署失败: {Key}, 错误: {Error}",
                    packageKey, transaction.ErrorMessage);
                return OperationResult.Fail(transaction.ErrorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "部署切换失败: {Key}", packageKey);
                return OperationResult.Fail(ex.Message);
            }
        }

        public async Task<OperationResult> ToggleModAsync(ModInfo mod, bool enable)
        {
            var result = await DeployToggleAsync(mod.RealName, enable);
            if (!result.Success) return result;

            mod.IsEnabled = enable;
            await RefreshFromRepositoryAsync();
            return result;
        }

        public async Task<OperationResult> ToggleModsAsync(IReadOnlyList<ModInfo> mods, bool enable)
        {
            var changed = false;
            var results = new List<OperationResult>();

            // 外层遮罩：整批期间常亮，而不是每个 MOD 闪一下
            using var loading = BeginLoading(enable ? "批量启用..." : "批量禁用...");

            // 批处理：循环内每次 DeployToggleAsync 都会写一次 Profile 元数据，
            // 原先就是 N 次完整 profiles JSON 序列化 + 原子写。作用域内只标脏，结束时落盘一次。
            // 作用域在刷新之前结束，保证 UI 刷新看到的是已落盘的状态。
            await using (await _profileService.BeginBatchAsync())
            {
                foreach (var mod in mods)
                {
                    if (mod.IsEnabled == enable) continue;

                    var result = await DeployToggleAsync(mod.RealName, enable);
                    results.Add(result);
                    if (!result.Success) continue;

                    mod.IsEnabled = enable;
                    changed = true;
                }
            }

            if (changed)
                await RefreshFromRepositoryAsync();

            return OperationResult.Aggregate(results);
        }

        public async Task<OperationResult> RenameModAsync(ModInfo mod, string newName)
        {
            var package = _packageRepository.GetByKey(mod.RealName);
            if (package == null)
            {
                _logger.LogWarning("重命名失败，仓库中找不到包: {Key}", mod.RealName);
                return OperationResult.Fail($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。");
            }

            package.DisplayName = newName.Trim();
            try
            {
                await _packageRepository.UpdatePackageAsync(package);
            }
            catch (Exception ex)
            {
                // 索引/manifest 写失败现在会上抛。这里转成 OperationResult 而不是让它冒到
                // SafeEvent：同一个方法的其它失败分支都走 OperationResult，混着两种通道
                // 会让调用方无从判断"返回了就一定没抛"。
                _logger.LogError(ex, "重命名 MOD 失败: {Key}", mod.RealName);
                return OperationResult.Fail($"重命名「{mod.Name}」失败：{ex.Message}");
            }

            mod.Name = package.DisplayName;
            await RefreshFromRepositoryAsync();
            return OperationResult.Ok();
        }

        public async Task<OperationResult> ChangePreviewAsync(ModInfo mod, string imagePath)
        {
            string? storedPath;
            try
            {
                storedPath = await _packageRepository.UpdatePreviewImageAsync(mod.RealName, imagePath);
            }
            catch (Exception ex)
            {
                // ObjectStore 现在把真实原因抛上来（仓库目录不可写、磁盘满、源图被占用……），
                // 直接透传比原来那句"请确认图片文件仍然存在且可读取"的猜测有用得多。
                _logger.LogError(ex, "更换预览图失败: {Key} ← {Path}", mod.RealName, imagePath);
                return OperationResult.Fail($"预览图保存失败：{ex.Message}");
            }

            if (string.IsNullOrEmpty(storedPath))
            {
                _logger.LogWarning("更换预览图失败，仓库中找不到包: {Key}", mod.RealName);
                return OperationResult.Fail($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。");
            }

            mod.PreviewImage = null;
            mod.PreviewImagePath = storedPath;
            await RefreshFromRepositoryAsync();
            return OperationResult.Ok();
        }

        public async Task<OperationResult> DeletePackageModAsync(ModInfo mod)
        {
            var result = await DeletePackageModCoreAsync(mod);
            if (!result.Success) return result;

            await RefreshFromRepositoryAsync();
            return result;
        }

        public async Task<OperationResult> DeletePackageModsAsync(IReadOnlyList<ModInfo> mods)
        {
            var changed = false;
            var results = new List<OperationResult>();

            using var loading = BeginLoading("批量删除...");

            // 同 ToggleModsAsync：每次删除都会经 RemovePackageReferencesAsync 写一次 Profile
            await using (await _profileService.BeginBatchAsync())
            {
                foreach (var mod in mods)
                {
                    var result = await DeletePackageModCoreAsync(mod);
                    results.Add(result);
                    changed |= result.Success;
                }
            }

            if (changed)
                await RefreshFromRepositoryAsync();

            return OperationResult.Aggregate(results);
        }

        private async Task<OperationResult> DeletePackageModCoreAsync(ModInfo mod)
        {
            using var loading = BeginLoading("删除中...");
            try
            {

                var package = _packageRepository.GetByKey(mod.RealName);
                if (package == null)
                {
                    _logger.LogWarning("删除失败，仓库中找不到包: {Key}", mod.RealName);
                    return OperationResult.Fail($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。");
                }

                // 先卸载已部署的文件；这一步失败就不能继续删仓库，否则游戏目录里会留下孤儿文件
                var undeploy = await DeployToggleAsync(package.PackageKey, false);
                if (!undeploy.Success)
                    return OperationResult.Fail($"无法从游戏目录移除「{mod.Name}」的已部署文件，已中止删除。{Environment.NewLine}{undeploy.Error}");

                var (success, _) = await _packageRepository.DeletePackageAsync(
                    package.PackageKey, _profileService.GetProfiles(), force: true);
                if (!success)
                {
                    _logger.LogWarning("从仓库删除包失败: {Key}", package.PackageKey);
                    return OperationResult.Fail($"从包仓库删除「{mod.Name}」失败。文件可能被占用或权限不足。");
                }

                await _profileService.RemovePackageReferencesAsync(package.PackageKey);
                await _modData.RemoveModAsync(package.PackageKey);
                _logger.LogInformation("包已从仓库和所有方案删除: {Key}", package.PackageKey);
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除包失败: {Key}", mod.RealName);
                return OperationResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// 生成完整部署计划（用于 UI 预览）。
        /// </summary>
        public async Task<DeploymentPlan?> CreateFullDeploymentPlanAsync()
        {
            try
            {
                return await _deploymentPlanner.CreatePlanAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成部署计划失败");
                return null;
            }
        }

        /// <summary>
        /// 执行完整部署计划。
        /// </summary>
        public async Task<DeploymentTransaction?> ExecuteDeploymentAsync(DeploymentPlan plan)
        {
            using var loading = BeginLoading("部署中...");
            try
            {

                var transaction = await _deploymentService.ExecuteAsync(plan);
                if (transaction.Status == DeploymentStatus.Committed)
                {
                    _logger.LogInformation("完整部署成功: {Count} 个操作", plan.TotalCount);
                    await RefreshFromRepositoryAsync();
                }

                return transaction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行部署失败");
                return null;
            }
        }

        // ─── 冲突分析（v2.0 Phase 4） ───

        /// <summary>
        /// 执行冲突分析，返回分析结果。
        /// </summary>
        public async Task<ConflictAnalysisResult?> AnalyzeConflictsAsync()
        {
            using var loading = BeginLoading("分析冲突...");
            try
            {

                var result = await _conflictAnalyzer.AnalyzeAsync();
                _logger.LogInformation("冲突分析完成: {Count} 个冲突", result.TotalConflicts);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "冲突分析失败");
                return null;
            }
        }

        // ─── 内部方法 ───

        private void UpdateStatusBar()
        {
            var enabled = AllMods.Count(m => m.IsEnabled);
            var profileInfo = _profileService.CurrentProfile != null
                ? $" | 方案: {_profileService.CurrentProfile.Name}"
                : "";
            StatusBarText = $"已加载MOD: {enabled}/{AllMods.Count}{profileInfo}";
        }

        // ─── 释放 ───

        private bool _disposed;

        /// <summary>
        /// 退订对单例服务的事件订阅。
        /// 本类注册为 AddTransient，而 ProfileService 是 AddSingleton：
        /// 单例事件持有 ViewModel 的强引用，不退订则每解析一次 MainViewModel
        /// 就永久泄漏一个（连同它引用的全部子 ViewModel 与 MOD 集合），
        /// 且后续 Profile 变更会向所有僵尸实例重复派发。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _profileService.ProfileChanged -= OnProfileChanged;
            _profileService.ProfileListChanged -= OnProfileListChanged;
        }
    }
}
