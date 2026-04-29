namespace UEModManager.Adapters
{
    /// <summary>
    /// 适配器能力矩阵。描述宿主适配器支持的功能特性。
    /// </summary>
    public class AdapterCapabilities
    {
        /// <summary>是否支持 pak 冲突检测（CUE4Parse）。</summary>
        public bool SupportsConflictDetection { get; init; }

        /// <summary>是否支持配置文件合并。</summary>
        public bool SupportsConfigMerge { get; init; }

        /// <summary>是否支持插件/DLL 加载。</summary>
        public bool SupportsPlugins { get; init; }

        /// <summary>是否支持硬链接部署。</summary>
        public bool SupportsHardLink { get; init; } = true;

        /// <summary>是否支持符号链接部署。</summary>
        public bool SupportsSymlink { get; init; } = true;

        /// <summary>是否支持自动路径检测。</summary>
        public bool SupportsAutoDetect { get; init; } = true;

        /// <summary>是否支持启动参数注入。</summary>
        public bool SupportsLaunchArgs { get; init; }

        /// <summary>是否支持 CNS 模式（剑星专用）。</summary>
        public bool SupportsCNS { get; init; }
    }
}
