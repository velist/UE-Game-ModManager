using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEModManager.Services.Paths;

namespace UEModManager.Services.Backends
{
    /// <summary>一条准备好、可以直接放进消息框的告知。</summary>
    public sealed record DeploymentDegradationNoticeContent(string Title, string Message);

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
            IReadOnlyList<DeploymentDegradationSummary> summaries)
        {
            if (summaries is null) throw new ArgumentNullException(nameof(summaries));
            if (summaries.Count == 0) throw new ArgumentException("没有降级就不该生成告知", nameof(summaries));

            var primary = summaries
                .OrderByDescending(s => s.FileCount)
                .ThenBy(s => (int)s.Kind)
                .First();

            var body = new StringBuilder(DescribePrimary(primary));

            foreach (var other in summaries.Where(s => !ReferenceEquals(s, primary)))
            {
                body.Append(Environment.NewLine).Append(Environment.NewLine)
                    .Append(DescribeSecondary(other));
            }

            body.Append(Environment.NewLine).Append(Environment.NewLine)
                .Append("同样的情况以后不会再提示，除非你换了存放位置或换了别的盘上的游戏。");

            return new DeploymentDegradationNoticeContent(TitleFor(primary.Kind), body.ToString());
        }

        private static string TitleFor(DeploymentDegradationKind kind) => kind switch
        {
            DeploymentDegradationKind.HardLinkCrossVolume => "这次没能用上硬链接",
            DeploymentDegradationKind.HardLinkUnsupported => "这次没能用上硬链接",
            _ => "部署方式已自动改为复制",
        };

        private static string DescribePrimary(DeploymentDegradationSummary summary)
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

                    return $"你在设置里选的是「硬链接」，但{where}。"
                        + $"硬链接只能在同一个盘里建立，所以这次的 {files} 是实打实复制过去的。"
                        + Environment.NewLine + Environment.NewLine
                        + "MOD 已经装好了，可以直接玩，只是这些文件在硬盘上多存了一份。"
                        + Environment.NewLine + Environment.NewLine
                        + $"想真正省下这份空间：到「设置 → 部署与仓库」把包仓库目录改到"
                        + $"{(game != null ? game : "游戏所在的那个盘")}，再重新部署一次。";
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
