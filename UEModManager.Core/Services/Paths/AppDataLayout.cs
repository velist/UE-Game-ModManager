using System;
using System.Collections.Generic;
using System.IO;

namespace UEModManager.Services.Paths;

/// <summary>
/// 可被用户自定义位置的数据根。
///
/// 这三个根的共同点是体积可能很大（MOD 仓库常见几十 GB），因此必须允许用户
/// 把它们放到别的盘；其余数据（JSON 索引、配置、日志）体积恒定为 MB 级，
/// 不提供自定义，避免配置面变得没人搞得清。
/// </summary>
public enum DataRoot
{
    /// <summary>包实体仓库。</summary>
    Repository,

    /// <summary>生成物（部署快照等）存储。</summary>
    Overwrites,

    /// <summary>备份根（其下再分部署事务备份与 MOD 备份）。</summary>
    Backups,
}

/// <summary>
/// 应用数据目录布局。纯计算：给定两个根目录与用户覆盖，产出全部数据路径。
///
/// 分层依据（详见 .claude/audit_reports/2026-07-27-data-location-migration-plan.md）：
/// - <b>本机（LocalAppData）</b>：内容里含本机绝对游戏路径的数据。漫游到另一台机器上
///   全是无效路径，反而制造困惑；MOD 仓库还可能有几十 GB，放漫游目录会拖垮域环境登录。
/// - <b>漫游（RoamingAppData）</b>：只有跨机器仍然成立的纯偏好——语言、主题、昵称头像。
/// - <b>安装目录</b>：只放随安装包分发的只读资源，本类完全不涉及。
///
/// 本类不碰文件系统，也不读配置文件，因此可独立测试；绑定真实环境目录与用户偏好
/// 的工作由主项目的 <c>AppPaths</c> 外观完成。
/// </summary>
public sealed class AppDataLayout
{
    private readonly IReadOnlyDictionary<DataRoot, string> _overrides;

    /// <param name="localRoot">本机数据根，通常是 <c>%LOCALAPPDATA%\UEModManager</c>。</param>
    /// <param name="roamingRoot">漫游数据根，通常是 <c>%APPDATA%\UEModManager</c>。</param>
    /// <param name="overrides">
    /// 用户自定义的可覆盖根。仅接受非空白值；空白项按"未自定义"处理，
    /// 因为配置文件里留下一个空字符串不应该把仓库指到当前工作目录。
    /// </param>
    public AppDataLayout(
        string localRoot,
        string roamingRoot,
        IReadOnlyDictionary<DataRoot, string>? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("本机数据根不能为空", nameof(localRoot));
        if (string.IsNullOrWhiteSpace(roamingRoot))
            throw new ArgumentException("漫游数据根不能为空", nameof(roamingRoot));

        LocalRoot = localRoot;
        RoamingRoot = roamingRoot;

        var cleaned = new Dictionary<DataRoot, string>();
        if (overrides != null)
        {
            foreach (var (key, value) in overrides)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    cleaned[key] = value.Trim();
                }
            }
        }
        _overrides = cleaned;
    }

    /// <summary>本机数据根。</summary>
    public string LocalRoot { get; }

    /// <summary>漫游数据根。</summary>
    public string RoamingRoot { get; }

    // ── 本机：随时可重建或与本机路径强绑定的数据 ──

    /// <summary>主配置（游戏安装路径等）。</summary>
    public string ConfigFile => Path.Combine(LocalRoot, "config.json");

    /// <summary>各游戏的 JSON 索引目录（MOD 清单 / 方案 / 分类 / 冲突覆盖）。</summary>
    public string DataDirectory => Path.Combine(LocalRoot, "Data");

    /// <summary>用户自选的游戏图标。</summary>
    public string GameIconsDirectory => Path.Combine(DataDirectory, "GameIcons");

    /// <summary>启动会话记录。</summary>
    public string LaunchSessionsDirectory => Path.Combine(DataDirectory, "LaunchSessions");

    /// <summary>日志目录。</summary>
    public string LogsDirectory => Path.Combine(LocalRoot, "Logs");

    /// <summary>包实体仓库（可自定义）。</summary>
    public string RepositoryRoot => Resolve(DataRoot.Repository, Path.Combine(LocalRoot, "Repository"));

    /// <summary>生成物存储（可自定义）。</summary>
    public string OverwritesRoot => Resolve(DataRoot.Overwrites, Path.Combine(LocalRoot, "Overwrites"));

    /// <summary>备份根（可自定义）。</summary>
    public string BackupsRoot => Resolve(DataRoot.Backups, Path.Combine(LocalRoot, "Backups"));

    /// <summary>
    /// 部署事务备份。崩溃回滚依赖此目录，绝不能随卸载或覆盖安装消失。
    /// </summary>
    public string DeploymentBackupsDirectory => Path.Combine(BackupsRoot, "Deployments");

    /// <summary>MOD 备份。</summary>
    public string ModBackupsDirectory => Path.Combine(BackupsRoot, "Mods");

    // ── 漫游：跨机器仍然成立的纯偏好 ──

    /// <summary>UI 偏好（语言 / 主题 / 背景）。</summary>
    public string UiConfigFile => Path.Combine(RoamingRoot, "ui_config.json");

    /// <summary>用户头像。</summary>
    public string AvatarsDirectory => Path.Combine(RoamingRoot, "Avatars");

    /// <summary>
    /// 用户自选背景图的副本。设置窗口把选中的图片复制进来再引用，原图被移动或删除后背景仍然可用。
    /// 与之配套的文件名记在 <see cref="UiConfigFile"/> 里，二者必须同层：
    /// 一个漫游一个不漫游的话，换机器后配置指向的图片留在原机，背景直接变空白。
    /// </summary>
    public string BackgroundsDirectory => Path.Combine(RoamingRoot, "Backgrounds");

    /// <summary>
    /// DPAPI 加密后的密钥文件（<c>.enc</c>）及其明文备份。
    ///
    /// <para>
    /// 目录名沿用历史的 <c>config</c> 而非属性名里的 Secrets：这是老版本就在用的位置，
    /// 改名会让已加密的文件失联，而密文一旦对不上就没有别处能重建。留在漫游层同理，
    /// 是保持原位，不是重新判定。
    /// </para>
    /// </summary>
    public string SecretsDirectory => Path.Combine(RoamingRoot, "config");

    /// <summary>该根是否已被用户自定义。</summary>
    public bool IsOverridden(DataRoot root) => _overrides.ContainsKey(root);

    /// <summary>取某个可覆盖根的当前生效路径。</summary>
    public string GetRoot(DataRoot root) => root switch
    {
        DataRoot.Repository => RepositoryRoot,
        DataRoot.Overwrites => OverwritesRoot,
        DataRoot.Backups => BackupsRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "未知的数据根"),
    };

    private string Resolve(DataRoot root, string fallback)
        => _overrides.TryGetValue(root, out var custom) ? custom : fallback;
}
