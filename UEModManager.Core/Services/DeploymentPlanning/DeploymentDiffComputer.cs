using System;
using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Services.DeploymentPlanning
{
    /// <summary>
    /// 期望文件状态：Profile 中已启用包想要部署的文件描述。
    /// </summary>
    public sealed record DesiredFile(
        string PackageKey,
        string PackageDisplayName,
        string SourcePath,
        string TargetPath,
        string RelativeTargetPath,
        string? FileHash,
        long FileSize,
        PackageKind Kind);

    /// <summary>
    /// 实际文件状态：游戏目录中已存在的文件描述（由 IO 扫描得到）。
    /// </summary>
    public sealed record DeployedFile(
        string? PackageKey,
        string? PackageDisplayName,
        string RelativePath,
        string? Hash,
        long FileSize,
        PackageKind Kind,
        bool BelongsToKnownPackage);

    /// <summary>
    /// 部署计划差异比较器。纯函数：给定期望状态 + 实际状态 → 输出 Add/Remove/Replace 操作。
    /// 不读 IO、不依赖 Service。
    ///
    /// 调用方（主项目 DeploymentPlanner）负责：
    /// - 收集 DesiredFile 字典（读 PackageRepository + 检查源文件存在）
    /// - 收集 DeployedFile 字典（扫描游戏目录）
    /// </summary>
    public static class DeploymentDiffComputer
    {
        /// <summary>
        /// 计算差异。
        /// </summary>
        /// <param name="desired">期望存在的文件，key=目标绝对路径。</param>
        /// <param name="actual">实际存在的文件，key=目标绝对路径。</param>
        /// <returns>需要执行的 Add / Remove / Replace 操作列表。</returns>
        public static List<DeploymentOperation> ComputeDiff(
            IReadOnlyDictionary<string, DesiredFile> desired,
            IReadOnlyDictionary<string, DeployedFile> actual)
        {
            if (desired == null) throw new ArgumentNullException(nameof(desired));
            if (actual == null) throw new ArgumentNullException(nameof(actual));

            var operations = new List<DeploymentOperation>();
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 期望存在但实际不存在 → Add；存在但哈希不同 → Replace
            foreach (var (path, want) in desired)
            {
                if (actual.TryGetValue(path, out var actualFile))
                {
                    consumed.Add(path);

                    if (!string.IsNullOrEmpty(want.FileHash)
                        && want.FileHash != actualFile.Hash)
                    {
                        operations.Add(MakeOp(DeploymentOperationType.Replace, want));
                    }
                    // 哈希相同或无法比较 → 跳过
                }
                else
                {
                    operations.Add(MakeOp(DeploymentOperationType.Add, want));
                }
            }

            // 实际存在但期望不存在 → Remove（仅移除属于已知包的文件）
            foreach (var (path, present) in actual)
            {
                if (consumed.Contains(path)) continue;
                if (!present.BelongsToKnownPackage) continue;

                operations.Add(new DeploymentOperation
                {
                    Type = DeploymentOperationType.Remove,
                    PackageKey = present.PackageKey ?? "unknown",
                    PackageDisplayName = present.PackageDisplayName ?? present.PackageKey ?? "未知包",
                    TargetPath = path,
                    RelativeTargetPath = present.RelativePath,
                    FileSize = present.FileSize,
                    PackageKind = present.Kind,
                });
            }

            return operations;
        }

        private static DeploymentOperation MakeOp(DeploymentOperationType type, DesiredFile info)
            => new()
            {
                Type = type,
                PackageKey = info.PackageKey,
                PackageDisplayName = info.PackageDisplayName,
                SourcePath = info.SourcePath,
                TargetPath = info.TargetPath,
                RelativeTargetPath = info.RelativeTargetPath,
                FileHash = info.FileHash,
                FileSize = info.FileSize,
                PackageKind = info.Kind,
            };
    }
}
