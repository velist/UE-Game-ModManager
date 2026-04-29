using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Conflict
{
    /// <summary>
    /// 冲突所有者：声称要部署到某个目标路径的一个包。
    /// </summary>
    public sealed record ArtifactOwner(
        string PackageKey,
        string DisplayName,
        int Priority,
        PackageKind Kind,
        string? ArtifactHash,
        long FileSize);

    /// <summary>
    /// 冲突求解结果（不含上下文，由调用方包装为 ConflictRecord）。
    /// </summary>
    public sealed record ConflictResolution(
        string WinnerPackageKey,
        string WinnerDisplayName,
        int WinnerPriority,
        IReadOnlyList<ConflictLoser> Losers,
        ResolutionMethod Method,
        string Reason,
        ConflictSeverity Severity);

    /// <summary>
    /// 冲突求解器。纯函数，无 IO，可独立单测。
    ///
    /// 职责：
    /// - 给定多个所有者 + 用户覆盖规则，求出胜者/败者链
    /// - 判定冲突严重程度（哈希一致 → Info；不一致 → Warning）
    ///
    /// 使用方（如 ConflictAnalyzer）负责：
    /// - 收集所有者（读 ProfileService/PackageRepository）
    /// - 持久化用户覆盖规则
    /// - 包装 ConflictResolution → ConflictRecord（填 HostGameName/ProfileId）
    /// </summary>
    public static class ConflictResolver
    {
        /// <summary>
        /// 求解一个目标路径上的冲突。
        /// </summary>
        /// <param name="targetRelativePath">目标相对路径（用于查 userOverrides）。</param>
        /// <param name="owners">声称该路径的所有者集合（>= 2 个才算冲突）。</param>
        /// <param name="userOverrides">用户覆盖规则：targetPath → 指定胜者 PackageKey（可选）。</param>
        /// <returns>求解结果；若 owners ≤ 1 返回 null（无冲突）。</returns>
        public static ConflictResolution? Resolve(
            string targetRelativePath,
            IReadOnlyList<ArtifactOwner> owners,
            IReadOnlyDictionary<string, string>? userOverrides = null)
        {
            if (owners == null) throw new ArgumentNullException(nameof(owners));
            if (owners.Count <= 1) return null;

            var sorted = owners.OrderBy(o => o.Priority).ToList();

            string winnerKey;
            ResolutionMethod method;

            if (userOverrides != null
                && userOverrides.TryGetValue(targetRelativePath, out var overrideKey)
                && sorted.Any(o => o.PackageKey == overrideKey))
            {
                winnerKey = overrideKey;
                method = ResolutionMethod.UserOverride;
            }
            else
            {
                winnerKey = sorted[0].PackageKey;
                method = ResolutionMethod.Priority;
            }

            var winner = sorted.First(o => o.PackageKey == winnerKey);
            var losers = sorted
                .Where(o => o.PackageKey != winnerKey)
                .Select(o => new ConflictLoser
                {
                    PackageKey = o.PackageKey,
                    DisplayName = o.DisplayName,
                    Priority = o.Priority
                })
                .ToList();

            var severity = DetermineSeverity(owners);

            var reason = method == ResolutionMethod.UserOverride
                ? $"用户指定 '{winner.DisplayName}' 为胜者"
                : $"'{winner.DisplayName}' 优先级最高 (P{winner.Priority})";

            return new ConflictResolution(
                WinnerPackageKey: winnerKey,
                WinnerDisplayName: winner.DisplayName,
                WinnerPriority: winner.Priority,
                Losers: losers,
                Method: method,
                Reason: reason,
                Severity: severity);
        }

        /// <summary>
        /// 判定冲突严重程度：所有所有者文件哈希一致 → Info（仅重复）；
        /// 哈希不同 → Warning（内容真实分歧）。
        /// </summary>
        public static ConflictSeverity DetermineSeverity(IReadOnlyList<ArtifactOwner> owners)
        {
            if (owners == null) throw new ArgumentNullException(nameof(owners));

            var distinctHashes = owners
                .Where(o => o.ArtifactHash != null)
                .Select(o => o.ArtifactHash)
                .Distinct()
                .Count();

            return distinctHashes <= 1 ? ConflictSeverity.Info : ConflictSeverity.Warning;
        }
    }
}
