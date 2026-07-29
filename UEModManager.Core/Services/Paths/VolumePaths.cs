using System;
using System.IO;

namespace UEModManager.Services.Paths;

/// <summary>
/// "两个路径在不在同一个盘上"的判定（纯函数，不碰 IO）。
///
/// <para><b>为什么值得单独一处</b></para>
/// 同一条判据被两个功能消费，而它们算错的后果都不小：
/// <list type="bullet">
/// <item><b>部署侧</b> —— 硬链接只能在同一个盘内建立。跨盘时 Windows 直接拒绝，
/// 后端降级为复制，用户选的"省空间"一点没发生。判定用来把降级的真实原因分开：
/// "两个盘不一样"和"这个盘的格式不支持硬链接"给用户的解法完全不同，
/// 只看 Win32 错误码分不出来（见 <see cref="Backends.HardLinkFailureClassifier"/>）。</item>
/// <item><b>引导侧</b> —— 首次运行选仓库位置时标出哪些盘和游戏在同一个盘，
/// 让用户在选之前就知道选哪个才能省空间。</item>
/// </list>
///
/// <para><b>只比盘符/共享根，不做任何 IO</b></para>
/// 挂载点与目录联接（junction）会让"同盘符"与"同卷"不完全等价，真正权威的判据是
/// <c>GetVolumePathName</c>，但那是 IO 而且要 P/Invoke。两个消费方都容得下这点误差：
/// 部署侧最终以 <c>CreateHardLink</c> 的真实结果为准，本判定只用来解释<b>已经发生</b>的失败；
/// 引导侧只是一条提示，标错了也不会让用户丢数据。
/// </summary>
public static class VolumePaths
{
    /// <summary>
    /// 取路径所在的盘根（<c>D:\</c>）或网络共享根（<c>\\server\share\</c>）。
    ///
    /// <para>
    /// <b>相对路径一律返回 <c>null</c></b>，不按当前工作目录展开：WPF 进程的工作目录不受控
    /// （快捷方式启动、拖拽启动、开机自启各不相同），拿它兜底会让"同不同盘"这个结论
    /// 随启动方式漂移，与 <see cref="RepositoryLocationValidator.IsPathAcceptable"/> 拒绝
    /// 相对路径是同一条理由。
    /// </para>
    /// </summary>
    public static string? TryGetVolumeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var trimmed = path.Trim();
            if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            if (!Path.IsPathFullyQualified(trimmed)) return null;

            var root = Path.GetPathRoot(Path.GetFullPath(trimmed));
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch
        {
            // 路径形态本身病态（超长、格式解析不了）时结论就是"说不清"，
            // 而不是把一个启动早期的提示变成异常。
            return null;
        }
    }

    /// <summary>
    /// 两个路径是不是在同一个盘上。
    /// <b>任一侧取不到盘根就返回 <c>false</c></b>——证明不了就不说同盘，
    /// 两个消费方误报"同盘"的代价都比误报"不同盘"大（前者会让提示彻底说反）。
    /// </summary>
    public static bool AreSameVolume(string? left, string? right)
    {
        var leftRoot = TryGetVolumeRoot(left);
        var rightRoot = TryGetVolumeRoot(right);

        return leftRoot != null && rightRoot != null
            && string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把盘根说成普通玩家看得懂的话：<c>D:\Games</c> → <c>D 盘</c>。
    /// 网络位置没有盘符，原样返回共享根。说不清时返回 <c>null</c>，由调用方决定怎么绕开
    /// ——绝不能编一个"未知盘"塞进用户看的句子里。
    /// </summary>
    public static string? TryDescribeVolume(string? path)
    {
        var root = TryGetVolumeRoot(path);
        if (root == null) return null;

        // "D:\" / "d:/" → "D 盘"；UNC 根保持原样，只去掉末尾分隔符
        if (root.Length >= 2 && root[1] == ':')
            return $"{char.ToUpperInvariant(root[0])} 盘";

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
