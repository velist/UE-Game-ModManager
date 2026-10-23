using UEModManager.Models;

namespace UEModManager.Services.Detection
{
    /// <summary>
    /// 包类型的玩家可读文案（纯函数）。
    ///
    /// 返回**未翻译的中文源串**，由界面层经 <c>UiText</c> / <c>loc:</c> 翻译，
    /// 卡片、列表、详情面板、方案管理共用这一份，避免各处各写一套叫法。
    /// </summary>
    public static class PackageKindText
    {
        /// <summary>短标签：MOD / 插件 / 配置。</summary>
        public static string Label(PackageKind kind) => kind switch
        {
            PackageKind.Plugin => "插件",
            PackageKind.Config => "配置",
            _ => "MOD",
        };

        /// <summary>一句话解释这类东西是什么、会被放到哪里。</summary>
        public static string Explain(PackageKind kind) => kind switch
        {
            PackageKind.Plugin => "由加载器（如 UE4SS、REFramework）运行的脚本或 DLL，给游戏加新功能，安装在游戏目录里",
            PackageKind.Config => "修改游戏设置的 .ini / .json 等文件，安装在游戏目录里",
            _ => "替换模型、贴图、音效等游戏资源，放进游戏的 MOD 文件夹",
        };

        /// <summary>详情面板标题。</summary>
        public static string DetailTitle(PackageKind kind) => kind switch
        {
            PackageKind.Plugin => "插件详情",
            PackageKind.Config => "配置详情",
            _ => "MOD 详情",
        };
    }
}
