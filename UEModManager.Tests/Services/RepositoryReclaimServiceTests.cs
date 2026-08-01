using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;
using UEModManager.Services.Repository;

namespace UEModManager.Tests.Services;

/// <summary>
/// <see cref="RepositoryReclaimService"/> 的磁盘扫描与回收测试。
///
/// 全部落在独立临时目录：仓库根与索引目录都通过测试用构造函数注入，
/// 绝不能碰真实的 %LOCALAPPDATA%\UEModManager —— 本项目已经因为这个踩过两次，
/// 而这个服务干的活儿是**删目录**，写错位置的后果比留下垃圾文件严重得多。
/// </summary>
public sealed class RepositoryReclaimServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_reclaim_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _repositoryRoot;
    private readonly string _dataDirectory;

    /// <summary>把"现在"推到未来，让所有已建好的目录都越过静置期。</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromHours(1);
    private DateTime FarFuture => DateTime.UtcNow.AddDays(1);

    public RepositoryReclaimServiceTests()
    {
        _repositoryRoot = Path.Combine(_root, "Repository");
        _dataDirectory = Path.Combine(_root, "Data");
        Directory.CreateDirectory(_repositoryRoot);
        Directory.CreateDirectory(_dataDirectory);
    }

    private RepositoryReclaimService CreateService()
        => new(NullLogger<RepositoryReclaimService>.Instance,
            new ObjectStore(NullLogger<ObjectStore>.Instance, _repositoryRoot),
            _dataDirectory);

    /// <summary>造一个"导入中途失败"形态的包目录：有 files/、无 manifest。</summary>
    private string MakePartialPackage(string key, string fileName = "mod.pak")
    {
        var dir = Path.Combine(_repositoryRoot, key);
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        File.WriteAllText(Path.Combine(dir, "files", fileName), new string('x', 64));
        return dir;
    }

    private void MakeManifest(string key)
        => File.WriteAllText(Path.Combine(_repositoryRoot, key, "manifest.json"), "{}");

    /// <summary>写一份某游戏的包索引（字段名与 PackageRepository.SaveIndexAsync 的输出一致）。</summary>
    private void WriteIndex(string gameName, params string[] packageKeys)
    {
        var items = string.Join(",", packageKeys.Select(k => $"{{\"PackageKey\":\"{k}\"}}"));
        File.WriteAllText(Path.Combine(_dataDirectory, $"{gameName}_packages.json"), $"[{items}]");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    // ─── 该回收的 ───

    [Fact]
    public void BuildPlan_PartialImportLeftover_IsReclaimable()
    {
        MakePartialPackage("LeftoverMod");
        WriteIndex("BlackMyth");   // 空索引，但确实读得出来

        var plan = CreateService().BuildPlan(FarFuture, Quiet);

        var entry = Assert.Single(plan.Reclaimable);
        Assert.Equal("LeftoverMod", entry.DirectoryName);
        Assert.True(entry.SizeBytes > 0);
    }

    [Fact]
    public void Reclaim_DeletesOnlyReclaimableDirectories()
    {
        MakePartialPackage("LeftoverMod");
        MakePartialPackage("KeptMod");
        MakeManifest("KeptMod");           // 失联包 → 只提示不删
        WriteIndex("BlackMyth");

        var service = CreateService();
        var result = service.Reclaim(service.BuildPlan(FarFuture, Quiet));

        Assert.Equal(1, result.DeletedCount);
        Assert.Empty(result.Failures);
        Assert.False(Directory.Exists(Path.Combine(_repositoryRoot, "LeftoverMod")));
        Assert.True(Directory.Exists(Path.Combine(_repositoryRoot, "KeptMod")));
    }

    // ─── 绝不能回收的 ───

    [Fact]
    public void BuildPlan_PackageIndexedByAnotherGame_IsNotReclaimable()
    {
        // 仓库根跨游戏共享：当前打开的是黑神话，但这个包属于剑星。
        // 只读当前游戏的索引就会把它判成孤儿——这是最容易犯、后果最重的一个错。
        MakePartialPackage("StellarBladeMod");
        WriteIndex("BlackMyth");
        WriteIndex("StellarBlade", "StellarBladeMod");

        var plan = CreateService().BuildPlan(FarFuture, Quiet);

        Assert.Empty(plan.Reclaimable);
    }

    [Fact]
    public void BuildPlan_CorruptIndexFile_ReclaimsNothing()
    {
        MakePartialPackage("LeftoverMod");
        WriteIndex("BlackMyth");
        File.WriteAllText(Path.Combine(_dataDirectory, "StellarBlade_packages.json"), "{ 这不是 JSON");

        var plan = CreateService().BuildPlan(FarFuture, Quiet);

        Assert.Empty(plan.Reclaimable);
    }

    [Fact]
    public void BuildPlan_MissingDataDirectory_ReclaimsNothing()
    {
        // 索引整体丢失 ≠ 所有包都无主
        MakePartialPackage("LeftoverMod");
        Directory.Delete(_dataDirectory, true);

        var plan = CreateService().BuildPlan(FarFuture, Quiet);

        Assert.Empty(plan.Reclaimable);
    }

    [Fact]
    public void BuildPlan_ForeignDirectoryUnderRepositoryRoot_IsNotReclaimable()
    {
        // 仓库根是用户可自定义的，完全可能被指到一个已有的文件夹上
        var mine = Path.Combine(_repositoryRoot, "我的截图");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "a.png"), "x");
        WriteIndex("BlackMyth");

        var plan = CreateService().BuildPlan(FarFuture, Quiet);

        Assert.Empty(plan.Reclaimable);
    }

    [Fact]
    public void BuildPlan_ActiveImportTempRoot_IsNotTreatedAsPackage()
    {
        Directory.CreateDirectory(Path.Combine(_repositoryRoot, ".import-tmp", "uemod_import_abc", "files"));
        WriteIndex("BlackMyth");

        var plan = CreateService().BuildPlan(FarFuture, Quiet);

        Assert.Empty(plan.Reclaimable);
    }

    [Fact]
    public void BuildPlan_RecentlyWrittenLeftover_IsNotReclaimable()
    {
        MakePartialPackage("BeingImported");
        WriteIndex("BlackMyth");

        // 用真实的"现在"：目录刚建好，静置期远未满
        var plan = CreateService().BuildPlan(DateTime.UtcNow, Quiet);

        Assert.Empty(plan.Reclaimable);
    }

    // ─── 解压临时目录 ───

    [Fact]
    public void ReclaimStaleImportTemp_RemovesOnlyGeneratedStaleDirectories()
    {
        var tempRoot = Path.Combine(_repositoryRoot, ".import-tmp");
        Directory.CreateDirectory(Path.Combine(tempRoot, "uemod_import_dead", "sub"));
        File.WriteAllText(Path.Combine(tempRoot, "uemod_import_dead", "sub", "big.pak"), "xxxx");
        Directory.CreateDirectory(Path.Combine(tempRoot, "用户放进来的东西"));

        var result = CreateService().ReclaimStaleImportTemp(tempRoot, FarFuture, Quiet);

        Assert.Equal(1, result.DeletedCount);
        Assert.False(Directory.Exists(Path.Combine(tempRoot, "uemod_import_dead")));
        Assert.True(Directory.Exists(Path.Combine(tempRoot, "用户放进来的东西")));
    }

    [Fact]
    public void ReclaimStaleImportTemp_ActiveExtraction_IsUntouched()
    {
        var tempRoot = Path.Combine(_repositoryRoot, ".import-tmp");
        var active = Path.Combine(tempRoot, "uemod_import_live");
        Directory.CreateDirectory(active);

        var result = CreateService().ReclaimStaleImportTemp(tempRoot, DateTime.UtcNow, Quiet);

        Assert.Equal(0, result.DeletedCount);
        Assert.True(Directory.Exists(active));
    }

    [Fact]
    public void ReclaimStaleImportTemp_MissingRoot_IsNoOp()
    {
        var result = CreateService().ReclaimStaleImportTemp(
            Path.Combine(_repositoryRoot, ".import-tmp"), FarFuture, Quiet);

        Assert.Equal(0, result.DeletedCount);
    }

    // ─── 深探测：子树时间戳 ───

    [Fact]
    public void BuildPlan_SubtreeWriteTime_BeatsDirectoryTimestamp()
    {
        // 包目录自身的 LastWriteTime 只在直接子项增删时更新：往 files/x.pak 里持续写
        // 不会让包目录"变新"。若只看目录自身的时间戳，正在导入的包会被误判成已静置。
        var dir = MakePartialPackage("BigMod");
        WriteIndex("BlackMyth");

        var old = DateTime.UtcNow.AddDays(-30);
        Directory.SetLastWriteTimeUtc(dir, old);
        Directory.SetLastWriteTimeUtc(Path.Combine(dir, "files"), old);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "files", "mod.pak"), DateTime.UtcNow);

        var plan = CreateService().BuildPlan(DateTime.UtcNow, Quiet);

        Assert.Empty(plan.Reclaimable);
    }
}
