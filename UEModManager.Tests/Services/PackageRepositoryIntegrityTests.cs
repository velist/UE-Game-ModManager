using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 仓库完整性与重复合并的行为测试。
/// 重点覆盖"仓库源路径"和"部署目标路径"不能混用，以及合并时的引用保护。
/// </summary>
public sealed class PackageRepositoryIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckIntegrity_UsesRepositorySourcePath_NotDeploymentTargetPath()
    {
        var (store, repository) = await CreateRepositoryAsync();
        var package = await AddPackageAsync(store, repository, "source-path", "payload");

        var issues = await repository.CheckIntegrityAsync();

        Assert.Empty(issues);
        Assert.Equal("mods/payload.pak", package.Artifacts[0].RelativeTargetPath);
    }

    [Fact]
    public async Task CheckIntegrity_FlagsHashMismatchAndUnregisteredFiles()
    {
        var (store, repository) = await CreateRepositoryAsync();
        await AddPackageAsync(store, repository, "integrity", "payload");

        var filesDir = store.GetPackageFilesDirectory("integrity");
        await File.WriteAllTextAsync(Path.Combine(filesDir, "payload.pak"), "changed");
        await File.WriteAllTextAsync(Path.Combine(filesDir, "unregistered.bin"), "extra");

        var issues = await repository.CheckIntegrityAsync();

        var issue = Assert.Single(issues);
        Assert.Contains("文件哈希不符", issue.issue);
        Assert.Contains("未登记文件", issue.issue);
    }

    [Fact]
    public async Task MergeDuplicates_DeletesOldUnreferencedCopy()
    {
        var (store, repository) = await CreateRepositoryAsync();
        var older = await AddPackageAsync(
            store, repository, "older", "same-content", DateTime.UtcNow.AddDays(-1), "same-hash");
        await AddPackageAsync(
            store, repository, "newer", "same-content", DateTime.UtcNow, "same-hash");

        var groups = repository.GetDuplicateGroups();
        Assert.Single(groups);
        Assert.Same(repository.GetByKey("newer"), groups[0][0]);
        Assert.Same(older, groups[0][1]);

        var result = await repository.MergeDuplicateGroupsAsync(Array.Empty<InstanceProfile>());

        Assert.Equal(1, result.GroupCount);
        Assert.Equal(1, result.DeletedCount);
        Assert.Empty(result.Skipped);
        Assert.Null(repository.GetByKey("older"));
        Assert.False(Directory.Exists(store.GetPackageDirectory("older")));
        Assert.NotNull(repository.GetByKey("newer"));
    }

    [Fact]
    public async Task MergeDuplicates_SkipsCopyReferencedByEnabledProfile()
    {
        var (store, repository) = await CreateRepositoryAsync();
        await AddPackageAsync(
            store, repository, "older", "same-content", DateTime.UtcNow.AddDays(-1), "same-hash");
        await AddPackageAsync(
            store, repository, "newer", "same-content", DateTime.UtcNow, "same-hash");

        var profile = new InstanceProfile { HostGameName = "Game" };
        profile.Packages.Add(new ProfilePackageEntry
        {
            PackageKey = "older",
            IsEnabled = true,
        });

        var result = await repository.MergeDuplicateGroupsAsync(new[] { profile });

        Assert.Equal(0, result.DeletedCount);
        Assert.Single(result.Skipped);
        Assert.NotNull(repository.GetByKey("older"));
        Assert.NotNull(repository.GetByKey("newer"));
        Assert.True(Directory.Exists(store.GetPackageDirectory("older")));
    }

    private async Task<(ObjectStore Store, PackageRepository Repository)> CreateRepositoryAsync()
    {
        var repositoryRoot = Path.Combine(_root, "repository");
        var dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(dataDirectory);

        var store = new ObjectStore(NullLogger<ObjectStore>.Instance, repositoryRoot);
        var repository = new PackageRepository(
            NullLogger<PackageRepository>.Instance, store, dataDirectory);
        await repository.SetCurrentGameAsync("Game");
        return (store, repository);
    }

    private static async Task<Package> AddPackageAsync(
        ObjectStore store,
        PackageRepository repository,
        string key,
        string content,
        DateTime? importedAt = null,
        string? contentHash = null)
    {
        var filesDir = store.GetPackageFilesDirectory(key);
        Directory.CreateDirectory(filesDir);
        var filePath = Path.Combine(filesDir, "nested", "payload.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, content);

        var hash = await ObjectStore.ComputeFileHashAsync(filePath);
        var package = new Package
        {
            PackageKey = key,
            DisplayName = key,
            HostGameName = "Game",
            ImportedAt = importedAt ?? DateTime.UtcNow,
            ContentHash = contentHash ?? hash,
            TotalSize = new FileInfo(filePath).Length,
        };
        package.Artifacts.Add(new PackageArtifact
        {
            PackageId = package.Id,
            RelativeSourcePath = $"{key}/files/nested/payload.pak",
            RelativeTargetPath = "mods/payload.pak",
            FileName = "payload.pak",
            FileSize = new FileInfo(filePath).Length,
            FileHash = hash,
            ArtifactType = ArtifactType.ModFile,
        });

        await repository.RegisterPackageAsync(package);
        return package;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不应掩盖测试本身的结果。
        }
    }
}
