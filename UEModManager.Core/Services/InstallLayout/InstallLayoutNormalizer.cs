using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEModManager.Models;

namespace UEModManager.Services.InstallLayout
{
    /// <summary>
    /// 把一个插件 / 配置包里的相对路径归位成「相对插件根」的安装位置。纯函数、幂等。
    ///
    /// 三条规则，按序套用：
    /// <list type="number">
    /// <item>游戏本体目录锚点：<c>PBZ/platform/steam/ue4ss/Mods/X/…</c> → <c>ue4ss/Mods/X/…</c>。</item>
    /// <item>加载器目录：<c>随便什么/ue4ss/Mods/X/…</c> → <c>ue4ss/Mods/X/…</c>。</item>
    /// <item>散装脚本 MOD 文件夹：<c>X/Scripts/main.lua</c>（没有 ue4ss/Mods 这层）→ <c>ue4ss/Mods/X/Scripts/main.lua</c>。</item>
    /// </list>
    /// 导入时用它决定入库路径；部署时再套一遍，老版本入库的包不用重新导入也能落到正确位置。
    /// </summary>
    public static class InstallLayoutNormalizer
    {
        /// <summary>归位一个包的全部相对路径（顺序与输入一一对应）。</summary>
        public static IReadOnlyList<string> Normalize(IReadOnlyList<string> relativePaths, InstallLayoutRules rules)
        {
            if (relativePaths == null) throw new ArgumentNullException(nameof(relativePaths));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (rules.IsEmpty) return relativePaths;

            var paths = relativePaths.Select(p => StripAnchors(Split(p), rules)).ToList();
            // 自带加载器本体的包（官方发布包、「MOD 自带 UE4SS」的整合包）目录结构由它自己说了算：
            // 老式结构的 UE4SS 从 exe 旁的 Mods 目录读 MOD，把它自带的 MOD 挪进 ue4ss/Mods 反而全都不加载。
            var runtime = IsLoaderRuntime(paths, rules);
            if (!runtime) paths = RehomeScriptMods(paths, rules);
            var modFolders = FindLooseScriptModFolders(paths, rules, runtime);
            var modsDirectory = Split(rules.ScriptModsDirectory);

            return paths.Select(parts =>
            {
                var folder = modFolders.FirstOrDefault(f => StartsWith(parts, f));
                if (folder != null)
                    parts = modsDirectory.Concat(parts.Skip(folder.Length - 1)).ToArray();
                return string.Join(Path.DirectorySeparatorChar, parts);
            }).ToList();
        }

        /// <summary>
        /// 归位后的包副本；路径没有任何变化时返回原实例。
        /// 玩家自己指定了安装子目录（<paramref name="targetRootPath"/> 非空）的包不动：位置是玩家定的。
        /// </summary>
        public static Package NormalizePackage(Package package, InstallLayoutRules rules, string? targetRootPath = null)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (package.Kind == PackageKind.Mod || package.Artifacts.Count == 0) return package;

            var before = package.Artifacts.Select(a => a.RelativeTargetPath ?? string.Empty).ToList();
            var relocate = !rules.IsEmpty && string.IsNullOrWhiteSpace(targetRootPath ?? package.TargetRootPath);
            var after = relocate ? Normalize(before, rules) : before;
            // 老版本把安装树里的图片一律记成「预览图」，部署时整批跳过：
            // 脚本 MOD 自带的贴图（PBZCompassMap 的 compass/*.png）因此从来没装进过游戏。
            var types = package.Artifacts
                .Select((a, i) => EffectiveArtifactType(a.ArtifactType, after[i], package.PreservesInstallLayout || relocate))
                .ToList();
            if (before.SequenceEqual(after, StringComparer.Ordinal)
                && types.SequenceEqual(package.Artifacts.Select(a => a.ArtifactType)))
                return package;

            return new Package
            {
                Id = package.Id, PackageKey = package.PackageKey, DisplayName = package.DisplayName,
                Version = package.Version, Note = package.Note, PreviewImagePath = package.PreviewImagePath,
                Kind = package.Kind, Tags = package.Tags.ToList(), ContentHash = package.ContentHash,
                TotalSize = package.TotalSize, ImportSourcePath = package.ImportSourcePath,
                ImportedAt = package.ImportedAt, LastModified = package.LastModified,
                HostGameName = package.HostGameName, TargetRootPath = package.TargetRootPath,
                // 归位后的路径自带方位，部署时不能再套包目录。
                PreservesInstallLayout = true,
                Artifacts = package.Artifacts.Select((a, i) => new PackageArtifact
                {
                    Id = a.Id, PackageId = a.PackageId, RelativeSourcePath = a.RelativeSourcePath,
                    RelativeTargetPath = after[i], FileName = a.FileName, FileSize = a.FileSize,
                    FileHash = a.FileHash, ArtifactType = types[i]
                }).ToList()
            };
        }

        /// <summary>
        /// 保留安装布局的包里，位于子目录中的图片是 MOD 自己的资源（UE4SS 脚本 MOD 的贴图、图标），
        /// 必须跟着装进游戏；只有包根上的图片才可能是给人看的预览图。
        /// </summary>
        public static ArtifactType EffectiveArtifactType(ArtifactType declared, string? relativeTargetPath, bool preservesInstallLayout)
            => declared == ArtifactType.PreviewImage && preservesInstallLayout && Split(relativeTargetPath).Length > 1
                ? ArtifactType.Other
                : declared;

        /// <summary>
        /// 这组路径里的脚本 MOD 文件夹（按标志文件认，如 <c>X/Scripts/main.lua</c> 的 <c>X</c>）。
        /// 导入时用它保护 MOD 文件夹里的 <c>enabled.txt</c> / 数据 txt：它们不是说明文件，丢了 MOD 就不加载。
        /// </summary>
        public static IReadOnlyList<string> ScriptModFolders(IEnumerable<string> relativePaths, InstallLayoutRules rules)
        {
            if (relativePaths == null) throw new ArgumentNullException(nameof(relativePaths));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parts in relativePaths.Select(Split))
            {
                foreach (var marker in rules.ScriptModMarkers.Select(Split))
                {
                    var folderLength = parts.Length - marker.Length;
                    if (folderLength >= 1 && Matches(parts, folderLength, marker))
                        folders.Add(string.Join(Path.DirectorySeparatorChar, parts[..folderLength]));
                }
            }
            return folders.ToList();
        }

        /// <summary>
        /// 这组路径是不是一份加载器本体（带 <c>UE4SS.dll</c>，或设置文件与某个 DLL 同在一层）。
        /// </summary>
        public static bool IsLoaderRuntime(IEnumerable<string> relativePaths, InstallLayoutRules rules)
        {
            if (relativePaths == null) throw new ArgumentNullException(nameof(relativePaths));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            return IsLoaderRuntime(relativePaths.Select(Split).ToList(), rules);
        }

        private static bool IsLoaderRuntime(List<string[]> paths, InstallLayoutRules rules)
        {
            if (rules.LoaderCoreFileNames.Count == 0 && rules.LoaderSettingsFileNames.Count == 0) return false;
            var files = paths.Where(p => p.Length > 0).ToList();
            if (files.Any(p => rules.LoaderCoreFileNames.Contains(p[^1], StringComparer.OrdinalIgnoreCase))) return true;

            foreach (var settings in files.Where(p =>
                         rules.LoaderSettingsFileNames.Contains(p[^1], StringComparer.OrdinalIgnoreCase)))
            {
                var directory = settings[..^1];
                if (files.Any(p => p.Length == settings.Length
                                   && p[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                   && p[..^1].SequenceEqual(directory, StringComparer.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 归位后的路径里，还缺启用标记的脚本 MOD 文件夹各自该补的标记文件（相对插件根）。
        ///
        /// UE4SS 只启动两种 MOD：写进 MOD 目录里 <c>mods.txt</c> 且标了 1 的，和文件夹里有 <c>enabled.txt</c> 的
        /// （UE4SSProgram.cpp <c>start_mods</c>，两步依次执行）。很多 MOD 压缩包只带脚本、指望玩家自己去改 mods.txt；
        /// 管理器里「启用这个包」就该等于「游戏会加载它」，所以部署时替它补一个空的 enabled.txt，
        /// 停用时随包一起撤掉。玩家的 mods.txt 一个字都不动。
        ///
        /// 不补的两种包：加载器本体（它自带的示例 MOD 开不开由它的 mods.txt 定），
        /// 和自己带了 MOD 列表文件的整合包（启用哪些由作者定）。
        /// </summary>
        public static IReadOnlyList<string> MissingEnableMarkers(IEnumerable<string> normalizedPaths, InstallLayoutRules rules)
        {
            if (normalizedPaths == null) throw new ArgumentNullException(nameof(normalizedPaths));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (string.IsNullOrEmpty(rules.ScriptModEnableMarker) || string.IsNullOrEmpty(rules.ScriptModsDirectory))
                return [];

            var paths = normalizedPaths.Select(Split).Where(p => p.Length > 0).ToList();
            if (IsLoaderRuntime(paths, rules)) return [];

            var modsDirectory = Split(rules.ScriptModsDirectory);
            var inMods = paths.Where(p => StartsWith(p, modsDirectory)).ToList();
            if (inMods.Any(p => p.Length == modsDirectory.Length + 1
                                && rules.ScriptModListFileNames.Contains(p[^1], StringComparer.OrdinalIgnoreCase)))
                return [];

            var result = new List<string>();
            foreach (var marker in rules.ScriptModMarkers.Select(Split))
            {
                // 只认直接放在 MOD 目录下的文件夹：{MOD 目录}/X/Scripts/main.lua
                foreach (var parts in inMods.Where(p => p.Length == modsDirectory.Length + 1 + marker.Length
                                                        && Matches(p, modsDirectory.Length + 1, marker)))
                {
                    var folder = parts[..(modsDirectory.Length + 1)];
                    var enable = folder.Append(rules.ScriptModEnableMarker!).ToArray();
                    if (paths.Any(p => p.SequenceEqual(enable, StringComparer.OrdinalIgnoreCase))) continue;
                    var joined = string.Join(Path.DirectorySeparatorChar, enable);
                    if (!result.Contains(joined, StringComparer.OrdinalIgnoreCase)) result.Add(joined);
                }
            }
            return result;
        }

        /// <summary>路径是否位于给定文件夹之内。</summary>
        public static bool IsInsideAny(string relativePath, IEnumerable<string> folders)
        {
            var parts = Split(relativePath);
            return folders.Select(Split).Any(folder => StartsWith(parts, folder));
        }

        /// <summary>同一个包、但不带任何文件的副本：部署层用它表示「这个包这次不落盘」。</summary>
        public static Package WithoutArtifacts(Package package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            return new Package
            {
                Id = package.Id, PackageKey = package.PackageKey, DisplayName = package.DisplayName,
                Version = package.Version, Kind = package.Kind, Tags = package.Tags.ToList(),
                HostGameName = package.HostGameName, TargetRootPath = package.TargetRootPath,
                PreservesInstallLayout = package.PreservesInstallLayout, Artifacts = []
            };
        }

        /// <summary>按方案条目归位全部包（方案条目里指定了安装子目录的同样不动）。</summary>
        public static Dictionary<string, Package> NormalizePackages(
            IReadOnlyDictionary<string, Package> packagesByKey, InstanceProfile? profile, InstallLayoutRules rules)
        {
            if (packagesByKey == null) throw new ArgumentNullException(nameof(packagesByKey));
            var result = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, package) in packagesByKey)
            {
                var entry = profile?.Packages.FirstOrDefault(p =>
                    string.Equals(p.PackageKey, key, StringComparison.OrdinalIgnoreCase));
                result[key] = NormalizePackage(package, rules, entry?.TargetRootPath ?? package.TargetRootPath);
            }
            return result;
        }

        private static string[] StripAnchors(string[] parts, InstallLayoutRules rules)
        {
            foreach (var anchor in rules.BinaryAnchors.Where(a => a.Count > 0))
            {
                // 文件名不参与匹配，且锚点之后至少还得剩一个文件名。
                for (var i = 0; i + anchor.Count < parts.Length; i++)
                {
                    if (!Matches(parts, i, anchor)) continue;
                    parts = parts[(i + anchor.Count)..];
                    break;
                }
            }

            for (var i = 1; i < parts.Length - 1; i++)
            {
                if (rules.LoaderDirectories.Contains(parts[i], StringComparer.OrdinalIgnoreCase))
                    return parts[i..];
            }
            return parts;
        }

        /// <summary>
        /// 把按另一种目录结构打包的脚本 MOD 挪到当前的脚本 MOD 目录下（<c>ue4ss/Mods/X</c> 与 <c>Mods/X</c> 互换）。
        ///
        /// 整个目录一起挪：MOD 之间共用的 <c>shared</c> 库和 MOD 文件夹必须待在同一个 Mods 目录里。
        /// 以加载器目录开头的别名（<c>ue4ss/Mods</c>）一定是脚本 MOD 目录；光秃秃的 <c>Mods</c> 太常见，
        /// 只有里面确实有脚本 MOD 文件夹（按标志文件认）才算。
        /// </summary>
        private static List<string[]> RehomeScriptMods(List<string[]> paths, InstallLayoutRules rules)
        {
            if (string.IsNullOrEmpty(rules.ScriptModsDirectory)) return paths;
            var current = Split(rules.ScriptModsDirectory);
            var markers = rules.ScriptModMarkers.Select(Split).ToList();

            // 长的在前：ue4ss/Mods 要先于 Mods 判断
            foreach (var alias in rules.ScriptModsDirectoryAliases.Select(Split)
                         .Where(a => a.Length > 0 && !a.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
                         .OrderByDescending(a => a.Length))
            {
                bool InAlias(string[] p) => StartsWith(p, alias) && !StartsWith(p, current);
                if (!paths.Any(InAlias)) continue;

                var certain = rules.LoaderDirectories.Contains(alias[0], StringComparer.OrdinalIgnoreCase);
                if (!certain && !paths.Any(p => InAlias(p) && markers.Any(m =>
                        p.Length == alias.Length + 1 + m.Length && Matches(p, alias.Length + 1, m))))
                    continue;

                paths = paths
                    .Select(p => InAlias(p) ? current.Concat(p.Skip(alias.Length)).ToArray() : p)
                    .ToList();
            }
            return paths;
        }

        /// <summary>
        /// 找出散装的脚本 MOD 文件夹（返回文件夹的完整路径段）。已经在加载器目录或 MOD 目录下的不算；
        /// 加载器本体的包里，位于任何一种脚本 MOD 目录下的也不算（<paramref name="runtime"/>）。
        /// </summary>
        private static List<string[]> FindLooseScriptModFolders(List<string[]> paths, InstallLayoutRules rules, bool runtime)
        {
            var folders = new List<string[]>();
            if (string.IsNullOrEmpty(rules.ScriptModsDirectory)) return folders;
            var modsDirectory = Split(rules.ScriptModsDirectory);
            var inPlace = runtime
                ? rules.AllScriptModsDirectories.Select(Split).ToList()
                : [modsDirectory];

            foreach (var parts in paths)
            {
                if (parts.Length == 0) continue;
                if (rules.LoaderDirectories.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var marker in rules.ScriptModMarkers.Select(Split))
                {
                    // 标志文件前面至少要有一层目录当 MOD 名。
                    var folderLength = parts.Length - marker.Length;
                    if (folderLength < 1 || !Matches(parts, folderLength, marker)) continue;
                    var folder = parts[..folderLength];
                    if (inPlace.Any(directory => StartsWith(folder, directory))) continue;
                    if (!folders.Any(f => f.SequenceEqual(folder, StringComparer.OrdinalIgnoreCase))) folders.Add(folder);
                }
            }
            // 长的在前：嵌套时按最贴近文件的那个 MOD 文件夹归位。
            return folders.OrderByDescending(f => f.Length).ToList();
        }

        private static bool Matches(string[] parts, int start, IReadOnlyList<string> expected)
        {
            if (start < 0 || start + expected.Count > parts.Length) return false;
            for (var j = 0; j < expected.Count; j++)
            {
                if (!string.Equals(parts[start + j], expected[j], StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static bool StartsWith(string[] parts, string[] prefix)
            => prefix.Length > 0 && parts.Length > prefix.Length && Matches(parts, 0, prefix);

        internal static string[] Split(string? path)
            => string.IsNullOrEmpty(path)
                ? []
                : path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    }
}
