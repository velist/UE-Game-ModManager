using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.DeploymentPlanning
{
    /// <summary>
    /// 单包启用/禁用部署操作构造器（纯函数 + 注入式 IO）。
    ///
    /// 给定一个包 + Profile 中的引用，输出该包要 Add/Replace/Remove 的 DeploymentOperation 列表。
    /// 内部需要的"目标文件是否已存在"通过 <paramref name="targetExists"/> 委托注入，
    /// 让单测可以伪造文件系统状态而不需要真实磁盘 IO。
    ///
    /// 主项目 DeploymentPlanner.CreateTogglePlanAsync 调用时传入 <c>File.Exists</c>。
    /// </summary>
    public static class TogglePlanBuilder
    {
        /// <summary>
        /// 构造单包启用/禁用的操作列表。
        /// </summary>
        /// <param name="package">目标包。</param>
        /// <param name="entry">该包在 Profile 中的引用条目（用于 PluginTargetPath 覆盖；可为 null）。</param>
        /// <param name="modPath">游戏 MOD 根目录绝对路径。</param>
        /// <param name="gamePath">游戏根目录绝对路径（插件需要）。</param>
        /// <param name="repositoryRoot">包仓库根目录绝对路径。源文件 = <c>repositoryRoot + artifact.RelativeSourcePath</c>。</param>
        /// <param name="enable">true=启用（Add/Replace），false=禁用（Remove）。</param>
        /// <param name="targetExists">目标文件存在性谓词（IO 注入）。Add/Replace 用它分支；
        /// Remove 用它过滤掉早已不存在的目标。</param>
        public static List<DeploymentOperation> BuildToggleOperations(
            Package package,
            ProfilePackageEntry? entry,
            string modPath,
            string gamePath,
            string repositoryRoot,
            bool enable,
            Func<string, bool> targetExists)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (targetExists == null) throw new ArgumentNullException(nameof(targetExists));

            var operations = new List<DeploymentOperation>();

            foreach (var artifact in package.Artifacts.Where(a => a.ArtifactType != ArtifactType.PreviewImage))
            {
                var sourcePath = Path.Combine(repositoryRoot, artifact.RelativeSourcePath);
                var targetPath = DeploymentTargetPathBuilder.ComputeTargetPath(
                    artifact, package, entry, modPath, gamePath);
                var relativePath = DeploymentTargetPathBuilder.ComputeRelativeTargetPath(
                    artifact, package, entry, modPath, gamePath, targetPath);

                if (enable)
                {
                    var opType = targetExists(targetPath)
                        ? DeploymentOperationType.Replace
                        : DeploymentOperationType.Add;

                    operations.Add(new DeploymentOperation
                    {
                        Type = opType,
                        PackageKey = package.PackageKey,
                        PackageDisplayName = package.DisplayName,
                        SourcePath = sourcePath,
                        TargetPath = targetPath,
                        RelativeTargetPath = relativePath,
                        FileHash = artifact.FileHash,
                        FileSize = artifact.FileSize,
                        PackageKind = package.Kind,
                    });
                }
                else if (targetExists(targetPath))
                {
                    operations.Add(new DeploymentOperation
                    {
                        Type = DeploymentOperationType.Remove,
                        PackageKey = package.PackageKey,
                        PackageDisplayName = package.DisplayName,
                        TargetPath = targetPath,
                        RelativeTargetPath = relativePath,
                        FileSize = artifact.FileSize,
                        PackageKind = package.Kind,
                    });
                }
            }

            return operations;
        }
    }
}
