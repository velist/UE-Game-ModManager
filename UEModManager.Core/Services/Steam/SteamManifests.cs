using System;
using System.Collections.Generic;
using System.Globalization;

namespace UEModManager.Services.Steam
{
    /// <summary>
    /// 解析 Steam 的 <c>steamapps/libraryfolders.vdf</c>：列出所有游戏库的根目录（纯函数）。
    /// </summary>
    public static class SteamLibraryFolders
    {
        /// <summary>
        /// 返回文件里登记的库根目录（不含 <c>steamapps</c>），按出现顺序、去重（不区分大小写）。
        /// 两种格式都认：新版每个库是一个对象（<c>"0" { "path" "D:\\SteamLibrary" ... }</c>），
        /// 旧版直接是字符串（<c>"1" "D:\\SteamLibrary"</c>）。读不出来返回空列表。
        /// </summary>
        public static IReadOnlyList<string> ParseLibraryPaths(string? vdfText)
        {
            var result = new List<string>();
            var folders = ValveKeyValues.Parse(vdfText).Child("libraryfolders");
            if (folders == null || !folders.IsObject) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in folders.Children)
            {
                // 库的键是序号；旧版同一层还有 TimeNextStatsReport / ContentStatsID 这类非库字段
                if (!IsIndex(entry.Key)) continue;

                var path = entry.Value.IsObject ? entry.Value.GetString("path") : entry.Value.Value;
                path = NormalizeLibraryPath(path);
                if (path.Length > 0 && seen.Add(path))
                    result.Add(path);
            }
            return result;
        }

        private static bool IsIndex(string key)
        {
            if (key.Length == 0) return false;
            foreach (var c in key)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static string NormalizeLibraryPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var trimmed = path.Trim().Replace('/', '\\');
            // 保留盘符根 "D:\" 的反斜杠，其余去掉结尾分隔符，免得同一个库被算成两个
            while (trimmed.Length > 3 && trimmed.EndsWith('\\'))
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            return trimmed;
        }
    }

    /// <summary>
    /// 一份 Steam 应用清单（<c>appmanifest_&lt;appid&gt;.acf</c>）里定位游戏要用的三个字段。
    /// </summary>
    /// <param name="AppId">Steam appid；清单与文件名里都读不出时为 0。</param>
    /// <param name="Name">Steam 记录的游戏标题；缺失时为空串。</param>
    /// <param name="InstallDir">安装目录名（<c>steamapps/common/</c> 下的那一段），保证是单段安全目录名。</param>
    public sealed record SteamAppManifest(uint AppId, string Name, string InstallDir)
    {
        private const string FilePrefix = "appmanifest_";
        private const string FileSuffix = ".acf";

        /// <summary>
        /// 解析清单文本（纯函数）。<paramref name="fileName"/> 可选：清单里缺 appid 时从
        /// <c>appmanifest_123.acf</c> 这个文件名里补。
        /// 没有 <c>AppState</c>、缺 <c>installdir</c>、或 installdir 不是单段目录名（带分隔符、盘符、<c>..</c>）时返回 null ——
        /// 这个值随后会被拼进路径，不能信任文件里写了什么。
        /// </summary>
        public static SteamAppManifest? Parse(string? acfText, string? fileName = null)
        {
            var state = ValveKeyValues.Parse(acfText).Child("AppState");
            if (state == null || !state.IsObject) return null;

            var installDir = state.GetString("installdir")?.Trim();
            if (!IsSafeDirectoryName(installDir)) return null;

            if (!TryParseAppId(state.GetString("appid"), out var appId))
                TryParseAppIdFromFileName(fileName, out appId);

            return new SteamAppManifest(appId, state.GetString("name")?.Trim() ?? string.Empty, installDir!);
        }

        /// <summary>从 <c>appmanifest_123.acf</c> 文件名取 appid。</summary>
        public static bool TryParseAppIdFromFileName(string? fileName, out uint appId)
        {
            appId = 0;
            if (string.IsNullOrEmpty(fileName)) return false;

            var slash = fileName.LastIndexOfAny(new[] { '\\', '/' });
            var name = slash >= 0 ? fileName.Substring(slash + 1) : fileName;
            if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)
                || name.Length <= FilePrefix.Length + FileSuffix.Length)
            {
                return false;
            }

            return TryParseAppId(
                name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileSuffix.Length), out appId);
        }

        private static bool TryParseAppId(string? text, out uint appId)
            => uint.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out appId) && appId != 0;

        private static bool IsSafeDirectoryName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == "..") return false;
            if (name.EndsWith('.') || name.EndsWith(' ')) return false;
            foreach (var c in name)
            {
                if (c < ' ' || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|')
                {
                    return false;
                }
            }
            return true;
        }
    }
}
