using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Backends;
using UEModManager.Services.Reconciliation;

namespace UEModManager.Tests.Services;

/// <summary>
/// 磁盘核对服务与它依赖的几处落盘改动：只读的核对、从游戏目录找回、把文件移出部署清单、重读包索引。
///
/// <para>
/// 硬链接部署在这里测（复制部署由 <see cref="DiskRefreshEndToEndTests"/> 走完整流程）：
/// 硬链接下游戏目录里的文件和仓库里的源文件是同一份数据，"删掉其中一头"之后另一头是什么状态、
/// 核对与找回会不会把两头搞混，必须在<b>真实的硬链接</b>上验证。
/// </para>
/// </summary>
public sealed class DiskReconcileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "reconcile-" + Guid.NewGuid().ToString("N"));
    private DiskRefreshHost? _host;

    private async Task<DiskRefreshHost> OpenAsync(params IDeploymentBackend[] backends)
        => _host = await DiskRefreshHost.OpenAsync(_root, backends);

    public void Dispose()
    {
        try
        {
            _host?.Deployment.PendingBackupCleanup.GetAwaiter().GetResult();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ═══════════════════════════════════════
    //  核对是只读的
    // ═══════════════════════════════════════

    [Fact]
    public Task Check_NeverWritesAnything_EvenWhenThereAreProblems() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("lost", ("l.bin", "LLLL"));
        await host.AddPackageAsync("installed", ("i.bin", "IIII"));
        await host.EnableAsync("installed");
        Directory.Delete(host.Store.GetPackageDirectory("lost"), recursive: true);
        Directory.Delete(host.ModPath, recursive: true);
        var before = host.Fingerprint();

        var check = await host.Reconcile.CheckAsync();

        Assert.Equal("lost", Assert.Single(check.Report.LostPackages).PackageKey);
        Assert.Equal("installed", Assert.Single(check.Report.RestorableDeployments).PackageKey);
        Assert.Equal(host.RepositoryRoot, check.RepositoryRoot);
        Assert.Equal(host.GamePath, check.GamePath);
        Assert.Equal(before, host.Fingerprint());
        // 被删掉的 MOD 目录没有被顺手建回来
        Assert.False(Directory.Exists(host.ModPath));
    });

    [Fact]
    public Task Check_FilesDirectoryEmptiedButFolderLeftBehind_IsStillLost() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 玩家只删了 files 里的东西，包文件夹（连同 manifest.json）还在：MOD 的内容一样是没了
        var host = await OpenAsync();
        await host.AddPackageAsync("hollow", ("a.bin", "AAAA"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        await host.Repository.RewriteManifestAsync("hollow");
        File.Delete(host.RepositoryFile("hollow", "a.bin"));
        Assert.True(File.Exists(host.Store.GetManifestPath("hollow")));

        var report = (await host.Reconcile.CheckAsync()).Report;

        Assert.Equal("hollow", Assert.Single(report.LostPackages).PackageKey);
        Assert.Empty(report.PartialPackages);
    });

    [Fact]
    public Task Check_UnregisteredFileLeftInFilesDirectory_IsPartialNotLost() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 登记的文件找不到，但 files 里还有别的文件（多半是被改了名）：不能当成"已经不在了"
        var host = await OpenAsync();
        await host.AddPackageAsync("renamed", ("a.bin", "AAAA"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        File.Move(host.RepositoryFile("renamed", "a.bin"), host.RepositoryFile("renamed", "a-renamed.bin"));

        var report = (await host.Reconcile.CheckAsync()).Report;

        Assert.Empty(report.LostPackages);
        Assert.Equal("renamed", Assert.Single(report.PartialPackages).PackageKey);
    });

    [Fact]
    public Task Check_NoGameConfiguredYet_OnlyLooksAtTheRepository() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        host.Game.Config.GamePath = "";
        host.Game.Config.ModPath = "";

        var report = (await host.Reconcile.CheckAsync()).Report;

        Assert.False(report.HasFindings);
        Assert.False(report.GameRootUnreachable);
        Assert.Equal(1, report.CheckedPackageCount);
    });

    [Fact]
    public Task Check_OnlyOldVersionDeploymentRecordsExist_DoesNotPretendNothingIsInstalled() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 从老版本升级上来、还没部署过：清单是空的，游戏目录里的文件记在旧格式的事务里，
        // 要到下一次部署才会被认领进清单。这时"清单里没有它的文件"不等于"游戏里没有它"。
        var host = await OpenAsync();
        await host.AddPackageAsync("legacy", ("l.bin", "LLLL"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        var legacyBackups = Path.Combine(host.Root, "LegacyBackups");
        var transactionDirectory = Path.Combine(legacyBackups, "0123456789ab");
        Directory.CreateDirectory(transactionDirectory);
        var transaction = new DeploymentTransaction
        {
            HostGameName = DiskRefreshHost.GameName, BackupDirectory = transactionDirectory,
            Status = DeploymentStatus.Committed, TotalOperations = 1, CompletedOperations = 1,
            ExecutedOperations =
            [
                new DeploymentOperation
                {
                    Type = DeploymentOperationType.Add, PackageKey = "legacy", PackageDisplayName = "MOD legacy",
                    SourcePath = host.RepositoryFile("legacy", "l.bin"), TargetPath = host.GameFile("legacy", "l.bin"),
                    RelativeTargetPath = @"legacy\l.bin", FileHash = "abc", FileSize = 4,
                },
            ],
        };
        await File.WriteAllTextAsync(Path.Combine(transactionDirectory, "transaction.json"),
            System.Text.Json.JsonSerializer.Serialize(transaction));
        Directory.CreateDirectory(Path.GetDirectoryName(host.GameFile("legacy", "l.bin"))!);
        await File.WriteAllTextAsync(host.GameFile("legacy", "l.bin"), "LLLL");
        Directory.Delete(host.Store.GetPackageDirectory("legacy"), recursive: true);

        var states = new DeploymentStateStore(NullLogger<DeploymentStateStore>.Instance,
            Path.Combine(host.Root, "DeploymentState"), legacyBackups);
        var reconcile = new DiskReconcileService(NullLogger<DiskReconcileService>.Instance,
            host.Repository, host.Profiles, host.Game, states);

        var report = (await reconcile.CheckAsync()).Report;

        Assert.True(report.DeploymentRecordUnknown);
        Assert.True(report.CannotSeeClearly);
        // 丢了源文件的包照样报出来，但带着"游戏里的情况不明"，不会被当成"游戏里没有"来移除
        Assert.True(Assert.Single(report.LostPackages).DeployedFilesUnknown);

        // 没有旧记录的宿主（清单同样是空的）：空清单就是真的什么都没装
        var plain = (await host.Reconcile.CheckAsync()).Report;
        Assert.False(plain.DeploymentRecordUnknown);
        Assert.False(Assert.Single(plain.LostPackages).DeployedFilesUnknown);
    });

    // ═══════════════════════════════════════
    //  硬链接部署
    // ═══════════════════════════════════════

    [Fact]
    public Task HardLink_SourceFolderDeleted_GameLinkStillHoldsTheData_AndRecoveryRestoresIt() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenHardLinkHostAsync();
        await host.AddPackageAsync("linked", ("l.bin", "LINKED-CONTENT"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.Profiles.SetPackageEnabledFlagAsync("linked", true);
        await DeployAsync(host, DeploymentBackendType.HardLink);
        var gameFile = host.GameFile("linked", "l.bin");
        Assert.Equal(2u, LinkCount(gameFile));

        // 玩家删了 MOD 库里的文件夹：删的只是目录项，数据还挂在游戏目录那一头
        Directory.Delete(host.Store.GetPackageDirectory("linked"), recursive: true);
        Assert.Equal(1u, LinkCount(gameFile));
        Assert.Equal("LINKED-CONTENT", await File.ReadAllTextAsync(gameFile));

        var lost = Assert.Single((await host.Reconcile.CheckAsync()).Report.LostPackages);
        Assert.True(lost.CanRecoverEverything);
        Assert.True(lost.HasDeployedFiles);

        var recovery = await host.Reconcile.RecoverFromGameAsync(lost);

        Assert.True(recovery.Success, recovery.Error);
        Assert.Equal(1, recovery.FilesRestored);
        Assert.Equal("LINKED-CONTENT", await File.ReadAllTextAsync(host.RepositoryFile("linked", "l.bin")));
        Assert.Empty(await host.Repository.CheckIntegrityAsync());
        // 游戏里的那一份没被动过，找回之后两边一致，不需要再部署什么
        Assert.Equal("LINKED-CONTENT", await File.ReadAllTextAsync(gameFile));
        Assert.False((await host.Reconcile.CheckAsync()).Report.HasFindings);
        Assert.Empty((await host.Planner.CreatePlanAsync()).Operations);
    });

    [Fact]
    public Task HardLink_GameFileDeleted_IsDetectedAndRelinkedWithoutTouchingTheSource() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenHardLinkHostAsync();
        await host.AddPackageAsync("linked", ("l.bin", "LINKED-CONTENT"));
        await host.Profiles.SetPackageEnabledFlagAsync("linked", true);
        await DeployAsync(host, DeploymentBackendType.HardLink);
        var gameFile = host.GameFile("linked", "l.bin");
        var repositoryFile = host.RepositoryFile("linked", "l.bin");

        // 游戏更新把 MOD 目录清掉了：删的是游戏那一头的目录项，仓库里的源文件毫发无损
        Directory.Delete(host.ModPath, recursive: true);
        Assert.Equal("LINKED-CONTENT", await File.ReadAllTextAsync(repositoryFile));
        Assert.Equal(1u, LinkCount(repositoryFile));

        var report = (await host.Reconcile.CheckAsync()).Report;
        var missing = Assert.Single(report.MissingDeployments);
        Assert.True(missing.CanReinstall);
        Assert.Empty(report.LostPackages);

        // 重新部署：计划器看磁盘，把丢的那个文件排成"新增"
        var plan = await host.Planner.CreatePlanAsync();
        Assert.Equal(1, plan.AddCount);
        Assert.Equal("linked", Assert.Single(DeploymentRestoration.FindRestoredFiles(plan)).PackageKey);
        await DeployAsync(host, DeploymentBackendType.HardLink);

        Assert.Equal("LINKED-CONTENT", await File.ReadAllTextAsync(gameFile));
        Assert.Equal(2u, LinkCount(gameFile));
        Assert.Equal(FileIdentity(repositoryFile), FileIdentity(gameFile));
        Assert.False((await host.Reconcile.CheckAsync()).Report.HasFindings);
    });

    private async Task<DiskRefreshHost> OpenHardLinkHostAsync()
    {
        var host = await OpenAsync(
            new CopyBackend(NullLogger<CopyBackend>.Instance),
            new HardLinkBackend(NullLogger<HardLinkBackend>.Instance));

        // 本组用例必须跑在真实的硬链接上：临时目录所在的盘不支持的话，这里就该失败而不是悄悄改测复制
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "probe.txt");
        File.WriteAllText(probe, "x");
        Assert.True(CreateHardLink(Path.Combine(_root, "probe-link.txt"), probe, IntPtr.Zero),
            $"临时目录所在的盘不支持硬链接（Win32Error={Marshal.GetLastWin32Error()}）");
        return host;
    }

    /// <summary>
    /// 按指定后端执行当前方案的部署计划。计划器的后端取自玩家偏好（进程级静态状态，测试里不去动它），
    /// 所以这里把同一份计划换个后端再执行；并确认没有降级——降级成复制的话就测不到硬链接了。
    /// </summary>
    private static async Task DeployAsync(DiskRefreshHost host, DeploymentBackendType backend)
    {
        var plan = await host.Planner.CreatePlanAsync();
        var transaction = await host.Deployment.ExecuteAsync(new DeploymentPlan
        {
            ProfileId = plan.ProfileId, HostGameName = plan.HostGameName, Operations = plan.Operations,
            BackendType = backend, StateBefore = plan.StateBefore, StateAfter = plan.StateAfter,
        });

        Assert.Equal(DeploymentStatus.Committed, transaction.Status);
        Assert.Equal(backend, transaction.BackendType);
        Assert.Empty(transaction.Degradations);
        await host.Deployment.PendingBackupCleanup;
    }

    // ═══════════════════════════════════════
    //  把文件移出部署清单
    // ═══════════════════════════════════════

    [Fact]
    public Task Release_DropsOnlyTheNamedPackagesAndLeavesEveryFileInPlace() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"), (@"sub\a2.bin", "A2"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.EnableAsync("a", "b");
        var revisionBefore = (await host.ReadStateAsync()).Revision;

        var released = await host.Reconcile.ReleaseDeployedFilesAsync(["A"]);

        Assert.Equal(2, released.Count);
        Assert.All(released, file => Assert.Equal("a", file.PackageKey));
        var state = await host.ReadStateAsync();
        Assert.Equal("b", Assert.Single(state.Files).PackageKey);
        Assert.NotEqual(revisionBefore, state.Revision);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.GameFile("a", "a.bin")));
        Assert.Equal("A2", await File.ReadAllTextAsync(host.GameFile("a", @"sub\a2.bin")));
        Assert.Equal("BBBB", await File.ReadAllTextAsync(host.GameFile("b", "b.bin")));
    });

    [Fact]
    public Task Release_NothingToRelease_WritesNothing() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.EnableAsync("a");
        var before = host.Fingerprint();

        Assert.Empty(await host.Reconcile.ReleaseDeployedFilesAsync(["never-installed"]));
        Assert.Empty(await host.Reconcile.ReleaseDeployedFilesAsync([]));
        Assert.Empty(await host.Reconcile.ReleaseDeployedFilesAsync(["", " "]));

        Assert.Equal(before, host.Fingerprint());
    });

    [Fact]
    public Task Release_InvalidatesPlansMadeBeforeIt() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 放手之前算好的计划还以为那些文件归管理器管（可能正要把它们撤掉）：必须作废，不能照着执行
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.EnableAsync("a");
        await host.Profiles.SetPackageEnabledFlagAsync("a", false);
        var stalePlan = await host.Planner.CreatePlanAsync();
        Assert.Equal(1, stalePlan.RemoveCount);

        await host.Reconcile.ReleaseDeployedFilesAsync(["a"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Deployment.ExecuteAsync(stalePlan));
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.GameFile("a", "a.bin")));
        // 重新算的计划里已经没有它：既不删也不管
        Assert.False((await host.Planner.CreatePlanAsync()).HasChanges);
    });

    // ═══════════════════════════════════════
    //  重读包索引
    // ═══════════════════════════════════════

    [Fact]
    public Task ReloadIndex_UnsavedChangesInMemory_AreNotOverwrittenByTheOlderFile() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        var package = await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        var index = Path.Combine(host.DataDirectory, $"{DiskRefreshHost.GameName}_packages.json");

        // 保存失败（索引文件被占用）：内存里的改动留着，磁盘上还是旧的
        package.DisplayName = "Renamed but not saved";
        using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => host.Repository.UpdatePackageAsync(package));
        Assert.Contains("\"MOD a\"", await File.ReadAllTextAsync(index));

        // 这时重读会把那份只存在于内存里的改动丢掉：拒绝
        Assert.False(await host.Repository.ReloadIndexAsync());
        Assert.Equal("Renamed but not saved", host.Repository.GetByKey("a")!.DisplayName);

        // 下一次保存成功之后，内存与磁盘重新一致，可以重读了
        await host.Repository.UpdatePackageAsync(host.Repository.GetByKey("a")!);
        Assert.Contains("Renamed but not saved", await File.ReadAllTextAsync(index));
        Assert.True(await host.Repository.ReloadIndexAsync());
        Assert.Equal("Renamed but not saved", host.Repository.GetByKey("a")!.DisplayName);
    });

    [Fact]
    public Task ReloadIndex_IndexFileMissing_KeepsWhatIsInMemory() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        File.Delete(Path.Combine(host.DataDirectory, $"{DiskRefreshHost.GameName}_packages.json"));

        Assert.False(await host.Repository.ReloadIndexAsync());

        Assert.NotNull(host.Repository.GetByKey("a"));
    });

    [Fact]
    public Task ReloadIndex_RegistrationFailedBeforeTheIndexWasSaved_DoesNotDropThePackageFromMemory() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 注册新包时先改内存、再写 manifest、最后才保存索引。manifest 写失败时还没走到保存索引那一步，
        // 但内存已经比磁盘新了：这时重读会把这个包从内存里抹掉，仓库里留下一个既没 manifest 也没索引的目录。
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        // 包目录的位置上放一个同名文件，manifest 就写不进去
        await File.WriteAllTextAsync(Path.Combine(host.RepositoryRoot, "blocked"), "not a directory");
        var package = new Package { PackageKey = "blocked", DisplayName = "Blocked", HostGameName = DiskRefreshHost.GameName };

        await Assert.ThrowsAnyAsync<IOException>(() => host.Repository.RegisterPackageAsync(package));
        Assert.NotNull(host.Repository.GetByKey("blocked"));
        Assert.False(host.IsInIndexOnDisk("blocked"));

        Assert.False(await host.Repository.ReloadIndexAsync());
        Assert.NotNull(host.Repository.GetByKey("blocked"));
        Assert.NotNull(host.Repository.GetByKey("a"));
    });

    // ═══════════════════════════════════════
    //  从游戏目录找回
    // ═══════════════════════════════════════

    [Fact]
    public Task Recover_LeavesNothingBehindButTheRestoredFiles() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("back", ("a.bin", "AAAA"), (@"deep\er\b.bin", "BBBBBB"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("back");
        Directory.Delete(host.Store.GetPackageDirectory("back"), recursive: true);
        var lost = Assert.Single((await host.Reconcile.CheckAsync()).Report.LostPackages);

        var result = await host.Reconcile.RecoverFromGameAsync(lost);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FilesRestored);
        // 包目录里只有 manifest 和 files 下的两个原件：没有残留的临时文件
        Assert.Equal(
            ["files\\a.bin", "files\\deep\\er\\b.bin", "manifest.json"],
            Directory.GetFiles(host.Store.GetPackageDirectory("back"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(host.Store.GetPackageDirectory("back"), path))
                .Order(StringComparer.Ordinal));
        Assert.Empty(await host.Repository.CheckIntegrityAsync());
    });

    [Fact]
    public Task Recover_NeverOverwritesAFileThatIsAlreadyBack() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("back", ("a.bin", "AAAA"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("back");
        Directory.Delete(host.Store.GetPackageDirectory("back"), recursive: true);
        var lost = Assert.Single((await host.Reconcile.CheckAsync()).Report.LostPackages);
        // 核对之后、找回之前，玩家自己把文件放回去了（内容还不一样）
        Directory.CreateDirectory(Path.GetDirectoryName(host.RepositoryFile("back", "a.bin"))!);
        await File.WriteAllTextAsync(host.RepositoryFile("back", "a.bin"), "PLAYER");

        var result = await host.Reconcile.RecoverFromGameAsync(lost);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.FilesRestored);
        Assert.Equal("PLAYER", await File.ReadAllTextAsync(host.RepositoryFile("back", "a.bin")));
    });

    // ─── 硬链接的真实性 ───

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static ByHandleFileInformation Describe(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(GetFileInformationByHandle(stream.SafeFileHandle, out var information),
            $"读取文件信息失败（Win32Error={Marshal.GetLastWin32Error()}）: {path}");
        return information;
    }

    /// <summary>指向这份数据的目录项个数：硬链接部署后是 2（仓库一个、游戏目录一个）。</summary>
    private static uint LinkCount(string path) => Describe(path).NumberOfLinks;

    /// <summary>两个路径是不是同一份数据（同卷 + 同文件号）。</summary>
    private static (uint Volume, uint High, uint Low) FileIdentity(string path)
    {
        var information = Describe(path);
        return (information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }
}
