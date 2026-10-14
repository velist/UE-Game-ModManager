using UEModManager.Localization;
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
using UEModManager.Services.Categories;
using UEModManager.Services.Conflict;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel — 协调所有子 ViewModel。
    /// </summary>
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly GameConfigService _gameConfig;
        private readonly NewCategoryService _categoryService;
        private readonly ProfileService _profileService;
        private readonly PackageRepository _packageRepository;
        private readonly PackageImportService _packageImportService;
        private readonly DataMigrationService _dataMigrationService;
        private readonly DeploymentPlanner _deploymentPlanner;
        private readonly DeploymentService _deploymentService;
        private readonly ConflictModuleService _conflictModule;
        private readonly OverwriteStore _overwriteStore;
        private readonly Services.Config.ConfigMergeEngine _configMergeEngine;
        private readonly ResolvedViewBuilder _resolvedViewBuilder;
        private readonly LaunchOrchestrator _launchOrchestrator;
        private readonly ILogger<MainViewModel> _logger;

        // ─── 服务访问器（供 code-behind 使用） ───

        public GameConfigService GameConfig => _gameConfig;
        public NewCategoryService CategoryService => _categoryService;
        public ProfileService ProfileService => _profileService;
        public PackageRepository PackageRepo => _packageRepository;
        public PackageImportService PackageImport => _packageImportService;
        public DataMigrationService DataMigration => _dataMigrationService;
        public DeploymentPlanner DeployPlanner => _deploymentPlanner;
        public DeploymentService DeployService => _deploymentService;
        public ConflictModuleService ConflictModule => _conflictModule;
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
        private string _userName = UiText.Get("未登录");

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
        /// <see cref="RefreshFromRepositoryAsync"/> 重建完 <see cref="AllMods"/> 之后触发，在 UI 线程上。
        ///
        /// 主窗口左侧的"已启用 / 已禁用"计数和顶部数量是 code-behind 手工算的，不走绑定。
        /// 冲突面板在自己的窗口里禁用了 7 个 MOD、磁盘和方案文件都已经变了，
        /// 但面板关掉后主窗口没人通知它重算 —— 左栏仍写"已启用 20"，用户据此判定"禁用无效"。
        /// 任何一处刷新了仓库，主窗口都应该跟着重算，而不是靠每个调用点自己记得。
        /// </summary>
        public event Action? RepositoryRefreshed;

        /// <summary>
        /// 当前游戏的所有 Profile 列表。
        /// </summary>
        public ObservableCollection<InstanceProfile> Profiles { get; } = new();

        public MainViewModel(
            GameConfigService gameConfig,
            NewCategoryService categoryService,
            ProfileService profileService,
            PackageRepository packageRepository,
            PackageImportService packageImportService,
            DataMigrationService dataMigrationService,
            DeploymentPlanner deploymentPlanner,
            DeploymentService deploymentService,
            ConflictModuleService conflictModule,
            OverwriteStore overwriteStore,
            Services.Config.ConfigMergeEngine configMergeEngine,
            ResolvedViewBuilder resolvedViewBuilder,
            LaunchOrchestrator launchOrchestrator,
            ILogger<MainViewModel> logger)
        {
            _gameConfig = gameConfig;
            _categoryService = categoryService;
            _profileService = profileService;
            _packageRepository = packageRepository;
            _packageImportService = packageImportService;
            _dataMigrationService = dataMigrationService;
            _deploymentPlanner = deploymentPlanner;
            _deploymentService = deploymentService;
            _conflictModule = conflictModule;
            _overwriteStore = overwriteStore;
            _configMergeEngine = configMergeEngine;
            _resolvedViewBuilder = resolvedViewBuilder;
            _launchOrchestrator = launchOrchestrator;
            _logger = logger;

            ModList = new ModListViewModel(logger);
            // ModListViewModel 的回调契约仍是 Task<bool>，
            // 这里把 OperationResult 降级成 bool 适配。失败原因由 View 层
            // （MainWindow 的 *FromUiAsync）直接从 OperationResult 取，不经过这条通道。
            ModList.ConfigureActions(
                async (mod, enable) => (await ToggleModAsync(mod, enable)).Success,
                async mod => (await DeletePackageModAsync(mod)).Success,
                async (mods, enable) => (await ToggleModsAsync(mods, enable)).Success,
                async mods => (await DeletePackageModsAsync(mods)).Success);

            // ModDetail 的操作回调刻意不在这里接：面板的每个操作都要先弹对话框
            //（重命名输入框、描述编辑器、选图、删除确认），只有 View 层能做。
            // 唯一接线点是 MainWindow 构造函数里的 ModDetail.ConfigureActions——
            // 这里再接一份，"谁生效"就取决于调用顺序。
            ModDetail = new ModDetailViewModel(logger);
            Categories = new CategoryViewModel(categoryService);

            // 连接子 ViewModel 事件
            ModList.ModSelected += OnModListSelectionChanged;

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

            // 冲突报告更新后立刻回填卡片角标：报告是异步算出来的，
            // 光靠 RefreshFromRepositoryAsync 会让角标比面板晚一拍。
            _conflictModule.ReportUpdated += _ => ApplyConflictBadges();
        }

        // ─── 选中项事件处理 ───

        /// <summary>
        /// MOD 列表选中项变化时同步详情面板。
        ///
        /// 这份逻辑曾经有两套：这里一套（<c>if (mod != null) IsDetailPanelOpen = true;</c>，
        /// 只开不关），MainWindow 里另有一套（<c>IsDetailPanelOpen = mod != null;</c>，会关）。
        /// 两个 handler 挂在同一个事件上、对 <c>mod == null</c> 的处理相反，真正生效的是
        /// 后订阅的那个——正确性靠订阅顺序维系，谁动一下构造顺序就会冒出
        /// "详情面板关不掉"或"详情面板不弹"这类说不清条件的抽风。
        ///
        /// 保留的是"没有选中项就没有详情可显示"这一份：
        /// <see cref="ModDetailViewModel.DeleteAsync"/> 自己也是 <c>CurrentMod = null</c>
        /// 之后立刻发 <c>CloseRequested</c>；删除 / 批量卸载后 code-behind 会把
        /// <c>SelectedMod</c> 置空，此时面板必须收起，否则会停在一个已经不存在的 MOD 上。
        /// </summary>
        private void OnModListSelectionChanged(ModInfo? mod)
        {
            ModDetail.CurrentMod = mod;
            IsDetailPanelOpen = mod != null;
        }

        /// <summary>
        /// 仓库刷新后 <see cref="AllMods"/> 全是新实例，选中项还攥着旧引用——
        /// 详情面板会停在一个已经不在列表里的对象上（重命名后名字不更新、
        /// 分类看着"改完又弹回去"）。按包键把选中项重新指向新实例；
        /// 包已不在仓库里（删除、切换游戏）就一起收起。
        ///
        /// 面板开合状态原样保留：刷新不是用户操作，不该把用户刚关掉的面板又弹回来。
        /// </summary>
        private void ResolveSelectionAfterRefresh()
        {
            var selected = ModList.SelectedMod;
            if (selected == null) return;

            var wasOpen = IsDetailPanelOpen;
            var fresh = AllMods.FirstOrDefault(m =>
                string.Equals(m.RealName, selected.RealName, StringComparison.OrdinalIgnoreCase));

            // fresh 为 null 时这一行会让 OnModListSelectionChanged 收起面板并清掉 CurrentMod
            ModList.SelectedMod = fresh;

            // fresh == selected 时上一行不发事件，这个赋值同样是无操作
            IsDetailPanelOpen = wasOpen && fresh != null;
        }

        // ─── Profile 事件处理 ───

        /// <summary>
        /// 把当前方案的名称与摘要刷进可绑定属性——侧栏方案选择器的两行文字绑的就是这两个。
        ///
        /// 这份显示逻辑此前有两套实现：本类写属性（当时 XAML 无人绑定，纯空转），
        /// MainWindow 另有一套直接写 <c>ProfileSelectorName.Text</c>，连字符串模板都逐字重复。
        /// 现在 XAML 绑定到属性，这里是唯一实现（那两个 TextBlock 的 x:Name 也一并去掉，
        /// 让 code-behind 想写回去都编译不过）。
        ///
        /// 读 <c>CurrentProfile</c> 而不读事件参数：<c>ProfileListChanged</c> 不带参数，
        /// 而"重命名当前方案"只发 <c>ProfileListChanged</c>（见 ProfileService.RenameProfileAsync），
        /// 漏掉它就是改完名字侧栏还显示旧名。
        /// </summary>
        public void RefreshProfileDisplay()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var profile = _profileService.CurrentProfile;
                CurrentProfileName = profile?.Name ?? UiText.Get("未选择");
                CurrentProfileSummary = profile == null
                    ? string.Empty
                    : UiText.Interpolate($"{profile.EnabledCount}/{profile.TotalCount} 已启用");
            });
        }

        /// <summary>事件参数刻意不用，一律读 CurrentProfile —— 理由见 RefreshProfileDisplay。</summary>
        private void OnProfileChanged(InstanceProfile? profile) => RefreshProfileDisplay();

        private void OnProfileListChanged()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Profiles.Clear();
                foreach (var p in _profileService.GetProfiles())
                    Profiles.Add(p);
            });

            // 方案改名后名称和"X/Y 已启用"都可能变，列表变更同样要刷一次显示
            RefreshProfileDisplay();
        }

        // ─── 初始化 ───

        /// <summary>
        /// 初始化：加载配置、加载方案、扫描 MOD。
        /// </summary>
        [RelayCommand]
        public async Task InitializeAsync()
        {
            using var loading = BeginLoading(UiText.Get("加载配置..."));
            try
            {

                await _gameConfig.LoadConfigAsync();
                CurrentGameName = _gameConfig.CurrentGameName;

                if (!string.IsNullOrEmpty(CurrentGameName))
                {
                    // 加载 Profile
                    LoadingMessage = UiText.Get("加载方案...");
                    await _profileService.SetCurrentGameAsync(CurrentGameName);
                    // OnProfileListChanged 内部已经带上了 RefreshProfileDisplay
                    OnProfileListChanged();

                    // v2.0: 初始化包仓库
                    LoadingMessage = UiText.Get("加载包仓库...");
                    await _packageRepository.SetCurrentGameAsync(CurrentGameName);

                    // v2.0: 初始化冲突分析器
                    await _conflictModule.SetCurrentGameAsync(CurrentGameName);
                    await _overwriteStore.SetCurrentGameAsync(CurrentGameName);

                    // 分类：数据文件按游戏名分片，漏掉这一步分类就永远不会从磁盘读回来
                    LoadingMessage = UiText.Get("加载分类...");
                    await _categoryService.SetCurrentGameAsync(CurrentGameName);

                    // v2.0: 检查并执行数据迁移
                    if (_dataMigrationService.NeedsMigration(CurrentGameName))
                    {
                        LoadingMessage = UiText.Get("迁移旧数据到 v2.0 格式...");
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

            // 切换游戏时清空图片缓存
            AsyncImageConverter.ClearCache();

            // 加载目标游戏的 Profile
            await _profileService.SetCurrentGameAsync(gameName);
            OnProfileListChanged();

            // v2.0: 加载包仓库
            await _packageRepository.SetCurrentGameAsync(gameName);

            // v2.0: 冲突分析器
            await _conflictModule.SetCurrentGameAsync(gameName);
            await _overwriteStore.SetCurrentGameAsync(gameName);

            // 分类同样按游戏名分片，和上面几个服务一起切，别再落下
            await _categoryService.SetCurrentGameAsync(gameName);

            // v2.0: 数据迁移（如需要）
            if (_dataMigrationService.NeedsMigration(gameName))
            {
                _logger.LogInformation("切换游戏时检测到需要数据迁移: {Game}", gameName);
                await _dataMigrationService.MigrateAsync(gameName);
            }

            await RefreshFromRepositoryAsync();
        }

        public async Task RefreshFromRepositoryAsync()
        {
            using var loading = BeginLoading(UiText.Get("刷新仓库..."));
            try
            {

                var profile = _profileService.CurrentProfile;
                var profileEntries = profile?.Packages.ToDictionary(p => p.PackageKey, StringComparer.OrdinalIgnoreCase)
                                     ?? new Dictionary<string, ProfilePackageEntry>(StringComparer.OrdinalIgnoreCase);
                var packages = _packageRepository.GetAllPackages();

                // 侧边栏分类目录必须覆盖列表里出现的每一个分类，否则列表右侧写着"人物"，
                // 左侧目录里却没有"人物"，右键"移动到分类"也没有可选项。
                // 用和下面 ModInfo.Categories 同一条显示规则（没标签 = 未分类）。
                try
                {
                    await _categoryService.EnsureCategoriesAsync(
                        packages.SelectMany(p => ModCategoryAssignment.DisplayCategoriesFor(p.Tags)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "从 MOD 标签补齐分类失败，侧边栏可能缺少分类");
                }

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
                            Categories = ModCategoryAssignment.DisplayCategoriesFor(package.Tags),
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

                    // AllMods 刚被整体重建，选中项还指着旧实例——重新按包键对上
                    ResolveSelectionAfterRefresh();
                });

                ApplyConflictBadges();
                RefreshProfileDisplay();
                UpdateStatusBar();
                _logger.LogInformation("仓库刷新完成: {Count} 个", packages.Count);

                // 放在最后：订阅方（主窗口）会读 ModList.Mods 与 AllMods，此时两者都已就位。
                Application.Current.Dispatcher.Invoke(() => RepositoryRefreshed?.Invoke());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新仓库失败");
            }
        }

        /// <summary>
        /// 重命名分类：包上的标签与侧边栏条目一起改，最后只刷新一次。
        ///
        /// 分类的权威存储是 <c>Package.Tags</c>。只改侧边栏的话包上还是旧名，
        /// 下一次刷新 <see cref="RefreshFromRepositoryAsync"/> 会按标签把旧名字补回目录，
        /// 用户看到的是"新名字 0 个 + 旧名字原样"。
        /// </summary>
        public async Task<OperationResult> RenameCategoryAsync(CategoryItem category, string newName)
        {
            var oldName = category.Name;
            newName = newName.Trim();
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return OperationResult.Ok();
            if (!ModCategoryAssignment.IsAssignableTarget(newName))
                return OperationResult.Fail(UiText.Interpolate($"「{newName}」是系统保留名称，不能用作分类名。"));
            if (Categories.Categories.Any(c => string.Equals(c.Name, newName, StringComparison.Ordinal)))
                return OperationResult.Fail(UiText.Interpolate($"已经有名为「{newName}」的分类了。"));

            var retagged = await RetagPackagesAsync(tags => ModCategoryAssignment.RenameTag(tags, oldName, newName));
            try
            {
                // 有包没改成功就不动侧边栏：那些包还叫旧名，旧条目得留着它们才有地方待。
                if (retagged.Success)
                    await Categories.DoRenameCategoryAsync(category, newName);
            }
            finally
            {
                // 无论成败都按仓库现状刷新一次，部分包已经改名的话界面要如实反映。
                await RefreshFromRepositoryAsync();
            }
            return retagged;
        }

        /// <summary>
        /// 删除分类：先把这个标签从所有包上摘掉（不论是不是主分类），再删侧边栏条目，最后只刷新一次。
        /// 摘完没有别的标签的包就成了"未分类"。
        ///
        /// 只删侧边栏条目的话，下一次刷新会按包上残留的标签把它补回来 —— "删了又长回来"。
        /// </summary>
        public async Task<OperationResult> DeleteCategoryAsync(CategoryItem category)
        {
            var name = category.Name;
            var retagged = await RetagPackagesAsync(tags => ModCategoryAssignment.RemoveTag(tags, name));
            try
            {
                // 有包没摘掉就保留条目：删了也会在刷新时被残留的标签补回来。
                if (retagged.Success)
                    await Categories.DeleteCategoryAsync(category);
            }
            finally
            {
                await RefreshFromRepositoryAsync();
            }
            return retagged;
        }

        /// <summary>
        /// 批量改包上的分类标签，逐个落盘，不刷新界面（由调用方最后统一刷一次）。
        /// <paramref name="retag"/> 返回 null 表示这个包不用动。
        ///
        /// 不复用 <see cref="MoveModToCategoryAsync"/>：它每移一个就整页重建一次 AllMods，
        /// 一个分类下几十个 MOD 就是几十次重建；它也只认主分类，处理不了附带的标签。
        /// </summary>
        private async Task<OperationResult> RetagPackagesAsync(Func<IReadOnlyList<string>, List<string>?> retag)
        {
            var failures = new List<OperationResult>();
            foreach (var package in _packageRepository.GetAllPackages().ToList())
            {
                var tags = retag(package.Tags);
                if (tags == null) continue;

                var previousTags = package.Tags;
                package.Tags = tags;
                try
                {
                    await _packageRepository.UpdatePackageAsync(package);
                }
                catch (Exception ex)
                {
                    // 与 MoveModToCategoryAsync 同理：内存里的实例就是仓库索引里那一个，写盘失败得还原。
                    package.Tags = previousTags;
                    _logger.LogError(ex, "修改 MOD 分类标签失败: {Key}", package.PackageKey);
                    failures.Add(OperationResult.Fail(
                        UiText.Interpolate($"修改「{package.DisplayName}」的分类失败：{ex.Message}")));
                }
            }
            return OperationResult.Aggregate(failures);
        }

        /// <summary>
        /// 把冲突报告回填到卡片角标。
        ///
        /// 与 <see cref="AllMods"/> 一样跑在 UI 线程上：<see cref="ModInfo.ConflictCount"/>
        /// 是绑定属性，后台线程改它会抛。
        /// </summary>
        private void ApplyConflictBadges()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            dispatcher.Invoke(() =>
            {
                foreach (var mod in AllMods)
                {
                    mod.ConflictCount = _conflictModule.ConflictCountFor(mod.RealName);
                    mod.IsDuplicate = _conflictModule.IsDuplicate(mod.RealName);
                }
            });
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
                Filter = UiText.Get(_gameConfig.CurrentEngineProfile.FileDialogFilter),
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
            using var loading = BeginLoading(UiText.Get("导入MOD..."));
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
            using var loading = BeginLoading(enable ? UiText.Get("启用中...") : UiText.Get("禁用中..."));
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

        /// <summary>
        /// 按当前方案整体重新部署。
        ///
        /// 冲突面板改的是"哪个包该落盘"，而单包开关只会处理自己那一个包：
        /// 用户把 A 设为胜者后，B 的文件仍在游戏目录里，光靠单包开关永远清不掉。
        /// 这里用完整视图重算，让败者条目按托管归属被移除、胜者条目被写入。
        /// </summary>
        public async Task<OperationResult> RedeployCurrentProfileAsync()
        {
            using var loading = BeginLoading(UiText.Get("应用冲突选择..."));
            try
            {
                var plan = await _deploymentPlanner.CreatePlanAsync();
                if (!plan.HasChanges)
                {
                    _logger.LogInformation("[Conflict] 部署已与当前方案一致，无需变更");
                    return OperationResult.Ok();
                }

                var transaction = await _deploymentService.ExecuteAsync(plan);
                if (transaction.Status == DeploymentStatus.Committed)
                {
                    _logger.LogInformation("[Conflict] 冲突选择已应用: +{Add} -{Remove} ~{Replace}",
                        plan.AddCount, plan.RemoveCount, plan.ReplaceCount);
                    await RefreshFromRepositoryAsync();
                    return OperationResult.Ok();
                }

                _logger.LogWarning("[Conflict] 应用冲突选择失败: {Error}", transaction.ErrorMessage);
                return OperationResult.Fail(transaction.ErrorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conflict] 应用冲突选择失败");
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
            using var loading = BeginLoading(enable ? UiText.Get("批量启用...") : UiText.Get("批量禁用..."));

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

        /// <summary>
        /// 按包键批量禁用，然后只重新部署一次。
        ///
        /// 冲突面板的"只保留 X"要一次关掉组内其余成员。逐个走
        /// <see cref="ToggleModAsync"/> 会让每次禁用都触发一次完整重新部署 ——
        /// 3 个成员的组要点 3 次、部署 3 遍，中间态还会让游戏目录短暂处于
        /// 半部署状态。这里先攒完所有开关，最后统一部署。
        /// </summary>
        public async Task<OperationResult> DisableManyAsync(IReadOnlyList<string> packageKeys)
        {
            ArgumentNullException.ThrowIfNull(packageKeys);

            var keys = packageKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keys.Count == 0) return OperationResult.Ok();

            // 找不到目标必须报错，不能静默成功。
            //
            // 事故原型：冲突面板拿到的是另一个 MainViewModel 实例（它的 AllMods 是空的），
            // 每个 key 都匹配不上、targets 为空、这里返回 Ok，面板于是显示"已禁用 X 并重新部署"，
            // 而主列表和游戏目录纹丝不动。空列表 + Ok 把"什么都没做"包装成了成功，
            // 用户和日志都拿不到任何线索。
            var missing = keys
                .Where(k => AllMods.All(m => !string.Equals(m.RealName, k, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
            {
                _logger.LogError("批量禁用：主列表里找不到 {Keys}（AllMods 共 {Count} 项）",
                    string.Join(", ", missing), AllMods.Count);
                return OperationResult.Fail(UiText.Format("找不到 MOD：{0}", string.Join("、", missing)));
            }

            var targets = keys
                .Select(k => AllMods.First(m =>
                    string.Equals(m.RealName, k, StringComparison.OrdinalIgnoreCase)))
                .Where(m => m.IsEnabled)
                .ToList();

            // 全部目标本来就是禁用，才算无事可做。
            if (targets.Count == 0) return OperationResult.Ok();

            _logger.LogInformation("批量禁用 {Count} 个 MOD: {Keys}",
                targets.Count, string.Join(", ", targets.Select(m => m.RealName)));

            using var loading = BeginLoading(UiText.Get("批量禁用..."));

            var results = new List<OperationResult>();
            await using (await _profileService.BeginBatchAsync())
            {
                foreach (var mod in targets)
                    results.Add(await DisableFlagAsync(mod));
            }

            var redeploy = await RedeployCurrentProfileAsync();
            results.Add(redeploy);

            await RefreshFromRepositoryAsync();
            return OperationResult.Aggregate(results);
        }

        /// <summary>冲突面板"禁用 X"的单包入口，语义同 <see cref="DisableManyAsync"/>。</summary>
        public Task<OperationResult> DisableManyAsyncForOne(string packageKey)
            => DisableManyAsync([packageKey]);

        /// <summary>
        /// 只写元数据、不触发部署的单包禁用 —— 给 <see cref="DisableManyAsync"/>
        /// 在批处理作用域内使用，部署留给调用方统一做一次。
        /// </summary>
        private async Task<OperationResult> DisableFlagAsync(ModInfo mod)
        {
            try
            {
                await _profileService.SetPackageEnabledFlagAsync(mod.RealName, false);
                mod.IsEnabled = false;
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "禁用 MOD 失败: {Key}", mod.RealName);
                return OperationResult.Fail(ex.Message);
            }
        }

        public async Task<OperationResult> RenameModAsync(ModInfo mod, string newName)
        {
            var package = _packageRepository.GetByKey(mod.RealName);
            if (package == null)
            {
                _logger.LogWarning("重命名失败，仓库中找不到包: {Key}", mod.RealName);
                return OperationResult.Fail(UiText.Interpolate($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。"));
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
                return OperationResult.Fail(UiText.Interpolate($"重命名「{mod.Name}」失败：{ex.Message}"));
            }

            mod.Name = package.DisplayName;
            await RefreshFromRepositoryAsync();
            return OperationResult.Ok();
        }

        /// <summary>
        /// 把 MOD 移动到指定分类。
        ///
        /// 必须写进包仓库的 <c>Package.Tags</c>，不能只改 <c>ModInfo.Categories</c>：
        /// <see cref="RefreshFromRepositoryAsync"/> 每次都用 <c>package.Tags</c> 重建整个 AllMods，
        /// 只改内存的话下一次刷新（切换游戏、启用/禁用任意一个 MOD）就把分类打回原样，
        /// 而用户已经看到界面更新过一次了。
        /// </summary>
        public async Task<OperationResult> MoveModToCategoryAsync(ModInfo mod, string categoryName)
        {
            if (!ModCategoryAssignment.IsAssignableTarget(categoryName))
            {
                _logger.LogWarning("移动分类失败，目标不合法: {Category}", categoryName);
                return OperationResult.Fail(UiText.Interpolate($"「{categoryName}」不是可用的分类目标。"));
            }

            if (ModCategoryAssignment.IsAlreadyIn(mod.Categories, categoryName))
                return OperationResult.Ok();

            var package = _packageRepository.GetByKey(mod.RealName);
            if (package == null)
            {
                _logger.LogWarning("移动分类失败，仓库中找不到包: {Key}", mod.RealName);
                return OperationResult.Fail(UiText.Interpolate($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。"));
            }

            var previousTags = package.Tags;
            package.Tags = ModCategoryAssignment.BuildCategoriesFor(categoryName);
            try
            {
                await _packageRepository.UpdatePackageAsync(package);
            }
            catch (Exception ex)
            {
                // 内存里的 Package 实例是仓库索引里那一个，写盘失败就得把标签放回去，
                // 否则界面刷新时会读到一个磁盘上并不存在的分类——跟分类服务里
                // 增删改失败要回滚内存是同一个道理。
                package.Tags = previousTags;
                _logger.LogError(ex, "移动 MOD 分类失败: {Key} -> {Category}", mod.RealName, categoryName);
                return OperationResult.Fail(UiText.Interpolate($"将「{mod.Name}」移动到「{categoryName}」失败：{ex.Message}"));
            }

            mod.Categories = ModCategoryAssignment.BuildCategoriesFor(categoryName);
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
                return OperationResult.Fail(UiText.Interpolate($"预览图保存失败：{ex.Message}"));
            }

            if (string.IsNullOrEmpty(storedPath))
            {
                _logger.LogWarning("更换预览图失败，仓库中找不到包: {Key}", mod.RealName);
                return OperationResult.Fail(UiText.Interpolate($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。"));
            }

            mod.PreviewImage = null;
            mod.PreviewImagePath = storedPath;
            await RefreshFromRepositoryAsync();
            return OperationResult.Ok();
        }

        /// <summary>
        /// 更新 MOD 的备注/描述。
        ///
        /// 权威存储是 <c>Package.Note</c>（<see cref="RefreshFromRepositoryAsync"/> 每次都用它
        /// 重建 <c>ModInfo.Description</c>），只改内存的话下一次刷新就把描述打回原样。
        /// </summary>
        public async Task<OperationResult> UpdateModNoteAsync(ModInfo mod, string note)
        {
            note = note.Trim();

            var package = _packageRepository.GetByKey(mod.RealName);
            if (package == null)
            {
                _logger.LogWarning("保存描述失败，仓库中找不到包: {Key}", mod.RealName);
                return OperationResult.Fail(UiText.Interpolate($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。"));
            }

            if (string.Equals(package.Note ?? string.Empty, note, StringComparison.Ordinal))
                return OperationResult.Ok();

            var previousNote = package.Note;
            package.Note = note;
            try
            {
                await _packageRepository.UpdatePackageAsync(package);
            }
            catch (Exception ex)
            {
                // 与 MoveModToCategoryAsync 同理：内存里是仓库索引里那一个 Package 实例，
                // 写盘失败要放回去，否则界面刷新会读到一个磁盘上并不存在的描述。
                package.Note = previousNote;
                _logger.LogError(ex, "保存 MOD 描述失败: {Key}", mod.RealName);
                return OperationResult.Fail(UiText.Interpolate($"保存「{mod.Name}」的描述失败：{ex.Message}"));
            }

            mod.Description = note;
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

            using var loading = BeginLoading(UiText.Get("批量删除..."));

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
            using var loading = BeginLoading(UiText.Get("删除中..."));
            try
            {

                var package = _packageRepository.GetByKey(mod.RealName);
                if (package == null)
                {
                    _logger.LogWarning("删除失败，仓库中找不到包: {Key}", mod.RealName);
                    return OperationResult.Fail(UiText.Interpolate($"仓库中找不到 MOD「{mod.Name}」对应的包记录，可能已被删除。"));
                }

                // 先卸载已部署的文件；这一步失败就不能继续删仓库，否则游戏目录里会留下孤儿文件
                var undeploy = await DeployToggleAsync(package.PackageKey, false);
                if (!undeploy.Success)
                    return OperationResult.Fail(UiText.Interpolate($"无法从游戏目录移除「{mod.Name}」的已部署文件，已中止删除。{Environment.NewLine}{undeploy.Error}"));

                var (success, _) = await _packageRepository.DeletePackageAsync(
                    package.PackageKey, _profileService.GetProfiles(), force: true);
                if (!success)
                {
                    _logger.LogWarning("从仓库删除包失败: {Key}", package.PackageKey);
                    return OperationResult.Fail(UiText.Interpolate($"从包仓库删除「{mod.Name}」失败。文件可能被占用或权限不足。"));
                }

                await _profileService.RemovePackageReferencesAsync(package.PackageKey);
                _logger.LogInformation("包已从仓库和所有方案删除: {Key}", package.PackageKey);
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除包失败: {Key}", mod.RealName);
                return OperationResult.Fail(ex.Message);
            }
        }

        // ─── 冲突分析（v2.0 Phase 4） ───
        //
        // 这里曾有一个零引用的冲突分析方法：catch 之后 return null，失败只进日志。
        // 分析失败和"没有冲突"在返回值上几乎分不出来，任何人给按钮接上它，
        // 就等于把"冲突检测静默失效"再造一遍。
        // 唯一入口是 MainWindow.OpenConflictPanel —— 它开 ConflictWindow，
        // 由窗口内的 SafeEvent.Run 统一记日志 + 弹窗，异常必须能上抛，故不在此处包一层。

        // ─── 内部方法 ───

        private void UpdateStatusBar()
        {
            var enabled = AllMods.Count(m => m.IsEnabled);
            var profileInfo = _profileService.CurrentProfile != null
                ? UiText.Interpolate($" | 方案: {_profileService.CurrentProfile.Name}")
                : "";
            StatusBarText = UiText.Interpolate($"已加载MOD: {enabled}/{AllMods.Count}{profileInfo}");
        }

        public void RefreshLanguage()
        {
            RefreshProfileDisplay();
            UpdateStatusBar();
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
