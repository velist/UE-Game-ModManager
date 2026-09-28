using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Config;
using UEModManager.Services.Conflict;

namespace UEModManager.Tests.Services;

/// <summary>
/// 覆盖规则的读写回归。
///
/// 旧版这里测的是"决策必须真的消除冲突"（设为胜者 → 待处理数归零）：那套语义
/// 已随冲突面板重做删除 —— 面板现在不写逐条胜者规则，一个组只给一个取舍，
/// 靠禁用 MOD 达成。留下的是**仍然在用**的部分：覆盖规则的批量读写。
/// 部署层的 OverrideResolver 仍按这些规则决定落盘哪一份（共享组件裁决、
/// 配置文件合并的归属都要用），因此这两条必须继续锁住。
/// </summary>
public sealed class ConflictDecisionIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ConflictDecision", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 批量写覆盖规则一次落盘（逐条写 569 次的替代路径），
    /// 语义与逐条 <c>SetOverrideAsync</c> 完全一致，且真的影响落盘内容。
    /// </summary>
    [Fact]
    public async Task SetOverridesAsync_WritesAllRulesInOneSave()
    {
        var f = await Fixture.OpenAsync(_root);
        // 用自带层级的路径（真实 UE4SS 载荷形状）：这类路径不会被加包目录，
        // 两个包的同名文件确实落在同一目标上。
        await f.AddPluginAsync("alpha", ("ue4ss\\Mods\\s1.lua", "A1"),
                                        ("ue4ss\\Mods\\s2.lua", "A2"),
                                        ("ue4ss\\Mods\\s3.lua", "A3"));
        await f.AddPluginAsync("beta", ("ue4ss\\Mods\\s1.lua", "B1"),
                                       ("ue4ss\\Mods\\s2.lua", "B2"),
                                       ("ue4ss\\Mods\\s3.lua", "B3"));

        var targets = new[]
        {
            Path.Combine(f.GameRoot, "ue4ss", "Mods", "s1.lua"),
            Path.Combine(f.GameRoot, "ue4ss", "Mods", "s2.lua"),
            Path.Combine(f.GameRoot, "ue4ss", "Mods", "s3.lua"),
        }.ToDictionary(p => p, _ => "beta", StringComparer.OrdinalIgnoreCase);

        await f.Conflicts.SetOverridesAsync(targets);
        Assert.Equal(3, f.Conflicts.GetOverrides().Count);

        var view = await f.Views.BuildAsync();
        Assert.Equal(DeploymentStatus.Committed,
            (await f.Deployment.ExecuteAsync(await f.Planner.CreatePlanForViewAsync(view))).Status);
        var deployed = Path.Combine(f.GameRoot, "ue4ss", "Mods");
        Assert.Equal("B1", await File.ReadAllTextAsync(Path.Combine(deployed, "s1.lua")));
        Assert.Equal("B2", await File.ReadAllTextAsync(Path.Combine(deployed, "s2.lua")));
        Assert.Equal("B3", await File.ReadAllTextAsync(Path.Combine(deployed, "s3.lua")));
    }

    /// <summary>批量清除覆盖规则同样只落盘一次，且读回为空。</summary>
    [Fact]
    public async Task RemoveOverridesAsync_ClearsSeveralRulesAtOnce()
    {
        var f = await Fixture.OpenAsync(_root);
        await f.AddPluginAsync("alpha", ("ue4ss\\Mods\\r1.lua", "A1"), ("ue4ss\\Mods\\r2.lua", "A2"));
        await f.AddPluginAsync("beta", ("ue4ss\\Mods\\r1.lua", "B1"), ("ue4ss\\Mods\\r2.lua", "B2"));

        var rules = new[]
        {
            Path.Combine(f.GameRoot, "ue4ss", "Mods", "r1.lua"),
            Path.Combine(f.GameRoot, "ue4ss", "Mods", "r2.lua"),
        }.ToDictionary(p => p, _ => "beta", StringComparer.OrdinalIgnoreCase);
        await f.Conflicts.SetOverridesAsync(rules);
        Assert.Equal(2, f.Conflicts.GetOverrides().Count);

        await f.Conflicts.RemoveOverridesAsync(rules.Keys);

        Assert.Empty(f.Conflicts.GetOverrides());
    }

    /// <summary>单条写入 / 清除，以及覆盖规则快照的防御性拷贝。</summary>
    [Fact]
    public async Task SetAndRemoveSingleOverride_RoundTripsThroughProfile()
    {
        var f = await Fixture.OpenAsync(_root);
        var target = Path.Combine(f.GameRoot, "CSharpLoader", "b1cs.ini");

        await f.Conflicts.SetOverrideAsync(target, "alpha");
        Assert.Equal("alpha", f.Conflicts.GetOverrides()[target]);

        var snapshot = Assert.IsType<Dictionary<string, string>>(f.Conflicts.GetOverrides());
        snapshot.Clear();
        Assert.Single(f.Conflicts.GetOverrides());

        await f.Conflicts.SetOverrideAsync(target, null);
        Assert.Empty(f.Conflicts.GetOverrides());
    }

    /// <summary>
    /// 导出的可移植覆盖规则必须去盘符 —— lock 文件换台机器还要能用。
    /// 这是 <c>GetPortableOverrides</c> 从旧 ConflictAnalyzer 搬到 ConflictModuleService 后
    /// 唯一被 ProfileLockService 依赖的行为。
    /// </summary>
    [Fact]
    public async Task GetPortableOverrides_StripsInstallDrive()
    {
        var f = await Fixture.OpenAsync(_root);
        await f.Conflicts.SetOverrideAsync(
            Path.Combine(f.GameRoot, "CSharpLoader", "b1cs.ini"), "alpha");

        var portable = f.Conflicts.GetPortableOverrides(f.Profiles.CurrentProfile!);
        Assert.Equal("alpha", portable["@game/CSharpLoader/b1cs.ini"]);
    }

    private sealed class Fixture
    {
        public string GameRoot { get; init; } = "";
        public ObjectStore Store { get; init; } = null!;
        public PackageRepository Repository { get; init; } = null!;
        public ProfileService Profiles { get; init; } = null!;
        public ConflictModuleService Conflicts { get; init; } = null!;
        public ResolvedViewBuilder Views { get; init; } = null!;
        public DeploymentPlanner Planner { get; init; } = null!;
        public DeploymentService Deployment { get; init; } = null!;

        public static async Task<Fixture> OpenAsync(string root)
        {
            var data = Path.Combine(root, "Data");
            var gameRoot = Path.Combine(root, "Game");
            Directory.CreateDirectory(Path.Combine(gameRoot, "Mods"));
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "Repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
            var game = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            game.Config.GameName = "Integration";
            game.Config.GamePath = gameRoot;
            game.Config.ModPath = Path.Combine(gameRoot, "Mods");
            await repository.SetCurrentGameAsync("Integration");
            await profiles.SetCurrentGameAsync("Integration");
            var overwrites = new OverwriteStore(NullLogger<OverwriteStore>.Instance, repository, null!,
                data, Path.Combine(root, "Overwrites"));
            await overwrites.SetCurrentGameAsync("Integration");
            var assetIndex = new AssetIndexService(
                NullLogger<AssetIndexService>.Instance, store, data);
            var module = new ConflictModuleService(
                NullLogger<ConflictModuleService>.Instance, repository, profiles, game, assetIndex);
            await module.SetCurrentGameAsync("Integration");
            var views = new ResolvedViewBuilder(NullLogger<ResolvedViewBuilder>.Instance, repository, profiles,
                new ConfigMergeEngine(NullLogger<ConfigMergeEngine>.Instance), overwrites, game);
            var state = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
                Path.Combine(root, "DeploymentState"));
            return new Fixture
            {
                GameRoot = gameRoot, Store = store, Repository = repository, Profiles = profiles, Conflicts = module,
                Views = views,
                Planner = new DeploymentPlanner(NullLogger<DeploymentPlanner>.Instance,
                    repository, store, profiles, game, views, state),
                Deployment = new DeploymentService(NullLogger<DeploymentService>.Instance,
                    [new CopyBackend(NullLogger<CopyBackend>.Instance)], overwrites, state, Path.Combine(root, "Backups"))
            };
        }

        /// <summary>
        /// 注册一个"自带目录层级"的插件包 —— 与真实压缩包导入的产物形状一致
        /// （ue4ss/Mods/xxx.lua 会被部署到 gamePath/ue4ss/Mods/xxx.lua，
        /// 因此不同包的同名文件确实会互相覆盖，这正是冲突检测要报的东西）。
        /// </summary>
        public async Task AddPluginAsync(string key, params (string Path, string Content)[] files)
        {
            var artifacts = new List<PackageArtifact>();
            var total = 0L;
            foreach (var (relative, content) in files)
            {
                var source = Path.Combine(Store.GetPackageFilesDirectory(key), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                await File.WriteAllTextAsync(source, content);
                var hash = await ObjectStore.ComputeFileHashAsync(source);
                var length = new FileInfo(source).Length;
                total += length;
                artifacts.Add(new PackageArtifact
                {
                    FileName = Path.GetFileName(relative),
                    ArtifactType = relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        ? ArtifactType.PluginFile : ArtifactType.Other,
                    RelativeSourcePath = $"{key}/files/{relative}",
                    RelativeTargetPath = relative,
                    FileSize = length,
                    FileHash = hash,
                });
            }

            var package = new Package
            {
                PackageKey = key, DisplayName = key, HostGameName = "Integration",
                Kind = PackageKind.Plugin, ContentHash = key, TotalSize = total, Artifacts = artifacts,
            };
            await Repository.RegisterPackageAsync(package);
            await Profiles.AddPackagesToCurrentProfileAsync([package]);
            await Profiles.SetPackageEnabledFlagAsync(key, true);
        }
    }

    public void Dispose()
    {
        // 与同目录其他测试一致：清理失败不该让测试变红。部署/覆盖写入刚结束时
        // 句柄释放有毫秒级延迟，全量并行跑时偶发 UnauthorizedAccessException。
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }
}
