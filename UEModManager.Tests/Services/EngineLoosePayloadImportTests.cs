using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Conflict;
using UEModManager.Services.Detection;
using Xunit;

namespace UEModManager.Tests.Services;

/// <summary>
/// 松散载荷的安装根由引擎决定，而不是由玩家的默认值决定。
///
/// RE 引擎的 <c>.lua</c> 是 REFramework 脚本，只在 <c>reframework/autorun/</c> 下会被加载；
/// 单文件导入没有方位信息，以前一律按"插件"走确认框里填的目标目录，而那个默认值是 UE 的
/// <c>Binaries/Win64</c> —— 文件装进去了、游戏从不去读，界面还显示导入成功。
/// </summary>
public sealed class EngineLoosePayloadImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "LoosePayloadRoot", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RE的单个lua脚本落在reframework的autorun下()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "re"), "生化危机9");
        var script = WriteFile(Path.Combine(_root, "re-downloads"), "MyScript.lua", "-- script");

        var result = Assert.Single(await fixture.Imports.ImportAsync([script]));

        Assert.True(result.Success, result.ErrorMessage);
        var package = result.Package!;
        Assert.Equal(PackageKind.Plugin, package.Kind);
        Assert.True(package.PreservesInstallLayout, "固定安装根的载荷不该再套包目录");
        Assert.Null(package.TargetRootPath);

        var artifact = Assert.Single(package.Artifacts);
        Assert.Equal(Path.Combine("reframework", "autorun", "MyScript.lua"), artifact.RelativeTargetPath);
        Assert.Equal(ArtifactType.PluginFile, artifact.ArtifactType);
    }

    [Fact]
    public async Task UE的单个pak不受影响()
    {
        // 没有声明固定安装根的引擎照旧：MOD 载荷进 MOD 目录、不带安装布局。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "ue"), "黑神话·悟空");
        var pak = WriteFile(Path.Combine(_root, "ue-downloads"), "Outfit_P.pak", "PAK");

        var result = Assert.Single(await fixture.Imports.ImportAsync([pak]));

        var package = result.Package!;
        Assert.Equal(PackageKind.Mod, package.Kind);
        Assert.False(package.PreservesInstallLayout);
        Assert.Equal("Outfit_P.pak", Assert.Single(package.Artifacts).RelativeTargetPath);
    }

    [Fact]
    public void RE引擎的档案声明了锚点与松散载荷根()
    {
        var profile = EngineProfile.Get(EngineType.REEngine);

        Assert.Contains("natives", profile.InstallAnchors);
        Assert.Contains("reframework", profile.InstallAnchors);
        Assert.Equal("reframework/autorun", profile.LoosePayloadRoots[".lua"]);
    }

    /// <summary>
    /// 同一个文件的两条导入入口必须给出同一种制品类型。
    ///
    /// 直接拖入走 <see cref="PackageKindDetector"/>，压缩包走
    /// <see cref="ArtifactTypeDetector.DetectForImport"/>；两套判定各写一份扩展名表时，
    /// 同一个 <c>.lua</c> 会随入口变成"插件"或"MOD 载荷"，部署位置与冲突语义也跟着变。
    /// </summary>
    [Fact]
    public void 两条导入入口对同一扩展名给出一致的制品类型()
    {
        // .json 刻意排除：压缩包分支不把它当配置，交由包级判定（含 CNS 的 MOD 元数据判据）
        // 决定，见 Core 的 ArtifactTypeDetectorTests.Import_JsonNotInConfigList_BecomesOther_NotConfig。
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json" };

        foreach (var engine in Enum.GetValues<EngineType>())
        {
            var profile = EngineProfile.Get(engine);
            var extensions = profile.DirectImportExtensions
                .Concat(profile.BinaryModExtensions)
                .Where(ext => !skipped.Contains(ext))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var ext in extensions)
            {
                var sample = "sample" + ext;
                var direct = PackageKindDetector.KindToArtifactType(PackageKindDetector.DetectByExtension(sample));
                var archive = ArtifactTypeDetector.DetectForImport(sample, profile.ModFileExtensions);

                Assert.True(direct == archive,
                    $"{engine} 的 {ext}：直接导入判为 {direct}，压缩包判为 {archive}");
            }
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string WriteFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// 预热只在"按容器资产比对"（UE）时才该真的去读容器。
    ///
    /// 其余引擎按文件路径比对，索引从包清单里现成就有 —— 以前预热写死按容器做，
    /// RE / Decima 的 .pak（也是它们的载荷）会被整包读一遍，算出来的索引却没人用。
    /// </summary>
    [Fact]
    public async Task 预热只在按容器比对的引擎上读容器()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "warmup"), "生化危机9");
        var pak = WriteFile(Path.Combine(_root, "warmup-downloads"), "Chunk_P.pak", "PAK");
        var package = Assert.Single(await fixture.Imports.ImportAsync([pak])).Package!;

        await fixture.Indexes.WarmUpAsync(package, ConflictStrategy.FilePath);
        Assert.Equal(0, fixture.Indexes.CachedContainerCount);

        await fixture.Indexes.WarmUpAsync(package, ConflictStrategy.ContainerAssets);
        Assert.Equal(1, fixture.Indexes.CachedContainerCount);
    }

    private sealed record Fixture(PackageImportService Imports, AssetIndexService Indexes)
    {
        public static async Task<Fixture> CreateAsync(string root, string gameName)
        {
            var data = Path.Combine(root, "data");
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            await repository.SetCurrentGameAsync(gameName);

            var config = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            config.Config.GameName = gameName;
            config.Config.GamePath = Path.Combine(root, "game");
            config.Config.ModPath = Path.Combine(root, "game", "mods");

            var indexes = new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data);
            var imports = new PackageImportService(NullLogger<PackageImportService>.Instance, repository,
                store, config,
                new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
                indexes);

            return new Fixture(imports, indexes);
        }
    }
}
