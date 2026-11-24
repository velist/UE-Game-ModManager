using System;
using System.Collections.Generic;

namespace UEModManager.Services.Steam
{
    /// <summary>
    /// 一个路径在 Steam 库里的位置：它属于哪个 <c>steamapps</c> 目录、哪个安装目录。
    /// </summary>
    /// <param name="SteamAppsDirectory"><c>…\steamapps</c> 的完整路径（清单 <c>appmanifest_*.acf</c> 就在这里）。</param>
    /// <param name="InstallDir"><c>steamapps\common\</c> 下的那一段目录名，对应清单里的 <c>installdir</c>。</param>
    /// <param name="InstallPath">游戏安装根目录：<c>…\steamapps\common\&lt;InstallDir&gt;</c>。</param>
    public sealed record SteamInstallLocation(string SteamAppsDirectory, string InstallDir, string InstallPath);

    /// <summary>
    /// 「游戏路径 → 它是不是装在 Steam 库里、是哪一款」（纯函数，不读磁盘）。
    ///
    /// appid 不靠记忆也不靠内置表：从路径里认出 <c>steamapps\common\&lt;installdir&gt;</c>，
    /// 再到同一个 <c>steamapps</c> 目录的清单里按 <c>installdir</c> 找出 appid。
    /// 清单由调用方读好传进来（解析见 <see cref="SteamAppManifest.Parse"/>）。
    /// </summary>
    public static class SteamInstallLocator
    {
        private const string SteamAppsSegment = "steamapps";
        private const string CommonSegment = "common";

        /// <summary>
        /// 从游戏目录或可执行文件路径里认出 Steam 库位置。
        /// 路径可以落在安装目录里任意深的位置（<c>…\common\Game\Binaries\Win64\Game.exe</c>）。
        /// 不在 Steam 库结构下、或路径不是盘符/UNC 绝对路径时返回 null。
        /// </summary>
        public static SteamInstallLocation? Locate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var normalized = path.Trim().Replace('/', '\\');
            var segments = normalized.Split('\\');

            // 取最靠后的一组 steamapps\common\X：库本身放在名叫 steamapps 的目录里时，里面那组才是真的
            for (var i = segments.Length - 3; i >= 1; i--)
            {
                if (!string.Equals(segments[i], SteamAppsSegment, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(segments[i + 1], CommonSegment, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var installDir = segments[i + 2];
                if (installDir.Length == 0 || installDir == "." || installDir == "..") return null;

                var steamApps = string.Join("\\", segments, 0, i + 1);
                if (!IsRooted(steamApps)) return null;

                return new SteamInstallLocation(
                    steamApps, installDir, steamApps + "\\" + segments[i + 1] + "\\" + installDir);
            }

            return null;
        }

        /// <summary>
        /// 在同一个 <c>steamapps</c> 目录的清单里找出这个安装目录属于哪款应用。
        /// 整段比对目录名、不区分大小写；清单没有 appid 的不算。
        /// 找不到、或同一个目录名对上了**不同**的 appid（无法判断是哪一款）时返回 null —— 宁可退回直接启动，
        /// 也不让 Steam 去启动另一款游戏。
        /// </summary>
        public static SteamAppManifest? FindOwner(
            SteamInstallLocation location, IEnumerable<SteamAppManifest?> manifests)
        {
            ArgumentNullException.ThrowIfNull(location);
            ArgumentNullException.ThrowIfNull(manifests);

            SteamAppManifest? found = null;
            foreach (var manifest in manifests)
            {
                if (manifest == null || manifest.AppId == 0) continue;
                if (!string.Equals(manifest.InstallDir, location.InstallDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (found != null && found.AppId != manifest.AppId) return null;
                found ??= manifest;
            }
            return found;
        }

        private static bool IsRooted(string path)
            => (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
               || path.StartsWith("\\\\", StringComparison.Ordinal);
    }
}
