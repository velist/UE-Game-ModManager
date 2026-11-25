using UEModManager.Models;
using UEModManager.Services.Reconciliation;

namespace UEModManager.Core.Tests.Services.Reconciliation;

/// <summary>
/// 「管理器记着的」和「磁盘上真有的」对账。
///
/// <para>
/// 这份报告会被用来提议"从列表里移除"，所以这里最重的是护栏那一组：
/// 位置访问不到、文件探测不了的时候，<b>一个包都不许判成丢失</b>。
/// 移动硬盘没插好就把玩家的 MOD 记录清空，是这个功能最不能出的事。
/// </para>
/// </summary>
public class DiskReconcilerTests
{
    // ─── 一个内存里的假磁盘 ───

    private sealed class FakeDisk
    {
        /// <summary>仓库里的文件：相对仓库根的路径 → 大小。</summary>
        public Dictionary<string, long> Repository { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>游戏目录里的文件：绝对路径 → 大小。</summary>
        public Dictionary<string, long> Game { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>探测不了的路径（仓库的相对路径或游戏的绝对路径）。</summary>
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>files 目录下还留着没登记文件的包。</summary>
        public HashSet<string> PackagesWithStrayFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RepositoryProbes { get; } = [];
        public List<string> GameProbes { get; } = [];
        public List<string> StrayFileProbes { get; } = [];

        public DiskProbes Probes => new(
            path =>
            {
                RepositoryProbes.Add(path);
                if (Unreadable.Contains(path)) return FileProbe.Unknown;
                return Repository.TryGetValue(path, out var size) ? FileProbe.Present(size) : FileProbe.Missing;
            },
            path =>
            {
                GameProbes.Add(path);
                if (Unreadable.Contains(path)) return FileProbe.Unknown;
                return Game.TryGetValue(path, out var size) ? FileProbe.Present(size) : FileProbe.Missing;
            },
            key =>
            {
                StrayFileProbes.Add(key);
                return PackagesWithStrayFiles.Contains(key);
            });
    }

    private static Package Pkg(string key, params (string File, long Size, string Hash)[] files) => new()
    {
        PackageKey = key,
        DisplayName = key + " 显示名",
        HostGameName = "Test",
        Artifacts = files.Select(f => new PackageArtifact
        {
            FileName = f.File,
            RelativeSourcePath = $@"{key}\files\{f.File}",
            RelativeTargetPath = f.File,
            FileSize = f.Size,
            FileHash = f.Hash,
        }).ToList(),
    };

    private static ManagedDeploymentFile Managed(string key, string file, long size, string hash) => new()
    {
        PackageKey = key,
        PackageDisplayName = key + " 显示名",
        TargetPath = $@"G:\Game\Mods\{key}\{file}",
        RelativeTargetPath = $@"{key}\{file}",
        FileSize = size,
        FileHash = hash,
    };

    /// <summary>把包登记的文件、清单里的文件都按登记的大小放到假磁盘上——一切正常的起点。</summary>
    private static FakeDisk IntactDisk(IEnumerable<Package> packages, IEnumerable<ManagedDeploymentFile>? managed = null)
    {
        var disk = new FakeDisk();
        foreach (var artifact in packages.SelectMany(p => p.Artifacts))
            disk.Repository[artifact.RelativeSourcePath] = artifact.FileSize;
        foreach (var file in managed ?? [])
            disk.Game[file.TargetPath] = file.FileSize;
        return disk;
    }

    private static DiskReconcileInput Input(
        IReadOnlyList<Package> packages,
        IReadOnlyList<ManagedDeploymentFile>? managed = null,
        IEnumerable<string>? enabled = null,
        bool repositoryReachable = true,
        bool gameReachable = true) => new()
    {
        Packages = packages,
        ManagedFiles = managed ?? [],
        EnabledPackageKeys = (enabled ?? []).ToList(),
        RepositoryRootReachable = repositoryReachable,
        GameRootReachable = gameReachable,
    };

    // ─── 一切正常 ───

    [Fact]
    public void Reconcile_EverythingInPlace_ReportsNothing()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")), Pkg("b", ("b.pak", 5, "h3")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1"), Managed("a", "a.ucas", 20, "h2") };
        var disk = IntactDisk(packages, managed);

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        Assert.False(report.HasFindings);
        Assert.Empty(report.LostPackages);
        Assert.Empty(report.PartialPackages);
        Assert.Empty(report.ModifiedPackages);
        Assert.Empty(report.MissingDeployments);
        Assert.Empty(report.UncheckedPackageKeys);
        Assert.False(report.SuspectedRepositoryLocationProblem);
        Assert.Equal(2, report.CheckedPackageCount);
        Assert.Equal(3, report.CheckedSourceFileCount);
        Assert.Equal(2, report.CheckedManagedFileCount);
    }

    [Fact]
    public void Reconcile_NothingRegisteredAtAll_IsNotAProblemEvenWithoutAnyDirectory()
    {
        // 全新安装：仓库目录要到第一次导入才创建，游戏目录也可能还没配
        var report = DiskReconciler.Reconcile(
            Input([], repositoryReachable: false, gameReachable: false), new FakeDisk().Probes);

        Assert.False(report.HasFindings);
        Assert.False(report.RepositoryRootUnreachable);
        Assert.False(report.GameRootUnreachable);
    }

    [Fact]
    public void Reconcile_DoesNotNeedHashesOrExtraProbesWhenNothingIsWrong()
    {
        // 只做"在不在 + 多大"：每个文件探一次，不去翻 files 目录
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1") };
        var disk = IntactDisk(packages, managed);

        DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        Assert.Equal([@"a\files\a.pak"], disk.RepositoryProbes);
        Assert.Equal([@"G:\Game\Mods\a\a.pak"], disk.GameProbes);
        Assert.Empty(disk.StrayFileProbes);
    }

    // ─── 仓库侧：整包丢失 / 部分丢失 / 被改过 ───

    [Fact]
    public void Reconcile_AllSourceFilesGone_IsLostAndOtherPackagesStayClean()
    {
        var packages = new[] { Pkg("gone", ("x.pak", 10, "h1"), ("x.ucas", 20, "h2")), Pkg("kept", ("k.pak", 5, "h3")) };
        var disk = IntactDisk(packages);
        disk.Repository.Remove(@"gone\files\x.pak");
        disk.Repository.Remove(@"gone\files\x.ucas");

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        var lost = Assert.Single(report.LostPackages);
        Assert.Equal("gone", lost.PackageKey);
        Assert.Equal("gone 显示名", lost.DisplayName);
        Assert.Equal(2, lost.TotalFiles);
        Assert.Equal([@"gone\files\x.pak", @"gone\files\x.ucas"], lost.MissingFiles);
        Assert.False(lost.IsEnabled);
        Assert.False(lost.BlocksDeployment);
        Assert.False(lost.HasDeployedFiles);
        Assert.Equal(0, lost.ManagedFileCount);
        Assert.Empty(report.PartialPackages);
        Assert.True(report.HasFindings);
        // 还有一个包好好的：位置没问题，就是这一个被删了
        Assert.False(report.SuspectedRepositoryLocationProblem);
    }

    [Fact]
    public void Reconcile_SomeSourceFilesGone_IsPartialNotLost()
    {
        var packages = new[] { Pkg("half", ("x.pak", 10, "h1"), ("x.ucas", 20, "h2"), ("x.utoc", 3, "h3")) };
        var disk = IntactDisk(packages);
        disk.Repository.Remove(@"half\files\x.ucas");

        var report = DiskReconciler.Reconcile(Input(packages, enabled: ["half"]), disk.Probes);

        Assert.Empty(report.LostPackages);
        var partial = Assert.Single(report.PartialPackages);
        Assert.Equal([@"half\files\x.ucas"], partial.MissingFiles);
        Assert.Equal(3, partial.TotalFiles);
        Assert.True(partial.BlocksDeployment);
        Assert.False(report.SuspectedRepositoryLocationProblem);
    }

    [Fact]
    public void Reconcile_RegisteredFilesGoneButStrayFilesRemain_IsPartialNotLost()
    {
        // 登记的一个都找不到，但 files 目录里还有别的文件：可能是被改了名、挪了层级。
        // 这不是"文件已经不在了"，不能提议当成整包丢失来移除。
        var packages = new[] { Pkg("renamed", ("x.pak", 10, "h1")), Pkg("ok", ("k.pak", 5, "h3")) };
        var disk = IntactDisk(packages);
        disk.Repository.Remove(@"renamed\files\x.pak");
        disk.PackagesWithStrayFiles.Add("renamed");

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        Assert.Empty(report.LostPackages);
        Assert.Equal("renamed", Assert.Single(report.PartialPackages).PackageKey);
        Assert.Equal(["renamed"], disk.StrayFileProbes);
    }

    [Fact]
    public void Reconcile_SizeDiffersFromRegistered_IsReportedAsModifiedNotAsAProblem()
    {
        var packages = new[] { Pkg("edited", ("settings.ini", 10, "h1")) };
        var disk = IntactDisk(packages);
        disk.Repository[@"edited\files\settings.ini"] = 12;

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        var modified = Assert.Single(report.ModifiedPackages);
        Assert.Equal([@"edited\files\settings.ini"], modified.ChangedFiles);
        Assert.Empty(modified.MissingFiles);
        Assert.Empty(report.LostPackages);
        Assert.Empty(report.PartialPackages);
        Assert.False(report.HasFindings);
    }

    [Fact]
    public void Reconcile_NegativeRegisteredSize_SkipsTheSizeComparison()
    {
        var packages = new[] { Pkg("legacy", ("old.pak", -1, "h1")) };
        var disk = IntactDisk(packages);
        disk.Repository[@"legacy\files\old.pak"] = 999;

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        Assert.Empty(report.ModifiedPackages);
        Assert.False(report.HasFindings);
    }

    [Fact]
    public void Reconcile_DuplicateRegistrations_AreCountedOnce()
    {
        var package = Pkg("dup", ("x.pak", 10, "h1"));
        package.Artifacts.Add(new PackageArtifact
        {
            FileName = "x.pak", RelativeSourcePath = "dup/files/x.pak", RelativeTargetPath = "x.pak", FileSize = 10, FileHash = "h1",
        });
        var disk = new FakeDisk();

        var report = DiskReconciler.Reconcile(Input([package, Pkg("ok", ("k.pak", 1, "h"))]), disk.Probes);

        var lost = Assert.Single(report.LostPackages, f => f.PackageKey == "dup");
        Assert.Equal(1, lost.TotalFiles);
        Assert.Single(lost.MissingFiles);
    }

    [Fact]
    public void Reconcile_PackageWithoutRegisteredFiles_IsNeverCalledLost()
    {
        var empty = new Package { PackageKey = "empty", DisplayName = "empty", HostGameName = "Test" };

        var report = DiskReconciler.Reconcile(Input([empty]), new FakeDisk().Probes);

        Assert.False(report.HasFindings);
        Assert.Equal(0, report.CheckedPackageCount);
    }

    // ─── 护栏：位置访问不到 / 探测不了 / 全都找不到 ───

    [Fact]
    public void Reconcile_RepositoryRootUnreachable_NeverCallsAnyPackageLost()
    {
        // 移动硬盘没插、网络盘掉线：仓库里一个文件都探不到，但那不是"MOD 被删了"
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")), Pkg("b", ("b.pak", 5, "h2")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1") };
        var disk = new FakeDisk();
        disk.Game[managed[0].TargetPath] = 10;

        var report = DiskReconciler.Reconcile(
            Input(packages, managed, ["a"], repositoryReachable: false), disk.Probes);

        Assert.True(report.RepositoryRootUnreachable);
        Assert.Empty(report.LostPackages);
        Assert.Empty(report.PartialPackages);
        Assert.Empty(report.UncheckedPackageKeys);
        Assert.False(report.SuspectedRepositoryLocationProblem);
        Assert.Equal(0, report.CheckedPackageCount);
        // 压根没去探仓库：位置都不在，探出来的"不存在"没有意义
        Assert.Empty(disk.RepositoryProbes);
        Assert.Empty(disk.StrayFileProbes);
        Assert.True(report.HasFindings);
    }

    [Fact]
    public void Reconcile_RepositoryRootUnreachable_DoesNotOfferReinstallEither()
    {
        // 仓库不在，游戏目录里又丢了文件：重新装回去需要仓库里的源文件，现在做不到
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1") };

        var report = DiskReconciler.Reconcile(
            Input(packages, managed, ["a"], repositoryReachable: false), new FakeDisk().Probes);

        var missing = Assert.Single(report.MissingDeployments);
        Assert.False(missing.CanReinstall);
        Assert.Empty(report.RestorableDeployments);
    }

    [Fact]
    public void Reconcile_GameRootUnreachable_NeverCallsAnyDeploymentMissing()
    {
        // 游戏所在的盘不在线：清单里的文件一个都探不到，但那不是"被游戏更新清掉了"
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")), Pkg("b", ("b.pak", 5, "h2")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1"), Managed("b", "b.pak", 5, "h2") };
        var disk = IntactDisk(packages);

        var report = DiskReconciler.Reconcile(
            Input(packages, managed, ["a", "b"], gameReachable: false), disk.Probes);

        Assert.True(report.GameRootUnreachable);
        Assert.Empty(report.MissingDeployments);
        Assert.Equal(0, report.CheckedManagedFileCount);
        Assert.Empty(disk.GameProbes);
        Assert.True(report.HasFindings);
    }

    [Fact]
    public void Reconcile_GameRootUnreachable_LostPackageDoesNotPretendTheGameHasNoCopy()
    {
        // 仓库里的丢了，游戏目录又访问不到：不知道游戏里还有没有它的文件，
        // 不能按"游戏里没有"处理（那样移除记录时会把游戏里仅剩的那份当成没人管的）
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")), Pkg("ok", ("k.pak", 1, "h")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1") };
        var disk = IntactDisk(packages);
        disk.Repository.Remove(@"a\files\a.pak");

        var report = DiskReconciler.Reconcile(
            Input(packages, managed, ["a"], gameReachable: false), disk.Probes);

        var lost = Assert.Single(report.LostPackages);
        Assert.True(lost.DeployedFilesUnknown);
        Assert.False(lost.HasDeployedFiles);
        Assert.False(lost.CanRecoverEverything);
        Assert.Equal(1, lost.ManagedFileCount);
    }

    [Fact]
    public void Reconcile_EveryPackageMissingAtOnce_IsTreatedAsALocationProblemFirst()
    {
        // 仓库根在，但里面是空的：盘符变了、文件夹被整个移走、指到了一个空文件夹
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")), Pkg("b", ("b.pak", 5, "h2")), Pkg("c", ("c.pak", 7, "h3")) };

        var report = DiskReconciler.Reconcile(Input(packages), new FakeDisk().Probes);

        Assert.True(report.SuspectedRepositoryLocationProblem);
        Assert.Equal(3, report.LostPackages.Count);
        Assert.False(report.RepositoryRootUnreachable);
    }

    [Fact]
    public void Reconcile_OnlyPackageMissing_IsAlsoALocationSuspect()
    {
        // 只有一个包时"全部丢失"和"它被删了"分不出来：一样先按位置问题问，由玩家确认
        var report = DiskReconciler.Reconcile(Input([Pkg("only", ("a.pak", 10, "h1"))]), new FakeDisk().Probes);

        Assert.True(report.SuspectedRepositoryLocationProblem);
        Assert.Single(report.LostPackages);
    }

    [Fact]
    public void Reconcile_OneIntactPackage_ProvesTheLocationIsRight()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")), Pkg("b", ("b.pak", 5, "h2")), Pkg("c", ("c.pak", 7, "h3")) };
        var disk = new FakeDisk();
        disk.Repository[@"c\files\c.pak"] = 7;

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        Assert.False(report.SuspectedRepositoryLocationProblem);
        Assert.Equal(2, report.LostPackages.Count);
    }

    [Fact]
    public void Reconcile_AModifiedOrPartialPackage_AlsoProvesTheLocationIsRight()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")), Pkg("b", ("b.pak", 5, "h2"), ("b.ucas", 6, "h4")) };
        var disk = new FakeDisk();
        disk.Repository[@"b\files\b.pak"] = 5;

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        Assert.False(report.SuspectedRepositoryLocationProblem);
        Assert.Single(report.LostPackages);
        Assert.Single(report.PartialPackages);
    }

    [Fact]
    public void Reconcile_UnreadableFile_MakesThePackageUncheckedNotLost()
    {
        var packages = new[] { Pkg("locked", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")), Pkg("ok", ("k.pak", 1, "h")) };
        var disk = IntactDisk(packages);
        disk.Repository.Remove(@"locked\files\a.pak");
        disk.Unreadable.Add(@"locked\files\a.ucas");

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        Assert.Equal(["locked"], report.UncheckedPackageKeys);
        Assert.Empty(report.LostPackages);
        Assert.Empty(report.PartialPackages);
        Assert.False(report.HasFindings);
    }

    [Fact]
    public void Reconcile_UnreadablePackagesDoNotProveTheLocation()
    {
        // 一个丢了、另一个探测不了：没有任何一个包能证明位置是对的
        var packages = new[] { Pkg("gone", ("a.pak", 10, "h1")), Pkg("locked", ("b.pak", 5, "h2")) };
        var disk = new FakeDisk();
        disk.Unreadable.Add(@"locked\files\b.pak");

        var report = DiskReconciler.Reconcile(Input(packages), disk.Probes);

        Assert.True(report.SuspectedRepositoryLocationProblem);
        Assert.Single(report.LostPackages);
        Assert.Equal(["locked"], report.UncheckedPackageKeys);
    }

    [Fact]
    public void Reconcile_UnreadableGameFile_IsNotCountedAsMissing()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1") };
        var disk = IntactDisk(packages);
        disk.Unreadable.Add(managed[0].TargetPath);

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        Assert.Empty(report.MissingDeployments);
        Assert.False(report.HasFindings);
    }

    // ─── 游戏侧：装好的文件不见了 ───

    [Fact]
    public void Reconcile_DeployedFilesGone_AreSummarisedPerPackage()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")), Pkg("b", ("b.pak", 5, "h3")) };
        var managed = new[]
        {
            Managed("a", "a.pak", 10, "h1"), Managed("a", "a.ucas", 20, "h2"), Managed("b", "b.pak", 5, "h3"),
        };
        var disk = IntactDisk(packages, managed);
        disk.Game.Remove(@"G:\Game\Mods\a\a.pak");
        disk.Game.Remove(@"G:\Game\Mods\a\a.ucas");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a", "b"]), disk.Probes);

        var missing = Assert.Single(report.MissingDeployments);
        Assert.Equal("a", missing.PackageKey);
        Assert.Equal("a 显示名", missing.DisplayName);
        Assert.Equal(2, missing.ManagedFileCount);
        Assert.Equal(2, missing.MissingFiles.Count);
        Assert.True(missing.IsEnabled);
        Assert.True(missing.IsKnownPackage);
        Assert.True(missing.CanReinstall);
        Assert.Equal(missing, Assert.Single(report.RestorableDeployments));
        Assert.True(report.HasFindings);
        // 仓库侧没有任何问题
        Assert.Empty(report.LostPackages);
        Assert.Empty(report.PartialPackages);
    }

    [Fact]
    public void Reconcile_OnlySomeDeployedFilesGone_StillReported()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1"), Managed("a", "a.ucas", 20, "h2") };
        var disk = IntactDisk(packages, managed);
        disk.Game.Remove(@"G:\Game\Mods\a\a.ucas");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        var missing = Assert.Single(report.MissingDeployments);
        Assert.Equal([@"G:\Game\Mods\a\a.ucas"], missing.MissingFiles);
        Assert.Equal(2, missing.ManagedFileCount);
    }

    [Fact]
    public void Reconcile_MissingFilesOfADisabledPackage_AreNotSomethingToReinstall()
    {
        // 清单里留着、方案里已经不启用：这是没清干净的旧记录，不是"启用的 MOD 没生效"
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1") };
        var disk = IntactDisk(packages);

        var report = DiskReconciler.Reconcile(Input(packages, managed, enabled: []), disk.Probes);

        var missing = Assert.Single(report.MissingDeployments);
        Assert.False(missing.IsEnabled);
        Assert.False(missing.CanReinstall);
        Assert.Empty(report.RestorableDeployments);
        Assert.False(report.HasFindings);
    }

    [Fact]
    public void Reconcile_MissingFilesOfAnUnknownOwner_AreReportedButNotReinstallable()
    {
        // 生成物、用户修复这类条目不属于任何包
        var managed = new[] { Managed("generated", "merged.ini", 10, "h1") };

        var report = DiskReconciler.Reconcile(Input([], managed, ["generated"]), new FakeDisk().Probes);

        var missing = Assert.Single(report.MissingDeployments);
        Assert.False(missing.IsKnownPackage);
        Assert.False(missing.CanReinstall);
        Assert.Equal("generated 显示名", missing.DisplayName);
        Assert.False(report.HasFindings);
    }

    [Fact]
    public void Reconcile_DeployedFilesGoneAndSourceAlsoBroken_CannotBeReinstalled()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")), Pkg("ok", ("k.pak", 1, "h")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1"), Managed("a", "a.ucas", 20, "h2") };
        var disk = IntactDisk(packages);
        disk.Repository.Remove(@"a\files\a.ucas");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        Assert.False(Assert.Single(report.MissingDeployments).CanReinstall);
        Assert.Single(report.PartialPackages);
    }

    [Fact]
    public void Reconcile_KeysAreMatchedIgnoringCase()
    {
        var packages = new[] { Pkg("MyMod", ("a.pak", 10, "h1")) };
        var managed = new[] { Managed("mymod", "a.pak", 10, "h1") };
        var disk = IntactDisk(packages);

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["MYMOD"]), disk.Probes);

        var missing = Assert.Single(report.MissingDeployments);
        Assert.True(missing.IsKnownPackage);
        Assert.True(missing.CanReinstall);
    }

    // ─── 仓库丢了、游戏里那份还在：能不能找回 ───

    [Fact]
    public void Reconcile_LostPackageStillInstalledInGame_IsFullyRecoverable()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")), Pkg("ok", ("k.pak", 1, "h")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1"), Managed("a", "a.ucas", 20, "h2") };
        var disk = IntactDisk(packages, managed);
        disk.Repository.Remove(@"a\files\a.pak");
        disk.Repository.Remove(@"a\files\a.ucas");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        var lost = Assert.Single(report.LostPackages);
        Assert.True(lost.CanRecoverEverything);
        Assert.True(lost.HasDeployedFiles);
        Assert.True(lost.BlocksDeployment);
        Assert.Equal(2, lost.ManagedFileCount);
        Assert.Equal(2, lost.DeployedFilesPresent.Count);
        Assert.False(lost.DeployedFilesUnknown);
        Assert.Equal(
            [(@"a\files\a.pak", @"G:\Game\Mods\a\a.pak", 10L, "h1"), (@"a\files\a.ucas", @"G:\Game\Mods\a\a.ucas", 20L, "h2")],
            lost.Recoverable.Select(r => (r.RelativeSourcePath, r.GameFilePath, r.FileSize, r.FileHash)));
        // 游戏里的文件好好的，游戏侧没有问题
        Assert.Empty(report.MissingDeployments);
    }

    [Fact]
    public void Reconcile_RecoverableCopyIsMatchedByContentNotByPath()
    {
        // 部署时路径会归位（多余的包装层被去掉、套上包目录），和仓库里的相对路径对不上是常态
        var package = new Package
        {
            PackageKey = "ue4ss", DisplayName = "ue4ss", HostGameName = "Test",
            Artifacts =
            [
                new PackageArtifact
                {
                    FileName = "main.lua", RelativeSourcePath = @"ue4ss\files\Wrapper\Mods\M\Scripts\main.lua",
                    RelativeTargetPath = @"Wrapper\Mods\M\Scripts\main.lua", FileSize = 7, FileHash = "lua",
                },
            ],
        };
        var managed = new[]
        {
            new ManagedDeploymentFile
            {
                PackageKey = "ue4ss", TargetPath = @"G:\Game\ue4ss\Mods\M\Scripts\main.lua",
                RelativeTargetPath = @"ue4ss\Mods\M\Scripts\main.lua", FileSize = 7, FileHash = "LUA",
            },
        };
        var disk = new FakeDisk();
        disk.Game[managed[0].TargetPath] = 7;

        var report = DiskReconciler.Reconcile(Input([package, Pkg("ok", ("k.pak", 1, "h"))], managed, ["ue4ss"]),
            IntactDiskWith(disk, "ok", "k.pak", 1).Probes);

        var recoverable = Assert.Single(Assert.Single(report.LostPackages).Recoverable);
        Assert.Equal(@"G:\Game\ue4ss\Mods\M\Scripts\main.lua", recoverable.GameFilePath);
    }

    private static FakeDisk IntactDiskWith(FakeDisk disk, string key, string file, long size)
    {
        disk.Repository[$@"{key}\files\{file}"] = size;
        return disk;
    }

    [Fact]
    public void Reconcile_GameCopyWithDifferentContent_IsNotRecoverable()
    {
        // 合并出来的配置、被游戏更新换掉的文件：哈希或大小对不上，不是原来那一份
        var packages = new[] { Pkg("cfg", ("Engine.ini", 10, "original")), Pkg("ok", ("k.pak", 1, "h")) };
        var managed = new[] { Managed("cfg", "Engine.ini", 14, "merged") };
        var disk = IntactDisk(packages, managed);
        disk.Repository.Remove(@"cfg\files\Engine.ini");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["cfg"]), disk.Probes);

        var lost = Assert.Single(report.LostPackages);
        Assert.Empty(lost.Recoverable);
        Assert.False(lost.CanRecoverEverything);
        // 但游戏里确实还留着它的文件：移除记录时不能顺手删掉
        Assert.True(lost.HasDeployedFiles);
    }

    [Fact]
    public void Reconcile_GameCopyChangedSizeOnDisk_IsNotRecoverable()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1")), Pkg("ok", ("k.pak", 1, "h")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1") };
        var disk = IntactDisk(packages, managed);
        disk.Repository.Remove(@"a\files\a.pak");
        disk.Game[managed[0].TargetPath] = 11;

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        var lost = Assert.Single(report.LostPackages);
        Assert.Empty(lost.Recoverable);
        Assert.True(lost.HasDeployedFiles);
    }

    [Fact]
    public void Reconcile_OnlyPartOfTheMissingFilesInGame_IsNotFullyRecoverable()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")), Pkg("ok", ("k.pak", 1, "h")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1"), Managed("a", "a.ucas", 20, "h2") };
        var disk = IntactDisk(packages, managed);
        disk.Repository.Remove(@"a\files\a.pak");
        disk.Repository.Remove(@"a\files\a.ucas");
        disk.Game.Remove(@"G:\Game\Mods\a\a.ucas");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        var lost = Assert.Single(report.LostPackages);
        Assert.Single(lost.Recoverable);
        Assert.False(lost.CanRecoverEverything);
        Assert.True(lost.HasDeployedFiles);
        // 两边都缺：游戏侧也报，但装不回去
        Assert.False(Assert.Single(report.MissingDeployments).CanReinstall);
    }

    [Fact]
    public void Reconcile_PartialPackageWhoseMissingFileIsInGame_IsRecoverable()
    {
        var packages = new[] { Pkg("a", ("a.pak", 10, "h1"), ("a.ucas", 20, "h2")) };
        var managed = new[] { Managed("a", "a.pak", 10, "h1"), Managed("a", "a.ucas", 20, "h2") };
        var disk = IntactDisk(packages, managed);
        disk.Repository.Remove(@"a\files\a.ucas");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["a"]), disk.Probes);

        var partial = Assert.Single(report.PartialPackages);
        Assert.True(partial.CanRecoverEverything);
        Assert.Equal(@"a\files\a.ucas", Assert.Single(partial.Recoverable).RelativeSourcePath);
    }

    [Fact]
    public void Reconcile_FileWithoutRegisteredHash_IsNeverOfferedForRecovery()
    {
        // 没有哈希就没法确认游戏里那份是不是原来的内容
        var package = Pkg("a", ("a.pak", 10, ""));
        var managed = new[] { Managed("a", "a.pak", 10, "") };
        var disk = new FakeDisk();
        disk.Game[managed[0].TargetPath] = 10;

        var report = DiskReconciler.Reconcile(Input([package], managed, ["a"]), disk.Probes);

        Assert.Empty(Assert.Single(report.LostPackages).Recoverable);
    }

    [Fact]
    public void Reconcile_AnotherPackagesGameFile_IsNeverUsedForRecovery()
    {
        var packages = new[] { Pkg("a", ("x.pak", 10, "same")), Pkg("b", ("x.pak", 10, "same")) };
        var managed = new[] { Managed("b", "x.pak", 10, "same") };
        var disk = IntactDisk(packages, managed);
        disk.Repository.Remove(@"a\files\x.pak");

        var report = DiskReconciler.Reconcile(Input(packages, managed, ["b"]), disk.Probes);

        var lost = Assert.Single(report.LostPackages);
        Assert.Equal("a", lost.PackageKey);
        Assert.Empty(lost.Recoverable);
        Assert.False(lost.HasDeployedFiles);
    }

    // ─── 入参 ───

    [Fact]
    public void Reconcile_NullArguments_Throw()
    {
        var probes = new FakeDisk().Probes;
        Assert.Throws<ArgumentNullException>(() => DiskReconciler.Reconcile(null!, probes));
        Assert.Throws<ArgumentNullException>(() => DiskReconciler.Reconcile(Input([]), null!));
        Assert.Throws<ArgumentException>(() =>
            DiskReconciler.Reconcile(new DiskReconcileInput { Packages = null! }, probes));
    }
}
