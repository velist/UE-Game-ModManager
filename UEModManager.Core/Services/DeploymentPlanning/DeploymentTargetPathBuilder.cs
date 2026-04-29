using System.IO;
using UEModManager.Models;

namespace UEModManager.Services.DeploymentPlanning
{
    /// <summary>
    /// 部署目标路径计算（纯函数）。
    ///
    /// MOD 包：<c>{modPath}/{packageKey}/{RelativeTargetPath}</c>
    /// 插件包：<c>{gamePath}/{PluginTargetPath}/{packageKey}/{RelativeTargetPath}</c>
    /// 其中 PluginTargetPath 优先取 ProfilePackageEntry.PluginTargetPath，否则取 Package.PluginTargetPath。
    ///
    /// 与主项目的部署逻辑一致；与 ResolvedView Layer 1 的"无 PackageKey 子目录"语义不同
    /// （后者用于冲突检测，详见 docs/findings/2026-04-28-conflict-detector-noop-by-design.md）。
    /// </summary>
    public static class DeploymentTargetPathBuilder
    {
        /// <summary>计算单个 Artifact 的部署目标绝对路径。</summary>
        public static string ComputeTargetPath(
            PackageArtifact artifact,
            Package package,
            ProfilePackageEntry? entry,
            string modPath,
            string gamePath)
        {
            if (package.Kind == PackageKind.Plugin)
            {
                var pluginPath = entry?.PluginTargetPath ?? package.PluginTargetPath ?? "";
                return Path.Combine(gamePath, pluginPath, package.PackageKey, artifact.RelativeTargetPath);
            }

            return Path.Combine(modPath, package.PackageKey, artifact.RelativeTargetPath);
        }

        /// <summary>计算单个 Artifact 部署目标的相对路径（用于 UI 展示）。</summary>
        public static string ComputeRelativeTargetPath(
            PackageArtifact artifact,
            Package package,
            ProfilePackageEntry? entry,
            string modPath,
            string gamePath,
            string absoluteTargetPath)
        {
            if (package.Kind == PackageKind.Plugin)
            {
                var pluginPath = entry?.PluginTargetPath ?? package.PluginTargetPath ?? "";
                var baseDir = Path.Combine(gamePath, pluginPath);
                return Path.GetRelativePath(baseDir, absoluteTargetPath);
            }

            return Path.GetRelativePath(modPath, absoluteTargetPath);
        }
    }
}
