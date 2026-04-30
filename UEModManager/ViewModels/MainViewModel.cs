using System;
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
    public partial class MainViewModel : ObservableObject
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
        private readonly Adapters.HostAdapterRegistry _adapterRegistry;
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
        public Adapters.HostAdapterRegistry AdapterRegistry => _adapterRegistry;
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
            Adapters.HostAdapterRegistry adapterRegistry,
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
            _adapterRegistry = adapterRegistry;
            _configMergeEngine = configMergeEngine;
            _resolvedViewBuilder = resolvedViewBuilder;
            _launchOrchestrator = launchOrchestrator;
            _logger = logger;

            ModList = new ModListViewModel(modService, gameConfig, logger);
            ModDetail = new ModDetailViewModel(modService, gameConfig, modData, logger);
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
            try
            {
                IsLoading = true;
                LoadingMessage = "加载配置...";

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

                    await RefreshModsAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化失败");
            }
            finally
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
            }
        }

        /// <summary>
        /// 刷新 MOD 列表，并同步到当前 Profile。
        /// </summary>
        [RelayCommand]
        public async Task RefreshModsAsync()
        {
            try
            {
                IsLoading = true;
                LoadingMessage = "扫描MOD...";

                // 初始化数据服务
                await _modData.SetCurrentGameAsync(_gameConfig.CurrentGameName);
                await _categoryService.SetCurrentGameAsync(_gameConfig.CurrentGameName);

                // 扫描文件系统
                var mods = await _modService.ScanModsAsync(
                    _gameConfig.CurrentModPath,
                    _gameConfig.CurrentBackupPath);

                // 更新 AllMods
                Application.Current.Dispatcher.Invoke(() =>
                {
                    AllMods.Clear();
                    foreach (var mod in mods)
                        AllMods.Add(mod);

                    // 更新子 ViewModel
                    ModList.SetSource(AllMods);
                    Categories.UpdateCounts(AllMods);
                });

                // 同步到当前 Profile
                await _profileService.SyncPackagesAsync(mods);
                UpdateProfileSummary();

                // 保存元数据
                await _modData.SaveModsAsync(AllMods);

                UpdateStatusBar();
                _logger.LogInformation("MOD 刷新完成: {Count} 个", mods.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新 MOD 失败");
            }
            finally
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
            }
        }

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

            await RefreshModsAsync();
        }

        /// <summary>
        /// 启动游戏。
        /// </summary>
        [RelayCommand]
        public void LaunchGame()
        {
            _gameConfig.LaunchGame();
        }

        // ─── MOD 操作 ───

        /// <summary>
        /// 导入 MOD。
        /// </summary>
        [RelayCommand]
        public async Task ImportModAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = _gameConfig.CurrentEngineProfile.FileDialogFilter,
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                IsLoading = true;
                LoadingMessage = "导入MOD...";

                try
                {
                    var count = await _modService.ImportModsAsync(
                        dialog.FileNames,
                        _gameConfig.CurrentModPath,
                        _gameConfig.CurrentBackupPath);

                    if (count > 0)
                        await RefreshModsAsync();

                    _logger.LogInformation("导入了 {Count} 个 MOD", count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "导入 MOD 失败");
                }
                finally
                {
                    IsLoading = false;
                    LoadingMessage = string.Empty;
                }
            }
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
        /// 通过文件路径导入 MOD（拖拽导入）。
        /// </summary>
        public async Task ImportModsAsync(string[] filePaths)
        {
            IsLoading = true;
            LoadingMessage = "导入MOD...";
            try
            {
                var count = await _modService.ImportModsAsync(
                    filePaths,
                    _gameConfig.CurrentModPath,
                    _gameConfig.CurrentBackupPath);
                if (count > 0)
                    await RefreshModsAsync();
                _logger.LogInformation("拖拽导入了 {Count} 个 MOD", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "拖拽导入 MOD 失败");
            }
            finally
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
            }
        }

        // ─── 部署操作（v2.0 Phase 3） ───

        /// <summary>
        /// 通过部署层切换 MOD 启用/禁用状态。
        /// 生成精简部署计划并执行。
        /// </summary>
        public async Task<bool> DeployToggleAsync(string packageKey, bool enable)
        {
            try
            {
                IsLoading = true;
                LoadingMessage = enable ? "启用中..." : "禁用中...";

                var plan = await _deploymentPlanner.CreateTogglePlanAsync(packageKey, enable);
                if (!plan.HasChanges)
                {
                    _logger.LogInformation("无需部署变更: {Key} (enable={Enable})", packageKey, enable);
                    return true;
                }

                var transaction = await _deploymentService.ExecuteAsync(plan);
                if (transaction.Status == DeploymentStatus.Committed)
                {
                    // 更新 Profile 中的启用状态（仅元数据 — 部署事务已成功）
                    await _profileService.SetPackageEnabledFlagAsync(packageKey, enable);
                    _logger.LogInformation("部署成功: {Key} → {State}", packageKey, enable ? "启用" : "禁用");
                    return true;
                }

                _logger.LogWarning("部署失败: {Key}, 错误: {Error}",
                    packageKey, transaction.ErrorMessage);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "部署切换失败: {Key}", packageKey);
                return false;
            }
            finally
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
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
            try
            {
                IsLoading = true;
                LoadingMessage = "部署中...";

                var transaction = await _deploymentService.ExecuteAsync(plan);
                if (transaction.Status == DeploymentStatus.Committed)
                {
                    _logger.LogInformation("完整部署成功: {Count} 个操作", plan.TotalCount);
                    await RefreshModsAsync();
                }

                return transaction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行部署失败");
                return null;
            }
            finally
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
            }
        }

        // ─── 冲突分析（v2.0 Phase 4） ───

        /// <summary>
        /// 执行冲突分析，返回分析结果。
        /// </summary>
        public async Task<ConflictAnalysisResult?> AnalyzeConflictsAsync()
        {
            try
            {
                IsLoading = true;
                LoadingMessage = "分析冲突...";

                var result = await _conflictAnalyzer.AnalyzeAsync();
                _logger.LogInformation("冲突分析完成: {Count} 个冲突", result.TotalConflicts);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "冲突分析失败");
                return null;
            }
            finally
            {
                IsLoading = false;
                LoadingMessage = string.Empty;
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
    }
}
