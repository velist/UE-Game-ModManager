using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// 游戏方案 / 实例配置。
    /// 一个 Host（游戏）可以有多个 Profile，每个 Profile 保存独立的 MOD 组合。
    /// </summary>
    public class InstanceProfile
    {
        /// <summary>唯一标识。</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>关联的游戏名称。</summary>
        public string HostGameName { get; init; } = default!;

        /// <summary>方案名称（用户可修改）。</summary>
        public string Name { get; set; } = "默认 MOD 方案";

        /// <summary>方案描述。</summary>
        public string? Description { get; set; }

        /// <summary>方案图标名称（shield/swords/camera/palette 等）。</summary>
        public string? IconName { get; set; }

        /// <summary>图标颜色（十六进制）。</summary>
        public string? IconColor { get; set; }

        /// <summary>是否为当前活跃方案。</summary>
        public bool IsActive { get; set; }

        /// <summary>部署后端类型。</summary>
        public DeploymentBackendType BackendType { get; set; } = DeploymentBackendType.Copy;

        /// <summary>创建时间。</summary>
        public DateTime CreatedAt { get; init; } = DateTime.Now;

        /// <summary>最后修改时间。</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>包列表（启用状态 + 优先级）。</summary>
        public List<ProfilePackageEntry> Packages { get; set; } = [];

        // ─── 计算属性（UI 用） ───

        /// <summary>MOD 数量。</summary>
        [JsonIgnore]
        public int ModCount => CountByKind(PackageKind.Mod);

        /// <summary>插件数量。</summary>
        [JsonIgnore]
        public int PluginCount => CountByKind(PackageKind.Plugin);

        /// <summary>配置文件数量。</summary>
        [JsonIgnore]
        public int ConfigCount => CountByKind(PackageKind.Config);

        /// <summary>已启用包数量。</summary>
        [JsonIgnore]
        public int EnabledCount
        {
            get
            {
                int count = 0;
                foreach (var p in Packages)
                    if (p.IsEnabled) count++;
                return count;
            }
        }

        /// <summary>总包数量。</summary>
        [JsonIgnore]
        public int TotalCount => Packages.Count;

        private int CountByKind(PackageKind kind)
        {
            int count = 0;
            foreach (var p in Packages)
                if (p.Kind == kind) count++;
            return count;
        }
    }

    /// <summary>
    /// 方案中的包条目。
    /// 记录某个包在某个 Profile 中的启用状态和优先级。
    /// </summary>
    public class ProfilePackageEntry
    {
        /// <summary>
        /// 包标识符，对应 ModInfo.RealName（文件夹名）。
        /// </summary>
        public string PackageKey { get; init; } = default!;

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; }

        /// <summary>优先级顺序（数字越小越优先）。</summary>
        public int Priority { get; set; }

        /// <summary>包类型。</summary>
        public PackageKind Kind { get; set; } = PackageKind.Mod;

        /// <summary>
        /// 非 MOD 包目标根路径（相对于游戏根目录）。
        /// </summary>
        public string? TargetRootPath { get; set; }

        /// <summary>
        /// 插件目标路径（旧字段，保留兼容）。
        /// </summary>
        public string? PluginTargetPath
        {
            get => TargetRootPath;
            set => TargetRootPath = value;
        }
    }
}
