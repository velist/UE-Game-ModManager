using System.IO;
using UEModManager.Models;
using UEModManager.Services.Import;
using UEModManager.Services.Security;

namespace UEModManager.Services.DeploymentPlanning
{
    /// <summary>
    /// 部署目标路径计算（纯函数）。
    ///
    /// MOD 包：<c>{modPath}/{packageKey}/{RelativeTargetPath}</c>
    /// 插件包：<c>{gamePath}/{TargetRootPath}/{packageKey}/{RelativeTargetPath}</c>，
    /// 但下列两种情形省略 <c>{packageKey}</c>：
    /// <list type=bullet>
    /// <item><c>package.PreservesInstallLayout</c> 为 true（文件夹 / 压缩包整体导入的产物）：
    /// 它的 RelativeTargetPath 就是相对游戏根的安装位置，裸文件名即意味着必须在游戏根
    /// （UE4SS 的 <c>dwmapi.dll</c>、CSharpLoader 的 <c>version.dll</c>）。</item>
    /// <item>RelativeTargetPath 自带目录层级（如 <c>ue4ss/Mods/mods.json</c>）：
    /// 那些层级本身就是安装位置。</item>
    /// </list>
    /// 再套一层包目录会让 UE4SS / CSharpLoader 按约定找不到文件，插件永远不生效。
    /// 同名冲突由冲突检测 + 覆盖规则处理，不再依赖目录隔离
    /// （隔离反而把代理 DLL 关进了永远不被加载的子目录）。
    /// 配置包：<c>{gamePath}/{TargetRootPath}/{RelativeTargetPath}</c>，同一配置目标合并后只部署一份。
    /// 其中 TargetRootPath 优先取 ProfilePackageEntry.TargetRootPath，否则取 Package.TargetRootPath。
    ///
    /// 与主项目的部署逻辑一致；与 ResolvedView Layer 1 的"无 PackageKey 子目录"语义不同
    /// （后者用于冲突检测，详见 docs/architecture/overview.md#load-order-conflicts）。
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
            if (package.Kind != PackageKind.Mod)
            {
                var targetRootPath = PathSanitizer.SanitizeRelative(entry?.TargetRootPath ?? package.TargetRootPath);
                var artifactPath = PathSanitizer.SanitizeRelative(artifact.RelativeTargetPath);
                // 保留安装布局的包（目录/压缩包整体导入）与自带层级的路径（含 Config 包）
                // 都直接用声明的位置，不再加包目录隔离。
                return package.Kind == PackageKind.Config
                       || package.PreservesInstallLayout
                       || ArchiveTargetPathResolver.HasDirectoryStructure(artifact.RelativeTargetPath)
                    ? Path.Combine(gamePath, targetRootPath, artifactPath)
                    : Path.Combine(gamePath, targetRootPath, PathSanitizer.SanitizeSegment(package.PackageKey), artifactPath);
            }

            return Path.Combine(modPath,
                PathSanitizer.SanitizeSegment(package.PackageKey),
                PathSanitizer.SanitizeRelative(artifact.RelativeTargetPath));
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
            if (package.Kind != PackageKind.Mod)
            {
                var targetRootPath = PathSanitizer.SanitizeRelative(entry?.TargetRootPath ?? package.TargetRootPath);
                var baseDir = Path.Combine(gamePath, targetRootPath);
                return Path.GetRelativePath(baseDir, absoluteTargetPath);
            }

            return Path.GetRelativePath(modPath, absoluteTargetPath);
        }
    }
}
