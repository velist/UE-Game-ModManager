using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Adapters
{
    /// <summary>
    /// 宿主适配器接口。
    /// 每种游戏引擎/宿主实现此接口，提供路径映射、文件识别、启动上下文等规则。
    /// 新增游戏支持 = 新增 Adapter 实现，核心代码零改动。
    /// </summary>
    public interface IHostAdapter
    {
        /// <summary>适配器唯一标识（如 "unreal-engine"、"generic-overlay"）。</summary>
        string AdapterKey { get; }

        /// <summary>显示名称。</summary>
        string DisplayName { get; }

        /// <summary>适配器能力描述。</summary>
        AdapterCapabilities Capabilities { get; }

        /// <summary>引擎类型（兼容旧版 EngineProfile）。</summary>
        EngineType EngineType { get; }

        /// <summary>
        /// MOD 文件扩展名集合（含点号，小写）。
        /// 用于文件类型识别和导入过滤。
        /// </summary>
        IReadOnlySet<string> ModFileExtensions { get; }

        /// <summary>
        /// 直接导入支持的扩展名（非压缩包）。
        /// </summary>
        IReadOnlySet<string> DirectImportExtensions { get; }

        /// <summary>
        /// 文件选择对话框过滤器字符串。
        /// </summary>
        string FileDialogFilter { get; }

        /// <summary>
        /// 默认 MOD 目录模式（相对于游戏根目录）。
        /// </summary>
        IReadOnlyList<string> DefaultModPathPatterns { get; }

        /// <summary>
        /// 文件分组优先级扩展名，靠前的优先作为组名。
        /// </summary>
        IReadOnlyList<string> GroupPriorityExtensions { get; }

        /// <summary>
        /// 判断此适配器是否适用于指定的游戏。
        /// </summary>
        bool CanHandle(string gameName, EngineType? engineType = null);

        /// <summary>
        /// 根据游戏名称获取自动检测关键词（用于路径搜索）。
        /// </summary>
        IReadOnlyList<string> GetSearchKeywords(string gameName);

        /// <summary>
        /// 根据游戏名称获取可执行文件搜索关键词。
        /// </summary>
        IReadOnlyList<string> GetExecutableKeywords(string gameName);

        /// <summary>
        /// 获取部署目标路径（游戏 MOD 目录）。
        /// </summary>
        string GetDeployTargetPath(string gameRootPath, string gameName);

        /// <summary>
        /// 获取备份目录路径。
        /// </summary>
        string GetBackupPath(string gameRootPath, string gameName);

        /// <summary>
        /// 判断指定目录名是否应在扫描时跳过。
        /// </summary>
        bool ShouldSkipDirectory(string directoryName, string gameName);

        /// <summary>
        /// 获取推荐的部署后端类型。
        /// </summary>
        DeploymentBackendType RecommendedBackend { get; }
    }
}
