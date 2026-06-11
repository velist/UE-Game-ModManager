using System.Collections.Generic;

namespace UEModManager.Models
{
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

        /// <summary>是否支持冲突检测（CUE4Parse）。</summary>
        public bool SupportsConflictDetection { get; init; }

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
                SupportsConflictDetection = true,
                GroupPriorityExtensions = new[] { ".pak", ".utoc", ".ucas" }
            },

            [EngineType.Unity] = new EngineProfile
            {
                DisplayName = "Unity",
                ModFileExtensions = new HashSet<string> { ".dll", ".unity3d", ".assets", ".bundle", ".assetbundle" },
                DirectImportExtensions = new HashSet<string> { ".dll", ".unity3d", ".assets", ".bundle", ".assetbundle" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.dll;*.unity3d;*.assets;*.bundle|所有文件|*.*",
                DefaultModPathPatterns = new[] { "BepInEx/plugins", "Mods" },
                SupportsConflictDetection = false,
                GroupPriorityExtensions = new[] { ".dll", ".unity3d", ".assets" }
            },

            [EngineType.REEngine] = new EngineProfile
            {
                DisplayName = "RE Engine",
                ModFileExtensions = new HashSet<string> { ".pak", ".tex", ".mesh", ".lua" },
                DirectImportExtensions = new HashSet<string> { ".pak", ".tex", ".mesh", ".lua" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pak;*.tex;*.mesh;*.lua|所有文件|*.*",
                DefaultModPathPatterns = new[] { "natives", "Mods" },
                SupportsConflictDetection = false,
                GroupPriorityExtensions = new[] { ".pak", ".tex", ".mesh" }
            },

            [EngineType.Godot] = new EngineProfile
            {
                DisplayName = "Godot",
                ModFileExtensions = new HashSet<string> { ".pck", ".gd", ".tres", ".tscn" },
                DirectImportExtensions = new HashSet<string> { ".pck", ".gd", ".tres", ".tscn" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pck;*.gd;*.tres;*.tscn|所有文件|*.*",
                DefaultModPathPatterns = new[] { "mods" },
                SupportsConflictDetection = false,
                GroupPriorityExtensions = new[] { ".pck", ".tres" }
            },

            [EngineType.Decima] = new EngineProfile
            {
                DisplayName = "Decima",
                ModFileExtensions = new HashSet<string> { ".bin", ".core", ".stream", ".pak", ".psarc" },
                DirectImportExtensions = new HashSet<string> { ".bin", ".core", ".stream", ".pak", ".psarc" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.bin;*.core;*.stream;*.pak|所有文件|*.*",
                DefaultModPathPatterns = new[] { "Mods", "data" },
                SupportsConflictDetection = false,
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
                SupportsConflictDetection = false,
                GroupPriorityExtensions = new[] { ".mpq", ".json", ".stl" }
            },

            [EngineType.Unknown] = new EngineProfile
            {
                DisplayName = "通用",
                ModFileExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json", ".dll", ".pck" },
                DirectImportExtensions = new HashSet<string> { ".pak", ".ucas", ".utoc", ".json", ".dll", ".pck" },
                FileDialogFilter = "MOD文件|*.zip;*.rar;*.7z;*.pak;*.dll;*.pck|所有文件|*.*",
                DefaultModPathPatterns = new[] { "Mods", "mods" },
                SupportsConflictDetection = false,
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
