using UEModManager.Services.Paths;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// <c>CreateHardLink</c> 失败之后"这算不算可以降级为复制、以及降级的真实原因是什么"的判定
    /// （纯函数，不碰 IO）。
    ///
    /// <para><b>为什么不直接看错误码</b></para>
    /// 直觉上 <c>ERROR_NOT_SAME_DEVICE</c>(17) 就代表跨盘，但真机日志里跨盘拿到的是
    /// <c>ERROR_INVALID_FUNCTION</c>(1)：包仓库默认在 <c>%LOCALAPPDATA%</c>（C 盘）、
    /// 游戏装在 D 盘，Windows 给的是 1 而不是 17。错误码到底给哪一个取决于目标卷的驱动，
    /// 不可靠。而"这两个路径在不在同一个盘"我们自己就能算准
    /// （<see cref="VolumePaths.AreSameVolume"/>），于是判据换成它：
    /// <list type="bullet">
    /// <item>不同盘 → <see cref="DeploymentDegradationKind.HardLinkCrossVolume"/>，
    /// 解法是把仓库搬到游戏所在的盘；</item>
    /// <item>同一个盘还失败 → <see cref="DeploymentDegradationKind.HardLinkUnsupported"/>，
    /// 那就是这个盘的文件系统不支持硬链接（exFAT/FAT32 的移动硬盘），搬仓库解决不了。</item>
    /// </list>
    /// 两句解法完全不同，给错了比不给更糟。
    ///
    /// <para><b>只有这两个错误码算"可降级"</b></para>
    /// 其余失败（目标被占用、权限不足、路径太长、链接数超上限）都是真的出了问题，
    /// 必须让 <c>DeployFileAsync</c> 抛出去走事务回滚——悄悄复制一份会把一次真实故障
    /// 伪装成部署成功。
    /// </summary>
    public static class HardLinkFailureClassifier
    {
        /// <summary>
        /// <c>ERROR_INVALID_FUNCTION</c>。文件系统不认这个操作。跨盘与不支持硬链接的盘都可能给它。
        /// </summary>
        public const int ErrorInvalidFunction = 1;

        /// <summary>
        /// <c>ERROR_NOT_SAME_DEVICE</c>。源和目标不在同一个卷上。
        /// <para>
        /// 此前代码里的注释把 17 写成"已存在"——那是 <c>ERROR_ALREADY_EXISTS</c>(183)，
        /// 完全是另一回事。注释错了不影响运行，但它正是"降级只写一条日志"这件事
        /// 一直没人觉得可疑的原因之一。
        /// </para>
        /// </summary>
        public const int ErrorNotSameDevice = 17;

        /// <summary>
        /// 判定。返回 <c>null</c> 表示这不是可降级的失败，调用方必须上抛。
        /// </summary>
        /// <param name="win32Error"><c>Marshal.GetLastWin32Error()</c> 的值。</param>
        /// <param name="sourcePath">仓库里的源文件绝对路径。</param>
        /// <param name="targetPath">游戏目录里的目标绝对路径。</param>
        public static DeploymentDegradationKind? Classify(
            int win32Error, string? sourcePath, string? targetPath)
        {
            if (win32Error is not (ErrorInvalidFunction or ErrorNotSameDevice))
                return null;

            // 盘根算不出来时不敢报"跨盘"：那条结论配的解法是"把仓库搬到游戏所在的盘"，
            // 而我们连它在哪个盘都说不清。回落到不针对具体盘的那一档。
            if (VolumePaths.TryGetVolumeRoot(sourcePath) == null
                || VolumePaths.TryGetVolumeRoot(targetPath) == null)
            {
                return DeploymentDegradationKind.HardLinkUnsupported;
            }

            return VolumePaths.AreSameVolume(sourcePath, targetPath)
                ? DeploymentDegradationKind.HardLinkUnsupported
                : DeploymentDegradationKind.HardLinkCrossVolume;
        }
    }
}
