using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 目录导入回归。
///
/// 现场症状：RAR 提示"请先手动解压"后，用户照做，把解压出来的文件夹拖进来却毫无反应。
/// 原因是 ImportSingleAsync 用 File.Exists 判定，文件夹被当成"文件不存在"，
/// 而唯一支持目录的 ImportPluginAsync 在生产链路上零调用方。
/// </summary>
public sealed class FolderImportIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "FolderImport", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FolderWithPak_ImportsAsModPackagePreservingBareFileName()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "mod"));
        var folder = Path.Combine(_root, "解压出来的MOD");
        WriteFile(folder, Path.Combine("SomeMod", "SomeMod_P.pak"), "PAK");
        WriteFile(folder, "readme.txt", "notes");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        var package = Assert.Single(results, r => r.Package!.Kind == PackageKind.Mod).Package!;
        Assert.Equal(PackageKind.Mod, package.Kind);
        // ~mods 不递归子目录：pak 必须以裸文件名登记，否则游戏扫不到。
        var artifact = Assert.Single(package.Artifacts);
        Assert.Equal("SomeMod_P.pak", artifact.RelativeTargetPath);
        // 同目录的非 MOD 文件不能消失：单独成一个保留目录结构的插件包。
        Assert.Contains(results, r => r.Package!.Kind != PackageKind.Mod);
    }

    [Fact]
    public async Task FolderWithoutPak_ImportsAsPluginKeepingDirectoryStructure()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "plugin"));
        var folder = Path.Combine(_root, "ue4ss");
        WriteFile(folder, Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "mods.json"), "{}");
        WriteFile(folder, Path.Combine("b1", "Binaries", "Win64", "ue4ss", "Mods", "BPML.lua"), "return {}");

        var results = await fixture.Imports.ImportSingleAsync(folder);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.ErrorMessage);
        var package = result.Package!;
        // 游戏根已经是 .../b1/Binaries/Win64，锚点必须剥掉，否则会多叠一层 Binaries/Win64。
        Assert.All(package.Artifacts, a =>
            Assert.StartsWith($"ue4ss{Path.DirectorySeparatorChar}Mods", a.RelativeTargetPath));
        Assert.Equal(2, package.Artifacts.Count);
        Assert.Equal(Path.Combine("ue4ss", "Mods", "mods.json"),
            package.Artifacts.Single(a => a.FileName == "mods.json").RelativeTargetPath);
    }

    [Fact]
    public async Task FolderImport_SkipsDevelopmentResidue()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "junk"));
        var folder = Path.Combine(_root, "with-junk");
        WriteFile(folder, Path.Combine("ue4ss", "Mods", "mods.json"), "{}");
        WriteFile(folder, Path.Combine("ue4ss", "Mods", "debug.log"), "noise");
        WriteFile(folder, Path.Combine("ue4ss", "Mods", "symbols.pdb"), "noise");

        var result = Assert.Single(await fixture.Imports.ImportSingleAsync(folder));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("mods.json", Assert.Single(result.Package!.Artifacts).FileName);
    }

    [Fact]
    public async Task MissingPath_ReportsFolderOrFileMissing()
    {
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "missing"));

        var result = Assert.Single(await fixture.Imports.ImportSingleAsync(Path.Combine(_root, "nope")));

        Assert.False(result.Success);
        Assert.Contains("不存在", result.ErrorMessage);
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed record Fixture(ObjectStore Store, PackageImportService Imports)
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
                new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, store, data));
            return new Fixture(store, imports);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
