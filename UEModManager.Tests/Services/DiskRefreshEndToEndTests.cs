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
    public Task GameDirectoryGone_LostModThatWasInstalled_IsLeftAloneForNow() => DiskRefreshHost.OnStaAsync(async () =>
    {
        // MOD 库里的丢了，游戏目录又访问不到：不知道游戏里还有没有它的文件，这次不提议移除
        var host = await OpenAsync();
        await host.AddPackageAsync("installed", ("i.bin", "IIII"));
        await host.AddPackageAsync("idle", ("d.bin", "DDDD"));
        await host.AddPackageAsync("fine", ("f.bin", "FFFF"));
        await host.EnableAsync("installed");
        Directory.Delete(host.Store.GetPackageDirectory("installed"), recursive: true);
        Directory.Delete(host.Store.GetPackageDirectory("idle"), recursive: true);
        Directory.Move(host.GamePath, host.GamePath + "-offline");
        var prompts = new ScriptedPrompts().Answer(DiskRefreshQuestionKind.RemoveLost, DiskRefreshAnswer.Primary);

        var result = await host.Main.RefreshFromDiskAsync(prompts);

        var question = Assert.Single(prompts.Asked);
        Assert.Equal(DiskRefreshQuestionKind.RemoveLost, question.Kind);
        Assert.Contains("MOD idle", question.Message);
        Assert.DoesNotContain("MOD installed", question.Message);
        Assert.Equal(["MOD idle"], result.Removed);
        Assert.True(host.IsListed("installed"));
        Assert.True(host.IsEnabled("installed"));
        Assert.Single((await host.ReadStateAsync()).Files);
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
        Assert.Equal("连游戏里的文件一起删除", question.SecondaryText);
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
    //  没有核对服务的宿主
    // ═══════════════════════════════════════

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
