using System;
using System.IO;

namespace UEModManager.Services.InstallLayout
{
    /// <summary>
    /// 「启动用的 exe 不在游戏本体旁边」的游戏的目录约定（两个目录都相对安装根）。
    ///
    /// 管理器记下的游戏路径是启动 exe 所在目录。绝大多数 UE 游戏的启动 exe 就是本体
    /// （<c>{工程}/Binaries/Win64</c>），加载器（UE4SS 的 dwmapi.dll、ue4ss/）也装在那里，两者是一回事。
    /// 影之刃零不是：Steam 启动的是 <c>steam\PBZ.exe</c>（约 1 MB 的壳），本体在
    /// <c>platform\steam\PBZ.exe</c>，加载器只有放在本体旁边才会被加载。
    /// </summary>
    /// <param name="LauncherDirectory">启动壳所在目录，如 <c>steam</c>。</param>
    /// <param name="BinaryDirectory">游戏本体所在目录，如 <c>platform\steam</c>。</param>
    public sealed record GameInstallLayout(string LauncherDirectory, string BinaryDirectory);

    /// <summary>插件根的解析结果。</summary>
    /// <param name="PluginRoot">插件 / 配置包的落盘基准（游戏本体所在目录）。</param>
    /// <param name="InstallRoot">游戏安装根；推不出来时为 null。</param>
    /// <param name="Redirected">插件根是否不同于记下的游戏路径。</param>
    public sealed record PluginRootResolution(string PluginRoot, string? InstallRoot, bool Redirected);

    /// <summary>
    /// 由「记下的游戏路径（exe 目录）」推出插件 / 配置包真正的落盘基准。纯函数，磁盘探测由调用方注入。
    /// </summary>
    public static class PluginRootResolver
    {
        public static PluginRootResolution Resolve(
            string? executableDirectory, GameInstallLayout? layout, Func<string, bool> directoryExists)
        {
            if (directoryExists == null) throw new ArgumentNullException(nameof(directoryExists));
            var exeDir = string.IsNullOrWhiteSpace(executableDirectory)
                ? string.Empty
                : Path.TrimEndingDirectorySeparator(executableDirectory.Trim());
            if (exeDir.Length == 0 || layout == null) return new PluginRootResolution(exeDir, null, false);

            // 先比本体目录：platform\steam 也以 steam 结尾，顺序反了会把本体目录误判成启动壳目录。
            if (TryStripSuffix(exeDir, layout.BinaryDirectory, out var rootFromBinary))
                return new PluginRootResolution(exeDir, rootFromBinary, false);

            if (TryStripSuffix(exeDir, layout.LauncherDirectory, out var installRoot))
            {
                var binary = Path.Combine(installRoot,
                    string.Join(Path.DirectorySeparatorChar, InstallLayoutNormalizer.Split(layout.BinaryDirectory)));
                // 本体目录不在（别的平台版本、目录结构变了）就不改道：宁可沿用旧位置，也不往不存在的目录里装。
                return directoryExists(binary)
                    ? new PluginRootResolution(binary, installRoot, true)
                    : new PluginRootResolution(exeDir, installRoot, false);
            }

            return new PluginRootResolution(exeDir, null, false);
        }

        private static bool TryStripSuffix(string directory, string relativeSuffix, out string remainder)
        {
            remainder = string.Empty;
            var suffix = InstallLayoutNormalizer.Split(relativeSuffix);
            if (suffix.Length == 0) return false;
            var parts = InstallLayoutNormalizer.Split(directory);
            if (parts.Length <= suffix.Length) return false;
            for (var i = 0; i < suffix.Length; i++)
            {
                if (!string.Equals(parts[parts.Length - suffix.Length + i], suffix[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var current = directory;
            for (var i = 0; i < suffix.Length; i++)
                current = Path.GetDirectoryName(current) ?? string.Empty;
            remainder = current;
            return remainder.Length > 0;
        }
    }
}
