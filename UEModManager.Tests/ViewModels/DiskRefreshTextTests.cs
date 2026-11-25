using System.Text.RegularExpressions;
using UEModManager.Localization;
using UEModManager.Services;
using UEModManager.Services.Reconciliation;
using UEModManager.Tests.Views;
using UEModManager.ViewModels;

namespace UEModManager.Tests.ViewModels;

/// <summary>
/// 「刷新」说的每一句话都要有英文。
///
/// 这些文案都是在代码里拼出来的，XAML 的译文扫描（<c>ExplicitXamlTranslationsHaveCatalogEntries</c>）
/// 覆盖不到；而 <see cref="UiText"/> 查不到译文时原样返回中文、不报错——漏译不会让任何别的测试变红。
/// 这里把每一个提问、每一条告知都在英文界面下生成一遍，里面不许剩下一个汉字。
/// </summary>
// 会切换进程级的界面语言：必须和其他改语言的测试一起独占运行
[Collection(LanguageIntegrationCollection.Name)]
public sealed class DiskRefreshTextTests : IDisposable
{
    private static readonly Regex Chinese = new("[\u4e00-\u9fff]", RegexOptions.Compiled);
    private readonly bool _previous = LanguageManager.IsEnglish;

    public void Dispose() => LanguageManager.SetEnglish(_previous);

    private static RepositoryFinding Finding(string name, bool enabled = false, int missing = 1, int total = 2)
        => new(name, name, enabled, total,
            Enumerable.Range(0, missing).Select(i => $@"{name}\files\{i}.pak").ToList(),
            [], [], 0, [], false);

    private static DeploymentFinding Deployment(string name)
        => new(name, name, true, true, true, 1, [$@"G:\Game\Mods\{name}\a.pak"]);

    private static IEnumerable<DiskRefreshQuestion> EveryQuestion()
    {
        // 带上启用着的缺文件 MOD，把"注意：其中 N 个还处于启用状态"那一句也带出来
        var findings = new[] { Finding("Alpha"), Finding("Beta", enabled: true) };
        yield return DiskRefreshText.EverythingMissing(@"D:\Mods\Repository", findings);
        yield return DiskRefreshText.RemoveLost(findings);
        yield return DiskRefreshText.RecoverFromGame(findings);
        yield return DiskRefreshText.RemoveStillInstalled(findings);
        yield return DiskRefreshText.RemoveIncomplete(findings);
        yield return DiskRefreshText.MissingDeployments([Deployment("Alpha"), Deployment("Beta")]);
        yield return DiskRefreshText.ForgetUnknownEntries(["Alpha", "Beta"]);
        // 名字多到要折叠的时候
        yield return DiskRefreshText.RemoveLost(
            Enumerable.Range(1, DiskRefreshText.MaxListedNames + 3).Select(i => Finding("Mod" + i)).ToList());
    }

    private static IEnumerable<DiskRefreshNotice> EveryNotice()
    {
        yield return DiskRefreshText.NoProblems();
        yield return DiskRefreshText.RepositoryUnreachable(@"X:\Mods\Repository");
        yield return DiskRefreshText.GameDirectoryUnreachable(@"E:\Games\Sample");
        yield return DiskRefreshText.DeploymentRecordUnknown();
        yield return DiskRefreshText.RecordsNotLoaded();
        yield return DiskRefreshText.PackageRecordsUnreadable(3);
        yield return DiskRefreshText.StillBlocked(["Beta"]);
        yield return DiskRefreshText.Done(removed: 1, recovered: 2, reinstalled: 3, disabled: 4, forgotten: 5)!;
        yield return DiskRefreshText.Failed(
        [
            DiskRefreshText.RecoverFailed("Alpha", "disk full"),
            DiskRefreshText.RemoveFailed("Alpha", "access denied"),
            DiskRefreshText.TidyFailed("file in use"),
            DiskRefreshText.RefreshFailed("unexpected"),
        ]);
    }

    [Fact]
    public void EveryQuestion_IsFullyEnglishInTheEnglishInterface()
    {
        LanguageManager.SetEnglish(true);

        foreach (var question in EveryQuestion())
        {
            foreach (var text in new[]
                     {
                         question.Title, question.Message, question.PrimaryText,
                         question.SecondaryText ?? string.Empty, question.DismissText,
                     })
                Assert.False(Chinese.IsMatch(text), $"{question.Kind} 里还有没翻译的文字：\n{text}");

            Assert.NotEmpty(question.Title);
            Assert.NotEmpty(question.PrimaryText);
            Assert.NotEmpty(question.DismissText);
        }
    }

    [Fact]
    public void EveryNotice_IsFullyEnglishInTheEnglishInterface()
    {
        LanguageManager.SetEnglish(true);

        foreach (var notice in EveryNotice())
        {
            Assert.False(Chinese.IsMatch(notice.Title + notice.Message),
                $"{notice.Kind} 里还有没翻译的文字：\n{notice.Title}\n{notice.Message}");
            Assert.NotEmpty(notice.Message);
        }
    }

    [Fact]
    public void BusyTextsAndLaunchNotice_AreEnglishInTheEnglishInterface()
    {
        LanguageManager.SetEnglish(true);

        foreach (var text in new[]
                 {
                     DiskRefreshText.Checking, DiskRefreshText.Recovering, DiskRefreshText.Removing,
                     DiskRefreshText.Tidying, DiskRefreshText.RestoredBeforeLaunch(2),
                 })
            Assert.False(Chinese.IsMatch(text), $"没翻译：{text}");

        Assert.Equal("2 mod(s) had files missing from the game folder. They have been reinstalled",
            DiskRefreshText.RestoredBeforeLaunch(2));
    }

    [Fact]
    public void TextsBuiltOutsideDiskRefreshText_HaveEnglishTranslations()
    {
        // 按钮、提示、体检项、部署报错里的文案：分散在 XAML 和几个服务里，逐条点名
        foreach (var source in new[]
                 {
                     "刷新",
                     "重新检查 MOD 文件是否还在（F5）",
                     "刷新 MOD 列表",
                     "知道了",
                     "游戏目录里没有完整的一份可以找回",
                     "游戏目录里的文件已经和原来的不一样了",
                     "「{0}」在 MOD 库里的文件不见了，处理它之前没法往游戏里装或撤任何 MOD。请点 MOD 列表上方的「刷新」（F5），管理器会帮你处理。",
                     "MOD 库的位置访问不到，没法核对 MOD 文件",
                     "游戏目录访问不到，没法核对已安装的 MOD 文件",
                     "{0} 个 MOD 在 MOD 库里的文件丢失或不完整（{1}）",
                     "{0} 个已启用 MOD 的文件在游戏目录里丢失（{1}），它们现在在游戏里不会生效",
                     "点 MOD 列表上方的「刷新」（F5）处理",
                     "{0} 个 MOD 的 {1} 个源文件、{2} 个已安装文件都在",
                     "已安装文件的记录读不出来，没法核对游戏目录里的 MOD 文件",
                     "当前游戏的 MOD 记录没有加载完整，没法核对 MOD 文件",
                     "方案里启用着 {0} 个找不到记录的 MOD（{1}），启用、禁用和启动都会因此失败",
                     "；",
                 })
            Assert.True(UiText.HasEnglish(source), $"缺英文译文：{source}");
    }

    [Fact]
    public void Questions_SayWhatThePlayerNeedsToKnow_InChinese()
    {
        LanguageManager.SetEnglish(false);
        var enabled = new[] { Finding("甲", enabled: true), Finding("乙") };

        // 默认的那个选择永远不删还存在的文件
        var installed = DiskRefreshText.RemoveStillInstalled(enabled);
        Assert.Equal("只从列表移除", installed.PrimaryText);
        Assert.Equal("连文件一起删除", installed.SecondaryText);
        Assert.Contains("游戏目录里的文件原样留下，管理器不会删除它们", installed.Message);

        // 保留一个启用着的缺文件 MOD 的后果要说在前面
        Assert.Contains("注意：其中 1 个还处于启用状态", DiskRefreshText.RemoveLost(enabled).Message);
        Assert.DoesNotContain("注意：", DiskRefreshText.RemoveLost([Finding("乙")]).Message);

        // 要删文件、或可能删错的，主按钮标红，默认焦点在"不动"上
        Assert.True(DiskRefreshText.EverythingMissing(@"D:\库", enabled).Danger);
        Assert.True(DiskRefreshText.RemoveIncomplete(enabled).Danger);
        Assert.False(DiskRefreshText.RecoverFromGame(enabled).Danger);
        Assert.False(DiskRefreshText.MissingDeployments([Deployment("甲")]).Danger);

        // 玩家看得懂的原因，而不是"部署清单不一致"
        Assert.Contains("可能是被手动删除，或者被游戏更新 / 重装清掉了。",
            DiskRefreshText.MissingDeployments([Deployment("甲")]).Message);
    }

    [Fact]
    public void LongNameLists_AreFoldedSoTheDialogStaysReadable()
    {
        LanguageManager.SetEnglish(false);
        var many = Enumerable.Range(1, DiskRefreshText.MaxListedNames + 5).Select(i => Finding("包" + i)).ToList();

        var message = DiskRefreshText.RemoveLost(many).Message;

        Assert.Contains($"这 {many.Count} 个 MOD", message);
        Assert.Contains("• 包" + DiskRefreshText.MaxListedNames, message);
        Assert.DoesNotContain("• 包" + (DiskRefreshText.MaxListedNames + 1), message);
        Assert.Contains("…还有 5 个", message);
    }

    [Fact]
    public void Done_NothingHappened_HasNothingToSay()
    {
        Assert.Null(DiskRefreshText.Done(0, 0, 0, 0));

        LanguageManager.SetEnglish(false);
        Assert.Equal("已从列表移除 2 个 MOD；已把 1 个 MOD 改为禁用", DiskRefreshText.Done(2, 0, 0, 1)!.Message);
    }
}
