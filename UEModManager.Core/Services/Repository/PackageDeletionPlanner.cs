using System;
using UEModManager.Models;

namespace UEModManager.Services.Repository
{
    /// <summary>
    /// 卸载包的执行决策。
    /// </summary>
    public enum PackageDeletionDecision
    {
        /// <summary>包未被任何 Profile 引用，可直接删除。</summary>
        SafeToDelete,

        /// <summary>包被 Profile 引用但全部已禁用 — 删除会让 Profile 出现"幽灵条目"，需要清理。</summary>
        ReferencedButDisabled,

        /// <summary>包在至少一个 Profile 中启用 — 必须先回滚部署再删除，否则游戏目录残留孤儿文件。</summary>
        ActivelyDeployed,
    }

    /// <summary>
    /// 卸载方案描述（包含决策 + 受影响的 Profile）。
    /// </summary>
    public sealed record PackageDeletionPlan(
        string PackageKey,
        PackageDeletionDecision Decision,
        PackageReferenceReport Reference,
        bool RequiresUserConfirmation,
        bool RequiresRollback,
        string Explanation);

    /// <summary>
    /// 卸载决策规划（纯函数）。
    ///
    /// 给定包的引用情况，决定：
    /// - 能不能直接删？
    /// - 需不需要先回滚？
    /// - 是否需要二次确认？
    ///
    /// 不做 IO；调用方拿到 <see cref="PackageDeletionPlan"/> 后再决定 UI 流程。
    /// </summary>
    public static class PackageDeletionPlanner
    {
        public static PackageDeletionPlan Plan(PackageReferenceReport reference)
        {
            if (reference == null) throw new ArgumentNullException(nameof(reference));

            if (!reference.IsReferenced)
            {
                return new PackageDeletionPlan(
                    PackageKey: reference.PackageKey,
                    Decision: PackageDeletionDecision.SafeToDelete,
                    Reference: reference,
                    RequiresUserConfirmation: false,
                    RequiresRollback: false,
                    Explanation: "无 Profile 引用此包，可直接删除");
            }

            if (reference.IsEnabledAnywhere)
            {
                return new PackageDeletionPlan(
                    PackageKey: reference.PackageKey,
                    Decision: PackageDeletionDecision.ActivelyDeployed,
                    Reference: reference,
                    RequiresUserConfirmation: true,
                    RequiresRollback: true,
                    Explanation: $"包仍在 {reference.EnabledReferenceCount} 个方案中处于启用状态。" +
                                 "必须先回滚已部署文件，否则游戏目录会残留孤儿文件。");
            }

            return new PackageDeletionPlan(
                PackageKey: reference.PackageKey,
                Decision: PackageDeletionDecision.ReferencedButDisabled,
                Reference: reference,
                RequiresUserConfirmation: true,
                RequiresRollback: false,
                Explanation: $"包被 {reference.ProfileReferenceCount} 个方案引用但均已禁用。" +
                             "删除后需要从这些方案中清理对应条目。");
        }
    }
}
