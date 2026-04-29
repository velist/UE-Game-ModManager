namespace UEModManager.Services.Profile
{
    /// <summary>
    /// v1.8 旧版 ModInfo 在 Core 端的视图（仅迁移/同步所需字段）。
    ///
    /// 主项目的 ModInfo 依赖 WPF（ImageSource），无法进 net8.0 的 Core。
    /// Profile 同步 / 数据迁移逻辑只关心几个标识字段，所以用这个 record 解耦。
    /// 主项目调用前把 ModInfo 投影成 LegacyModEntry 再传入。
    /// </summary>
    public sealed record LegacyModEntry(
        string RealName,
        bool IsEnabled,
        bool IsPlugin,
        string? PluginTargetPath);
}
