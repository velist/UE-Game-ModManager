using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 完整性判据的**特征测试**：走真实文件系统打 <c>PackageRepository.CheckIntegrityAsync</c>，
/// 逐条钉住它实际产出的中文文案。
///
/// <para>
/// 存在的理由：判定逻辑已抽到 Core 的 <c>PackageIntegrityAnalyzer</c>，那边有纯函数单测。
/// 但纯函数测的是"给定探测结果怎么判"，测不到"IO 层有没有把磁盘事实正确地喂进去"——
/// 探测器把大小读错、把路径拼错、或者漏传了某个字段，Core 的测试全绿而用户看到的结论是错的。
/// 这里从磁盘状态出发端到端验证，补的正是那一段。
/// </para>
/// <para>
/// 七类判据里，哈希不符与未登记文件由 <see cref="PackageRepositoryIntegrityTests"/> 覆盖，
/// 本类覆盖其余五类，外加"大小与哈希同时不符要报两条"这个抽取时差点弄丢的行为。
/// </para>
/// </summary>
public sealed class PackageRepositoryIntegrityJudgementTests : IDisposable
{
    private const string Key = "judge";
    private const string Payload = "payload";          // 7 字节
    private const string SourcePath = $"{Key}/files/nested/payload.pak";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>manifest 不在就短路：不再逐文件比对，只报这一条。</summary>
    [Fact]
    public async Task CheckIntegrity_ManifestDeleted_ReportsManifestMissingOnly()
    {
        var (store, repo) = await CreateAsync();
        await AddAsync(store, repo);
        File.Delete(store.GetManifestPath(Key));

        var issue = Assert.Single(await repo.CheckIntegrityAsync());

        Assert.Equal(Key, issue.packageKey);
        Assert.Equal("manifest.json 缺失", issue.issue);
    }

    /// <summary>
    /// 源路径不在 <c>{packageKey}/files/</c> 内。顺带验证多条问题用"；"连接：
    /// 那个实体文件因为没能登记上，会再被算成一个未登记文件。
    /// </summary>
    [Fact]
    public async Task CheckIntegrity_SourcePathOutsideFilesDirectory_ReportsIllegalPathAndJoinsWithFullWidthSemicolon()
    {
        var (store, repo) = await CreateAsync();
        await AddAsync(store, repo, sourcePath: "mods/payload.pak");

        var issue = Assert.Single(await repo.CheckIntegrityAsync());

        Assert.Equal("非法仓库源路径: mods/payload.pak；发现 1 个未登记文件", issue.issue);
    }

    /// <summary>同一个实体文件被登记两次：只报后一条，且用 manifest 里的原样值。</summary>
    [Fact]
    public async Task CheckIntegrity_SameSourcePathRegisteredTwice_ReportsDuplicateOnce()
    {
        var (store, repo) = await CreateAsync();
        await AddAsync(store, repo, extraArtifactWithSameSourcePath: true);

        var issue = Assert.Single(await repo.CheckIntegrityAsync());

        Assert.Equal($"重复登记文件: {SourcePath}", issue.issue);
    }

    /// <summary>登记了但磁盘上没有——数据丢失，永远不会被自动清理。</summary>
    [Fact]
    public async Task CheckIntegrity_RegisteredFileDeletedFromDisk_ReportsFileMissing()
    {
        var (store, repo) = await CreateAsync();
        await AddAsync(store, repo);
        File.Delete(Path.Combine(store.GetPackageFilesDirectory(Key), "nested", "payload.pak"));

        var issue = Assert.Single(await repo.CheckIntegrityAsync());

        Assert.Equal($"文件缺失: {SourcePath}", issue.issue);
    }

    /// <summary>大小对不上但哈希是对的：只报大小，且带上期望值与实际值。</summary>
    [Fact]
    public async Task CheckIntegrity_SizeMismatchWithCorrectHash_ReportsExpectedAndActualBytes()
    {
        var (store, repo) = await CreateAsync();
        await AddAsync(store, repo, declaredSize: 999);

        var issue = Assert.Single(await repo.CheckIntegrityAsync());

        Assert.Equal($"文件大小不符: {SourcePath}（期望 999, 实际 {Payload.Length}）", issue.issue);
    }

    /// <summary>
    /// 大小与哈希同时对不上要报**两条**。
    ///
    /// <para>
    /// 一个被整体换掉的文件通常两者都不符，两条都报出来用户才知道是"内容被换了"
    /// 而不只是"被截断了"。抽取到 Core 时这条差点丢掉——第一版的判定函数只返回单条问题，
    /// 会把它压成一条。
    /// </para>
    /// </summary>
    [Fact]
    public async Task CheckIntegrity_SizeAndHashBothMismatch_ReportsBothIssues()
    {
        var (store, repo) = await CreateAsync();
        await AddAsync(store, repo, declaredSize: 999, declaredHash: "deadbeefdeadbeef");

        var issue = Assert.Single(await repo.CheckIntegrityAsync());

        Assert.Equal(
            $"文件大小不符: {SourcePath}（期望 999, 实际 {Payload.Length}）；文件哈希不符: {SourcePath}",
            issue.issue);
    }

    // ─── 构造辅助 ───

    private async Task<(ObjectStore Store, PackageRepository Repository)> CreateAsync()
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

    /// <summary>
    /// 落一个内容正确的实体文件，再按参数登记一条（或两条）可能撒谎的 manifest 记录。
    /// 默认参数下包是完好的，每个用例只改它要验的那一项。
    /// </summary>
    private static async Task AddAsync(
        ObjectStore store,
        PackageRepository repository,
        string sourcePath = SourcePath,
        long? declaredSize = null,
        string? declaredHash = null,
        bool extraArtifactWithSameSourcePath = false)
    {
        var filePath = Path.Combine(store.GetPackageFilesDirectory(Key), "nested", "payload.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, Payload);

        var realHash = await ObjectStore.ComputeFileHashAsync(filePath);
        var realSize = new FileInfo(filePath).Length;

        var package = new Package
        {
            PackageKey = Key,
            DisplayName = Key,
            HostGameName = "Game",
            ImportedAt = DateTime.UtcNow,
            ContentHash = realHash,
            TotalSize = realSize,
        };

        PackageArtifact Artifact() => new()
        {
            PackageId = package.Id,
            RelativeSourcePath = sourcePath,
            RelativeTargetPath = "mods/payload.pak",
            FileName = "payload.pak",
            FileSize = declaredSize ?? realSize,
            FileHash = declaredHash ?? realHash,
            ArtifactType = ArtifactType.ModFile,
        };

        package.Artifacts.Add(Artifact());
        if (extraArtifactWithSameSourcePath) package.Artifacts.Add(Artifact());

        await repository.RegisterPackageAsync(package);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不应掩盖测试本身的结果。
        }
    }
}
