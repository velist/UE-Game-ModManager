using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Conflict;

namespace UEModManager.Tests.Services;

/// <summary>
/// 散装容器组导入回归。
///
/// UE5 的 IoStore MOD 是 <c>.pak + .utoc + .ucas</c> 三个同名文件，缺一个游戏就不加载。
/// 玩家从 N 网下载解压后，最自然的操作是把这三个文件一起选中拖进来；
/// 压缩包 / 文件夹导入会把同名文件归成一个包，而散装文件这条路径曾经是"一个文件一个包"：
/// 三件套被拆成 <c>X</c>、<c>X_2</c>、<c>X_3</c> 三个包，各自部署进自己的包目录，
/// 游戏在任何一个目录里都凑不齐三件套。
/// </summary>
public sealed class LooseContainerSetImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "LooseContainerSet", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 一起选中的三件套_合成一个包()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "all"));
        var downloads = Path.Combine(_root, "downloads-all");
        var pak = WriteFile(downloads, "Outfit_P.pak", "PAK");
        var utoc = WriteFile(downloads, "Outfit_P.utoc", "UTOC");
        var ucas = WriteFile(downloads, "Outfit_P.ucas", "UCAS");

        var results = await fixture.Imports.ImportAsync([pak, utoc, ucas]);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.ErrorMessage);
        var package = result.Package!;
        Assert.Equal("Outfit_P", package.PackageKey);
        Assert.Equal(PackageKind.Mod, package.Kind);
        Assert.Equal(
            ["Outfit_P.pak", "Outfit_P.ucas", "Outfit_P.utoc"],
            package.Artifacts.Select(a => a.RelativeTargetPath).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Outfit_P.pak")]
    [InlineData("Outfit_P.utoc")]
    [InlineData("Outfit_P.ucas")]
    public async Task 只选中其中一个_同目录的同名容器一并带上(string picked)
    {
        // 文件选择框里三个文件并排，新手经常只点 .pak。只导这一个，MOD 在游戏里必然不生效，
        // 而界面上它是"已启用"——玩家无从知道问题出在自己少选了两个文件。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "one-" + Path.GetExtension(picked)[1..]));
        var downloads = Path.Combine(_root, "downloads-" + Path.GetExtension(picked)[1..]);
        WriteFile(downloads, "Outfit_P.pak", "PAK");
        WriteFile(downloads, "Outfit_P.utoc", "UTOC");
        WriteFile(downloads, "Outfit_P.ucas", "UCAS");
        WriteFile(downloads, "Other_P.pak", "OTHER");

        var results = await fixture.Imports.ImportAsync([Path.Combine(downloads, picked)]);

        var package = Assert.Single(results).Package!;
        Assert.Equal(3, package.Artifacts.Count);
        Assert.DoesNotContain(package.Artifacts, a => a.FileName == "Other_P.pak");
    }

    [Fact]
    public async Task 不同名的容器_仍然各自成包()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "distinct"));
        var downloads = Path.Combine(_root, "downloads-distinct");
        var first = WriteFile(downloads, "Hair_P.pak", "HAIR");
        var second = WriteFile(downloads, "Face_P.pak", "FACE");

        var results = await fixture.Imports.ImportAsync([first, second]);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        Assert.All(results, r => Assert.Single(r.Package!.Artifacts));
    }

    [Fact]
    public async Task 插件与配置文件_不参与容器归组()
    {
        // 同名的 .dll 与 .ini 不是"同一个 MOD 的几个分片"：它们的安装位置由玩家在确认框里指定，
        // 悄悄并进 MOD 包会把它们带到 MOD 目录去。
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "plugin"));
        var downloads = Path.Combine(_root, "downloads-plugin");
        var pak = WriteFile(downloads, "Tool.pak", "PAK");
        WriteFile(downloads, "Tool.dll", "DLL");
        WriteFile(downloads, "Tool.ini", "INI");

        var results = await fixture.Imports.ImportAsync([pak]);

        var package = Assert.Single(results).Package!;
        Assert.Equal("Tool.pak", Assert.Single(package.Artifacts).FileName);
    }

    private static string WriteFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed record Fixture(PackageImportService Imports)
    {
        public static async Task<Fixture> CreateAsync(string root)
        {
            var data = Path.Combine(root, "data");
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            await repository.SetCurrentGameAsync("Game");
            var config = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            config.Config.GameName = "Game";
            config.Config.GamePath = Path.Combine(root, "game", "b1", "Binaries", "Win64");
            config.Config.ModPath = Path.Combine(root, "game", "b1", "Content", "Paks", "~mods");
            var imports = new PackageImportService(NullLogger<PackageImportService>.Instance, repository,
                store, config,
                new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data),
                new AssetIndexService(NullLogger<AssetIndexService>.Instance, store, data));
            return new Fixture(imports);
        }
    }

    public void Dispose()
    {
        // ImportAsync 导完会在后台预热资产索引（fire-and-forget），测试结束时它可能还握着缓存文件。
        // 临时目录删不掉不是被测行为的一部分：重试几次，仍不行就留给系统清理。
        for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
