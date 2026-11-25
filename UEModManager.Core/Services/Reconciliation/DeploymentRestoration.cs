using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Reconciliation
{
    /// <summary>一个包这次被重新装回去的文件数。</summary>
    public sealed record RestoredDeployment(string PackageKey, string DisplayName, int FileCount);

    /// <summary>
    /// 从一份部署计划里读出"丢了又装回去"的文件。纯函数。
    ///
    /// 计划器每次都会看磁盘：管理器记着装过、游戏目录里却没有的文件，会作为一条"新增"重新装进去
    /// （见 <c>DeploymentPlanner</c>）。这件事以前悄悄发生，玩家不知道自己的 MOD 曾经没在游戏里。
    /// 这里把它从计划里认出来，好让启动流程说一句"丢了，已重新装好"。
    /// </summary>
    public static class DeploymentRestoration
    {
        /// <summary>
        /// 计划里重新装回去的文件，按包汇总。
        ///
        /// 判据三条同时成立：<b>清单里有</b>（管理器记着装过）、<b>这次还要</b>（仍在最终视图里）、
        /// <b>磁盘上没有</b>（计划给它排的是"新增"）。第一次启用的 MOD 不在清单里；
        /// 禁用后还原游戏原文件的那一类"新增"不在这次的最终视图里——两者都不算。
        /// </summary>
        public static IReadOnlyList<RestoredDeployment> FindRestoredFiles(DeploymentPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (plan.StateBefore == null || plan.StateAfter == null) return [];

            var ownedBefore = plan.StateBefore.Files
                .Select(f => f.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wantedAfter = plan.StateAfter.Files
                .Select(f => f.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return plan.Operations
                .Where(o => o.Type == DeploymentOperationType.Add
                            && ownedBefore.Contains(o.TargetPath)
                            && wantedAfter.Contains(o.TargetPath))
                .GroupBy(o => o.PackageKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RestoredDeployment(
                    g.Key,
                    g.Select(o => o.PackageDisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? g.Key,
                    g.Select(o => o.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
                .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
