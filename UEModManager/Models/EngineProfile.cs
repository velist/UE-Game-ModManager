using System.Collections.Generic;
using UEModManager.Services.Conflict;

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
                DirectImportExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pak;*.ucas;*.utoc|所有文件|*.*",
                DefaultModPathPatterns = new[] { "Content/Paks/~mods", "Content/Paks/Mods" },
                ConflictStrategy = ConflictStrategy.ContainerAssets,
                SharedComponentRules = UnrealSharedComponentRules,
                GroupPriorityExtensions = new[] { ".pak", ".utoc", ".ucas" }
            },

            [EngineType.Unity] = new EngineProfile
            {
                DisplayName = "Unity",
                ModFileExtensions = new HashSet<string> { ".dll", ".unity3d", ".assets", ".bundle", ".assetbundle" },
                DirectImportExtensions = new HashSet<string> { ".dll", ".unity3d", ".assets", ".bundle", ".assetbundle" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.dll;*.unity3d;*.assets;*.bundle|所有文件|*.*",
                DefaultModPathPatterns = new[] { "BepInEx/plugins", "Mods" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".dll", ".unity3d", ".assets" }
            },

            [EngineType.REEngine] = new EngineProfile
            {
                DisplayName = "RE Engine",
                ModFileExtensions = new HashSet<string> { ".pak", ".tex", ".mesh", ".lua" },
                DirectImportExtensions = new HashSet<string> { ".pak", ".tex", ".mesh", ".lua" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pak;*.tex;*.mesh;*.lua|所有文件|*.*",
                DefaultModPathPatterns = new[] { "natives", "Mods" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".pak", ".tex", ".mesh" }
            },

            [EngineType.Godot] = new EngineProfile
            {
                DisplayName = "Godot",
                ModFileExtensions = new HashSet<string> { ".pck", ".gd", ".tres", ".tscn" },
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
                DirectImportExtensions = new HashSet<string> { ".mpq", ".json", ".xml", ".stl", ".meta", ".d4a", ".dds", ".wtf" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.mpq;*.json;*.xml;*.stl;*.meta;*.d4a;*.dds;*.wtf|所有文件|*.*",
                // mods/ 是 D4ModManager 等加载器约定目录；WTF/ 用于反和谐 Config.wtf。
                DefaultModPathPatterns = new[] { "mods", "WTF" },
                ConflictStrategy = ConflictStrategy.FilePath,
                GroupPriorityExtensions = new[] { ".mpq", ".json", ".stl" }
            },

            [EngineType.Unknown] = new EngineProfile
            {
                DisplayName = "通用",
                ModFileExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json", ".dll", ".pck" },
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
