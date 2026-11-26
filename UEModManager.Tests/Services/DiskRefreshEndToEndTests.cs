using Microsoft.Extensions.DependencyInjection;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.ViewModels;

namespace UEModManager.Tests.Services;

/// <summary>
/// 「刷新」端到端：在临时目录里搭一套真实的仓库 / 方案 / 游戏目录，
/// 像玩家那样到资源管理器里把文件夹删掉，再点刷新，看列表、方案、部署清单和磁盘是不是都对得上。
///
/// <para>
/// 每条用例的"玩家回答"都是预先定好的；没料到的对话框会让用例直接失败，
/// 所以这里同时钉住了"哪种情形弹哪个框、不该弹的不弹"。
/// </para>
/// </summary>
public sealed class DiskRefreshEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UEModManager.Tests", "refresh-" + Guid.NewGuid().ToString("N"));
    private DiskRefreshHost? _host;

    private async Task<DiskRefreshHost> OpenAsync() => _host = await DiskRefreshHost.OpenAsync(_root);

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
    //  什么都没变
    // ═══════════════════════════════════════

    [Fact]
    public Task NothingChanged_SaysSoWithAToastAndWritesNoFile() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("installed", ("a.bin", "AAAA"), (@"sub\b.bin", "BBBBBB"));
        await host.AddPackageAsync("idle", ("c.bin", "CC"));
        await host.EnableAsync("installed");
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var first = await host.Main.RefreshFromDiskAsync(prompts);
        var second = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.False(first.Skipped);
        Assert.False(first.Report!.HasFindings);
        Assert.Equal(2, first.Report.CheckedPackageCount);
        Assert.Equal(2, first.Report.CheckedManagedFileCount);
        Assert.False(second.Report!.HasFindings);
        // 不弹窗，只有一条不打断操作的轻提示
        Assert.Empty(prompts.Asked);
        Assert.Equal([DiskRefreshNoticeKind.NoProblems, DiskRefreshNoticeKind.NoProblems], prompts.NoticeKinds);
        Assert.All(prompts.Notices, notice => Assert.True(notice.IsToast));
        Assert.Equal("已刷新，没有发现问题", prompts.Notices[0].Message);
        // 幂等：刷新两次，磁盘上一个字节、一个时间戳都没变
        Assert.Equal(before, host.Fingerprint());
        Assert.Equal(2, host.Main.AllMods.Count);
        Assert.True(host.IsEnabled("installed"));
        Assert.False(host.Main.IsRefreshingFromDisk);
    });

    [Fact]
    public Task SecondClickWhileRunning_IsIgnored() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        var prompts = new ScriptedPrompts();

        var running = host.Main.RefreshFromDiskAsync(prompts);
        Assert.True(host.Main.IsRefreshingFromDisk);
        var again = await host.Main.RefreshFromDiskAsync(prompts);
        var finished = await running;

        Assert.True(again.Skipped);
        Assert.Null(again.Report);
        Assert.False(finished.Skipped);
        Assert.False(host.Main.IsRefreshingFromDisk);
        // 只跑了一遍：只有一条"没有发现问题"
        Assert.Single(prompts.Notices);
    });

    // ═══════════════════════════════════════
    //  MOD 库里的文件夹被删了
    // ═══════════════════════════════════════

    [Fact]
    public Task RepositoryFolderDeleted_Remove_TakesItOutOfListProfileAndIndex() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.AddPackageAsync("kept", ("k.bin", "KKKK"));
        await host.EnableAsync("kept");
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        // 只弹一次，点名是哪个 MOD
        var question = Assert.Single(prompts.Asked);
        Assert.Equal(DiskRefreshQuestionKind.RemoveLost, question.Kind);
        Assert.Contains("这 1 个 MOD 的文件已经不在 MOD 库里了，要从列表里移除吗？", question.Message);
        Assert.Contains("MOD gone", question.Message);
        Assert.DoesNotContain("MOD kept", question.Message);
        Assert.Equal("移除", question.PrimaryText);
        Assert.Equal("保留", question.DismissText);

        Assert.Equal(["MOD gone"], result.Removed);
        Assert.False(host.IsListed("gone"));
        Assert.False(host.IsInProfile("gone"));
        Assert.False(host.Repository.Exists("gone"));
        Assert.False(host.IsInIndexOnDisk("gone"));
        // 没出问题的那个原封不动
        Assert.True(host.IsListed("kept"));
        Assert.True(host.IsEnabled("kept"));
        Assert.Equal("KKKK", await File.ReadAllTextAsync(host.GameFile("kept", "k.bin")));
        Assert.Equal("已从列表移除 1 个 MOD", Assert.Single(prompts.Notices).Message);
        Assert.Empty(result.Failures);
    });

    [Fact]
    public Task RepositoryFolderDeleted_Keep_ChangesNothing() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.AddPackageAsync("kept", ("k.bin", "KKKK"));
        await host.ShowListAsync();
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Dismiss);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(result.Removed);
        Assert.True(host.IsListed("gone"));
        Assert.True(host.IsInProfile("gone"));
        Assert.True(host.IsInIndexOnDisk("gone"));
        Assert.Equal(before, host.Fingerprint());
        // 有问题、玩家选择先留着：不再多说一句"没有发现问题"
        Assert.Empty(prompts.Notices);
    });

    [Fact]
    public Task TwoEnabledModsLostAtOnce_AreBothRemovedInOneGo() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 逐个删除在这里走不通：删 a 要先重新规划部署，而规划时 b 的源文件也不在，直接失败；反过来一样。
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.AddPackageAsync("c", ("c.bin", "CCCC"));
        await host.EnableAsync("a", "b", "c");
        foreach (var key in new[] { "a", "b" })
        {
            Directory.Delete(host.Store.GetPackageDirectory(key), recursive: true);
            Directory.Delete(Path.Combine(host.ModPath, key), recursive: true);
        }

        // 修之前：随便开关一个别的 MOD 都会失败，而且报错要说得出是谁、点哪里能解决
        var blocked = await host.Main.DeployToggleAsync("c", false);
        Assert.False(blocked.Success);
        Assert.Contains("在 MOD 库里的文件不见了", blocked.Error);
        Assert.Contains("「刷新」", blocked.Error);

        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Primary);
        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal([DiskRefreshQuestionKind.RemoveLost], prompts.AskedKinds);
        Assert.Contains("这 2 个 MOD", prompts.Asked[0].Message);
        Assert.Contains("注意：其中 2 个还处于启用状态", prompts.Asked[0].Message);
        Assert.Equal(["MOD a", "MOD b"], result.Removed.Order());
        Assert.Empty(result.Failures);
        Assert.False(host.IsListed("a"));
        Assert.False(host.IsListed("b"));
        // 部署清单也清干净了：只剩 c 的文件
        var state = await host.ReadStateAsync();
        Assert.Equal(["c"], state.Files.Select(f => f.PackageKey).Distinct());
        Assert.Equal("CCCC", await File.ReadAllTextAsync(host.GameFile("c", "c.bin")));
        // 修之后：部署恢复正常
        Assert.True((await host.Main.DeployToggleAsync("c", false)).Success);
        Assert.False(File.Exists(host.GameFile("c", "c.bin")));
    });

    // ═══════════════════════════════════════
    //  护栏：位置访问不到时什么都不动
    // ═══════════════════════════════════════

    [Fact]
    public Task RepositoryRootGone_RemovesNothingAndOnlyReportsTheLocation() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.EnableAsync("a");
        // 移动硬盘没插好 / 文件夹被整个移走
        var elsewhere = host.RepositoryRoot + "-unplugged";
        Directory.Move(host.RepositoryRoot, elsewhere);
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.True(result.Report!.RepositoryRootUnreachable);
        Assert.Empty(result.Report.LostPackages);
        // 没有任何"要不要移除"的提问，只有一条说明位置的提示
        Assert.Empty(prompts.Asked);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.RepositoryUnreachable, notice.Kind);
        Assert.False(notice.IsToast);
        Assert.Equal("找不到 MOD 库的位置", notice.Title);
        Assert.Contains(host.RepositoryRoot, notice.Message);
        Assert.Contains("管理器没有改动任何 MOD 的记录", notice.Message);
        Assert.Empty(result.Removed);
        Assert.True(host.IsListed("a") && host.IsListed("b"));
        Assert.True(host.IsInProfile("a") && host.IsInProfile("b"));
        Assert.True(host.IsInIndexOnDisk("a") && host.IsInIndexOnDisk("b"));
        Assert.True(host.IsEnabled("a"));
        // 核对是只读的：没有把那个不存在的仓库目录顺手建出来（建出来就成了"在、但是空的"）
        Assert.False(Directory.Exists(host.RepositoryRoot));
        Assert.Equal(before, host.Fingerprint());

        // 插回去再刷新：一切如常
        Directory.Move(elsewhere, host.RepositoryRoot);
        var after = new ScriptedPrompts();
        Assert.False((await host.Main.RefreshFromDiskAsync(after)).Report!.HasFindings);
        Assert.Equal([DiskRefreshNoticeKind.NoProblems], after.NoticeKinds);
    });

    [Fact]
    public Task ManagerStartedWhileTheLibraryIsUnreachable_StillListsTheMods_AndRefreshNamesTheLocation() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 以前：启动时 MOD 库所在的盘没接上 → 建不出仓库目录、加载在半路中断 → 列表是空的、没有任何提示。
        // 这时点刷新，方案里没有启用的 MOD 就说"没有发现问题"，有的话说"读不出 MOD 的记录"，都没说到点子上。
        // 现在列表照常显示，刷新如实说找不到 MOD 库的位置。
        var first = await OpenAsync();
        await first.AddPackageAsync("a", ("a.bin", "AAAA"));
        await first.AddPackageAsync("b", ("b.bin", "BBBB"));
        await first.EnableAsync("a");
        first.Main.Dispose();
        var plugBackIn = first.UnplugLibrary();

        // 重新打开管理器：服务只装配、不预先加载，由主视图模型走真实的启动加载链
        var host = _host = await DiskRefreshHost.OpenWithoutLoadingAsync(_root);
        await host.Main.InitializeAsync();
        Assert.True(host.IsListed("a") && host.IsListed("b"));
        Assert.True(host.IsEnabled("a"));
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.True(result.Report!.RepositoryRootUnreachable);
        Assert.Empty(prompts.Asked);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.RepositoryUnreachable, notice.Kind);
        Assert.Contains(host.RepositoryRoot, notice.Message);
        Assert.Empty(result.Removed);
        Assert.True(host.IsListed("a") && host.IsListed("b"));
        Assert.True(host.IsInProfile("a") && host.IsInProfile("b"));
        Assert.True(host.IsInIndexOnDisk("a") && host.IsInIndexOnDisk("b"));
        Assert.True(host.IsEnabled("a"));
        Assert.Equal(before, host.Fingerprint());

        // 把硬盘接回去再点一次刷新：不用重启，一切如常
        plugBackIn();
        var after = new ScriptedPrompts();
        Assert.False((await host.Main.RefreshFromDiskAsync(after)).Report!.HasFindings);
        Assert.Equal([DiskRefreshNoticeKind.NoProblems], after.NoticeKinds);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.GameFile("a", "a.bin")));
    });

    [Fact]
    public Task PlayerDeletedTheWholeLibraryFolder_NoticeSaysHowToClearTheList_AndThatPathWorks() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 玩家自己把整个 MOD 库文件夹删了：这时"请先让它恢复可用"无从做起，
        // 而看不清的时候刷新又什么都不提议——只说那一句的话，这些 MOD 就永远留在列表里了。
        // 提示里要给出走得通的路：重新打开管理器（那时这个位置会被重新建出来），再点刷新就能确认移除。
        var first = await OpenAsync();
        await first.AddPackageAsync("a", ("a.bin", "AAAA"));
        await first.AddPackageAsync("b", ("b.bin", "BBBB"));
        await first.ShowListAsync();
        Directory.Delete(first.RepositoryRoot, recursive: true);
        var prompts = new ScriptedPrompts();

        var result = await first.Main.RefreshFromDiskAsync(prompts);

        Assert.True(result.Report!.RepositoryRootUnreachable);
        Assert.Empty(prompts.Asked);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.RepositoryUnreachable, notice.Kind);
        Assert.Contains("如果是你自己把这个文件夹删掉了：请关掉管理器再重新打开，然后点一次「刷新」", notice.Message);
        Assert.Empty(result.Removed);
        Assert.True(first.IsListed("a") && first.IsListed("b"));
        first.Main.Dispose();

        // 照提示做：重新打开管理器，走真实的启动加载链
        var host = _host = await DiskRefreshHost.OpenWithoutLoadingAsync(_root);
        await host.Main.InitializeAsync();
        Assert.True(Directory.Exists(host.RepositoryRoot));
        Assert.True(host.IsListed("a") && host.IsListed("b"));
        var confirm = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.EverythingMissing, DiskRefreshAnswer.Primary);

        var cleared = await host.Main.RefreshFromDiskAsync(confirm);

        // 位置在、里面是空的：先当位置问题问一次，确认"确实删了"之后不再逐个追问
        Assert.Equal([DiskRefreshQuestionKind.EverythingMissing], confirm.AskedKinds);
        Assert.Equal(["MOD a", "MOD b"], cleared.Removed.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Empty(cleared.Failures);
        Assert.False(host.IsListed("a") || host.IsListed("b"));
        Assert.False(host.IsInProfile("a") || host.IsInProfile("b"));
        Assert.False(host.IsInIndexOnDisk("a") || host.IsInIndexOnDisk("b"));
    });

    [Fact]
    public Task DeletingAModWhileTheLibraryIsUnreachable_IsRefused_AndSaysWhy() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 不经过「刷新」、直接在列表里删 MOD 也一样：库访问不到时不删记录。
        // 以前这里会报"删除成功"——记录没了，硬盘上的文件夹却原封不动，接回来就成了没人认领的残留
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.EnableAsync("a");
        var plugBackIn = host.UnplugLibrary();

        var result = await host.Main.DeletePackageModAsync(host.Main.AllMods.Single(m => m.RealName == "a"));

        Assert.False(result.Success);
        Assert.Contains("MOD 库的位置现在访问不到", result.Error);
        Assert.Contains("「MOD a」", result.Error);
        Assert.True(host.IsListed("a"));
        Assert.True(host.IsInProfile("a"));
        Assert.True(host.IsInIndexOnDisk("a"));
        // 一开始就停下，不是"删了一半"：它还启用着，游戏里装着的那一份也没被撤掉
        Assert.True(host.IsEnabled("a"));
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.GameFile("a", "a.bin")));

        // 接回去：文件一个没少，刷新也说没问题
        plugBackIn();
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.RepositoryFile("a", "a.bin")));
        var prompts = new ScriptedPrompts();
        Assert.False((await host.Main.RefreshFromDiskAsync(prompts)).Report!.HasFindings);
    });

    [Fact]
    public Task RecordsStuckOnThePreviousGame_NothingIsOfferedAndNothingChanges() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 切换游戏时加载在半路出了错：方案已经换成新游戏的，包索引还停在上一个游戏。
        // （加载链是"先方案、后包索引"，中间任何一步抛异常都会停在这里。这个状态在这里是手工摆出来的。）
        // 照常核对的话，新游戏方案里启用的每个 MOD 都"找不到记录"，会被提议从方案里去掉。
        var host = await OpenAsync();
        await host.AddPackageAsync("old-game-mod", ("o.bin", "OOOO"));
        const string newGame = "另一个游戏";
        await host.Profiles.SetCurrentGameAsync(newGame);
        var mod = new Package { PackageKey = "new-game-mod", DisplayName = "新游戏的 MOD", HostGameName = newGame };
        await host.Profiles.AddPackagesToCurrentProfileAsync([mod]);
        await host.Profiles.SetPackageEnabledFlagAsync("new-game-mod", true);
        host.Game.Config.GameName = newGame;
        Assert.Equal(DiskRefreshHost.GameName, host.Repository.CurrentGameName);
        await host.ShowListAsync();
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.True(result.Report!.RecordsUnknown);
        Assert.Empty(result.Report.UnknownEnabledKeys);
        // 没有任何提问，只有一条说明
        Assert.Empty(prompts.Asked);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.RecordsNotLoaded, notice.Kind);
        Assert.False(notice.IsToast);
        Assert.Equal("MOD 的记录没有加载完整", notice.Title);
        Assert.Contains("这次没有改动任何东西", notice.Message);
        Assert.Empty(result.Forgotten);
        Assert.True(host.IsEnabled("new-game-mod"));
        Assert.Equal(before, host.Fingerprint());
    });

    [Fact]
    public Task EveryPackageMissing_IsALocationQuestionFirst_AndDefaultRemovesNothing() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 仓库根还在，但里面是空的：盘符变了、指到了一个空文件夹
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.ShowListAsync();
        foreach (var directory in Directory.GetDirectories(host.RepositoryRoot))
            Directory.Delete(directory, recursive: true);
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.EverythingMissing, DiskRefreshAnswer.Dismiss);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.True(result.Report!.SuspectedRepositoryLocationProblem);
        var question = Assert.Single(prompts.Asked);
        Assert.Equal(DiskRefreshQuestionKind.EverythingMissing, question.Kind);
        Assert.Contains("这通常不是 MOD 被删了，而是这个位置出了问题", question.Message);
        Assert.Contains(host.RepositoryRoot, question.Message);
        // 移除是标红的危险按钮，默认焦点在"先不动"上
        Assert.True(question.Danger);
        Assert.Equal("先不动", question.DismissText);
        Assert.Empty(result.Removed);
        Assert.True(host.IsListed("a") && host.IsListed("b"));
        Assert.True(host.IsInIndexOnDisk("a") && host.IsInIndexOnDisk("b"));
        Assert.Equal(before, host.Fingerprint());
    });

    [Fact]
    public Task EveryPackageMissing_PlayerConfirmsTheyDeletedThem_RemovesWithoutAskingTwice() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("installed", ("i.bin", "IIII"));
        await host.AddPackageAsync("idle", ("d.bin", "DDDD"));
        await host.EnableAsync("installed");
        foreach (var directory in Directory.GetDirectories(host.RepositoryRoot))
            Directory.Delete(directory, recursive: true);
        var prompts = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.EverythingMissing, DiskRefreshAnswer.Primary)
            .Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        // 已经确认过"确实删了"，没装在游戏里的那个不再问第二遍；游戏里还有一份的那个照样先问找回
        Assert.Equal(
            [DiskRefreshQuestionKind.EverythingMissing, DiskRefreshQuestionKind.RecoverFromGame],
            prompts.AskedKinds);
        Assert.Equal(["MOD idle"], result.Removed);
        Assert.Equal(["MOD installed"], result.Recovered);
        Assert.False(host.IsListed("idle"));
        Assert.True(host.IsListed("installed"));
        Assert.Equal("IIII", await File.ReadAllTextAsync(host.RepositoryFile("installed", "i.bin")));
    });

    [Fact]
    public Task GameDirectoryGone_ChangesNothingAndOnlyReportsTheLocation() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.EnableAsync("a", "b");
        var elsewhere = host.GamePath + "-offline";
        Directory.Move(host.GamePath, elsewhere);
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.True(result.Report!.GameRootUnreachable);
        Assert.Empty(result.Report.MissingDeployments);
        Assert.Empty(prompts.Asked);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.GameDirectoryUnreachable, notice.Kind);
        Assert.Equal("找不到游戏目录", notice.Title);
        Assert.Contains(host.GamePath, notice.Message);
        Assert.True(host.IsEnabled("a") && host.IsEnabled("b"));
        Assert.Equal(2, (await host.ReadStateAsync()).Files.Count);
        Assert.False(Directory.Exists(host.GamePath));
        Assert.Equal(before, host.Fingerprint());
    });

    [Fact]
    public Task GameDirectoryGone_NothingIsOfferedForRemoval_NotEvenAModThatWasNeverInstalled() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 游戏目录访问不到时只提示位置。提示里写着"没有改动任何 MOD 的状态"，
        // 紧接着再问"要不要移除"就是自相矛盾——哪怕是从没装进过游戏的 MOD，也等游戏目录回来再说。
        var host = await OpenAsync();
        await host.AddPackageAsync("installed", ("i.bin", "IIII"));
        await host.AddPackageAsync("idle", ("d.bin", "DDDD"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        await host.EnableAsync("installed");
        Directory.Delete(host.Store.GetPackageDirectory("installed"), recursive: true);
        Directory.Delete(host.Store.GetPackageDirectory("idle"), recursive: true);
        Directory.Move(host.GamePath, host.GamePath + "-offline");
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(prompts.Asked);
        Assert.Equal([DiskRefreshNoticeKind.GameDirectoryUnreachable], prompts.NoticeKinds);
        Assert.Empty(result.Removed);
        Assert.True(host.IsListed("installed") && host.IsListed("idle"));
        Assert.True(host.IsEnabled("installed"));
        Assert.Single((await host.ReadStateAsync()).Files);
        Assert.Equal(before, host.Fingerprint());

        // 游戏目录回来之后再刷新，才开始处理
        Directory.Move(host.GamePath + "-offline", host.GamePath);
        var later = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary)
            .Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Primary);
        var second = await host.Main.RefreshFromDiskAsync(later);
        Assert.Equal(["MOD installed"], second.Recovered);
        Assert.Equal(["MOD idle"], second.Removed);
    });

    [Fact]
    public Task DeploymentRecordUnreadable_NothingIsTouched_BecauseTheGameSideIsUnknown() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 清单读不出来时拿到的是"空清单"。按空清单继续的话，丢了源文件的 MOD 会被当成"游戏里没有它的文件"
        // 提议移除；等清单恢复可读，游戏里仅剩的那一份就会被下一次部署当成该撤掉的删掉。
        var host = await OpenAsync();
        await host.AddPackageAsync("survivor", ("s.bin", "SSSS"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("survivor");
        Directory.Delete(host.Store.GetPackageDirectory("survivor"), recursive: true);
        var stateFile = Assert.Single(Directory.GetFiles(
            Path.Combine(host.Root, "DeploymentState"), "state.json", SearchOption.AllDirectories));
        var goodState = await File.ReadAllTextAsync(stateFile);
        await File.WriteAllTextAsync(stateFile, "{ 读不出来的清单");
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.True(result.Report!.DeploymentRecordUnknown);
        Assert.Empty(prompts.Asked);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.DeploymentRecordUnknown, notice.Kind);
        Assert.Contains("这次没有改动任何东西", notice.Message);
        Assert.True(host.IsListed("survivor"));
        Assert.Equal("SSSS", await File.ReadAllTextAsync(host.GameFile("survivor", "s.bin")));
        Assert.Equal(before, host.Fingerprint());

        // 清单恢复可读之后：游戏里那份还在，先问的是找回
        await File.WriteAllTextAsync(stateFile, goodState);
        var later = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary);
        Assert.Equal(["MOD survivor"], (await host.Main.RefreshFromDiskAsync(later)).Recovered);
    });

    // ═══════════════════════════════════════
    //  游戏目录里装好的文件被删了
    // ═══════════════════════════════════════

    [Fact]
    public Task DeployedFolderDeleted_Reinstall_PutsTheFilesBack() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("victim", ("v.bin", "VVVV"), (@"sub\w.bin", "WWWWWW"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("victim", "other");
        Directory.Delete(Path.Combine(host.ModPath, "victim"), recursive: true);
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.MissingDeployments, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        var question = Assert.Single(prompts.Asked);
        Assert.Equal(DiskRefreshQuestionKind.MissingDeployments, question.Kind);
        Assert.Contains("这 1 个已启用的 MOD，装在游戏目录里的文件不见了", question.Message);
        Assert.Contains("MOD victim", question.Message);
        Assert.DoesNotContain("MOD other", question.Message);
        Assert.Contains("可能是被手动删除，或者被游戏更新 / 重装清掉了。", question.Message);
        Assert.Equal("重新装回去（推荐）", question.PrimaryText);
        Assert.Equal("改为禁用", question.SecondaryText);

        Assert.Equal(["MOD victim"], result.Reinstalled);
        Assert.Equal("VVVV", await File.ReadAllTextAsync(host.GameFile("victim", "v.bin")));
        Assert.Equal("WWWWWW", await File.ReadAllTextAsync(host.GameFile("victim", @"sub\w.bin")));
        Assert.True(host.IsEnabled("victim"));
        Assert.Equal(3, (await host.ReadStateAsync()).Files.Count);
        Assert.Equal("已重新装好 1 个 MOD 的文件", Assert.Single(prompts.Notices).Message);
        // 装回去之后再刷新就没有问题了
        Assert.False((await host.Main.RefreshFromDiskAsync(new ScriptedPrompts())).Report!.HasFindings);
    });

    [Fact]
    public Task WholeModFolderOfTheGameDeleted_Reinstall_RestoresEveryEnabledMod() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 游戏更新 / 重装把整个 MOD 目录清掉了：体检里就是那句"MOD 路径不存在"
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.AddPackageAsync("off", ("x.bin", "XXXX"));
        await host.EnableAsync("a", "b");
        Directory.Delete(host.ModPath, recursive: true);
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.MissingDeployments, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Contains("这 2 个已启用的 MOD", Assert.Single(prompts.Asked).Message);
        Assert.Equal(["MOD a", "MOD b"], result.Reinstalled.Order());
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.GameFile("a", "a.bin")));
        Assert.Equal("BBBB", await File.ReadAllTextAsync(host.GameFile("b", "b.bin")));
        Assert.False(File.Exists(host.GameFile("off", "x.bin")));
    });

    [Fact]
    public Task DeployedFolderDeleted_Disable_TurnsItOffAndCleansTheManifest() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("victim", ("v.bin", "VVVV"), (@"sub\w.bin", "WWWWWW"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("victim", "other");
        // 只删了一部分：剩下的那个文件在"改为禁用"时要一并撤掉
        File.Delete(host.GameFile("victim", "v.bin"));
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.MissingDeployments, DiskRefreshAnswer.Secondary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal(["MOD victim"], result.Disabled);
        Assert.Empty(result.Reinstalled);
        Assert.False(host.IsEnabled("victim"));
        Assert.True(host.IsInProfile("victim"));
        Assert.False(host.Main.AllMods.Single(m => m.RealName == "victim").IsEnabled);
        Assert.False(File.Exists(host.GameFile("victim", "v.bin")));
        Assert.False(File.Exists(host.GameFile("victim", @"sub\w.bin")));
        // 清单与方案一致：只剩还启用着的那个
        var state = await host.ReadStateAsync();
        Assert.Equal("other", Assert.Single(state.Files).PackageKey);
        Assert.Equal("OOOO", await File.ReadAllTextAsync(host.GameFile("other", "o.bin")));
        // MOD 库里的原件一个都没动
        Assert.Equal("VVVV", await File.ReadAllTextAsync(host.RepositoryFile("victim", "v.bin")));
        Assert.Equal("已把 1 个 MOD 改为禁用", Assert.Single(prompts.Notices).Message);
        Assert.False((await host.Main.RefreshFromDiskAsync(new ScriptedPrompts())).Report!.HasFindings);
    });

    [Fact]
    public Task DeployedFolderDeleted_NotNow_ChangesNothing() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("victim", ("v.bin", "VVVV"));
        await host.EnableAsync("victim");
        Directory.Delete(Path.Combine(host.ModPath, "victim"), recursive: true);
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.MissingDeployments, DiskRefreshAnswer.Dismiss);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(result.Reinstalled);
        Assert.Empty(result.Disabled);
        Assert.True(host.IsEnabled("victim"));
        Assert.False(File.Exists(host.GameFile("victim", "v.bin")));
        Assert.Equal(before, host.Fingerprint());
        Assert.Empty(prompts.Notices);
    });

    // ═══════════════════════════════════════
    //  MOD 库里缺了一部分
    // ═══════════════════════════════════════

    [Fact]
    public Task PartOfAModMissing_IsPointedOutAndNothingIsDeletedByDefault() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("half", ("a.bin", "AAAA"), (@"sub\b.bin", "BBBBBB"));
        await host.AddPackageAsync("whole", ("c.bin", "CCCC"));
        await host.ShowListAsync();
        File.Delete(host.RepositoryFile("half", @"sub\b.bin"));
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveIncomplete, DiskRefreshAnswer.Dismiss);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        var question = Assert.Single(prompts.Asked);
        Assert.Equal(DiskRefreshQuestionKind.RemoveIncomplete, question.Kind);
        Assert.Contains("MOD half（缺 1 个，共 2 个文件）", question.Message);
        Assert.Contains("修好它的办法是重新导入一次", question.Message);
        // 移除要删文件，是标红的危险按钮，默认"先留着"
        Assert.True(question.Danger);
        Assert.Equal("先留着", question.DismissText);
        Assert.Empty(result.Removed);
        Assert.True(host.IsListed("half"));
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.RepositoryFile("half", "a.bin")));
        Assert.Equal(before, host.Fingerprint());
    });

    [Fact]
    public Task PartOfAModMissing_PlayerChoosesRemove_ClearsItSoItCanBeImportedAgain() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("half", ("a.bin", "AAAA"), (@"sub\b.bin", "BBBBBB"));
        await host.AddPackageAsync("whole", ("c.bin", "CCCC"));
        File.Delete(host.RepositoryFile("half", @"sub\b.bin"));
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveIncomplete, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal(["MOD half"], result.Removed);
        Assert.False(host.IsListed("half"));
        Assert.False(host.IsInProfile("half"));
        Assert.False(host.IsInIndexOnDisk("half"));
        Assert.False(Directory.Exists(host.Store.GetPackageDirectory("half")));
        Assert.True(host.IsListed("whole"));
    });

    [Fact]
    public Task PartOfAnInstalledModMissing_IsRecoveredFromTheGameFolder() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("half", ("a.bin", "AAAA"), (@"sub\b.bin", "BBBBBB"));
        await host.EnableAsync("half");
        File.Delete(host.RepositoryFile("half", @"sub\b.bin"));
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal([DiskRefreshQuestionKind.RecoverFromGame], prompts.AskedKinds);
        Assert.Equal(["MOD half"], result.Recovered);
        Assert.Equal("BBBBBB", await File.ReadAllTextAsync(host.RepositoryFile("half", @"sub\b.bin")));
        Assert.Empty(await host.Repository.CheckIntegrityAsync());
    });

    [Fact]
    public Task TextureTheGameNeeds_GoneFromTheLibrary_IsFoundAndRecovered_SoInstallingWorksAgain() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 老版本入库的脚本 MOD：自带的贴图被登记成了"预览图"，但部署时会装进游戏。
        // 它在 MOD 库里丢了以后，任何启用 / 禁用都会报"文件不见了，请点刷新"——刷新必须真的看得见它。
        var host = await OpenAsync();
        await host.AddLayoutPluginAsync("hud", (@"hud\main.bin", "MAIN"), (@"hud\icons\icon.png", "ICON-PIXELS"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("hud");
        File.Delete(host.RepositoryFile("hud", @"hud\icons\icon.png"));
        // 别的 MOD 也因此装不了
        var blocked = await host.Main.DeployToggleAsync("other", true);
        Assert.False(blocked.Success);
        Assert.Contains("在 MOD 库里的文件不见了", blocked.Error);
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal([DiskRefreshQuestionKind.RecoverFromGame], prompts.AskedKinds);
        Assert.Equal(["插件 hud"], result.Recovered);
        Assert.Equal("ICON-PIXELS", await File.ReadAllTextAsync(host.RepositoryFile("hud", @"hud\icons\icon.png")));
        // 挡路的没了：别的 MOD 现在装得上
        Assert.True((await host.Main.DeployToggleAsync("other", true)).Success);
        Assert.Equal("OOOO", await File.ReadAllTextAsync(host.GameFile("other", "o.bin")));
    });

    // ═══════════════════════════════════════
    //  MOD 库里的丢了，游戏目录里那份还在
    // ═══════════════════════════════════════

    [Fact]
    public Task SourceLostButStillInstalled_Recover_CopiesItBackAndEverythingWorksAgain() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("survivor", ("s.bin", "SSSS"), (@"sub\t.bin", "TTTTTT"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("survivor");
        Directory.Delete(host.Store.GetPackageDirectory("survivor"), recursive: true);
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        // 先问的是"找回"，而不是"移除"
        var question = Assert.Single(prompts.Asked);
        Assert.Equal(DiskRefreshQuestionKind.RecoverFromGame, question.Kind);
        Assert.Contains("游戏目录里还装着完整的一份", question.Message);
        Assert.Equal("找回（推荐）", question.PrimaryText);

        Assert.Equal(["MOD survivor"], result.Recovered);
        Assert.Empty(result.Removed);
        Assert.Equal("SSSS", await File.ReadAllTextAsync(host.RepositoryFile("survivor", "s.bin")));
        Assert.Equal("TTTTTT", await File.ReadAllTextAsync(host.RepositoryFile("survivor", @"sub\t.bin")));
        Assert.True(File.Exists(host.Store.GetManifestPath("survivor")));
        // 逐文件重算哈希的完整性检查也通过：找回来的就是原件
        Assert.Empty(await host.Repository.CheckIntegrityAsync());
        Assert.True(host.IsListed("survivor"));
        Assert.True(host.IsEnabled("survivor"));
        // 游戏里那份没被动过
        Assert.Equal("SSSS", await File.ReadAllTextAsync(host.GameFile("survivor", "s.bin")));
        Assert.Equal("已找回 1 个 MOD", Assert.Single(prompts.Notices).Message);

        // 找回之后照常禁用、启用
        Assert.True((await host.Main.DeployToggleAsync("survivor", false)).Success);
        Assert.False(File.Exists(host.GameFile("survivor", "s.bin")));
        Assert.True((await host.Main.DeployToggleAsync("survivor", true)).Success);
        Assert.Equal("TTTTTT", await File.ReadAllTextAsync(host.GameFile("survivor", @"sub\t.bin")));
    });

    [Fact]
    public Task SourceLostButStillInstalled_RemoveFromListOnly_NeverDeletesTheGameCopy() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("survivor", ("s.bin", "SSSS"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("survivor");
        Directory.Delete(host.Store.GetPackageDirectory("survivor"), recursive: true);
        var prompts = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Dismiss)
            .Answer(DiskRefreshQuestionKind.RemoveStillInstalled, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal(
            [DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshQuestionKind.RemoveStillInstalled],
            prompts.AskedKinds);
        var question = prompts.Asked[1];
        Assert.Equal("只从列表移除", question.PrimaryText);
        Assert.Equal("连文件一起删除", question.SecondaryText);
        Assert.Equal("先不处理", question.DismissText);
        // 默认的那个按钮不删文件
        Assert.False(question.Danger);

        Assert.Equal(["MOD survivor"], result.Removed);
        Assert.False(host.IsListed("survivor"));
        Assert.False(host.IsInProfile("survivor"));
        Assert.False(host.IsInIndexOnDisk("survivor"));
        // 游戏里仅剩的那一份还在，而且已经不归管理器管
        Assert.Equal("SSSS", await File.ReadAllTextAsync(host.GameFile("survivor", "s.bin")));
        Assert.Empty((await host.ReadStateAsync()).Files);

        // 之后再怎么部署别的 MOD，都不会把它当成"该撤掉的"删掉
        Assert.True((await host.Main.DeployToggleAsync("other", true)).Success);
        Assert.True((await host.Main.DeployToggleAsync("other", false)).Success);
        Assert.Equal("SSSS", await File.ReadAllTextAsync(host.GameFile("survivor", "s.bin")));
    });

    [Fact]
    public Task SourceLostButStillInstalled_DeleteGameFilesToo_OnlyWhenThePlayerAsksForIt() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("survivor", ("s.bin", "SSSS"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("survivor", "other");
        Directory.Delete(host.Store.GetPackageDirectory("survivor"), recursive: true);
        var prompts = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Dismiss)
            .Answer(DiskRefreshQuestionKind.RemoveStillInstalled, DiskRefreshAnswer.Secondary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal(["MOD survivor"], result.Removed);
        Assert.Empty(result.Failures);
        Assert.False(File.Exists(host.GameFile("survivor", "s.bin")));
        Assert.Equal("other", Assert.Single((await host.ReadStateAsync()).Files).PackageKey);
        Assert.Equal("OOOO", await File.ReadAllTextAsync(host.GameFile("other", "o.bin")));
    });

    [Fact]
    public Task SourceLostButStillInstalled_NotNow_KeepsEverythingAndExplainsTheConsequence() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("survivor", ("s.bin", "SSSS"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("survivor", "other");
        Directory.Delete(host.Store.GetPackageDirectory("survivor"), recursive: true);
        // 另一个 MOD 在游戏里的文件也丢了：本来可以装回去，但缺文件的那个还启用着，什么都装不了
        File.Delete(host.GameFile("other", "o.bin"));
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Dismiss)
            .Answer(DiskRefreshQuestionKind.RemoveStillInstalled, DiskRefreshAnswer.Dismiss);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(result.Removed);
        Assert.True(host.IsListed("survivor"));
        Assert.Equal("SSSS", await File.ReadAllTextAsync(host.GameFile("survivor", "s.bin")));
        Assert.Equal(before, host.Fingerprint());
        // 不再去问"要不要装回去"（做不到），而是说明为什么做不到
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.StillBlocked, notice.Kind);
        Assert.Contains("MOD survivor", notice.Message);
        Assert.Contains("再点一次「刷新」可以重新处理", notice.Message);
    });

    [Fact]
    public Task GameCopyWasChanged_RecoveryIsRefused_AndNothingIsOverwrittenOrDeleted() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("survivor", ("s.bin", "SSSS"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("survivor");
        Directory.Delete(host.Store.GetPackageDirectory("survivor"), recursive: true);
        // 大小没变、内容变了（游戏更新换掉了它）：核对时看不出来，找回时重算哈希才发现
        await File.WriteAllTextAsync(host.GameFile("survivor", "s.bin"), "XXXX");
        var prompts = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary)
            .Answer(DiskRefreshQuestionKind.RemoveStillInstalled, DiskRefreshAnswer.Dismiss);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(result.Recovered);
        Assert.Contains("「MOD survivor」没能找回：游戏目录里的文件已经和原来的不一样了", Assert.Single(result.Failures));
        // 不是原件就不写进 MOD 库
        Assert.False(File.Exists(host.RepositoryFile("survivor", "s.bin")));
        Assert.Equal("XXXX", await File.ReadAllTextAsync(host.GameFile("survivor", "s.bin")));
        Assert.True(host.IsListed("survivor"));
        Assert.Contains(DiskRefreshNoticeKind.Failed, prompts.NoticeKinds);
    });

    [Fact]
    public Task GameCopyHasADifferentSize_RecoveryIsNotEvenOffered() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("survivor", ("s.bin", "SSSS"));
        await host.AddPackageAsync("other", ("o.bin", "OOOO"));
        await host.EnableAsync("survivor");
        Directory.Delete(host.Store.GetPackageDirectory("survivor"), recursive: true);
        await File.WriteAllTextAsync(host.GameFile("survivor", "s.bin"), "changed by a game update");
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveStillInstalled, DiskRefreshAnswer.Dismiss);

        await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal([DiskRefreshQuestionKind.RemoveStillInstalled], prompts.AskedKinds);
        Assert.Equal("changed by a game update", await File.ReadAllTextAsync(host.GameFile("survivor", "s.bin")));
    });

    [Fact]
    public Task EachSideLostADifferentHalf_RecoverThenReinstall_MakesBothWholeAgain() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // MOD 库里丢了一个文件，游戏目录里丢了另一个：两边互相补齐
        var host = await OpenAsync();
        await host.AddPackageAsync("split", ("a.bin", "AAAA"), ("b.bin", "BBBBBB"));
        await host.EnableAsync("split");
        File.Delete(host.RepositoryFile("split", "a.bin"));
        File.Delete(host.GameFile("split", "b.bin"));
        var prompts = new ScriptedPrompts()
            .Answer(DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshAnswer.Primary)
            .Answer(DiskRefreshQuestionKind.MissingDeployments, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        // 先找回（之后才装得回去），再问装回去
        Assert.Equal(
            [DiskRefreshQuestionKind.RecoverFromGame, DiskRefreshQuestionKind.MissingDeployments],
            prompts.AskedKinds);
        Assert.Equal(["MOD split"], result.Recovered);
        Assert.Equal(["MOD split"], result.Reinstalled);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.RepositoryFile("split", "a.bin")));
        Assert.Equal("BBBBBB", await File.ReadAllTextAsync(host.GameFile("split", "b.bin")));
        Assert.Empty(await host.Repository.CheckIntegrityAsync());
        Assert.Equal("已找回 1 个 MOD；已重新装好 1 个 MOD 的文件", Assert.Single(prompts.Notices).Message);
        Assert.False((await host.Main.RefreshFromDiskAsync(new ScriptedPrompts())).Report!.HasFindings);
    });

    // ═══════════════════════════════════════
    //  动手前再看一眼 / 一次整理
    // ═══════════════════════════════════════

    [Fact]
    public Task FolderCameBackWhileTheDialogWasOpen_IsNotRemoved() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 对话框开着的时候玩家把文件夹从回收站还原了，然后才点「移除」：
        // 照着弹框那一刻的结论去删，会把刚还原回来的文件夹连同记录一起删掉
        var host = await OpenAsync();
        await host.AddPackageAsync("restored", ("r.bin", "RRRR"));
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.AddPackageAsync("kept", ("k.bin", "KKKK"));
        Directory.Delete(host.Store.GetPackageDirectory("restored"), recursive: true);
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Primary);
        prompts.WhileDialogIsOpen = _ =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(host.RepositoryFile("restored", "r.bin"))!);
            File.WriteAllText(host.RepositoryFile("restored", "r.bin"), "RRRR");
        };

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Contains("这 2 个 MOD", Assert.Single(prompts.Asked).Message);
        // 仍然不在的那个移除了；回来了的那个原封不动
        Assert.Equal(["MOD gone"], result.Removed);
        Assert.True(host.IsListed("restored"));
        Assert.True(host.IsInIndexOnDisk("restored"));
        Assert.Equal("RRRR", await File.ReadAllTextAsync(host.RepositoryFile("restored", "r.bin")));
        Assert.Empty(result.Failures);
    });

    [Fact]
    public Task RemovalAlreadyNeedsATidyUp_MissingFilesOfOtherModsAreReinstalledWithoutASecondQuestion() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 前面的选择已经要整理一次游戏目录了，而整理就是按方案对齐——别的 MOD 缺的文件会一并装回去。
        // 这时再问"要不要装回去"，玩家选"先不处理"也拦不住，所以不问，装完如实说。
        var host = await OpenAsync();
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.AddPackageAsync("victim", ("v.bin", "VVVV"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        await host.EnableAsync("gone", "victim", "fine");
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);
        Directory.Delete(Path.Combine(host.ModPath, "gone"), recursive: true);
        Directory.Delete(Path.Combine(host.ModPath, "victim"), recursive: true);
        // 只准备了「移除」的回答：要是还弹"装回去 / 改为禁用"，用例会直接失败
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Equal([DiskRefreshQuestionKind.RemoveLost], prompts.AskedKinds);
        Assert.Equal(["MOD gone"], result.Removed);
        Assert.Equal(["MOD victim"], result.Reinstalled);
        Assert.Equal("VVVV", await File.ReadAllTextAsync(host.GameFile("victim", "v.bin")));
        Assert.Equal("已从列表移除 1 个 MOD；已重新装好 1 个 MOD 的文件", Assert.Single(prompts.Notices).Message);
        Assert.Equal(["fine", "victim"], (await host.ReadStateAsync()).Files.Select(f => f.PackageKey).Order());
    });

    // ═══════════════════════════════════════
    //  方案里启用着、却已经没有记录的 MOD
    // ═══════════════════════════════════════

    /// <summary>像"别的实例删了包 / 手工恢复了旧索引"那样，把一个包从磁盘上的索引里拿掉。</summary>
    private static void DropFromIndexOnDisk(DiskRefreshHost host, string key)
    {
        var path = Path.Combine(host.DataDirectory, $"{DiskRefreshHost.GameName}_packages.json");
        var packages = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var entry = packages.Single(node => (string?)node!["PackageKey"] == key);
        packages.Remove(entry);
        File.WriteAllText(path, packages.ToJsonString());
    }

    [Fact]
    public Task EnabledEntryWithoutARecord_IsFoundAndCanBeDroppedFromTheProfile_KeepingItsGameFiles() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("ghost", ("g.bin", "GGGG"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        await host.EnableAsync("ghost", "fine");
        DropFromIndexOnDisk(host, "ghost");
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.ForgetUnknownEntries, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        // 索引重读之后，方案里那条启用着的引用指向了一个不存在的包：刷新要把它报出来，不能说"没有发现问题"
        Assert.Equal(["ghost"], result.Report!.UnknownEnabledKeys);
        var question = Assert.Single(prompts.Asked);
        Assert.Equal(DiskRefreshQuestionKind.ForgetUnknownEntries, question.Kind);
        Assert.Contains("• ghost", question.Message);
        Assert.Contains("它们装在游戏目录里的文件会原样留下", question.Message);

        Assert.Equal(["ghost"], result.Forgotten);
        Assert.False(host.IsInProfile("ghost"));
        Assert.False(host.IsListed("ghost"));
        // 记录都没了，不知道这些文件该不该删：留下，并且不再归管理器管
        Assert.Equal("GGGG", await File.ReadAllTextAsync(host.GameFile("ghost", "g.bin")));
        Assert.Equal("fine", Assert.Single((await host.ReadStateAsync()).Files).PackageKey);
        Assert.Equal("已从方案里去掉 1 个找不到记录的 MOD", Assert.Single(prompts.Notices).Message);
        // 挡路的没了，部署恢复正常，而且不会把留下的文件删掉
        Assert.True((await host.Main.DeployToggleAsync("fine", false)).Success);
        Assert.Equal("GGGG", await File.ReadAllTextAsync(host.GameFile("ghost", "g.bin")));
    });

    [Fact]
    public Task EnabledEntryWithoutARecord_LeftInPlace_ExplainsWhyNothingCanBeInstalled() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("ghost", ("g.bin", "GGGG"));
        await host.AddPackageAsync("victim", ("v.bin", "VVVV"));
        await host.EnableAsync("ghost", "victim");
        DropFromIndexOnDisk(host, "ghost");
        File.Delete(host.GameFile("victim", "v.bin"));
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.ForgetUnknownEntries, DiskRefreshAnswer.Dismiss);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(result.Forgotten);
        Assert.True(host.IsInProfile("ghost"));
        // "要不要装回去"问了也做不到：改成说明是谁挡着
        Assert.Equal([DiskRefreshQuestionKind.ForgetUnknownEntries], prompts.AskedKinds);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.StillBlocked, notice.Kind);
        Assert.Contains("• ghost", notice.Message);
        Assert.False(File.Exists(host.GameFile("victim", "v.bin")));
    });

    [Fact]
    public Task NoPackageRecordsAtAllButTheProfileHasMods_IsTreatedAsUnreadableRecords_NotAsDeletedMods() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 索引整个是空的，方案里却有启用的 MOD：是索引没读出来（或被清空了），不是这些 MOD 被一个个删了。
        // 这时把方案里的引用清掉，等于替一次读盘事故把玩家的方案抹空。
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.EnableAsync("a", "b");
        await File.WriteAllTextAsync(
            Path.Combine(host.DataDirectory, $"{DiskRefreshHost.GameName}_packages.json"), "[]");
        var before = host.Fingerprint();
        var prompts = new ScriptedPrompts();

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(prompts.Asked);
        var notice = Assert.Single(prompts.Notices);
        Assert.Equal(DiskRefreshNoticeKind.PackageRecordsUnreadable, notice.Kind);
        Assert.Contains("当前方案里有 2 个启用的 MOD", notice.Message);
        Assert.Empty(result.Forgotten);
        Assert.True(host.IsInProfile("a") && host.IsInProfile("b"));
        Assert.True(host.IsEnabled("a") && host.IsEnabled("b"));
        Assert.Equal(2, (await host.ReadStateAsync()).Files.Count);
        Assert.Equal("AAAA", await File.ReadAllTextAsync(host.GameFile("a", "a.bin")));
        Assert.Equal(before, host.Fingerprint());
    });

    [Fact]
    public Task PackageWithAnUnusableKeyInTheIndex_IsSkippedInsteadOfAbortingTheWholeRefresh() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // 索引里混进一个包键不合法的条目（手工改坏的、别的版本写的）：它自己查不了，但不能连累别的包
        var host = await OpenAsync();
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        var path = Path.Combine(host.DataDirectory, $"{DiskRefreshHost.GameName}_packages.json");
        var packages = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
        var broken = System.Text.Json.Nodes.JsonNode.Parse(packages[1]!.ToJsonString())!;
        broken["PackageKey"] = "bad:key";
        broken["DisplayName"] = "Broken entry";
        broken["Artifacts"]![0]!["RelativeSourcePath"] = @"bad:key\files\f.bin";
        packages.Add(broken);
        await File.WriteAllTextAsync(path, packages.ToJsonString());
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        Assert.Empty(result.Failures);
        Assert.Equal(["bad:key"], result.Report!.UncheckedPackageKeys);
        Assert.Equal(["MOD gone"], result.Removed);
        Assert.True(host.IsListed("bad:key"));
        Assert.True(host.IsListed("fine"));
    });

    // ═══════════════════════════════════════
    //  索引被外部改过
    // ═══════════════════════════════════════

    [Fact]
    public Task Refresh_RereadsThePackageIndexFromDisk() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.AddPackageAsync("b", ("b.bin", "BBBB"));
        await host.Main.RefreshFromRepositoryAsync();
        Assert.Equal("MOD a", host.Main.AllMods.Single(m => m.RealName == "a").Name);
        // 另一个实例（或手工恢复的备份）改了索引文件
        var index = Path.Combine(host.DataDirectory, $"{DiskRefreshHost.GameName}_packages.json");
        await File.WriteAllTextAsync(index, (await File.ReadAllTextAsync(index)).Replace("\"MOD a\"", "\"Renamed elsewhere\""));

        await host.Main.RefreshFromDiskAsync(new ScriptedPrompts());

        Assert.Equal("Renamed elsewhere", host.Main.AllMods.Single(m => m.RealName == "a").Name);
        Assert.Equal(2, host.Main.AllMods.Count);
    });

    [Fact]
    public Task Refresh_UnreadableIndexFile_KeepsTheListInsteadOfEmptyingIt() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("a", ("a.bin", "AAAA"));
        await host.Main.RefreshFromRepositoryAsync();
        var index = Path.Combine(host.DataDirectory, $"{DiskRefreshHost.GameName}_packages.json");
        await File.WriteAllTextAsync(index, "{ 这不是合法的索引");

        var result = await host.Main.RefreshFromDiskAsync(new ScriptedPrompts());

        // 读不出来就继续用内存里的那份：不能因为点了一下刷新，MOD 就从界面上全没了
        Assert.True(host.IsListed("a"));
        Assert.Equal(1, result.Report!.CheckedPackageCount);
        Assert.False(await host.Repository.ReloadIndexAsync());
        Assert.NotNull(host.Repository.GetByKey("a"));
    });

    // ═══════════════════════════════════════
    //  没有核对服务的宿主 / 容器接线
    // ═══════════════════════════════════════

    /// <summary>
    /// 程序里视图模型是从容器解析出来的，而核对服务是它的一个<b>可选</b>构造参数。
    /// 这个依赖要是没注入进去，程序不会报任何错——点「刷新」永远只会说"没有发现问题"。
    /// 所以两头都钉住：注册了就一定注入；注册那一行确实在。
    /// </summary>
    [Fact]
    public Task ViewModelResolvedFromTheContainer_GetsTheReconcileServiceWhenItIsRegistered() => DiskRefreshHost.OnStaAsync(async () =>
    {
        var host = await OpenAsync();
        await host.AddPackageAsync("gone", ("g.bin", "GGGG"));
        await host.AddPackageAsync("kept", ("k.bin", "KKKK"));
        Directory.Delete(host.Store.GetPackageDirectory("gone"), recursive: true);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(host.Game).AddSingleton(host.Categories).AddSingleton(host.Profiles)
            .AddSingleton(host.Repository).AddSingleton(host.Imports).AddSingleton(host.Migration)
            .AddSingleton(host.Planner).AddSingleton(host.Deployment).AddSingleton(host.Conflicts)
            .AddSingleton(host.Overwrites).AddSingleton(host.Merge).AddSingleton(host.Views)
            .AddSingleton(host.Launcher).AddSingleton(host.States);
        // 与 App.xaml.cs 同样的两行注册
        services.AddSingleton<DiskReconcileService>();
        services.AddTransient<MainViewModel>();
        using var provider = services.BuildServiceProvider();

        using var resolved = provider.GetRequiredService<MainViewModel>();
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Dismiss);
        var result = await resolved.RefreshFromDiskAsync(prompts);

        // 真的去核对了磁盘：发现了被删掉的那个包
        Assert.NotNull(result.Report);
        Assert.Equal("gone", Assert.Single(result.Report!.LostPackages).PackageKey);
        Assert.Equal([DiskRefreshQuestionKind.RemoveLost], prompts.AskedKinds);
    });

    [Fact]
    public void AppRegistersTheReconcileService()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "UEModManager.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var app = File.ReadAllText(Path.Combine(directory!.FullName, "UEModManager", "App.xaml.cs"));

        Assert.Contains("services.AddSingleton<DiskReconcileService>();", app);
    }

    [Fact]
    public async Task HostWithoutTheReconcileService_StillRebuildsTheListAndNeverAsksAnything()
    {
        // 视图模型的这个依赖是可选的（老的构造调用不用跟着改）：没有它时刷新退化成只重画列表，不能抛
        var host = await ConflictTestHost.OpenAsync(_root);
        var prompts = new ScriptedPrompts();

        var result = await host.OnStaAsync(() => host.Main.RefreshFromDiskAsync(prompts));

        Assert.False(result.Skipped);
        Assert.Null(result.Report);
        Assert.Empty(prompts.Asked);
        Assert.Equal([DiskRefreshNoticeKind.NoProblems], prompts.NoticeKinds);
        Assert.False(host.Main.IsRefreshingFromDisk);
    }
}
