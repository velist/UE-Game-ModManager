using System;
using System.Collections.Generic;

namespace UEModManager.Models
{
    /// <summary>
    /// Package 与旧版 ModInfo 之间的兼容映射。
    /// Package 模型本身位于 UEModManager.Core 项目（纯 Domain）；
    /// 涉及主项目类型（ModInfo 含 WPF 依赖）的转换方法集中在此处。
    /// </summary>
    public static class PackageMappers
    {
        /// <summary>从旧版 ModInfo 创建 Package（用于 v1.8 → v2.0 数据迁移）。</summary>
        public static Package FromModInfo(ModInfo mod, string hostGameName)
        {
            var kind = mod.IsPlugin ? PackageKind.Plugin : PackageKind.Mod;

            return new Package
            {
                PackageKey = mod.RealName,
                DisplayName = mod.Name,
                Kind = kind,
                Tags = mod.Categories.Count > 0 ? new List<string>(mod.Categories) : new List<string> { "未分类" },
                PreviewImagePath = mod.PreviewImagePath,
                TotalSize = mod.FileSize,
                ImportedAt = mod.InstallDate,
                LastModified = DateTime.Now,
                HostGameName = hostGameName,
                PluginTargetPath = mod.IsPlugin ? mod.PluginTargetPath : null,
            };
        }
    }
}
