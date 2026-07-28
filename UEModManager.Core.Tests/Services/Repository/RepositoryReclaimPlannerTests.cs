using UEModManager.Services.Repository;

namespace UEModManager.Core.Tests.Services.Repository;

/// <summary>
/// 仓库孤儿回收的判定测试。
///
/// 这里的每一条"不回收"用例都对应一种真实的误删场景，比"能回收"的用例重要得多：
/// 判据放宽一点点，删掉的就是用户几十 GB 的 MOD。
/// </summary>
public class RepositoryReclaimPlannerTests
{
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromHours(1);

    /// <summary>一个"证据齐全的导入残留"：无 manifest、形态是 files/、已静置一天。</summary>
    private static RepositoryEntryProbe Orphan(
        string name = "LeftoverMod",
        bool hasManifest = false,
        DateTime? lastWrite = null,
        long size = 1024,
        string[]? dirs = null,
        string[]? files = null)
        => new(
            name,
            hasManifest,
            lastWrite ?? Now.AddDays(-1),
            size,
            dirs ?? ["files"],
            files ?? []);

    private static RepositoryReclaimPlan Plan(
        IEnumerable<RepositoryEntryProbe> probes,
        IReadOnlyCollection<string>? registered = null,
        bool scanComplete = true)
        => RepositoryReclaimPlanner.Plan(probes, registered ?? [], scanComplete, Now, Quiet);

    // ─── 该回收的 ───

    [Fact]
    public void Plan_NoManifestNotIndexedIdle_IsReclaimable()
    {
        var plan = Plan([Orphan()]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RepositoryEntryDisposition.Reclaimable, entry.Disposition);
        Assert.Contains("导入残留", entry.Reason);
        Assert.Equal(1024, plan.ReclaimableBytes);
    }

    [Fact]
    public void Plan_PreviewImageAlongsideFiles_StillReclaimable()
    {
        // ObjectStore.StorePreviewImage 会在包目录里写 preview.*，这不影响"是仓库产物"的判断
        var plan = Plan([Orphan(files: ["preview.png"])]);

        Assert.Single(plan.Reclaimable);
    }

    [Fact]
    public void Plan_ReclaimableBytes_SumsOnlyReclaimableEntries()
    {
        var plan = Plan(
            [
                Orphan("A", size: 100),
                Orphan("B", size: 200, hasManifest: true),   // 失联包，不计入
                Orphan("C", size: 400),
            ]);

        Assert.Equal(500, plan.ReclaimableBytes);
        Assert.Equal(2, plan.Reclaimable.Count);
    }

    // ─── 绝不能回收的 ───

    [Fact]
    public void Plan_IndexedKey_IsKept()
    {
        var plan = Plan([Orphan("MyMod")], registered: ["MyMod"]);

        Assert.Equal(RepositoryEntryDisposition.Keep, Assert.Single(plan.Entries).Disposition);
    }

    [Fact]
    public void Plan_IndexedKeyDifferentCase_IsKept()
    {
        // PackageRepository.GetByKey 用 OrdinalIgnoreCase，回收判定必须同口径，
        // 否则 mymod / MyMod 会被当成两个键，已登记的包被判成孤儿删掉。
        var plan = Plan([Orphan("MyMod")], registered: ["mymod"]);

        Assert.Equal(RepositoryEntryDisposition.Keep, Assert.Single(plan.Entries).Disposition);
    }

    [Fact]
    public void Plan_IndexedByAnotherGame_IsKept()
    {
        // 仓库根跨游戏共享而索引按游戏分文件：只要任何一个游戏登记过它就是有主的
        var plan = Plan(
            [Orphan("BlackMythMod"), Orphan("StellarBladeMod")],
            registered: ["BlackMythMod", "StellarBladeMod"]);

        Assert.All(plan.Entries, e => Assert.Equal(RepositoryEntryDisposition.Keep, e.Disposition));
        Assert.Empty(plan.Reclaimable);
    }

    [Fact]
    public void Plan_HasManifestButNotIndexed_IsUnregisteredNotReclaimable()
    {
        // 注册流程先写 manifest 再写索引：有 manifest 说明数据是完整的，
        // 不在索引里更可能是索引写失败/索引损坏，删掉就是删用户唯一的副本
        var plan = Plan([Orphan("GhostMod", hasManifest: true)]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RepositoryEntryDisposition.Unregistered, entry.Disposition);
        Assert.Empty(plan.Reclaimable);
        Assert.Single(plan.Unregistered);
        Assert.Equal(0, plan.ReclaimableBytes);
    }

    [Fact]
    public void Plan_InternalDirectory_IsKept()
    {
        // .import-tmp 是正在进行的解压的落脚点，判成"残留包"删掉就是掐死用户的导入
        var plan = Plan([Orphan(".import-tmp", dirs: ["files"])]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RepositoryEntryDisposition.Keep, entry.Disposition);
        Assert.Contains("内部目录", entry.Reason);
    }

    [Fact]
    public void Plan_RegistryScanIncomplete_KeepsEverything()
    {
        var plan = Plan([Orphan("A"), Orphan("B")], scanComplete: false);

        Assert.Empty(plan.Reclaimable);
        Assert.All(plan.Entries, e => Assert.Contains("未能完整读出", e.Reason));
    }

    [Fact]
    public void Plan_RecentlyWritten_IsKept()
    {
        // 另一个实例正在往这个目录里导入
        var plan = Plan([Orphan(lastWrite: Now.AddMinutes(-5))]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RepositoryEntryDisposition.Keep, entry.Disposition);
        Assert.Contains("正在进行的导入", entry.Reason);
    }

    [Fact]
    public void Plan_MissingFilesSubdirectory_IsKept()
    {
        // 用户把仓库根指到了自己已有的文件夹，里面的子目录既没 manifest 也不在索引里
        var plan = Plan([Orphan("我的存档", dirs: [])]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RepositoryEntryDisposition.Keep, entry.Disposition);
        Assert.Contains("缺少 files 子目录", entry.Reason);
    }

    [Fact]
    public void Plan_ExtraSubdirectory_IsKept()
    {
        var plan = Plan([Orphan("SomeGame", dirs: ["files", "Saved"])]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RepositoryEntryDisposition.Keep, entry.Disposition);
        Assert.Contains("Saved", entry.Reason);
    }

    [Fact]
    public void Plan_ExtraTopLevelFile_IsKept()
    {
        var plan = Plan([Orphan(files: ["readme.txt"])]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(RepositoryEntryDisposition.Keep, entry.Disposition);
        Assert.Contains("readme.txt", entry.Reason);
    }

    // ─── 深探测门槛 ───

    [Theory]
    [InlineData(".import-tmp", false, false)]   // 内部目录
    [InlineData("Registered", false, false)]    // 已登记
    [InlineData("HasManifest", true, false)]    // 有 manifest
    [InlineData("Candidate", false, true)]      // 唯一需要深探测的形态
    public void NeedsDeepProbe_OnlyForRealCandidates(string name, bool hasManifest, bool expected)
    {
        var registered = new HashSet<string>(["Registered"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, RepositoryReclaimPlanner.NeedsDeepProbe(name, hasManifest, registered));
    }

    // ─── 解压临时目录 ───

    [Fact]
    public void PlanImportTemp_StaleGeneratedDirectory_IsReclaimable()
    {
        var entries = RepositoryReclaimPlanner.PlanImportTemp(
            [Orphan("uemod_import_" + Guid.NewGuid().ToString("N"), lastWrite: Now.AddHours(-3))],
            Now, Quiet);

        Assert.Equal(RepositoryEntryDisposition.Reclaimable, Assert.Single(entries).Disposition);
    }

    [Fact]
    public void PlanImportTemp_ForeignDirectory_IsKept()
    {
        // 名字不是本程序生成的就不碰 —— 用户可能把别的东西放进了 .import-tmp
        var entries = RepositoryReclaimPlanner.PlanImportTemp(
            [Orphan("用户自己的文件夹", lastWrite: Now.AddDays(-30))], Now, Quiet);

        var entry = Assert.Single(entries);
        Assert.Equal(RepositoryEntryDisposition.Keep, entry.Disposition);
        Assert.Contains("不是本程序生成", entry.Reason);
    }

    [Fact]
    public void PlanImportTemp_ActiveExtraction_IsKept()
    {
        var entries = RepositoryReclaimPlanner.PlanImportTemp(
            [Orphan("uemod_import_abc", lastWrite: Now.AddSeconds(-30))], Now, Quiet);

        var entry = Assert.Single(entries);
        Assert.Equal(RepositoryEntryDisposition.Keep, entry.Disposition);
        Assert.Contains("正在进行的解压", entry.Reason);
    }

    [Fact]
    public void DefaultQuietPeriod_IsGenerousEnoughForSlowCopies()
    {
        // 静置期是"正在导入的包不会被误删"的最后一道闸，缩短它等于放大误删窗口。
        // 这条断言存在的意义是：想改这个值的人必须先解释为什么。
        Assert.Equal(TimeSpan.FromHours(1), RepositoryReclaimPlanner.DefaultQuietPeriod);
    }
}
