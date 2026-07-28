using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using UEModManager.Core.Utils;
using UEModManager.Services;
using UEModManager.Services.Repository;

namespace UEModManager.Views
{
    /// <summary>
    /// "仓库残留目录"的用户交互，供管理中心与仓库管理窗口共用。
    ///
    /// <para>
    /// 单独抽出来是因为这里的措辞和确认流程直接关系到会不会误删用户数据：
    /// 两个窗口各写一份，早晚会有一份忘了二次确认或漏报了判据。
    /// 判定本身在 <see cref="RepositoryReclaimPlanner"/>（Core），这里只负责说人话和问一句。
    /// </para>
    /// </summary>
    internal static class RepositoryReclaimPrompt
    {
        /// <summary>
        /// 把回收计划渲染成可以并进"完整性检查"结果里的条目。
        /// </summary>
        public static IEnumerable<string> DescribeIssues(RepositoryReclaimPlan plan)
        {
            foreach (var entry in plan.Unregistered)
                yield return $"[{entry.DirectoryName}] {entry.Reason}";

            foreach (var entry in plan.Reclaimable)
                yield return $"[{entry.DirectoryName}] {entry.Reason}（{FileSizeFormatter.Format(entry.SizeBytes)}）";
        }

        /// <summary>
        /// 若有可回收的残留目录，向用户确认后删除。返回是否真的删了东西（调用方据此刷新界面）。
        ///
        /// <para>
        /// 只回收 <see cref="RepositoryEntryDisposition.Reclaimable"/>；
        /// 失联包（有 manifest、无索引记录）只在文案里提一句，不提供"一键删除"入口——
        /// 那类目录的数据是完整的，正确的处置是重新登记而不是删掉。
        /// </para>
        /// </summary>
        public static bool ConfirmAndReclaim(Window owner, RepositoryReclaimService reclaim, RepositoryReclaimPlan plan)
        {
            if (plan.Reclaimable.Count == 0) return false;

            var preview = string.Join("\n",
                plan.Reclaimable.Take(5).Select(e => $"  · {e.DirectoryName}（{FileSizeFormatter.Format(e.SizeBytes)}）"));
            if (plan.Reclaimable.Count > 5)
                preview += $"\n  · …… 另有 {plan.Reclaimable.Count - 5} 个";

            var answer = CyberMessageBox.Show(owner,
                $"发现 {plan.Reclaimable.Count} 个导入残留目录，共占用 " +
                $"{FileSizeFormatter.Format(plan.ReclaimableBytes)}：\n{preview}\n\n" +
                "这些目录没有 manifest.json、任何游戏的 MOD 清单里也没有记录，" +
                "是导入中途失败或程序被强制结束留下的，删除不会影响任何已导入的 MOD。\n\n是否清理？",
                "清理导入残留", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                yesText: "清理", noText: "保留");

            if (answer != MessageBoxResult.Yes) return false;

            var result = reclaim.Reclaim(plan);

            var summary = $"已清理 {result.DeletedCount} 个残留目录，释放 {FileSizeFormatter.Format(result.FreedBytes)}。";
            if (result.Failures.Count > 0)
            {
                summary += $"\n\n{result.Failures.Count} 个删除失败（文件可能被占用），可稍后重试：\n"
                    + string.Join("\n", result.Failures.Take(5).Select(f => $"  · {f.DirectoryName}: {f.Error}"));
            }

            CyberMessageBox.Show(owner, summary, "清理完成", MessageBoxButton.OK,
                result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            return result.DeletedCount > 0;
        }
    }
}
