using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEModManager.Services.Paths;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// 一条准备好、可以直接放进消息框的告知。
    /// </summary>
    /// <param name="Title">标题。</param>
    /// <param name="Message">正文。</param>
    /// <param name="FixTargetVolumeRoot">
    /// 一键搬过去的落盘（游戏所在的盘根，例如 <c>D:\</c>）；这次没有可一键修复的办法时为 <c>null</c>。
    /// 非 <c>null</c> 时界面上要给一个动作按钮，而不是只有"知道了"。
    /// </param>
    /// <param name="FixButtonText">动作按钮上的字；<see cref="FixTargetVolumeRoot"/> 为 <c>null</c> 时无意义。</param>
    public sealed record DeploymentDegradationNoticeContent(
        string Title,
        string Message,
        string? FixTargetVolumeRoot = null,
        string? FixButtonText = null)
    {
        /// <summary>这条告知能不能给出一键解决。</summary>
        public bool CanFixInPlace => !string.IsNullOrWhiteSpace(FixTargetVolumeRoot);
    }

    /// <summary>
    /// 一次"这回值不值得跟用户说"的判定结果。
    /// </summary>
    /// <param name="ShouldNotify">要不要弹。</param>
    /// <param name="Signature">
    /// 本次情形的签名。决定弹了之后，调用方把它存起来；下次同样的签名就不再打扰。
    /// </param>
    /// <param name="Reason">判定理由，进日志用——"为什么这次没提示"必须能查。</param>
    public readonly record struct DeploymentDegradationNoticeDecision(
        bool ShouldNotify,
        string Signature,
        string Reason);

    /// <summary>
    /// 部署降级要不要告知用户、以及告知什么（纯函数，不碰 IO）。
    ///
    /// <para><b>要解决的问题</b></para>
    /// 用户在设置里选了"硬链接"，界面显示已选中，部署也成功——但仓库默认在系统盘、
    /// 游戏通常装在别的盘，于是每个文件其实都在实打实复制。空间一点没省，
    /// 而界面上一个字都看不到，只有日志里一行 <c>LogWarning</c>。
    /// 功能没坏，坏的是"用户以为它在做的事"和"它实际在做的事"对不上。
    ///
    /// <para><b>什么时候值得说一次</b></para>
    /// 这是提示不是错误，弹多了比不弹更糟（用户会开始无视所有弹窗）。三条约束：
    /// <list type="number">
    /// <item><b>按原因聚合，不按文件。</b>一次部署上万个文件都是同一个原因，
    /// 聚合在 <see cref="DeploymentDegradationCollector"/> 里完成，本类只接聚合后的结果。</item>
    /// <item><b>同一种情形只说一次。</b>判据是<see cref="BuildSignature">签名</see>
    /// ——"降级原因 + 哪两个盘"。存签名而不是存一个"已提示过"的布尔，是因为情况真的会变：
    /// 用户把仓库搬到别的盘、或者换一个装在别的盘的游戏之后，结论跟着变，
    /// 那时值得再说一次；而同一组合下重复弹只是骚扰。</item>
    /// <item><b>部署失败时不说。</b>由调用方保证（只在事务提交成功后询问本类）：
    /// 在一个"部署失败已回滚"的错误框后面再补一句"顺便说这次没用上硬链接"，
    /// 只会让用户以为两件事有因果关系。</item>
    /// </list>
    ///
    /// <para><b>文案写给普通玩家</b></para>
    /// 不出现 Win32 错误码，不出现"卷"——玩家的词是"盘"。每一句都必须回答三件事之一：
    /// 发生了什么、有什么影响、怎么才能用上。
    /// </summary>
    public static class DeploymentDegradationNotice
    {
        /// <summary>
        /// 本次情形的签名："原因 + 源盘 + 目标盘"，多条原因按稳定顺序拼接。
        /// 盘根算不出来时用空串占位——签名只要求"同样的情形得出同样的串"，不要求可读。
        /// </summary>
        public static string BuildSignature(IReadOnlyList<DeploymentDegradationSummary>? summaries)
        {
            if (summaries is null || summaries.Count == 0) return string.Empty;

            return string.Join(";", summaries
                .Select(s => $"{(int)s.Kind}|{VolumePaths.TryGetVolumeRoot(s.SourcePath)}"
                    + $">{VolumePaths.TryGetVolumeRoot(s.TargetPath)}")
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>判定。</summary>
        /// <param name="summaries">本次部署聚合后的降级；空表示没降级。</param>
        /// <param name="lastNotifiedSignature">上次已经告知过的签名（存在用户偏好里）；从没告知过为 <c>null</c>。</param>
        public static DeploymentDegradationNoticeDecision Decide(
            IReadOnlyList<DeploymentDegradationSummary>? summaries, string? lastNotifiedSignature)
        {
            if (summaries is null || summaries.Count == 0)
                return new DeploymentDegradationNoticeDecision(false, string.Empty, "本次部署没有发生降级");

            var signature = BuildSignature(summaries);

            if (!string.IsNullOrEmpty(lastNotifiedSignature)
                && string.Equals(signature, lastNotifiedSignature, StringComparison.OrdinalIgnoreCase))
            {
                return new DeploymentDegradationNoticeDecision(false, signature,
                    "同样的情形已经告知过一次，不再重复打扰");
            }

            return new DeploymentDegradationNoticeDecision(true, signature,
                "这套盘的组合还没告知过，值得说一次");
        }

        /// <summary>
        /// 生成告知内容。多条原因时以"受影响文件最多"的那条为主句，其余各补一句，
        /// 免得把一个提示写成一篇说明书。
        /// </summary>
        public static DeploymentDegradationNoticeContent BuildContent(
            IReadOnlyList<DeploymentDegradationSummary> summaries, bool english = false)
        {
            if (summaries is null) throw new ArgumentNullException(nameof(summaries));
            if (summaries.Count == 0) throw new ArgumentException("没有降级就不该生成告知", nameof(summaries));

            var primary = summaries
                .OrderByDescending(s => s.FileCount)
                .ThenBy(s => (int)s.Kind)
                .First();

            var fixTarget = TryGetFixTargetVolumeRoot(primary);
            var body = new StringBuilder(english ? DescribePrimaryEnglish(primary, fixTarget != null) : DescribePrimary(primary, fixTarget != null));

            foreach (var other in summaries.Where(s => !ReferenceEquals(s, primary)))
            {
                body.Append(Environment.NewLine).Append(Environment.NewLine)
                    .Append(english ? $"Another {other.FileCount} files also used copies because the selected deployment method was unavailable." : DescribeSecondary(other));
            }

            body.Append(Environment.NewLine).Append(Environment.NewLine)
                .Append(english ? "This notice will not appear again for the same situation, unless you change storage or select a game on another drive."
                    : "同样的情况以后不会再提示，除非你换了存放位置或换了别的盘上的游戏。");

            return new DeploymentDegradationNoticeContent(
                english ? "Deployment used file copies" : TitleFor(primary.Kind), body.ToString(),
                fixTarget,
                fixTarget == null ? null : BuildFixButtonText(fixTarget, english));
        }

        /// <summary>
        /// 这次能不能靠"把 MOD 搬到游戏所在的盘"解决；不能则返回 <c>null</c>。
        ///
        /// <para>
        /// <b>只有跨盘这一种能一键修</b>，这个收窄是刻意的：
        /// <list type="bullet">
        /// <item><see cref="DeploymentDegradationKind.HardLinkUnsupported"/> ——
        /// 两个位置已经在同一个盘上了，是那个盘的<b>格式</b>不支持硬链接（exFAT/FAT32）。
        /// 把仓库搬到同一个盘上不会有任何改变，用户等上十几分钟换来一模一样的提示，
        /// 那比不给按钮糟得多。</item>
        /// <item><see cref="DeploymentDegradationKind.BackendUnavailable"/> ——
        /// 跟盘根本无关，搬到哪都一样。</item>
        /// </list>
        /// 算不出游戏所在的盘时同样返回 <c>null</c>：指错盘会让用户照着搬一遍几十 GB，
        /// 然后发现什么都没变。宁可退回只有"知道了"的提示。
        /// </para>
        /// </summary>
        public static string? TryGetFixTargetVolumeRoot(DeploymentDegradationSummary summary)
        {
            if (summary is null) throw new ArgumentNullException(nameof(summary));
            if (summary.Kind != DeploymentDegradationKind.HardLinkCrossVolume) return null;

            var repoRoot = VolumePaths.TryGetVolumeRoot(summary.SourcePath);
            var gameRoot = VolumePaths.TryGetVolumeRoot(summary.TargetPath);

            // 两个盘都得说得清，而且确实不是同一个——判据说不清时不给按钮
            if (repoRoot == null || gameRoot == null) return null;
            return string.Equals(repoRoot, gameRoot, StringComparison.OrdinalIgnoreCase)
                ? null
                : gameRoot;
        }

        /// <summary>
        /// 动作按钮上的字。<b>面向玩家，不堆盘符术语</b>：说的是"要发生什么"
        /// （帮你搬过去），而不是"要执行什么操作"（迁移包仓库根目录）。
        /// 带上盘名是因为它是这句话里唯一的具体信息，去掉之后用户不知道要搬去哪。
        /// </summary>
        public static string BuildFixButtonText(string fixTargetVolumeRoot, bool english = false)
        {
            if (english) return $"Move to {VolumePaths.TryGetVolumeRoot(fixTargetVolumeRoot)?.TrimEnd('\\', '/') ?? "the game drive"}";
            var where = VolumePaths.TryDescribeVolume(fixTargetVolumeRoot);
            return where == null ? "帮我搬到游戏所在的盘" : $"帮我搬到 {where}";
        }

        private static string TitleFor(DeploymentDegradationKind kind) => kind switch
        {
            DeploymentDegradationKind.HardLinkCrossVolume => "这次没能用上硬链接",
            DeploymentDegradationKind.HardLinkUnsupported => "这次没能用上硬链接",
            _ => "部署方式已自动改为复制",
        };

        private static string DescribePrimaryEnglish(DeploymentDegradationSummary summary, bool canFixInPlace)
        {
            var reason = summary.Kind switch
            {
                DeploymentDegradationKind.HardLinkCrossVolume => "Hard links require the mod repository and game to be on the same drive. They are currently on different drives.",
                DeploymentDegradationKind.HardLinkUnsupported => "Hard links are unavailable between these locations. The drive format may not support them, as with exFAT or FAT32.",
                _ => "The selected deployment method was unavailable."
            };
            var remedy = summary.Kind switch
            {
                DeploymentDegradationKind.HardLinkCrossVolume when canFixInPlace => "Use the button below to move your existing mods to the game drive. The app will move the files and switch storage for you.",
                DeploymentDegradationKind.HardLinkCrossVolume => "To save space, move mod storage to the game drive in Settings > Mod storage. The app will move the data with it.",
                DeploymentDegradationKind.HardLinkUnsupported => "To use hard links, keep the game and mod repository on the same NTFS drive.",
                _ => string.Empty
            };
            return reason + $"\n\n{summary.FileCount} files were copied instead. Your mods are ready to play, but these copies use additional disk space."
                + (remedy.Length > 0 ? "\n\n" + remedy : string.Empty);
        }

        private static string DescribePrimary(DeploymentDegradationSummary summary, bool canFixInPlace)
        {
            var files = $"{summary.FileCount} 个文件";

            switch (summary.Kind)
            {
                case DeploymentDegradationKind.HardLinkCrossVolume:
                {
                    // 两个盘都说得清才点名，否则退回不带盘符的说法：
                    // 指错盘会让用户照着搬一遍仓库，然后发现什么都没变。
                    var repo = VolumePaths.TryDescribeVolume(summary.SourcePath);
                    var game = VolumePaths.TryDescribeVolume(summary.TargetPath);
                    var where = repo != null && game != null
                        ? $"你的 MOD 存放在 {repo}，游戏装在 {game}"
                        : "你的 MOD 存放位置和游戏不在同一个盘";

                    // 能一键修时，最后一段说的是"点下面那个按钮"，而不是"你自己去设置里改"。
                    // 后者对相当一部分玩家等于没说——他们会关掉弹窗然后放弃；
                    // 更糟的是照做之后会撞上"改位置只改指针不搬数据"，MOD 当场从界面上消失。
                    var howTo = canFixInPlace
                        ? "想真正省下这份空间：点下面的按钮，程序会把已经导入的 MOD 整个搬过去，"
                            + "搬完自动切换，你不用手动拷任何文件。"
                        : $"想真正省下这份空间：到「设置 → 部署与仓库」把 MOD 存放位置改到"
                            + $"{(game ?? "游戏所在的那个盘")}，程序会连数据一起搬过去。";

                    return $"你在设置里选的是「硬链接」，但{where}。"
                        + $"硬链接只能在同一个盘里建立，所以这次的 {files} 是实打实复制过去的。"
                        + Environment.NewLine + Environment.NewLine
                        + "MOD 已经装好了，可以直接玩，只是这些文件在硬盘上多存了一份。"
                        + Environment.NewLine + Environment.NewLine
                        + howTo;
                }

                case DeploymentDegradationKind.HardLinkUnsupported:
                {
                    return "你在设置里选的是「硬链接」，但这两个位置之间建不了硬链接"
                        + "（通常是因为盘的格式不支持，比如 exFAT、FAT32 格式的移动硬盘）。"
                        + $"所以这次的 {files} 是实打实复制过去的。"
                        + Environment.NewLine + Environment.NewLine
                        + "MOD 已经装好了，可以直接玩，只是这些文件在硬盘上多存了一份。"
                        + Environment.NewLine + Environment.NewLine
                        + "想真正省下这份空间：把 MOD 存放位置换到和游戏同一个盘上，"
                        + "并且那个盘要是 NTFS 格式（Windows 自带的硬盘默认就是）。";
                }

                default:
                {
                    return "你在设置里选的部署方式这次用不上，已经改用复制完成。"
                        + $"这次的 {files} 是实打实复制过去的。"
                        + Environment.NewLine + Environment.NewLine
                        + "MOD 已经装好了，可以直接玩，只是这些文件在硬盘上多存了一份。";
                }
            }
        }

        private static string DescribeSecondary(DeploymentDegradationSummary summary) => summary.Kind switch
        {
            DeploymentDegradationKind.HardLinkCrossVolume =>
                $"另有 {summary.FileCount} 个文件因为不在同一个盘，也改成了复制。",
            DeploymentDegradationKind.HardLinkUnsupported =>
                $"另有 {summary.FileCount} 个文件所在的盘不支持硬链接，也改成了复制。",
            _ =>
                $"另有 {summary.FileCount} 个文件因为部署方式不可用，也改成了复制。",
        };
    }
}
