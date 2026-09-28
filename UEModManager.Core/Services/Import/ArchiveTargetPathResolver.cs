using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEModManager.Services.Import
{
    /// <summary>
    /// 包内目标路径推导（纯函数）。
    ///
    /// 解决"非 Mod 包丢失包内目录结构"的问题：旧实现一律用 <c>Path.GetFileName(file)</c>
    /// 作为 <c>RelativeTargetPath</c>，于是
    /// <c>b1/Binaries/Win64/ue4ss/Mods/mods.json</c> 会部署成
    /// <c>b1/Binaries/Win64/mods.json</c>，
    /// <c>.../CSharpLoader/Mods/PerfectDodgeV2.4/config.json</c> 会变成
    /// <c>b1/Binaries/Win64/config.json</c>。
    /// UE4SS 与 CSharpLoader 都不会去那里找文件，插件因此永远不生效。
    ///
    /// 规则：
    /// <list type="bullet">
    /// <item>Mod 包：仍用裸文件名（UE 的 ~mods 不递归，目录层级只会让 pak 被忽略）。</item>
    /// <item>非 Mod 包：保留相对解压根的相对路径，让插件目录结构原样落盘。</item>
    /// </list>
    /// </summary>
    public static class ArchiveTargetPathResolver
    {
        /// <summary>
        /// 游戏根锚点：这些片段之后的部分才是"相对游戏根"的结构。
        /// 压缩包常把整个 <c>ModName/b1/Binaries/Win64/...</c> 层级打进去，
        /// 若不剥掉就会部署成 <c>&lt;gamePath&gt;/b1/Binaries/Win64/...</c>，
        /// 而 gamePath 本身就已经是 <c>.../b1/Binaries/Win64</c>，游戏自然找不到文件。
        /// 每个锚点是一组必须连续匹配的路径段。
        /// </summary>
        private static readonly string[][] GameAnchors =
        [
            ["Binaries", "Win64"],
            ["Content", "Paks"],
            ["Saved", "Config"],
        ];

        /// <summary>
        /// 计算 Artifact 的目标相对路径。
        /// </summary>
        /// <param name="filePath">解压产物的绝对路径。</param>
        /// <param name="archiveRoot">本次解压的根目录（tempDir）。</param>
        /// <param name="keepDirectoryStructure">
        /// 是否保留包内目录结构。非 Mod 包应为 true；Mod 包为 false。
        /// </param>
        /// <param name="commonPrefix">
        /// 这组文件共有的包装目录（通常就是压缩包里的 MOD 名），由
        /// <see cref="StripCommonPrefix"/> 在**未剥锚点**的相对路径上算出。
        /// 先剥它再剥游戏根锚点，顺序不可颠倒，理由见
        /// <see cref="ResolveRelativePath"/>。
        /// </param>
        public static string Resolve(
            string filePath, string archiveRoot, bool keepDirectoryStructure, string? commonPrefix = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("文件路径不能为空", nameof(filePath));
            if (!keepDirectoryStructure) return Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(archiveRoot)) return Path.GetFileName(filePath);

            var relative = ResolveRelativePath(filePath, archiveRoot);
            if (string.IsNullOrEmpty(relative)) return Path.GetFileName(filePath);

            // 先剥共有的包装目录（MOD 名），再剥游戏根锚点（b1/Binaries/Win64）。
            return StripLeadingGameAnchor(StripPrefix(relative, commonPrefix));
        }

        /// <summary>
        /// 文件相对解压根的原始相对路径，**不剥游戏根锚点**。
        ///
        /// 存在的理由是"共有的包装目录"必须在剥锚点**之前**计算：
        /// 剥锚点会削掉一部分文件的目录层（<c>MOD名/b1/Binaries/Win64/version.dll</c> 变成 <c>version.dll</c>），
        /// 若在剥完之后再找共有前缀，最短的那条路径可能已经只剩裸文件名，
        /// 共有前缀就再也算不出来 —— 同一个包里没命中锚点的文件
        /// （<c>MOD名/说明与事项.txt</c>）于是带着 MOD 名目录落进游戏根，
        /// 在游戏根下留下一个以 MOD 命名的杂项目录。
        /// </summary>
        /// <param name="filePath">解压产物的绝对路径。</param>
        /// <param name="archiveRoot">本次解压的根目录。</param>
        /// <returns>相对解压根的路径；文件不在解压根下时返回空串。</returns>
        public static string ResolveRelativePath(string filePath, string archiveRoot)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return string.Empty;
            if (string.IsNullOrWhiteSpace(archiveRoot)) return string.Empty;

            var root = Path.GetFullPath(archiveRoot);
            var file = Path.GetFullPath(filePath);
            var relative = Path.GetRelativePath(root, file);

            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative)
                ? string.Empty
                : relative;
        }

        /// <summary>
        /// 剥掉路径中第一个"游戏根锚点"及其之前的所有层级。
        /// 找不到锚点时原样返回，交由 <see cref="StripCommonPrefix"/> 处理包装目录。
        /// </summary>
        public static string StripLeadingGameAnchor(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return relativePath;

            var parts = Split(relativePath);
            for (var i = 0; i < parts.Length; i++)
            {
                foreach (var anchor in GameAnchors)
                {
                    if (i + anchor.Length > parts.Length) continue;
                    var matched = true;
                    for (var j = 0; j < anchor.Length; j++)
                    {
                        if (!string.Equals(parts[i + j], anchor[j], StringComparison.OrdinalIgnoreCase))
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (!matched) continue;
                    var remainder = parts.Skip(i + anchor.Length).ToList();
                    // 锚点就是整个路径（不含文件名）时没有可用的相对结构，保持原样
                    return remainder.Count == 0 ? relativePath : string.Join(Path.DirectorySeparatorChar, remainder);
                }
            }

            return relativePath;
        }

        /// <summary>
        /// 去掉指定的前缀目录。前缀为空或路径不含该前缀时原样返回。
        /// </summary>
        public static string StripPrefix(string relativePath, string? prefix)
        {
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrWhiteSpace(prefix)) return relativePath;

            var normalizedPrefix = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
            return relativePath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                ? relativePath[normalizedPrefix.Length..]
                : relativePath;
        }

        /// <summary>
        /// 把路径规范成"游戏根相对路径"：反斜杠分隔、去掉盘符与前后分隔符。
        /// </summary>
        public static string NormalizeGameRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;

            var trimmed = relativePath.Trim();
            try
            {
                return Services.Security.PathSanitizer.SanitizeRelative(trimmed);
            }
            catch (ArgumentException)
            {
                // 非法路径（绝对路径/.. 上跳）在导入阶段就该被拒绝，
                // 由调用方跳过，不要在这里静默改写成别的目标。
                return string.Empty;
            }
        }

        /// <summary>
        /// 包是否为"需要保留目录结构"的类型。只有 Mod 不保留：
        /// 其内容由 UE 按 pak 加载，包内层级无意义；插件/配置则依赖目录位置。
        /// </summary>
        public static bool KeepsDirectoryStructure(Models.PackageKind kind)
            => kind != Models.PackageKind.Mod;

        /// <summary>
        /// 相对目标路径是否自带目录层级。
        ///
        /// 自带层级说明这个路径是"从文件夹/压缩包整体导入"的产物，层级本身就是安装位置
        /// （例如 <c>ue4ss/Mods/mods.json</c>、<c>CSharpLoader/Mods/X/config.json</c>）。
        /// 部署时不能再套一层包目录，否则 UE4SS / CSharpLoader 按约定找不到文件。
        /// 裸文件名（<c>dll.dll</c>）则没有方位信息，仍需包目录隔离以避免同名互相覆盖。
        /// </summary>
        public static bool HasDirectoryStructure(string? relativePath)
            => !string.IsNullOrEmpty(relativePath)
               && relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;

        /// <summary>
        /// 找出这组文件共有的、可安全剥离的前缀目录。
        ///
        /// 压缩包里常见一层包装目录（<c>ModName/...</c>）。
        /// 若原样保留，部署后会变成 <c>&lt;目标根&gt;/ModName/...</c>，游戏仍然找不到。
        /// 这里只剥"所有文件都共享"的那层前缀。
        ///
        /// **但绝不剥掉安装锚点目录**：<c>ue4ss/Mods/...</c>、<c>CSharpLoader/Mods/...</c>
        /// 这类层级本身就是文件的安装位置，剥掉后插件同样不生效。
        /// 因此当共有前缀的第一段是已知安装目录时，直接返回空串（不剥）。
        /// </summary>
        public static string StripCommonPrefix(IReadOnlyList<string> relativePaths)
        {
            if (relativePaths == null) throw new ArgumentNullException(nameof(relativePaths));
            if (relativePaths.Count == 0) return string.Empty;

            var split = relativePaths
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(Split)
                .ToList();
            if (split.Count == 0) return string.Empty;

            // 第一段是安装锚点时，前缀很可能就是"全部目录层"（例如所有文件都在 ue4ss/Mods/ 下），
            // 剥掉会连安装位置一起丢掉。这种情况一律不剥。
            if (IsInstallAnchor(split[0][0])) return string.Empty;

            // 只剥到"最短路径的目录层数 - 1"为止，保证每个文件都还剩文件名
            var maxDepth = split.Min(parts => parts.Length) - 1;
            var common = 0;
            // 最多剥一层。共享前缀有时只是"所有文件恰好都在 ue4ss/Mods/ 下"，
            // 一次剥清会让它们全部落到目标根；只剥最外层包装目录既能收拾
            // ModName/... 这种打包习惯，又不会连安装层级一起削掉。
            while (common < maxDepth && common < 1)
            {
                var candidate = split[0][common];
                if (split.Any(parts => !string.Equals(parts[common], candidate, StringComparison.OrdinalIgnoreCase)))
                    break;
                common++;
            }

            if (common == 0) return string.Empty;
            return string.Join(Path.DirectorySeparatorChar, split[0].Take(common));
        }

        /// <summary>
        /// 安装锚点目录名：这些名字本身就是"文件该待的地方"，不能当作包装目录剥掉。
        /// </summary>
        private static readonly HashSet<string> InstallAnchors = new(StringComparer.OrdinalIgnoreCase)
        {
            "ue4ss", "csharploader", "binaries", "win64", "win32", "content", "paks", "~mods",
            // "b1" 是黑神话悟空的项目名（游戏根 = .../b1/Binaries/Win64）。
            // 把它算作安装锚点，是为了识别"整个输入就是一个 MOD、只是自带 b1 层级"的形状。
            "b1",
            "saved", "config", "engine", "plugins", "mods", "mod",
        };

        /// <summary>该目录名是否为安装锚点（不可作为包装目录剥离）。</summary>
        public static bool IsInstallAnchor(string? directoryName)
            => !string.IsNullOrEmpty(directoryName) && InstallAnchors.Contains(directoryName);

        private static string[] Split(string path)
            => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }
}
