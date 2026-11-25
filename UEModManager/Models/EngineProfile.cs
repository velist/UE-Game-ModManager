using System;
using System.Collections.Generic;
using UEModManager.Services.Conflict;
using UEModManager.Services.InstallLayout;
using UEModManager.Services.Loaders;

namespace UEModManager.Models
{
    /// <summary>
    /// 冲突检测策略。
    ///
    /// <see cref="FilePath"/> 是退化路径：把包的 Artifact 目标路径当成资产键
    /// （非 UE 引擎的游戏、以及所有非 MOD 包都走这条）。
    /// <see cref="ContainerAssets"/> 要打开包里的 .pak/.utoc 读资产索引 ——
    /// 只有这样做才能发现"两个 MOD 改到了同一张贴图"，而它们在磁盘上
    /// 落到的是两个不同的 pak 文件，按文件路径永远比不出来。
    /// </summary>
    public enum ConflictStrategy
    {
        /// <summary>按部署目标文件路径比对。</summary>
        FilePath,

        /// <summary>打开容器读资产索引后比对。</summary>
        ContainerAssets
    }

    /// <summary>
    /// 引擎配置档案，定义每种引擎的 MOD 文件格式、导入过滤器、路径模式等。
    /// </summary>
    public class EngineProfile
    {
        /// <summary>引擎显示名。</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>MOD 文件扩展名集合（含点号，小写）。</summary>
        public HashSet<string> ModFileExtensions { get; init; } = new();

        /// <summary>直接导入支持的扩展名（非压缩包）。</summary>
        public HashSet<string> DirectImportExtensions { get; init; } = new();

        /// <summary>
        /// 主 MOD 载荷扩展名：游戏从 MOD 目录加载的资源容器（UE 的 .pak 三件套、Godot 的 .pck…）。
        ///
        /// 压缩包 / 文件夹导入据此分流：命中的文件按 MOD 处理、进 MOD 目录；其余文件
        /// （脚本、DLL、配置、松散资源）整单元保留目录结构，作为插件或配置装进游戏目录。
        /// 必须是 <see cref="ModFileExtensions"/> 的子集，且不能含 .json / .dll / .lua 这类
        /// 插件或配置也会用到的扩展名，否则 UE4SS 的 mods.json 之类会被误当成 MOD。
        /// </summary>
        public HashSet<string> BinaryModExtensions { get; init; } = new();

        /// <summary>
        /// 本引擎额外的"安装锚点目录"名：这些名字本身就是文件该待的地方，
        /// 既不能被当成压缩包的包装目录剥掉，也不能当成"这里面装了多个 MOD"的切分依据。
        ///
        /// 通用锚点表（ue4ss / Binaries / Content / b1 / Mods …）在 Core 的
        /// <c>ArchiveTargetPathResolver</c> 里，这里只补引擎自己的：RE 引擎的 natives（松散资源由
        /// LooseFileLoader 按游戏内路径读取）、reframework（脚本与插件）、pak_mods（REFramework 挂载的 pak）、
        /// Unity 的 BepInEx、暗黑4 的 WTF。
        ///
        /// 缺了它们的后果是实打实的：<c>natives/stm/x.tex</c> 会被判成"natives 是包装目录"，
        /// 包名变成 natives、natives/ 那一层被剥掉，文件落到游戏根而不是 natives/ 下 ——
        /// 游戏永远读不到，界面还显示导入成功。
        /// </summary>
        public HashSet<string> InstallAnchors { get; init; } = new();

        /// <summary>
        /// 松散载荷的固定安装根（扩展名 → 相对游戏根的目录）。
        ///
        /// 单文件导入没有方位信息，按这里给出的位置落盘，而不是扔进 MOD 目录或玩家填的目标目录：
        /// RE 引擎的 <c>.lua</c> 是 REFramework 脚本，只在 <c>reframework/autorun/</c> 下才会被加载
        /// （见 <c>ReEngineModLayout</c> 的约定）。其它引擎没有这类固定根，留空即可。
        /// </summary>
        public IReadOnlyDictionary<string, string> LoosePayloadRoots { get; init; }
            = new Dictionary<string, string>();

        /// <summary>
        /// 加载器的安装约定（相对「插件根」= 游戏本体所在目录）：UE 的 UE4SS 目录叫什么、
        /// 脚本 MOD 文件夹放哪、怎么认出一个散装的脚本 MOD 文件夹、哪些配置游戏只读一份。
        /// 游戏自己的本体目录锚点由 <c>GameConfigService.GetInstallLayout</c> 再补进去。
        /// </summary>
        public InstallLayoutRules InstallLayout { get; init; } = InstallLayoutRules.Empty;

        /// <summary>
        /// UE 游戏的加载器约定。UE4SS（3.x 起的目录结构）整个装在游戏本体旁边：
        /// 代理 DLL（dwmapi.dll）与 <c>ue4ss/</c> 并列，脚本 MOD 在 <c>ue4ss/Mods/{MOD名}/</c>，
        /// Lua MOD 的入口是 <c>Scripts/main.lua</c>，C++ MOD 的入口是 <c>dlls/main.dll</c>。
        /// </summary>
        /// <remarks>
        /// 这里给的是默认值（现行结构）。游戏里实际装的是老式结构（<c>UE4SS.dll</c> 与 <c>Mods\</c> 直接在 exe 旁）时，
        /// <c>GameConfigService.CurrentInstallLayoutRules</c> 会按探测结果把 <see cref="InstallLayoutRules.ScriptModsDirectory"/>
        /// 换成 <c>Mods</c>；判定规则在 Core 的 <c>Ue4ssLoader</c>。
        /// </remarks>
        public static readonly InstallLayoutRules UnrealInstallLayout = new()
        {
            LoaderDirectories = [Ue4ssLoader.LoaderDirectoryName],
            ScriptModsDirectory = Ue4ssLoader.SubfolderModsDirectory,
            ScriptModsDirectoryAliases = [Ue4ssLoader.SubfolderModsDirectory, Ue4ssLoader.FlatModsDirectory],
            LoaderCoreFileNames = [Ue4ssLoader.CoreFileName],
            LoaderSettingsFileNames = [Ue4ssLoader.SettingsFileName],
            ScriptModEnableMarker = Ue4ssLoader.EnableMarkerFileName,
            ScriptModListFileNames = ["mods.txt", "mods.json"],
            ScriptModMarkers = ["Scripts/main.lua", "dlls/main.dll"],
            LoaderScriptExtensions = [".lua"],
            SingletonConfigFileNames = ["Engine.ini", "Scalability.ini", "GameUserSettings.ini", "Game.ini", "Input.ini"],
        };

        /// <summary>文件选择对话框过滤器字符串。</summary>
        public string FileDialogFilter { get; init; } = string.Empty;

        /// <summary>默认 MOD 目录模式（相对于游戏根目录）。</summary>
        public string[] DefaultModPathPatterns { get; init; } = [];

        /// <summary>冲突检测策略。</summary>
        public ConflictStrategy ConflictStrategy { get; init; } = ConflictStrategy.FilePath;

        /// <summary>
        /// 共享组件规则：多个 MOD 各带一份、而实际上带一份就够的运行时文件。
        ///
        /// 规则作用于非 MOD 包的条目 —— 那些条目本来就是相对游戏根的文件路径。
        /// pak 内部的资产不走这里：pak 里是引擎资产路径，与 MOD 自带的 ue4ss/ 目录
        /// 没有可比性。
        /// </summary>
        public IReadOnlyList<SharedComponentRule> SharedComponentRules { get; init; } = [];

        /// <summary>
        /// UE 游戏的共享组件规则。
        ///
        /// 路径前缀与文件名都要覆盖：UE4SS 有的包按 ue4ss/ 目录带，有的只把
        /// 代理 DLL 放在 Binaries/Win64 根部 —— 只认前缀会漏掉后者。
        /// </summary>
        public static readonly SharedComponentRule[] UnrealSharedComponentRules =
        [
            new("UE4SS 运行时",
                ["ue4ss/", "binaries/win64/ue4ss/"],
                ["dwmapi.dll", "ue4ss.dll", "ue4ss-settings.ini", "mods.txt", "mods.json", "vtablelayout.ini"]),
            new("CSharpLoader 运行时",
                ["csharploader/"],
                ["version.dll", "b1cs.ini"]),
            new("代理 DLL",
                [],
                ["xinput1_3.dll", "d3d11.dll", "d3d12.dll", "dinput8.dll", "winmm.dll", "dsound.dll"])
        ];
        /// <summary>文件分组优先级扩展名，靠前的优先作为组名。</summary>
        public string[] GroupPriorityExtensions { get; init; } = [];

        /// <summary>
        /// 全局引擎配置注册表。
        /// </summary>
        public static readonly Dictionary<EngineType, EngineProfile> Profiles = new()
        {
            [EngineType.UnrealEngine] = new EngineProfile
            {
                DisplayName = "Unreal Engine",
                ModFileExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json" },
                BinaryModExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc" },
                DirectImportExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pak;*.ucas;*.utoc|所有文件|*.*",
                DefaultModPathPatterns = new[] { "Content/Paks/~mods", "Content/Paks/Mods" },
                ConflictStrategy = ConflictStrategy.ContainerAssets,
                SharedComponentRules = UnrealSharedComponentRules,
                InstallLayout = UnrealInstallLayout,
                GroupPriorityExtensions = new[] { ".pak", ".utoc", ".ucas" }
            },

            [EngineType.Unity] = new EngineProfile
            {
                DisplayName = "Unity",
                ModFileExtensions = new HashSet<string> { ".dll", ".unity3d", ".assets", ".bundle", ".assetbundle" },
                BinaryModExtensions = new HashSet<string> { ".unity3d", ".assets", ".bundle", ".assetbundle" },
                DirectImportExtensions = new HashSet<string> { ".dll", ".unity3d", ".assets", ".bundle", ".assetbundle" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.dll;*.unity3d;*.assets;*.bundle|所有文件|*.*",
                // BepInEx 的插件目录：压缩包少打这一层时不能当包装目录剥掉。
                InstallAnchors = new HashSet<string> { "bepinex" },
                DefaultModPathPatterns = new[] { "BepInEx/plugins", "Mods" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".dll", ".unity3d", ".assets" }
            },

            [EngineType.REEngine] = new EngineProfile
            {
                DisplayName = "RE Engine",
                // .lua 不在这里：REFramework 脚本是"由加载器跑起来的载荷"，
                // 与 UE4SS 的 lua 同类，制品类型一律算插件（见 ArtifactTypeDetector.PluginExtensions）。
                // 以前两边各写一份（这里算 MOD、那边算插件），同一个 .lua 直接拖入与放进压缩包
                // 会得到两种身份，部署位置与冲突语义随入口变化。
                ModFileExtensions = new HashSet<string> { ".pak", ".tex", ".mesh" },
                BinaryModExtensions = new HashSet<string> { ".pak" },
                DirectImportExtensions = new HashSet<string> { ".pak", ".tex", ".mesh", ".lua" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pak;*.tex;*.mesh;*.lua|所有文件|*.*",
                // RE 的松散资源由 REFramework 的 LooseFileLoader 按游戏内路径读取，
                // natives/ 与 reframework/ 必须原样保留；pak 交给 REFramework 的 pak_mods/。
                InstallAnchors = new HashSet<string> { "natives", "reframework", "pak_mods" },
                LoosePayloadRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".lua"] = "reframework/autorun",
                },
                DefaultModPathPatterns = new[] { "natives", "Mods" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".pak", ".tex", ".mesh" }
            },

            [EngineType.Godot] = new EngineProfile
            {
                DisplayName = "Godot",
                ModFileExtensions = new HashSet<string> { ".pck", ".gd", ".tres", ".tscn" },
                BinaryModExtensions = new HashSet<string> { ".pck" },
                DirectImportExtensions = new HashSet<string> { ".pck", ".gd", ".tres", ".tscn" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pck;*.gd;*.tres;*.tscn|所有文件|*.*",
                DefaultModPathPatterns = new[] { "mods" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".pck", ".tres" }
            },

            [EngineType.Decima] = new EngineProfile
            {
                DisplayName = "Decima",
                ModFileExtensions = new HashSet<string> { ".bin", ".core", ".stream", ".pak", ".psarc" },
                BinaryModExtensions = new HashSet<string> { ".bin", ".core", ".stream", ".pak", ".psarc" },
                DirectImportExtensions = new HashSet<string> { ".bin", ".core", ".stream", ".pak", ".psarc" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.bin;*.core;*.stream;*.pak|所有文件|*.*",
                DefaultModPathPatterns = new[] { "Mods", "data" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".bin", ".core", ".stream" }
            },

            [EngineType.Diablo4Engine] = new EngineProfile
            {
                DisplayName = "暗黑 4 引擎",
                // MOD 主体：MPQ 打包包；数据：JSON/XML；专属二进制：.stl 字符串、.meta 索引、.d4a 动画；
                // 贴图：.dds（.png 不收，避免误识别普通图片）；启动配置：.wtf。
                ModFileExtensions = new HashSet<string> { ".mpq", ".json", ".xml", ".stl", ".meta", ".d4a", ".dds", ".wtf" },
                BinaryModExtensions = new HashSet<string> { ".mpq" },
                DirectImportExtensions = new HashSet<string> { ".mpq", ".json", ".xml", ".stl", ".meta", ".d4a", ".dds", ".wtf" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.mpq;*.json;*.xml;*.stl;*.meta;*.d4a;*.dds;*.wtf|所有文件|*.*",
                // mods/ 是 D4ModManager 等加载器约定目录；WTF/ 用于反和谐 Config.wtf。
                InstallAnchors = new HashSet<string> { "wtf" },
                DefaultModPathPatterns = new[] { "mods", "WTF" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".mpq", ".json", ".stl" }
            },

            [EngineType.Unknown] = new EngineProfile
            {
                DisplayName = "通用",
                ModFileExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json", ".dll", ".pck" },
                BinaryModExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".pck" },
                DirectImportExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json", ".dll", ".pck" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pak;*.dll;*.pck|所有文件|*.*",
                DefaultModPathPatterns = new[] { "Mods", "mods" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".pak", ".dll", ".pck" }
            }
        };

        /// <summary>
        /// 获取指定引擎类型的配置，不存在时返回 Unknown 配置。
        /// </summary>
        public static EngineProfile Get(EngineType type)
        {
            return Profiles.TryGetValue(type, out var profile) ? profile : Profiles[EngineType.Unknown];
        }

        /// <summary>
        /// 将字符串解析为 EngineType 枚举。
        /// </summary>
        public static EngineType Parse(string? value)
        {
            if (string.IsNullOrEmpty(value)) return EngineType.Unknown;
            return value.ToLower() switch
            {
                "unrealengine" or "unreal engine" or "ue" => EngineType.UnrealEngine,
                "unity" => EngineType.Unity,
                "reengine" or "re engine" => EngineType.REEngine,
                "godot" => EngineType.Godot,
                "decima" => EngineType.Decima,
                "diablo4engine" or "diablo4" or "d4engine" or "d4" or "暗黑4引擎" or "暗黑 4 引擎" => EngineType.Diablo4Engine,
                _ => EngineType.Unknown
            };
        }
    }
}
