using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;
using UEModManager.Services.DeploymentPlanning;
using UEModManager.Tests.Infrastructure;
using UEModManager.ViewModels;

namespace UEModManager.Tests.Services;

/// <summary>
/// 冲突模块端到端测试的共用宿主：**真实 pak → 真实导入 → 真实索引 → 真实分析 → 真实禁用**。

/// <para>
/// 全程走生产链路上的真实服务（只把数据目录等位置参数换成本次临时目录）。
/// 单独抽出来是因为冲突面板的接线用例也必须在"一份真的 MainViewModel"上断言 ——
/// 面板曾经拿到容器另造的第二个 MainViewModel（<c>AllMods</c> 为空），
/// 而用假对象测量不到这件事。
/// </para>
/// </summary>
internal static class ConflictTestHost
{
    public static async Task<Host> OpenAsync(string root)
    {
        var data = Path.Combine(root, "Data");
        var gameRoot = Path.Combine(root, "game", "b1", "Binaries", "Win64");
        Directory.CreateDirectory(gameRoot);

        var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "Repository"));
        var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
        var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
        var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
        game.Config.GameName = "黑神话·悟空";
        game.Config.GamePath = gameRoot;
        game.Config.ModPath = Path.Combine(root, "game", "b1", "Content", "Paks", "~mods");

        await repository.SetCurrentGameAsync("黑神话·悟空");
        await profiles.SetCurrentGameAsync("黑神话·悟空");

        var overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, repository, null!,
            data, Path.Combine(root, "Overwrites"));
        await overwrites.SetCurrentGameAsync("黑神话·悟空");

        var indexes = new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data);
        var imports = new PackageImportService(NullLogger<PackageImportService>.Instance,
            repository, store, game,
            new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
            indexes);
        var conflicts = new ConflictModuleService(NullLogger<ConflictModuleService>.Instance,
            repository, profiles, game, indexes);
        await conflicts.SetCurrentGameAsync("黑神话·悟空");

        var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
            new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), overwrites, game);
        var state = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
            Path.Combine(root, "DeploymentState"));
        var planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
            repository, store, profiles, game, views, state);
        var deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
            [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state,
            Path.Combine(root, "Backups"));
        // 启动记录写到临时目录：默认位置是开发机的真实 %LOCALAPPDATA%。
        var orchestrator = new LaunchOrchestrator(NullLogger<LaunchOrchestrator>.Instance,
            game, profiles, views, planner, deployment, conflicts,
            Path.Combine(root, "LaunchSessions"));

        var categoryService = new NewCategoryService(NullLogger<NewCategoryService>.Instance, data);
        await categoryService.SetCurrentGameAsync("黑神话·悟空");

        var main = new MainViewModel(game, categoryService, profiles, repository, imports,
            new DataMigrationService(NullLogger<DataMigrationService>.Instance, repository, store, game),
            planner, deployment, conflicts, overwrites,
            new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), views, orchestrator,
            NullLogger<MainViewModel>.Instance);

        return new Host(root, repository, imports, profiles, conflicts, main);
    }

    internal sealed record Host(
        string Root, PackageRepository Repository, PackageImportService Imports,
        ProfileService Profiles, ConflictModuleService Conflicts, MainViewModel Main)
    {
        /// <summary>导入若干 pak，每个都以给定名字成为一个 MOD 包。</summary>
        public async Task ImportPaksAsync(params (string Name, byte[] Bytes)[] paks)
        {
            var directory = Path.Combine(Root, "paks");
            Directory.CreateDirectory(directory);

            foreach (var (name, bytes) in paks)
            {
                var path = Path.Combine(directory, name + ".pak");
                File.WriteAllBytes(path, bytes);
                var result = Assert.Single(await Imports.ImportSingleAsync(path));
                Assert.True(result.Success, result.ErrorMessage);
            }
        }

        public async Task ImportArchiveAsync(string zip)
        {
            var results = await Imports.ImportAsync([zip]);
            Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        }

        /// <summary>把仓库里的包全部加进当前方案并启用，并刷新一次主列表。</summary>
        public Task EnableAllAsync() => OnStaAsync(async () =>
        {
            var packages = Repository.GetAllPackages().ToList();
            await Profiles.AddPackagesToCurrentProfileAsync(packages);
            foreach (var package in packages)
                await Profiles.SetPackageEnabledFlagAsync(package.PackageKey, true);

            await Main.RefreshFromRepositoryAsync();
        });

        /// <summary>
        /// 在 <see cref="StaApplication"/> 的派发线程上跑一段异步工作。
        ///
        /// 凡是会碰 <c>MainViewModel</c> 或 <c>ProfileService</c> 的调用都必须走这里：
        /// 主窗口的操作几乎都会同步触发 <c>ProfileChanged</c>，而订阅方
        /// 会直接 <c>Dispatcher.Invoke</c> 回写绑定属性。在别的线程上跑，
        /// 这个 Invoke 就会落到"派发线程正在阻塞等待"的死锁上（实测会挂住）。
        /// </summary>
        public Task OnStaAsync(Func<Task> work) => StaApplication.RunAsync(work);

        /// <summary>同上，带返回值。</summary>
        public Task<T> OnStaAsync<T>(Func<Task<T>> work) => StaApplication.RunAsync(work);
    }
}
