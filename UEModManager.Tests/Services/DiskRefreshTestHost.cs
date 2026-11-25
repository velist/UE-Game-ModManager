using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;
using UEModManager.Services.Launch;
using UEModManager.Services.Steam;
using UEModManager.Tests.Infrastructure;
using UEModManager.ViewModels;

namespace UEModManager.Tests.Services;

/// <summary>
/// 「刷新 / 磁盘核对」端到端测试的共用宿主：真实的仓库、方案、部署、主视图模型，
/// 全部落在本次测试的临时目录里——<b>不碰任何真实的游戏目录或 MOD 库</b>。
///
/// <para>
/// 只有两样是假的：启动游戏要用的系统接口（<see cref="NoLaunchPlatform"/>，绝不会真的起进程），
/// 以及玩家的回答（各测试自己给 <c>IDiskRefreshPrompts</c>）。
/// </para>
/// </summary>
internal sealed class DiskRefreshHost
{
    public const string GameName = "Test";

    public required string Root { get; init; }
    public required string DataDirectory { get; init; }
    public required string GamePath { get; init; }
    public required string ModPath { get; init; }
    public required ObjectStore Store { get; init; }
    public required PackageRepository Repository { get; init; }
    public required ProfileService Profiles { get; init; }
    public required GameConfigService Game { get; init; }
    public required ResolvedViewBuilder Views { get; init; }
    public required DeploymentStateStore States { get; init; }
    public required DeploymentPlanner Planner { get; init; }
    public required DeploymentService Deployment { get; init; }
    public required ConflictModuleService Conflicts { get; init; }
    public required DiskReconcileService Reconcile { get; init; }
    public required LaunchOrchestrator Launcher { get; init; }
    public required HealthCheckService Health { get; init; }
    public required MainViewModel Main { get; init; }

    /// <summary>主视图模型的其余依赖：只有"从容器里解析视图模型"的用例用得到。</summary>
    public required NewCategoryService Categories { get; init; }
    public required PackageImportService Imports { get; init; }
    public required DataMigrationService Migration { get; init; }
    public required OverwriteStore Overwrites { get; init; }
    public required ConfigMergeEngine Merge { get; init; }

    public string RepositoryRoot => Store.RepositoryRoot;

    /// <param name="backends">部署后端；默认只有复制（与正式版的注册一致）。</param>
    public static async Task<DiskRefreshHost> OpenAsync(string root, params IDeploymentBackend[] backends)
    {
        var data = Path.Combine(root, "Data");
        var gamePath = Path.Combine(root, "Game");
        var modPath = Path.Combine(gamePath, "Mods");
        Directory.CreateDirectory(gamePath);
        File.WriteAllBytes(Path.Combine(gamePath, "Game.exe"), []);

        var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "Repository"));
        var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
        var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
        var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
        game.Config.GameName = GameName;
        game.Config.GamePath = gamePath;
        game.Config.ModPath = modPath;
        game.Config.ExecutableName = "Game.exe";

        await repository.SetCurrentGameAsync(GameName);
        await profiles.SetCurrentGameAsync(GameName);

        var overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, repository, null!,
            data, Path.Combine(root, "Overwrites"));
        await overwrites.SetCurrentGameAsync(GameName);

        var indexes = new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data);
        var imports = new PackageImportService(NullLogger<PackageImportService>.Instance,
            repository, store, game,
            new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
            indexes);
        var conflicts = new ConflictModuleService(NullLogger<ConflictModuleService>.Instance,
            repository, profiles, game, indexes);
        await conflicts.SetCurrentGameAsync(GameName);

        var merge = new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance);
        var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
            merge, overwrites, game);
        var states = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
            Path.Combine(root, "DeploymentState"));
        var planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
            repository, store, profiles, game, views, states);
        var deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
            backends.Length > 0 ? backends : [new CopyBackend(NullLogger<CopyBackend>.Instance)],
            overwrites, states, Path.Combine(root, "Backups"));
        var launcher = new LaunchOrchestrator(NullLogger<LaunchOrchestrator>.Instance,
            game, profiles, views, planner, deployment, conflicts,
            Path.Combine(root, "LaunchSessions"), new NoLaunchPlatform(), FastTimings);

        var reconcile = new DiskReconcileService(NullLogger<DiskReconcileService>.Instance,
            repository, profiles, game, states, views);
        var health = new HealthCheckService(NullLogger<HealthCheckService>.Instance,
            game, profiles, repository, views, states);

        var categories = new NewCategoryService(NullLogger<NewCategoryService>.Instance, data);
        await categories.SetCurrentGameAsync(GameName);

        var migration = new DataMigrationService(NullLogger<DataMigrationService>.Instance, repository, store, game);
        var main = new MainViewModel(game, categories, profiles, repository, imports, migration,
            planner, deployment, conflicts, overwrites, merge, views, launcher,
            NullLogger<MainViewModel>.Instance, reconcile);

        return new DiskRefreshHost
        {
            Root = root, DataDirectory = data, GamePath = gamePath, ModPath = modPath,
            Store = store, Repository = repository, Profiles = profiles, Game = game, Views = views,
            States = states, Planner = planner, Deployment = deployment, Conflicts = conflicts,
            Reconcile = reconcile, Launcher = launcher, Health = health, Main = main,
            Categories = categories, Imports = imports, Migration = migration, Overwrites = overwrites, Merge = merge,
        };
    }

    private static readonly LaunchWatchTimings FastTimings = new()
    {
        PollInterval = TimeSpan.FromSeconds(1),
        DirectSettle = TimeSpan.FromSeconds(2),
        HandOffGrace = TimeSpan.FromSeconds(2),
        SteamTimeout = TimeSpan.FromSeconds(3),
    };

    // ─── 场景搭建 ───

    /// <summary>
    /// 往仓库里放一个 MOD 包并加进当前方案（未启用）。文件内容就是给的字符串。
    /// 用 .bin 扩展名：冲突分析按文件路径比，不会去解析容器格式。
    /// </summary>
    public async Task<Package> AddPackageAsync(string key, params (string RelativePath, string Content)[] files)
    {
        var artifacts = new List<PackageArtifact>();
        long total = 0;
        foreach (var (relativePath, content) in files)
        {
            var source = Path.Combine(Store.GetPackageFilesDirectory(key), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, content);
            var size = new FileInfo(source).Length;
            total += size;
            artifacts.Add(new PackageArtifact
            {
                FileName = Path.GetFileName(relativePath),
                RelativeSourcePath = Path.Combine(key, "files", relativePath),
                RelativeTargetPath = relativePath,
                FileHash = await ObjectStore.ComputeFileHashAsync(source),
                FileSize = size,
                ArtifactType = ArtifactType.ModFile,
            });
        }

        var package = new Package
        {
            PackageKey = key, DisplayName = "MOD " + key, HostGameName = GameName, Kind = PackageKind.Mod,
            Tags = ["未分类"], TotalSize = total, Artifacts = artifacts,
            ContentHash = string.Join("|", artifacts.Select(a => a.FileHash)),
        };
        await Repository.RegisterPackageAsync(package);
        await Profiles.AddPackagesToCurrentProfileAsync([package]);
        return package;
    }

    /// <summary>
    /// 往仓库里放一个<b>保留安装布局</b>的插件包（文件夹 / 压缩包整体导入的那种：包内路径就是相对游戏根的位置），
    /// 并加进当前方案（未启用）。
    ///
    /// 图片一律登记成预览图——老版本就是这么入库的。部署时其中带目录的会被改判成要装的文件
    /// （<c>InstallLayoutNormalizer.EffectiveArtifactType</c>），包根上的才是真的预览图。
    /// </summary>
    public async Task<Package> AddLayoutPluginAsync(string key, params (string RelativePath, string Content)[] files)
    {
        var artifacts = new List<PackageArtifact>();
        long total = 0;
        foreach (var (relativePath, content) in files)
        {
            var source = Path.Combine(Store.GetPackageFilesDirectory(key), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, content);
            var size = new FileInfo(source).Length;
            total += size;
            artifacts.Add(new PackageArtifact
            {
                FileName = Path.GetFileName(relativePath),
                RelativeSourcePath = Path.Combine(key, "files", relativePath),
                RelativeTargetPath = relativePath,
                FileHash = await ObjectStore.ComputeFileHashAsync(source),
                FileSize = size,
                ArtifactType = relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? ArtifactType.PreviewImage
                    : ArtifactType.PluginFile,
            });
        }

        var package = new Package
        {
            PackageKey = key, DisplayName = "插件 " + key, HostGameName = GameName, Kind = PackageKind.Plugin,
            PreservesInstallLayout = true, Tags = ["未分类"], TotalSize = total, Artifacts = artifacts,
            ContentHash = string.Join("|", artifacts.Select(a => a.FileHash)),
        };
        await Repository.RegisterPackageAsync(package);
        await Profiles.AddPackagesToCurrentProfileAsync([package]);
        return package;
    }

    /// <summary>保留安装布局的插件包部署后的位置：直接相对游戏根，不套包名。</summary>
    public string PluginFile(string relativePath) => Path.Combine(GamePath, relativePath);

    /// <summary>经主视图模型启用（真实部署），与玩家点开关走的是同一条路。</summary>
    public async Task EnableAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            var result = await Main.DeployToggleAsync(key, true);
            Assert.True(result.Success, result.Error);
        }
        await Main.RefreshFromRepositoryAsync();
        await Deployment.PendingBackupCleanup;
    }

    /// <summary>
    /// 程序启动时的那一次列表构建。它会把还没登记的分类写进分类文件（只有第一次才写），
    /// 所以"刷新不写任何文件"的用例要先过了这一步再取指纹。
    /// </summary>
    public Task ShowListAsync() => Main.RefreshFromRepositoryAsync();

    /// <summary>
    /// 让 MOD 库的位置变成"访问不到、也建不出来"：把仓库目录挪走，再用一个同名<b>文件</b>占住原来的位置。
    ///
    /// 真实情形是移动硬盘没插、网络盘掉线——那时去建这个目录抛的同样是 <see cref="IOException"/>。
    /// 用同名文件来模拟，测试不必依赖一个真的不存在的盘符，也不会在临时目录之外留下任何东西。
    /// </summary>
    /// <returns>把它恢复原状（"把硬盘接回去"）。</returns>
    public Action UnplugLibrary()
    {
        var root = RepositoryRoot;
        var elsewhere = root + "-unplugged";
        Directory.Move(root, elsewhere);
        File.WriteAllBytes(root, []);
        return () =>
        {
            File.Delete(root);
            Directory.Move(elsewhere, root);
        };
    }

    public string RepositoryFile(string key, string relativePath)
        => Path.Combine(Store.GetPackageFilesDirectory(key), relativePath);

    /// <summary>MOD 包部署后的位置：MOD 目录下套一层包名。</summary>
    public string GameFile(string key, string relativePath) => Path.Combine(ModPath, key, relativePath);

    public Task<DeploymentState> ReadStateAsync() => States.ReadAsync(GameName, GamePath, ModPath);

    public bool IsEnabled(string key)
        => Profiles.CurrentProfile!.Packages.Any(p => p.PackageKey == key && p.IsEnabled);

    public bool IsInProfile(string key)
        => Profiles.GetProfiles().Any(profile => profile.Packages.Any(p => p.PackageKey == key));

    public bool IsListed(string key) => Main.AllMods.Any(m => m.RealName == key);

    /// <summary>磁盘上的包索引里有没有这个包（不是内存里的）。</summary>
    public bool IsInIndexOnDisk(string key)
    {
        var path = Path.Combine(DataDirectory, $"{GameName}_packages.json");
        return File.Exists(path) && File.ReadAllText(path).Contains($"\"PackageKey\": \"{key}\"");
    }

    /// <summary>
    /// 临时目录下全部文件的指纹：路径、大小、最后写入时间、内容哈希。
    /// 两次指纹完全相同 = 中间这段操作没有写过任何文件。
    /// </summary>
    public string Fingerprint()
        => string.Join("\n", Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new FileInfo(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return $"{Path.GetRelativePath(Root, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|"
                       + Convert.ToHexString(SHA256.HashData(stream));
            })
            .Concat(Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => "dir:" + Path.GetRelativePath(Root, path))));

    /// <summary>
    /// 凡是碰 <see cref="MainViewModel"/> 或 <see cref="ProfileService"/> 的都在常驻的界面线程上跑，
    /// 理由同 <c>ConflictTestHost.OnStaAsync</c>：它们会同步回写绑定属性。
    /// </summary>
    public static Task OnStaAsync(Func<Task> work) => StaApplication.RunAsync(work);

    /// <summary>启动游戏要用的系统接口的假实现：不读 Steam、不开链接、不起进程、不真的等待。</summary>
    internal sealed class NoLaunchPlatform : IGameLaunchPlatform
    {
        public List<string> StartedExecutables { get; } = [];

        public bool IsSteamClientAvailable() => false;
        public IReadOnlyList<SteamAppManifest> ReadAppManifests(string steamAppsDirectory) => [];
        public void OpenUri(string uri) => throw new InvalidOperationException("测试里不应该打开任何链接");

        public IStartedProcess? StartExecutable(
            string path, string workingDirectory, string? arguments, IReadOnlyDictionary<string, string> environment)
        {
            StartedExecutables.Add(path);
            return new FakeProcess();
        }

        public IReadOnlyList<RunningProcessInfo> SnapshotProcesses() => [];
        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;

        private sealed class FakeProcess : IStartedProcess
        {
            public int Id => 4242;
            public bool HasExited => false;
        }
    }
}

/// <summary>预先定好回答的"玩家"：没料到的问题直接让测试失败，所以每条用例都精确知道弹了哪些框。</summary>
internal sealed class ScriptedPrompts : IDiskRefreshPrompts
{
    private readonly Dictionary<DiskRefreshQuestionKind, DiskRefreshAnswer> _answers = [];

    public List<DiskRefreshQuestion> Asked { get; } = [];
    public List<DiskRefreshNotice> Notices { get; } = [];

    /// <summary>对话框弹出来、玩家还没点的那一刻要发生的事（比如把文件夹从回收站还原）。</summary>
    public Action<DiskRefreshQuestion>? WhileDialogIsOpen { get; set; }

    public ScriptedPrompts Answer(DiskRefreshQuestionKind kind, DiskRefreshAnswer answer)
    {
        _answers[kind] = answer;
        return this;
    }

    public DiskRefreshAnswer Ask(DiskRefreshQuestion question)
    {
        Asked.Add(question);
        WhileDialogIsOpen?.Invoke(question);
        if (_answers.TryGetValue(question.Kind, out var answer)) return answer;
        throw new Xunit.Sdk.XunitException($"没料到会被问：{question.Kind}\n{question.Title}\n{question.Message}");
    }

    public void Notify(DiskRefreshNotice notice) => Notices.Add(notice);

    public IEnumerable<DiskRefreshQuestionKind> AskedKinds => Asked.Select(q => q.Kind);
    public IEnumerable<DiskRefreshNoticeKind> NoticeKinds => Notices.Select(n => n.Kind);
}
