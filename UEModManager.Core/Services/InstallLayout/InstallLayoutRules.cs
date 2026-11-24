using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.InstallLayout
{
    /// <summary>
    /// 把「压缩包 / 文件夹里的相对路径」归位成「相对插件根的安装位置」所需的规则。全是数据：
    /// 引擎给一部分（UE 的 UE4SS 约定），游戏给一部分（本体目录叫什么）。
    /// </summary>
    public sealed record InstallLayoutRules
    {
        public static InstallLayoutRules Empty { get; } = new();

        /// <summary>
        /// 游戏本体目录在安装根下的相对位置（逐段），如 <c>["platform","steam"]</c>。
        /// 路径里出现它，说明打包者是从安装根（或更外层）开始打的包：它及之前的层级都要剥掉。
        /// 通用的 <c>Binaries/Win64</c> 由 <c>ArchiveTargetPathResolver</c> 负责，这里只放游戏自己的。
        /// </summary>
        public IReadOnlyList<IReadOnlyList<string>> BinaryAnchors { get; init; } = [];

        /// <summary>
        /// 直接坐落在插件根下的加载器目录名（UE 的 <c>ue4ss</c>）。
        /// 路径里出现它，它之前的层级全是包装，剥掉；它自己保留。
        /// </summary>
        public IReadOnlyList<string> LoaderDirectories { get; init; } = [];

        /// <summary>脚本 MOD 文件夹该待的目录（相对插件根），如 <c>ue4ss\Mods</c>；为空表示本引擎没有这类约定。</summary>
        public string? ScriptModsDirectory { get; init; }

        /// <summary>
        /// 认出「这是一个脚本 MOD 文件夹」的标志文件（相对 MOD 文件夹），如 <c>Scripts/main.lua</c>、<c>dlls/main.dll</c>。
        /// </summary>
        public IReadOnlyList<string> ScriptModMarkers { get; init; } = [];

        /// <summary>只有放进脚本 MOD 文件夹才会被加载的脚本扩展名（含点），如 <c>.lua</c>。</summary>
        public IReadOnlyList<string> LoaderScriptExtensions { get; init; } = [];

        /// <summary>游戏只会读一份的配置文件名（UE 的 Engine.ini 等）：一个包里带多份就是「多选一」。</summary>
        public IReadOnlyList<string> SingletonConfigFileNames { get; init; } = [];

        public bool IsEmpty
            => BinaryAnchors.Count == 0 && LoaderDirectories.Count == 0 && string.IsNullOrEmpty(ScriptModsDirectory);

        /// <summary>
        /// 规则里涉及的目录名：它们是安装位置，导入时既不能当包装目录剥掉，也不能当「多个 MOD」的切分依据。
        /// </summary>
        public IReadOnlySet<string> AnchorNames(IEnumerable<string>? engineAnchors = null)
        {
            var names = new HashSet<string>(engineAnchors ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var anchor in BinaryAnchors.Where(a => a.Count > 0)) names.Add(anchor[0]);
            foreach (var loader in LoaderDirectories) names.Add(loader);
            return names;
        }
    }
}
