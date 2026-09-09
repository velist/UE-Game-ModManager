using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class ImportBudgetIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "ImportBudget", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryImport_RejectsOverBudgetRootOrNestedArchiveBeforeRegistration(bool nested)
    {
        Directory.CreateDirectory(_root);
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "ordinary"));
        var archive = Path.Combine(_root, "payload.zip");
        long budget = 8192;
        if (nested)
        {
            var child = Path.Combine(_root, "child.zip");
            CreateZip(child, ("nested.pak", new byte[4096]));
            var childBytes = await File.ReadAllBytesAsync(child);
            CreateZip(archive, ("root.pak", new byte[2048]), ("child.zip", childBytes));
            budget = 2048 + childBytes.Length + 1024;
        }
        else CreateZip(archive, ("payload.pak", new byte[65536]));
        var imports = new PackageImportService(NullLogger<PackageImportService>.Instance,
            fixture.Repository, fixture.Store, fixture.Config,
            new RepositoryReclaimService(NullLogger<RepositoryReclaimService>.Instance, fixture.Store, fixture.Data),
            budget);

        var result = Assert.Single(await imports.ImportSingleAsync(archive));

        Assert.False(result.Success);
        Assert.Contains("上限", result.ErrorMessage);
        Assert.Empty(fixture.Repository.GetAllPackages());
        var temp = Path.Combine(fixture.Store.RepositoryRoot, ".import-tmp");
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [Theory]
    [InlineData(8192, false)]
    [InlineData(32768, true)]
    public async Task BundleImport_UsesCumulativeWriteBudgetAndCleansFailedPackage(long budget, bool succeeds)
    {
        var source = await Fixture.CreateAsync(Path.Combine(_root, "source"));
        var package = new Package
        {
            PackageKey = "two-files", DisplayName = "Two files", HostGameName = "Game", Kind = PackageKind.Mod
        };
        foreach (var name in new[] { "first.pak", "second.pak" })
        {
            var input = Path.Combine(_root, name);
            await File.WriteAllBytesAsync(input, new byte[5000]);
            var (relative, hash, size) = await source.Store.StoreFileAsync(package.PackageKey, input, name);
            package.Artifacts.Add(new PackageArtifact
            {
                PackageId = package.Id, RelativeSourcePath = relative, RelativeTargetPath = name,
                FileName = name, FileHash = hash, FileSize = size, ArtifactType = ArtifactType.ModFile
            });
        }
        await source.Repository.RegisterPackageAsync(package);
        await source.Profiles.AddPackagesToCurrentProfileAsync([package]);
        var archive = Path.Combine(_root, "bundle.zip");
        await source.Locks.ExportBundleAsync(archive);
        var destination = await Fixture.CreateAsync(Path.Combine(_root, "destination"));
        var preview = await destination.Locks.PreviewBundleImportAsync(archive);

        if (succeeds)
        {
            var profile = await destination.Locks.ApplyBundleImportAsync(archive, preview.LockFile, budget);
            Assert.Single(profile.Packages);
            Assert.Single(destination.Repository.GetAllPackages());
            Assert.Equal(2, Directory.GetFiles(
                destination.Store.GetPackageFilesDirectory(package.PackageKey), "*.pak").Length);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                destination.Locks.ApplyBundleImportAsync(archive, preview.LockFile, budget));
            Assert.Empty(destination.Repository.GetAllPackages());
            Assert.False(Directory.Exists(destination.Store.GetPackageDirectory(package.PackageKey)));
            Assert.Single(destination.Profiles.GetProfiles());
        }
    }

    [Fact]
    public async Task BundlePreview_StopsCompressedMetadataBombBeforeAllocatingFullPayload()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "metadata.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("profile.lock.json").Open())
        {
            var block = new byte[65536];
            Array.Fill(block, (byte)' ');
            for (var i = 0; i < 257; i++) entry.Write(block);
        }
        var fixture = await Fixture.CreateAsync(Path.Combine(_root, "preview"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Locks.PreviewBundleImportAsync(archive));

        Assert.Empty(fixture.Repository.GetAllPackages());
    }

    private static void CreateZip(string path, params (string Name, byte[] Bytes)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var output = zip.CreateEntry(entry.Name).Open();
            output.Write(entry.Bytes);
        }
    }

    private sealed record Fixture(string Data, ObjectStore Store, PackageRepository Repository,
        ProfileService Profiles, GameConfigService Config, ProfileLockService Locks)
    {
        public static async Task<Fixture> CreateAsync(string root)
        {
            var data = Path.Combine(root, "data");
            var store = new ObjectStore(NullLogger<ObjectStore>.Instance, Path.Combine(root, "repository"));
            var repository = new PackageRepository(NullLogger<PackageRepository>.Instance, store, data);
            await repository.SetCurrentGameAsync("Game");
            var profiles = new ProfileService(NullLogger<ProfileService>.Instance, data);
            await profiles.SetCurrentGameAsync("Game");
            var config = new GameConfigService(NullLogger<GameConfigService>.Instance, Path.Combine(root, "config.json"));
            config.Config.GameName = "Game";
            config.Config.GamePath = Path.Combine(root, "game");
            config.Config.ModPath = Path.Combine(root, "game", "mods");
            return new Fixture(data, store, repository, profiles, config,
                new ProfileLockService(NullLogger<ProfileLockService>.Instance, profiles, repository));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
