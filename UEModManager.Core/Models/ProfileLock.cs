using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UEModManager.Models
{
    /// <summary>
    /// Profile 锁定文件 schema 版本（用于向后兼容判断）。
    /// </summary>
    public static class ProfileLockSchema
    {
        public const int CurrentVersion = 1;
    }

    /// <summary>
    /// Profile lock 文件——可分享、可复现的方案快照。
    ///
    /// 包含：
    /// - 元数据（创建时间、应用版本、目标游戏）
    /// - Profile 设置（名称、描述、后端类型）
    /// - 包列表（key、版本、启用状态、优先级、可选哈希）
    /// - 冲突覆盖规则
    ///
    /// 不包含：
    /// - 包文件本身（由本地仓库提供，缺失则提示用户）
    /// - 本机绝对路径（避免锁定到具体环境）
    /// - 用户凭据 / 邮箱 / token（隐私）
    /// </summary>
    public sealed class ProfileLock
    {
        [JsonPropertyName("lockVersion")]
        public int LockVersion { get; init; } = ProfileLockSchema.CurrentVersion;

        [JsonPropertyName("exportedAt")]
        public DateTime ExportedAt { get; init; } = DateTime.UtcNow;

        [JsonPropertyName("exportedByApp")]
        public string ExportedByApp { get; init; } = "";

        [JsonPropertyName("host")]
        public ProfileLockHost Host { get; init; } = new();

        [JsonPropertyName("profile")]
        public ProfileLockProfile Profile { get; init; } = new();

        [JsonPropertyName("packages")]
        public List<ProfileLockPackage> Packages { get; init; } = [];

        [JsonPropertyName("conflictOverrides")]
        public Dictionary<string, string> ConflictOverrides { get; init; } = new();
    }

    public sealed class ProfileLockHost
    {
        [JsonPropertyName("gameName")]
        public string GameName { get; init; } = "";

        [JsonPropertyName("engine")]
        public string Engine { get; init; } = "";
    }

    public sealed class ProfileLockProfile
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("backendType")]
        public string BackendType { get; init; } = "Copy";
    }

    public sealed class ProfileLockPackage
    {
        [JsonPropertyName("packageKey")]
        public string PackageKey { get; init; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; init; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "Mod";  // Mod / Plugin / Config

        [JsonPropertyName("version")]
        public string Version { get; init; } = "1.0.0";

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; init; }

        [JsonPropertyName("priority")]
        public int Priority { get; init; }

        /// <summary>包内容哈希（可选，用于检测远端版本是否与本地匹配）。</summary>
        [JsonPropertyName("contentHash")]
        public string? ContentHash { get; init; }
    }
}
