using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using UEModManager.Services.InstallLayout;

namespace UEModManager.Services.Loaders
{
    /// <summary>插件根（游戏本体所在目录）里 UE4SS 的安装状态。</summary>
    public enum Ue4ssInstallState
    {
        /// <summary>没有装：脚本 MOD 放得再对也不会被加载。</summary>
        NotInstalled,

        /// <summary>已安装（不论是管理器装的、MOD 整合包自带的还是玩家手动装的）。</summary>
        Installed,

        /// <summary>只装了一半，见 <see cref="Ue4ssDetection.Problem"/>。</summary>
        Incomplete,
    }

    /// <summary>UE4SS 的两种目录结构。</summary>
    public enum Ue4ssLayout
    {
        /// <summary>
        /// 现行结构（v4.0.0 起为默认，3.0.1 之后的实验版就已如此）：exe 旁只有代理 DLL，
        /// 其余全部在 <c>ue4ss\</c> 里，脚本 MOD 在 <c>ue4ss\Mods</c>。
        /// </summary>
        Subfolder,

        /// <summary>
        /// 老式结构（3.0.1 及更早的正式版）：<c>UE4SS.dll</c>、<c>UE4SS-settings.ini</c>、<c>Mods\</c> 都直接在 exe 旁。
        /// </summary>
        Flat,
    }

    /// <summary>安装残缺的原因。</summary>
    public enum Ue4ssProblem
    {
        None,

        /// <summary>有 UE4SS 本体，但没有任何能把它带起来的代理 DLL：游戏启动时不会加载它。</summary>
        MissingProxy,

        /// <summary>有代理 DLL 或 UE4SS 的设置文件，但 UE4SS 本体（UE4SS.dll）不在：加载必然失败。</summary>
        MissingCore,
    }

    /// <summary>检测结果。</summary>
    /// <param name="State">安装状态。</param>
    /// <param name="Layout">目录结构；没装时按现行结构。</param>
    /// <param name="Problem">残缺时缺的是哪一半。</param>
    /// <param name="ScriptModsDirectory">这种结构下 UE4SS 实际读取脚本 MOD 的目录（相对插件根）。</param>
    /// <param name="ProxyFileName">认出的代理 DLL 文件名，没有时为 null。</param>
    public sealed record Ue4ssDetection(
        Ue4ssInstallState State, Ue4ssLayout Layout, Ue4ssProblem Problem, string ScriptModsDirectory, string? ProxyFileName)
    {
        public bool IsInstalled => State == Ue4ssInstallState.Installed;
    }

    /// <summary>GitHub 上最新一版 UE4SS 正式版的普通包（不是 zDEV 开发版）。</summary>
    /// <param name="Tag">发布标签，如 <c>v4.0.0</c>。</param>
    /// <param name="AssetName">资产文件名，如 <c>UE4SS_v4.0.0.zip</c>。</param>
    /// <param name="AssetApiUrl">走 api.github.com 的资产下载地址（需带 <c>Accept: application/octet-stream</c>）。</param>
    /// <param name="BrowserDownloadUrl">github.com 的直链，API 地址失败时的备选。</param>
    /// <param name="Size">字节数。</param>
    /// <param name="Sha256">GitHub 给出的 sha256（小写十六进制），没有时为 null。</param>
    public sealed record Ue4ssRelease(
        string Tag, string AssetName, string AssetApiUrl, string BrowserDownloadUrl, long Size, string? Sha256)
    {
        /// <summary>给玩家看的版本号：<c>v4.0.0</c>。</summary>
        public string DisplayVersion => Tag;
    }

    /// <summary>
    /// UE4SS（UE4SS-RE/RE-UE4SS，MIT）的检测、发布解析与压缩包校验（纯函数）。
    ///
    /// 事实依据（2026-11-25 核对 v4.0.0 / v3.0.1 / v2.5.2 的发布包与 v4.0.0 源码）：
    /// <list type="bullet">
    /// <item>代理 DLL 的加载顺序（<c>UE4SS/proxy_generator/main.cpp</c> 的 <c>load_ue4ss_dll</c>）：
    /// <c>--ue4ss-path</c> 命令行 → <c>override.txt</c> → <c>{exe 目录}\ue4ss\UE4SS.dll</c> → <c>{exe 目录}\UE4SS.dll</c>。
    /// 都失败时弹「Failed to load UE4SS.dll」错误框。</item>
    /// <item>脚本 MOD 目录（<c>UE4SSProgram.cpp</c> 的 <c>setup_mod_directory_path</c>）：默认是 UE4SS.dll 所在目录下的
    /// <c>Mods</c>；它不存在而 <c>{exe 目录}\Mods</c> 存在时，退回后者（官方为老安装留的兼容）。</item>
    /// <item>发布包：v4.0.0 是 <c>dwmapi.dll</c> + <c>ue4ss\{UE4SS.dll, UE4SS-settings.ini, Mods\…}</c>；
    /// v3.0.1 是平铺的 <c>dwmapi.dll</c> + <c>UE4SS.dll</c> + <c>UE4SS-settings.ini</c> + <c>Mods\</c>；
    /// v2.5.2 的 Xinput 版没有 UE4SS.dll，<c>xinput1_3.dll</c> 自己就是本体。</item>
    /// <item>代理 DLL 可以是「游戏会加载的任何一个 DLL 的名字」（官方安装指南原话），所以只能按常见名字认，
    /// 认不出时不能断言「没装」。</item>
    /// </list>
    /// </summary>
    public static class Ue4ssLoader
    {
        public const string CoreFileName = "UE4SS.dll";
        public const string SettingsFileName = "UE4SS-settings.ini";
        public const string LoaderDirectoryName = "ue4ss";
        public const string DefaultProxyFileName = "dwmapi.dll";
        public const string OverrideFileName = "override.txt";
        public const string ModsDirectoryName = "Mods";
        public const string EnableMarkerFileName = "enabled.txt";

        /// <summary>现行结构的脚本 MOD 目录。</summary>
        public const string SubfolderModsDirectory = LoaderDirectoryName + @"\" + ModsDirectoryName;

        /// <summary>老式结构的脚本 MOD 目录。</summary>
        public const string FlatModsDirectory = ModsDirectoryName;

        /// <summary>UE4SS 2.5 的 Xinput 版：这个 DLL 自己就是 UE4SS，旁边没有 UE4SS.dll。</summary>
        public const string LegacyXinputFileName = "xinput1_3.dll";

        /// <summary>
        /// 常被用作 UE4SS 代理的 DLL 名字。<c>dwmapi.dll</c> 是官方包自带的；其余是作者为个别游戏改名的常见选择
        /// （官方 proxy_generator 可以按任何系统 DLL 生成代理）。
        /// </summary>
        public static readonly IReadOnlyList<string> KnownProxyFileNames =
        [
            DefaultProxyFileName, LegacyXinputFileName, "xinput1_4.dll", "xinput9_1_0.dll",
            "version.dll", "winmm.dll", "winhttp.dll", "dinput8.dll", "dsound.dll",
            "d3d9.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll",
        ];

        /// <summary>官方仓库的「最新正式版」接口（不含预发布与实验版）。</summary>
        public const string LatestReleaseApiUrl = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/latest";

        /// <summary>给玩家手动下载用的发布页。</summary>
        public const string ReleasesPageUrl = "https://github.com/UE4SS-RE/RE-UE4SS/releases/latest";

        /// <summary>普通包的文件名：<c>UE4SS_v4.0.0.zip</c>。zDEV 开发版、自定义配置包、带提交号的实验版都不匹配。</summary>
        private static readonly Regex StandardAssetName =
            new(@"^UE4SS_v\d+\.\d+\.\d+\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 判断插件根里的安装状态与目录结构。
        /// </summary>
        /// <param name="fileExists">相对插件根的文件是否存在。</param>
        /// <param name="directoryExists">相对插件根的目录是否存在。</param>
        /// <param name="identifiedProxies">
        /// 调用方按内容认出的代理 DLL 文件名（见 <see cref="LooksLikeProxy"/>）：作者把代理改成了不常见的名字时，
        /// 只靠 <see cref="KnownProxyFileNames"/> 认不出来，会把完好的安装误报成「缺代理 DLL」。
        /// </param>
        public static Ue4ssDetection Detect(
            Func<string, bool> fileExists, Func<string, bool> directoryExists, IEnumerable<string>? identifiedProxies = null)
        {
            ArgumentNullException.ThrowIfNull(fileExists);
            ArgumentNullException.ThrowIfNull(directoryExists);

            var subfolderCore = fileExists(LoaderDirectoryName + @"\" + CoreFileName);
            var flatCore = fileExists(CoreFileName);
            var subfolderSettings = fileExists(LoaderDirectoryName + @"\" + SettingsFileName);
            var flatSettings = fileExists(SettingsFileName);
            var proxy = KnownProxyFileNames.FirstOrDefault(fileExists) ?? identifiedProxies?.FirstOrDefault();
            // 2.5 的 Xinput 版：没有 UE4SS.dll，设置文件与 xinput1_3.dll 并列就是完整安装
            var legacyXinput = !subfolderCore && !flatCore && flatSettings && fileExists(LegacyXinputFileName);

            if (subfolderCore || flatCore || legacyXinput)
            {
                // 代理 DLL 先找 ue4ss\UE4SS.dll，所以两种都在时生效的是现行结构
                var layout = subfolderCore ? Ue4ssLayout.Subfolder : Ue4ssLayout.Flat;
                var mods = ScriptModsDirectoryFor(layout, directoryExists);
                return proxy != null
                    ? new(Ue4ssInstallState.Installed, layout, Ue4ssProblem.None, mods, proxy)
                    : new(Ue4ssInstallState.Incomplete, layout, Ue4ssProblem.MissingProxy, mods, null);
            }

            // 没有本体。官方代理 DLL、或 UE4SS 自己的设置文件在，说明装过但本体丢了（被杀软删掉是最常见的原因）。
            // 光有 ue4ss\Mods 不算：那是管理器自己装脚本 MOD 时建出来的。
            var hasOverride = fileExists(OverrideFileName);
            if (subfolderSettings)
                return new(Ue4ssInstallState.Incomplete, Ue4ssLayout.Subfolder, Ue4ssProblem.MissingCore, SubfolderModsDirectory, proxy);
            if (flatSettings)
                return new(Ue4ssInstallState.Incomplete, Ue4ssLayout.Flat, Ue4ssProblem.MissingCore, FlatModsDirectory, proxy);
            if (fileExists(DefaultProxyFileName))
            {
                // override.txt 把本体指到了别处：按已安装算，脚本 MOD 目录管理器无从得知，沿用现行结构
                return hasOverride
                    ? new(Ue4ssInstallState.Installed, Ue4ssLayout.Subfolder, Ue4ssProblem.None, SubfolderModsDirectory, DefaultProxyFileName)
                    : new(Ue4ssInstallState.Incomplete, Ue4ssLayout.Subfolder, Ue4ssProblem.MissingCore, SubfolderModsDirectory, DefaultProxyFileName);
            }

            return new(Ue4ssInstallState.NotInstalled, Ue4ssLayout.Subfolder, Ue4ssProblem.None, SubfolderModsDirectory, null);
        }

        /// <summary>
        /// 按一份文件清单（相对插件根）判断。目录是否存在由清单里的路径推出。
        /// </summary>
        public static Ue4ssDetection Detect(IEnumerable<string> relativeFilePaths)
        {
            ArgumentNullException.ThrowIfNull(relativeFilePaths);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in relativeFilePaths)
            {
                var parts = InstallLayoutNormalizer.Split(path);
                if (parts.Length == 0) continue;
                files.Add(string.Join('\\', parts));
                for (var i = 1; i < parts.Length; i++) directories.Add(string.Join('\\', parts[..i]));
            }
            return Detect(p => files.Contains(Canonical(p)), p => directories.Contains(Canonical(p)));
        }

        /// <summary>单个代理 DLL 的大小上限：官方代理只有几十到几百 KB，超过这个数的不必读进来比对。</summary>
        public const long MaxProxyFileSize = 4 * 1024 * 1024;

        /// <summary>
        /// 按内容判断一个 DLL 是不是 UE4SS 的代理：官方 proxy_generator 生成的代理里都带着它要加载的
        /// 文件名 <c>UE4SS.dll</c>（UTF-16，<c>LoadLibrary(L"UE4SS.dll")</c>）。v3.0.1 与 v4.0.0 的 dwmapi.dll 实测都有。
        /// 代理叫什么名字都认得出来；UE4SS.dll 本体不要拿来问（它自己也含这个字符串）。
        /// </summary>
        public static bool LooksLikeProxy(ReadOnlySpan<byte> content)
            => content.IndexOf(ProxySignature) >= 0;

        private static ReadOnlySpan<byte> ProxySignature => "U\0E\04\0S\0S\0.\0d\0l\0l\0"u8;

        /// <summary>
        /// 这种结构下 UE4SS 实际读取脚本 MOD 的目录。现行结构里 <c>ue4ss\Mods</c> 不存在、而 exe 旁有 <c>Mods</c> 时，
        /// UE4SS 会退回去读后者（玩家只把新 DLL 换进老安装的情形）。
        /// </summary>
        public static string ScriptModsDirectoryFor(Ue4ssLayout layout, Func<string, bool> directoryExists)
        {
            ArgumentNullException.ThrowIfNull(directoryExists);
            if (layout == Ue4ssLayout.Flat) return FlatModsDirectory;
            return !directoryExists(SubfolderModsDirectory) && directoryExists(FlatModsDirectory)
                ? FlatModsDirectory
                : SubfolderModsDirectory;
        }

        /// <summary>
        /// 把检测到的脚本 MOD 目录套进安装归位规则：之后导入、部署、「装错位置」检测用的都是这一份。
        /// 没有 UE4SS 约定的规则（别的引擎）原样返回。
        /// </summary>
        public static InstallLayoutRules ApplyTo(InstallLayoutRules rules, Ue4ssDetection detection)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(detection);
            if (string.IsNullOrEmpty(rules.ScriptModsDirectory)
                || string.Equals(rules.ScriptModsDirectory, detection.ScriptModsDirectory, StringComparison.OrdinalIgnoreCase))
                return rules;
            return rules with { ScriptModsDirectory = detection.ScriptModsDirectory };
        }

        /// <summary>
        /// 一组（已归位的）包内路径里，位于脚本 MOD 目录下的脚本 MOD 文件夹名。加载器本体的包不算
        /// （它自带的示例 MOD 不是玩家装的脚本 MOD）。用来回答「这个方案 / 这次导入用不用得上 UE4SS」。
        /// </summary>
        public static IReadOnlyList<string> ScriptModNames(IEnumerable<string> relativePaths, InstallLayoutRules rules)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            ArgumentNullException.ThrowIfNull(rules);
            var paths = relativePaths.ToList();
            if (rules.ScriptModMarkers.Count == 0 || InstallLayoutNormalizer.IsLoaderRuntime(paths, rules)) return [];

            var names = new List<string>();
            foreach (var parts in paths.Select(InstallLayoutNormalizer.Split))
            {
                foreach (var directory in rules.AllScriptModsDirectories.Select(InstallLayoutNormalizer.Split))
                {
                    foreach (var marker in rules.ScriptModMarkers.Select(InstallLayoutNormalizer.Split))
                    {
                        if (parts.Length != directory.Length + 1 + marker.Length) continue;
                        if (!SegmentsEqual(parts, 0, directory) || !SegmentsEqual(parts, directory.Length + 1, marker)) continue;
                        var name = parts[directory.Length];
                        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                    }
                }
            }
            return names;
        }

        /// <summary>
        /// 从 GitHub 的发布 JSON（单个发布或列表）里挑出最新一版正式版的普通包。解析失败或没有时返回 null。
        /// 草稿、预发布（experimental 系列）一律跳过；资产只认 <c>UE4SS_v{主}.{次}.{修订}.zip</c>。
        /// </summary>
        public static Ue4ssRelease? ParseLatestRelease(string? releaseJson)
        {
            if (string.IsNullOrWhiteSpace(releaseJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(releaseJson);
                var releases = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : [doc.RootElement];

                foreach (var release in releases)
                {
                    if (release.ValueKind != JsonValueKind.Object) continue;
                    if (GetBool(release, "draft") || GetBool(release, "prerelease")) continue;
                    var tag = GetString(release, "tag_name");
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = GetString(asset, "name");
                        if (!IsStandardAssetName(name)) continue;

                        var apiUrl = GetString(asset, "url");
                        var browserUrl = GetString(asset, "browser_download_url");
                        if (!IsOfficialUrl(apiUrl) && !IsOfficialUrl(browserUrl)) continue;

                        var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                        var digest = GetString(asset, "digest");
                        var sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            ? digest[7..].ToLowerInvariant()
                            : null;

                        return new Ue4ssRelease(tag!, name!,
                            IsOfficialUrl(apiUrl) ? apiUrl! : string.Empty,
                            IsOfficialUrl(browserUrl) ? browserUrl! : string.Empty,
                            size, sha);
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        /// <summary>是不是普通包的文件名（<c>UE4SS_v4.0.0.zip</c>）。</summary>
        public static bool IsStandardAssetName(string? name)
            => !string.IsNullOrEmpty(name) && StandardAssetName.IsMatch(name);

        /// <summary>
        /// 下载地址是否来自 GitHub 官方（只接受 api.github.com / github.com 上 UE4SS-RE/RE-UE4SS 的地址）。
        /// 发布 JSON 是网络数据，不能让它把下载引到别处。
        /// </summary>
        public static bool IsOfficialUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            return (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.StartsWith("/repos/UE4SS-RE/RE-UE4SS/", StringComparison.OrdinalIgnoreCase))
                || (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.StartsWith("/UE4SS-RE/RE-UE4SS/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 校验下载下来的压缩包确实是一份完整的 UE4SS：条目路径都老实待在包内（没有 <c>..</c>、绝对路径、盘符），
        /// 并且按 <see cref="Detect(IEnumerable{string})"/> 判断是「已安装」的形状（代理 DLL + UE4SS 本体）。
        /// </summary>
        /// <param name="entryNames">压缩包里的全部条目名（含目录条目）。</param>
        /// <returns>null 表示通过，否则是不通过的原因（给日志用）。</returns>
        public static string? ValidateArchiveEntries(IEnumerable<string> entryNames)
        {
            ArgumentNullException.ThrowIfNull(entryNames);
            var files = new List<string>();
            foreach (var name in entryNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':')
                    || InstallLayoutNormalizer.Split(name).Any(segment => segment is ".." or "."))
                    return $"压缩包里有越界的路径: {name}";
                if (!name.EndsWith('/') && !name.EndsWith('\\')) files.Add(name);
            }

            if (files.Count == 0) return "压缩包是空的";
            var detection = Detect(files);
            return detection.State switch
            {
                Ue4ssInstallState.Installed => null,
                Ue4ssInstallState.Incomplete when detection.Problem == Ue4ssProblem.MissingProxy => "压缩包里没有代理 DLL",
                _ => "压缩包里没有 UE4SS 本体（UE4SS.dll）",
            };
        }

        private static string Canonical(string path) => string.Join('\\', InstallLayoutNormalizer.Split(path));

        private static bool SegmentsEqual(string[] parts, int start, string[] expected)
        {
            if (start + expected.Length > parts.Length) return false;
            for (var i = 0; i < expected.Length; i++)
            {
                if (!string.Equals(parts[start + i], expected[i], StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static string? GetString(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool GetBool(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
