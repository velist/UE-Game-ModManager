using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Localization;
using UEModManager.Services.Reconciliation;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// 「刷新」要说的每一句话。
    ///
    /// 集中在这里，一是让流程（<see cref="MainViewModel.RefreshFromDiskAsync"/>）里只剩判断、没有文案，
    /// 二是这些话都要有英文——写成散落在各处的拼接，漏译了没有任何测试会发现。
    /// 措辞面向普通玩家：说"MOD 库""游戏目录"，不说"仓库""部署清单"。
    ///
    /// <para>
    /// 按钮文字要短：消息框固定 420 宽，三个按钮的提问要在一排里放得下（中英文都是），
    /// 放不下会折成两排。详细的解释写在正文里，按钮上只留动作本身。
    /// </para>
    /// </summary>
    public static class DiskRefreshText
    {
        /// <summary>一条提示里最多列几个 MOD 的名字，多出来的折成"还有 N 个"。</summary>
        public const int MaxListedNames = 8;

        // ─── 进行中 ───

        public static string Checking => UiText.Get("正在检查 MOD 文件...");
        public static string Recovering => UiText.Get("正在找回 MOD 文件...");
        public static string Removing => UiText.Get("正在移除...");
        public static string Tidying => UiText.Get("正在整理游戏目录里的 MOD 文件...");

        // ─── 告知 ───

        public static DiskRefreshNotice NoProblems()
            => new(DiskRefreshNoticeKind.NoProblems, string.Empty, UiText.Get("已刷新，没有发现问题"), IsToast: true);

        public static DiskRefreshNotice RepositoryUnreachable(string repositoryRoot)
            => new(DiskRefreshNoticeKind.RepositoryUnreachable, UiText.Get("找不到 MOD 库的位置"),
                Paragraphs(
                    UiText.Interpolate($"MOD 库所在的位置现在访问不到：{repositoryRoot}"),
                    UiText.Get("可能是移动硬盘没插好、网络盘掉线了，或者这个文件夹被移走、改了名。请先让它恢复可用，再点一次「刷新」。"),
                    UiText.Get("为了安全，管理器没有改动任何 MOD 的记录。")),
                IsToast: false);

        public static DiskRefreshNotice GameDirectoryUnreachable(string gamePath)
            => new(DiskRefreshNoticeKind.GameDirectoryUnreachable, UiText.Get("找不到游戏目录"),
                Paragraphs(
                    UiText.Interpolate($"游戏目录现在访问不到：{gamePath}"),
                    UiText.Get("可能是游戏所在的硬盘没连接好，或者游戏被移动、卸载了。游戏还在的话，请到设置里重新选择游戏路径。"),
                    UiText.Get("为了安全，管理器没有改动任何 MOD 的状态。")),
                IsToast: false);

        public static DiskRefreshNotice StillBlocked(IReadOnlyList<RepositoryFinding> blockers)
            => new(DiskRefreshNoticeKind.StillBlocked, UiText.Get("还有 MOD 缺文件"),
                Paragraphs(
                    UiText.Interpolate($"这 {blockers.Count} 个缺文件的 MOD 还处于启用状态："),
                    Names(blockers.Select(b => b.DisplayName)),
                    UiText.Get("在处理它们之前，管理器没法往游戏目录里装或撤任何 MOD，也没法通过管理器启动游戏。再点一次「刷新」可以重新处理。")),
                IsToast: false);

        /// <summary>处理完之后的一句话总结；什么都没做时返回 null。</summary>
        public static DiskRefreshNotice? Done(int removed, int recovered, int reinstalled, int disabled)
        {
            var parts = new List<string>();
            if (removed > 0) parts.Add(UiText.Interpolate($"已从列表移除 {removed} 个 MOD"));
            if (recovered > 0) parts.Add(UiText.Interpolate($"已找回 {recovered} 个 MOD"));
            if (reinstalled > 0) parts.Add(UiText.Interpolate($"已重新装好 {reinstalled} 个 MOD 的文件"));
            if (disabled > 0) parts.Add(UiText.Interpolate($"已把 {disabled} 个 MOD 改为禁用"));
            return parts.Count == 0
                ? null
                : new(DiskRefreshNoticeKind.Done, string.Empty, string.Join(UiText.Get("；"), parts), IsToast: true);
        }

        public static DiskRefreshNotice Failed(IEnumerable<string> reasons)
            => new(DiskRefreshNoticeKind.Failed, UiText.Get("有些操作没有完成"),
                string.Join(Environment.NewLine, reasons), IsToast: false);

        public static string RecoverFailed(string modName, string? reason)
            => UiText.Interpolate($"「{modName}」没能找回：{reason}");

        public static string RemoveFailed(string modName, string? reason)
            => UiText.Interpolate($"「{modName}」没能移除：{reason}");

        public static string TidyFailed(string? reason)
            => UiText.Interpolate($"游戏目录里的 MOD 文件没有整理完：{reason}");

        public static string RefreshFailed(string? reason)
            => UiText.Interpolate($"刷新没有完成：{reason}");

        /// <summary>启动游戏前把丢失的文件装回去之后的一句话。</summary>
        public static string RestoredBeforeLaunch(int modCount)
            => UiText.Interpolate($"有 {modCount} 个 MOD 的文件在游戏目录里丢了，已重新装好");

        // ─── 提问 ───

        public static DiskRefreshQuestion EverythingMissing(string repositoryRoot, IReadOnlyList<RepositoryFinding> lost)
            => new(DiskRefreshQuestionKind.EverythingMissing, UiText.Get("MOD 库里的 MOD 都找不到了"),
                Paragraphs(
                    UiText.Interpolate($"MOD 库里登记的 {lost.Count} 个 MOD，文件全都找不到了："),
                    Names(lost.Select(f => f.DisplayName)),
                    UiText.Interpolate($"MOD 库的位置：{repositoryRoot}"),
                    UiText.Get("这通常不是 MOD 被删了，而是这个位置出了问题：硬盘换了盘符、文件夹被整个移走，或者指到了一个空文件夹。请先检查一下。"),
                    UiText.Get("如果确实是你自己把它们删掉的，可以把它们从列表里移除。")),
                PrimaryText: UiText.Get("确实删了，从列表移除"),
                SecondaryText: null,
                DismissText: UiText.Get("先不动"),
                Danger: true);

        public static DiskRefreshQuestion RemoveLost(IReadOnlyList<RepositoryFinding> lost)
            => new(DiskRefreshQuestionKind.RemoveLost, UiText.Get("MOD 的文件不见了"),
                Paragraphs(
                    UiText.Interpolate($"这 {lost.Count} 个 MOD 的文件已经不在 MOD 库里了，要从列表里移除吗？"),
                    Names(lost.Select(f => f.DisplayName)),
                    UiText.Get("保留的话，它们会继续留在列表里，但没法启用。"),
                    BlockingNote(lost)),
                PrimaryText: UiText.Get("移除"),
                SecondaryText: null,
                DismissText: UiText.Get("保留"));

        public static DiskRefreshQuestion RecoverFromGame(IReadOnlyList<RepositoryFinding> recoverable)
            => new(DiskRefreshQuestionKind.RecoverFromGame, UiText.Get("可以从游戏目录找回"),
                Paragraphs(
                    UiText.Interpolate($"这 {recoverable.Count} 个 MOD 在 MOD 库里的文件不见了，不过游戏目录里还装着完整的一份："),
                    Names(recoverable.Select(f => f.DisplayName)),
                    UiText.Get("要把游戏目录里的这一份复制回 MOD 库吗？找回之后可以照常启用、禁用。")),
                PrimaryText: UiText.Get("找回（推荐）"),
                SecondaryText: null,
                DismissText: UiText.Get("不找回"));

        public static DiskRefreshQuestion RemoveStillInstalled(IReadOnlyList<RepositoryFinding> installed)
            => new(DiskRefreshQuestionKind.RemoveStillInstalled, UiText.Get("游戏目录里还装着这些 MOD"),
                Paragraphs(
                    UiText.Interpolate($"这 {installed.Count} 个 MOD 在 MOD 库里已经没有文件了，但游戏目录里还留着它们的文件："),
                    Names(installed.Select(f => f.DisplayName)),
                    UiText.Get("只从列表移除：游戏目录里的文件原样留下，管理器不会删除它们，也不再管理它们。"),
                    UiText.Get("连文件一起删除：把游戏目录里的这些文件也删掉，删掉后没法恢复。"),
                    BlockingNote(installed)),
                PrimaryText: UiText.Get("只从列表移除"),
                SecondaryText: UiText.Get("连文件一起删除"),
                DismissText: UiText.Get("先不处理"));

        public static DiskRefreshQuestion RemoveIncomplete(IReadOnlyList<RepositoryFinding> partial)
            => new(DiskRefreshQuestionKind.RemoveIncomplete, UiText.Get("这些 MOD 的文件不完整"),
                Paragraphs(
                    UiText.Interpolate($"这 {partial.Count} 个 MOD 在 MOD 库里缺了一部分文件，没法正常装进游戏："),
                    Names(partial.Select(f =>
                        UiText.Interpolate($"{f.DisplayName}（缺 {f.MissingFiles.Count} 个，共 {f.TotalFiles} 个文件）"))),
                    UiText.Get("修好它的办法是重新导入一次。导入之前要先把它从列表里移除，否则会被当成重复的 MOD 导不进去。"),
                    UiText.Get("移除会把它在 MOD 库里剩下的文件、以及已经装进游戏目录的文件一起删掉。要现在移除吗？"),
                    BlockingNote(partial)),
                PrimaryText: UiText.Get("移除"),
                SecondaryText: null,
                DismissText: UiText.Get("先留着"),
                Danger: true);

        public static DiskRefreshQuestion MissingDeployments(IReadOnlyList<DeploymentFinding> missing)
            => new(DiskRefreshQuestionKind.MissingDeployments, UiText.Get("游戏目录里的 MOD 文件不见了"),
                Paragraphs(
                    UiText.Interpolate($"这 {missing.Count} 个已启用的 MOD，装在游戏目录里的文件不见了，现在在游戏里不会生效："),
                    Names(missing.Select(f => f.DisplayName)),
                    UiText.Get("可能是被手动删除，或者被游戏更新 / 重装清掉了。"),
                    UiText.Get("重新装回去：把缺的文件重新装进游戏目录。"),
                    UiText.Get("改为禁用：把它们设为禁用，游戏目录里剩下的文件也一并撤掉。")),
                PrimaryText: UiText.Get("重新装回去（推荐）"),
                SecondaryText: UiText.Get("改为禁用"),
                DismissText: UiText.Get("先不处理"));

        // ─── 拼装 ───

        /// <summary>
        /// 列表里有启用着的缺文件 MOD 时补一句后果：它们会让所有部署都停下，这是玩家选"保留"之前该知道的。
        /// </summary>
        private static string? BlockingNote(IReadOnlyList<RepositoryFinding> findings)
        {
            var blocking = findings.Count(f => f.BlocksDeployment);
            return blocking == 0
                ? null
                : UiText.Interpolate($"注意：其中 {blocking} 个还处于启用状态。在处理它们之前，管理器没法启用、禁用其他 MOD，也没法通过管理器启动游戏。");
        }

        private static string Names(IEnumerable<string> names)
        {
            var all = names.ToList();
            var lines = all.Take(MaxListedNames).Select(name => "• " + name).ToList();
            if (all.Count > MaxListedNames)
            {
                var more = all.Count - MaxListedNames;
                lines.Add(UiText.Interpolate($"…还有 {more} 个"));
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string Paragraphs(params string?[] paragraphs)
            => string.Join(Environment.NewLine + Environment.NewLine,
                paragraphs.Where(p => !string.IsNullOrEmpty(p)));
    }
}
