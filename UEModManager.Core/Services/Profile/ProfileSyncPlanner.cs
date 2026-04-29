using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Profile
{
    /// <summary>Profile 同步结果（纯数据）。</summary>
    public sealed record ProfileSyncResult(
        List<ProfilePackageEntry> Packages,
        int Added,
        int Removed,
        int Updated);

    /// <summary>
    /// Profile.Packages 与扫描到的 ModInfo 列表的同步规划（纯函数）。
    ///
    /// 主项目 <c>ProfileService.SyncPackagesAsync</c> 会先把 ModInfo 投影为 <see cref="LegacyModEntry"/>
    /// 再调用此处。返回的是"应该是的"完整 packages 列表，调用方负责赋给 Profile 并落盘。
    ///
    /// 同步规则：
    /// - 扫描到但 Profile 中没有 → 加入（priority 接续）
    /// - Profile 中有但扫描没有 → 移除
    /// - 两边都有 → IsEnabled / Kind 用扫描值刷新
    /// </summary>
    public static class ProfileSyncPlanner
    {
        public static ProfileSyncResult ComputeSync(
            InstanceProfile profile,
            IReadOnlyList<LegacyModEntry> scannedMods)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (scannedMods == null) throw new ArgumentNullException(nameof(scannedMods));

            var existing = profile.Packages;

            var existingByKey = existing.ToDictionary(
                p => p.PackageKey, p => p, StringComparer.OrdinalIgnoreCase);

            var scannedByKey = scannedMods.ToDictionary(
                m => m.RealName, m => m, StringComparer.OrdinalIgnoreCase);

            int nextPriority = existing.Count > 0
                ? existing.Max(p => p.Priority) + 1
                : 0;

            var result = new List<ProfilePackageEntry>(existing.Count);
            int updated = 0;
            int added = 0;
            int removed = 0;

            // 1. 保留已存在的（按原顺序）—— 但跳过扫描中已不存在的（视为 Removed）
            foreach (var entry in existing)
            {
                if (!scannedByKey.TryGetValue(entry.PackageKey, out var scanned))
                {
                    removed++;
                    continue;
                }

                // 扫描状态可能变了，刷新 IsEnabled / Kind
                bool changed = false;
                if (entry.IsEnabled != scanned.IsEnabled)
                {
                    entry.IsEnabled = scanned.IsEnabled;
                    changed = true;
                }

                var freshKind = LegacyProfileMigrator.DetermineKind(scanned);
                if (entry.Kind != freshKind)
                {
                    entry.Kind = freshKind;
                    changed = true;
                }

                if (changed) updated++;
                result.Add(entry);
            }

            // 2. 追加扫描到的新条目（不在 existing 里）
            foreach (var mod in scannedMods)
            {
                if (existingByKey.ContainsKey(mod.RealName)) continue;

                result.Add(LegacyProfileMigrator.ToProfileEntry(mod, nextPriority++));
                added++;
            }

            return new ProfileSyncResult(result, added, removed, updated);
        }
    }
}
