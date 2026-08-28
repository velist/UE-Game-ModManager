using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace UEModManager.Services.Paths;

/// <summary>候选位置所在卷的类型。由主项目查 <c>DriveInfo</c> 后填入。</summary>
public enum RepositoryVolumeKind
{
    /// <summary>查不出来（UNC 根、卷未就绪、权限不足）。</summary>
    Unknown,

    /// <summary>本机固定磁盘。唯一没有额外风险的一类。</summary>
    Fixed,

    /// <summary>可移动磁盘（U 盘、移动硬盘、SD 卡）。</summary>
    Removable,

    /// <summary>网络位置（映射盘或 UNC 路径）。</summary>
    Network,
}

/// <summary>候选位置的整体结论。</summary>
public enum RepositoryLocationSeverity
{
    /// <summary>可以直接用。</summary>
    Ok,

    /// <summary>能用，但有需要用户知道的代价，界面上要让他再确认一次。</summary>
    Warning,

    /// <summary>不能用，必须换一个。</summary>
    Blocked,
}

/// <summary>候选位置上发现的一条问题。<see cref="Code"/> 供测试与日志使用，<see cref="Message"/> 直接展示给用户。</summary>
public enum RepositoryLocationIssueCode
{
    /// <summary>没填路径。</summary>
    Empty,

    /// <summary>不是一个完整的绝对路径（相对路径、只有盘符没有分隔符等）。</summary>
    NotAbsolute,

    /// <summary>路径里有非法字符，或格式本身就解析不了。</summary>
    Malformed,

    /// <summary>试写失败：没有权限、只读介质、被占用。</summary>
    NotWritable,

    /// <summary>可移动磁盘：介质拔掉后仓库就不在了。</summary>
    RemovableVolume,

    /// <summary>网络位置：断网或未登录时仓库整个不可用。</summary>
    NetworkVolume,

    /// <summary>在本程序的安装目录内：卸载或覆盖安装会清空那里。</summary>
    InsideInstallDirectory,

    /// <summary>目标盘可用空间偏少。</summary>
    LowFreeSpace,

    /// <summary>选中的目录里已经有别的东西，落点会退到它下面的专用子目录。</summary>
    DirectoryNotEmpty,
}

/// <summary>一条问题：代码 + 可直接展示的中文说明。</summary>
public sealed record RepositoryLocationIssue(RepositoryLocationIssueCode Code, string Message);

/// <summary>
/// 对候选位置的一次观测。全部 IO（存不存在、空不空、写不写得进去、在哪个卷上）
/// 由主项目做完再填进来。
/// </summary>
/// <param name="SelectedPath">用户在目录选择器里选中的路径，未做任何加工。</param>
/// <param name="DirectoryHasContent">
/// 选中目录里已经有内容。为 true 时落点会退到 <c>&lt;选中目录&gt;\UEModManager\Repository</c>，
/// 见 <see cref="RepositoryLocationValidator.ResolveRepositoryPath"/>。
/// </param>
/// <param name="IsWritable">试写是否成功。</param>
/// <param name="WriteFailureMessage">试写失败的原因，直接来自异常消息；成功时为 <c>null</c>。</param>
/// <param name="VolumeKind">所在卷的类型。</param>
/// <param name="AvailableBytes">所在卷的可用字节数；查不到为 <c>null</c>（不据此报警）。</param>
/// <param name="IsInsideInstallDirectory">是否落在本程序的安装目录里。</param>
public readonly record struct RepositoryLocationProbe(
    string? SelectedPath,
    bool DirectoryHasContent,
    bool IsWritable,
    string? WriteFailureMessage,
    RepositoryVolumeKind VolumeKind,
    long? AvailableBytes,
    bool IsInsideInstallDirectory);

/// <summary>候选位置的判定结果。</summary>
/// <param name="Severity">整体结论。</param>
/// <param name="ResolvedPath">真正会被写进配置的路径；<see cref="Severity"/> 为 Blocked 时无意义。</param>
/// <param name="Issues">发现的全部问题（警告在前、提示在后），可直接逐条显示给用户。</param>
public sealed record RepositoryLocationVerdict(
    RepositoryLocationSeverity Severity,
    string ResolvedPath,
    IReadOnlyList<RepositoryLocationIssue> Issues)
{
    /// <summary>是否允许用这个位置继续。</summary>
    public bool CanUse => Severity != RepositoryLocationSeverity.Blocked;

    /// <summary>是否需要让用户再确认一次（有代价但不禁止）。</summary>
    public bool NeedsConfirmation => Severity == RepositoryLocationSeverity.Warning;
}

/// <summary>
/// 首次运行引导里"用户选的这个位置能不能用"的判定（纯函数，不碰 IO）。
///
/// <para><b>为什么不是简单地把路径写下去就完事</b></para>
/// 引导是<b>我们主动</b>把用户推到一个目录选择器前面的，因此他选出的每一种坏位置都算我们制造的：
/// <list type="number">
/// <item><b>不可写</b> —— <c>UiPreferences.SaveRepositoryRoot</c> 的写失败现在会上抛，
/// 而引导跑在启动早期。不先判一次，一个选错目录的用户换来的是启动崩溃。</item>
/// <item><b>可移动盘 / 网络位置</b> —— U 盘拔掉、网络断开之后仓库整个消失，
/// 而用户此时已经把几十 GB 导进去了。</item>
/// <item><b>安装目录</b> —— 卸载与覆盖安装会清空那里。把用户数据放进安装目录
/// 正是数据搬迁那一整轮要根治的病根，引导不该反手再造一个。</item>
/// <item><b>已经有别的东西的目录</b> —— 这条最隐蔽。仓库回收器
/// （<c>RepositoryReclaimPlanner</c>）的第 5 条判据"目录形态必须与仓库产物一致"
/// 就是专门为"用户把仓库根指到了自己的文件夹"准备的救命判据。它挡得住误删，
/// 但挡不住用户自己的文件夹从此混进包目录、在管理中心里被列成一堆"失联包"。
/// 引导不制造这个场景：目录非空时落点自动退到它下面的
/// <c>UEModManager\Repository</c> 子目录，并把最终落点显示给用户。</item>
/// </list>
/// </summary>
public static class RepositoryLocationValidator
{
    /// <summary>目录非空时自动追加的子目录（相对选中目录）。</summary>
    public const string SubdirectoryName = "UEModManager";

    /// <summary>仓库目录名，与 <see cref="AppDataLayout.RepositoryRoot"/> 默认位置的末段保持一致。</summary>
    public const string RepositoryDirectoryName = "Repository";

    /// <summary>
    /// 低于此值就提示空间偏少。
    ///
    /// <para>
    /// 取 10 GiB：单个大型整合包解压后到几 GB 是常态，而仓库是<b>跨游戏共享</b>的，
    /// 装两三个游戏的 MOD 就能吃掉这个数。这只是提示，不阻止 —— 用户完全可能先选一个小盘
    /// 试用，之后再在设置里改。
    /// </para>
    /// </summary>
    public const long RecommendedFreeBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// 算出真正会被写进配置的路径。
    ///
    /// <para>
    /// 选中目录为空（或还不存在）时直接用它 —— 用户显然是特意建来放仓库的；
    /// 已经有内容时退到 <c>&lt;选中目录&gt;\UEModManager\Repository</c>，
    /// 免得把用户自己的文件夹变成仓库根（理由见类注释第 4 条）。
    /// 选盘符根目录（<c>D:\</c>）几乎必然走后一条，这正是想要的结果。
    /// </para>
    /// </summary>
    public static string ResolveRepositoryPath(string selectedPath, bool directoryHasContent)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            throw new ArgumentException("选中路径不能为空", nameof(selectedPath));

        var trimmed = selectedPath.Trim();
        return directoryHasContent
            ? Path.Combine(trimmed, SubdirectoryName, RepositoryDirectoryName)
            : trimmed;
    }

    /// <summary>判定。</summary>
    public static RepositoryLocationVerdict Validate(RepositoryLocationProbe probe)
    {
        if (!IsPathAcceptable(probe.SelectedPath, out var blocking, out var trimmedPath))
        {
            return new RepositoryLocationVerdict(
                RepositoryLocationSeverity.Blocked, string.Empty, One(blocking!));
        }

        var resolved = ResolveRepositoryPath(trimmedPath, probe.DirectoryHasContent);

        if (!probe.IsWritable)
        {
            // 试写失败一律拦下：写不进去的位置写进配置，下一次读仓库就是空的，
            // 而用户以为自己设置成功了。原因带上，"权限不足"和"磁盘只读"要让他分得清。
            var detail = string.IsNullOrWhiteSpace(probe.WriteFailureMessage)
                ? string.Empty
                : $"（{probe.WriteFailureMessage}）";
            return new RepositoryLocationVerdict(
                RepositoryLocationSeverity.Blocked, resolved,
                One(new RepositoryLocationIssue(RepositoryLocationIssueCode.NotWritable,
                    $"这个位置写不进去{detail}，请换一个，或换成有写入权限的文件夹。")));
        }

        var issues = new List<RepositoryLocationIssue>();

        // ── 警告级：能用，但用户必须知道代价 ──

        if (probe.IsInsideInstallDirectory)
        {
            issues.Add(new RepositoryLocationIssue(RepositoryLocationIssueCode.InsideInstallDirectory,
                "这是本程序自己的安装文件夹。卸载或覆盖安装时这里可能被整个清空，"
                + "你导入的 MOD 会一起消失。建议换到程序之外的位置。"));
        }

        switch (probe.VolumeKind)
        {
            case RepositoryVolumeKind.Removable:
                issues.Add(new RepositoryLocationIssue(RepositoryLocationIssueCode.RemovableVolume,
                    "这是一个可移动磁盘。设备拔掉之后，已导入的 MOD 会全部读不到，"
                    + "游戏也没法启用它们。"));
                break;
            case RepositoryVolumeKind.Network:
                issues.Add(new RepositoryLocationIssue(RepositoryLocationIssueCode.NetworkVolume,
                    "这是一个网络位置。断网或没连上共享时 MOD 会全部读不到，"
                    + "导入和部署的速度也会明显变慢。"));
                break;
        }

        // 查不到可用空间（网络位置、卷未就绪）时不报警：判据本身不成立，
        // 拿"查不到"当"不够"会给一整类用户凭空加一条假警告。
        if (probe.AvailableBytes is { } available && available < RecommendedFreeBytes)
        {
            issues.Add(new RepositoryLocationIssue(RepositoryLocationIssueCode.LowFreeSpace,
                $"这个位置只剩 {DiskSpacePrecheck.Humanize(available)} 可用。"
                + $"MOD 包比较占地方，建议挑一个至少有 "
                + $"{DiskSpacePrecheck.Humanize(RecommendedFreeBytes)} 空闲的盘。"));
        }

        // ── 提示级：不影响结论，但落点变了必须让用户看见 ──

        if (probe.DirectoryHasContent)
        {
            issues.Add(new RepositoryLocationIssue(RepositoryLocationIssueCode.DirectoryNotEmpty,
                $"这个文件夹非空，MOD 会单独存到 {SubdirectoryName}\\{RepositoryDirectoryName} 子目录，不与现有文件混放。"));
        }

        var severity = HasWarning(issues)
            ? RepositoryLocationSeverity.Warning
            : RepositoryLocationSeverity.Ok;

        return new RepositoryLocationVerdict(severity, resolved,
            new ReadOnlyCollection<RepositoryLocationIssue>(issues));
    }

    /// <summary>
    /// 路径本身是否成立。<b>调用方必须在任何 IO 之前先过这一关</b>：
    /// 拿一个空的/相对的/含非法字符的路径去建目录、试写、查卷，得到的失败原因会指向错误的方向
    /// （用户看到"访问被拒绝"，而真正的问题是他给了一个相对路径）。
    /// 公开出来就是为了让主项目的 IO 适配层复用同一份判据，而不是各写一份。
    /// </summary>
    /// <param name="selectedPath">用户选中的原始路径。</param>
    /// <param name="issue">不成立时给出可直接展示的原因；成立时为 <c>null</c>。</param>
    /// <param name="normalizedPath">剪掉首尾空白后的路径；不成立时为空串。</param>
    public static bool IsPathAcceptable(
        string? selectedPath, out RepositoryLocationIssue? issue, out string normalizedPath)
    {
        normalizedPath = selectedPath?.Trim() ?? string.Empty;

        if (normalizedPath.Length == 0)
        {
            issue = new RepositoryLocationIssue(RepositoryLocationIssueCode.Empty,
                "还没有选择文件夹。");
            normalizedPath = string.Empty;
            return false;
        }

        // .NET 里 Path 的解析方法对含 '\0' 的路径不再抛异常，而是一路传到系统调用才失败，
        // 于是"路径不合法"会被推迟成后面那次试写的 IO 错误，报给用户的原因也就跟着错了。
        // GetInvalidPathChars 在 .NET Core 上正好只有 '\0' 这一个字符，判据与它保持同步。
        if (normalizedPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            issue = new RepositoryLocationIssue(RepositoryLocationIssueCode.Malformed,
                "这个路径里有不能用于文件夹名的字符，请重新选择。");
            normalizedPath = string.Empty;
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(normalizedPath))
            {
                // 相对路径会被解析成"相对当前工作目录"，而 WPF 进程的工作目录不受控
                // （从快捷方式启动、拖拽文件启动、开机自启各不相同），仓库位置会跟着漂。
                issue = new RepositoryLocationIssue(RepositoryLocationIssueCode.NotAbsolute,
                    "请选择一个完整的文件夹路径，例如 D:\\Games\\Mods。");
                normalizedPath = string.Empty;
                return false;
            }
        }
        catch (ArgumentException)
        {
            // 留一层兜底：这个判断是 .NET 版本相关的，将来它若重新开始抛，
            // 结论仍是"路径本身不成立"而不是一个没人接得住的异常
            issue = new RepositoryLocationIssue(RepositoryLocationIssueCode.Malformed,
                "这个路径里有不能用于文件夹名的字符，请重新选择。");
            normalizedPath = string.Empty;
            return false;
        }

        issue = null;
        return true;
    }

    private static bool HasWarning(List<RepositoryLocationIssue> issues)
    {
        foreach (var issue in issues)
        {
            if (issue.Code != RepositoryLocationIssueCode.DirectoryNotEmpty) return true;
        }
        return false;
    }

    private static IReadOnlyList<RepositoryLocationIssue> One(RepositoryLocationIssue issue)
        => new ReadOnlyCollection<RepositoryLocationIssue>(new[] { issue });
}
