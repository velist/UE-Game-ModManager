using System;
using System.Collections.Generic;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.Repository
{
    /// <summary>
    /// 单个包的引用计数结果。
    /// </summary>
    public sealed record PackageReferenceReport(
        string PackageKey,
        int ProfileReferenceCount,
        int EnabledReferenceCount,
        IReadOnlyList<Guid> ReferencingProfileIds,
        IReadOnlyList<Guid> EnabledInProfileIds)
    {
        /// <summary>是否有任何 Profile 引用此包。</summary>
        public bool IsReferenced => ProfileReferenceCount > 0;

        /// <summary>是否在至少一个 Profile 中处于启用状态。</summary>
        public bool IsEnabledAnywhere => EnabledReferenceCount > 0;
    }

    /// <summary>
    /// 包引用计数器（纯函数）。
    ///
    /// 用于卸载/删除包前的安全检查：
    /// - 该包被多少个 Profile 引用？
    /// - 在多少个 Profile 里仍是启用状态？
    ///
    /// 调用方拿到 <see cref="PackageReferenceReport"/> 后决定：
    /// - IsReferenced=false → 直接删 ObjectStore 是安全的
    /// - IsReferenced=true 且 IsEnabledAnywhere=false → 仅元数据引用，可提示用户清理
    /// - IsEnabledAnywhere=true → 必须先回滚部署，否则游戏目录残留孤儿文件
    /// </summary>
    public static class PackageReferenceCounter
    {
        /// <summary>
        /// 统计单个 packageKey 在给定 Profile 列表中的引用情况。
        /// </summary>
        public static PackageReferenceReport Count(
            string packageKey,
            IEnumerable<InstanceProfile> profiles)
        {
            if (string.IsNullOrEmpty(packageKey))
                throw new ArgumentException("packageKey 不能为空", nameof(packageKey));
            if (profiles == null)
                throw new ArgumentNullException(nameof(profiles));

            var refIds = new List<Guid>();
            var enabledIds = new List<Guid>();

            foreach (var profile in profiles)
            {
                if (profile?.Packages == null) continue;

                var entry = profile.Packages.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.PackageKey) &&
                    p.PackageKey.Equals(packageKey, StringComparison.OrdinalIgnoreCase));

                if (entry == null) continue;

                refIds.Add(profile.Id);
                if (entry.IsEnabled)
                    enabledIds.Add(profile.Id);
            }

            return new PackageReferenceReport(
                PackageKey: packageKey,
                ProfileReferenceCount: refIds.Count,
                EnabledReferenceCount: enabledIds.Count,
                ReferencingProfileIds: refIds,
                EnabledInProfileIds: enabledIds);
        }

        /// <summary>
        /// 一次扫描所有 Profile，返回每个 packageKey 的引用计数。
        /// 适合"清理孤儿包"批处理。
        /// </summary>
        public static Dictionary<string, PackageReferenceReport> CountAll(
            IEnumerable<InstanceProfile> profiles)
        {
            if (profiles == null)
                throw new ArgumentNullException(nameof(profiles));

            var profileList = profiles.Where(p => p != null).ToList();
            var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in profileList)
            {
                if (profile.Packages == null) continue;
                foreach (var entry in profile.Packages)
                {
                    if (!string.IsNullOrEmpty(entry.PackageKey))
                        allKeys.Add(entry.PackageKey);
                }
            }

            var result = new Dictionary<string, PackageReferenceReport>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in allKeys)
                result[key] = Count(key, profileList);

            return result;
        }
    }
}
