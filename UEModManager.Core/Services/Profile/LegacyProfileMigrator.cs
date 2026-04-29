using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Services.Profile
{
    /// <summary>
    /// v1.8 → v2.0 数据迁移：旧版 ModInfo 列表 → ProfilePackageEntry 列表（纯函数）。
    ///
    /// 主项目 <c>ProfileService.CreateDefaultProfileAsync</c> 在读到旧 <c>{game}_mods.json</c> 后，
    /// 把每条 ModInfo 投影为 <see cref="LegacyModEntry"/>，再调用此处构造 Profile.Packages。
    ///
    /// 维持与原有逻辑一致的 Kind 判定规则（保留 v1.8 行为，避免迁移结果发生静默变化）：
    /// - <see cref="LegacyModEntry.IsPlugin"/>=true → <see cref="PackageKind.Plugin"/>
    /// - 文件名以 .ini/.cfg/.json/.yaml 结尾 → <see cref="PackageKind.Config"/>
    /// - 其他 → <see cref="PackageKind.Mod"/>
    /// </summary>
    public static class LegacyProfileMigrator
    {
        /// <summary>从 LegacyModEntry 推断 PackageKind（保留 v1.8 旧规则）。</summary>
        public static PackageKind DetermineKind(LegacyModEntry mod)
        {
            if (mod == null) throw new System.ArgumentNullException(nameof(mod));
            if (mod.IsPlugin) return PackageKind.Plugin;

            var name = mod.RealName.ToLowerInvariant();
            if (name.EndsWith(".ini") || name.EndsWith(".cfg")
                || name.EndsWith(".json") || name.EndsWith(".yaml"))
                return PackageKind.Config;

            return PackageKind.Mod;
        }

        /// <summary>构造单个 ProfilePackageEntry（用于迁移 / 同步追加）。</summary>
        public static ProfilePackageEntry ToProfileEntry(LegacyModEntry mod, int priority)
        {
            if (mod == null) throw new System.ArgumentNullException(nameof(mod));
            return new ProfilePackageEntry
            {
                PackageKey = mod.RealName,
                IsEnabled = mod.IsEnabled,
                Priority = priority,
                Kind = DetermineKind(mod),
                PluginTargetPath = mod.IsPlugin ? mod.PluginTargetPath : null,
            };
        }

        /// <summary>
        /// 把整个 ModInfo 列表（已投影）转换为 ProfilePackageEntry 列表，按输入顺序连续编号 priority。
        /// </summary>
        public static List<ProfilePackageEntry> BuildPackagesFromLegacyMods(
            IReadOnlyList<LegacyModEntry> mods)
        {
            if (mods == null) throw new System.ArgumentNullException(nameof(mods));

            var entries = new List<ProfilePackageEntry>(mods.Count);
            for (int i = 0; i < mods.Count; i++)
                entries.Add(ToProfileEntry(mods[i], priority: i));
            return entries;
        }
    }
}
