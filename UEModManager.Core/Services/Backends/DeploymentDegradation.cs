using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// 一次部署里"没能按用户选的方式做"的原因。
    ///
    /// <para>
    /// 每一项都对应一句不同的解法：跨盘要搬仓库，格式不支持要换盘，后端不可用则跟盘无关。
    /// 合并成一个笼统的"降级了"会让告知文案退化成"出了点问题"，那和现在只写一条
    /// <c>LogWarning</c> 没有本质区别。
    /// </para>
    /// </summary>
    public enum DeploymentDegradationKind
    {
        /// <summary>仓库与游戏不在同一个盘，硬链接建不了，改为复制。默认配置下的常态。</summary>
        HardLinkCrossVolume,

        /// <summary>在同一个盘上但文件系统不支持硬链接（exFAT/FAT32 的移动硬盘最常见），改为复制。</summary>
        HardLinkUnsupported,

        /// <summary>用户选的后端在本机不可用或没注册，整批改用复制。</summary>
        BackendUnavailable,
    }

    /// <summary>
    /// 后端上报的一条降级记录，粒度是<b>单个文件</b>。
    /// </summary>
    /// <param name="Kind">原因。</param>
    /// <param name="SourcePath">仓库里的源文件路径（<see cref="DeploymentDegradationKind.BackendUnavailable"/> 时为空）。</param>
    /// <param name="TargetPath">游戏目录里的目标路径（同上）。</param>
    /// <param name="Detail">
    /// 诊断用的技术细节，例如 Win32 错误码。<b>只进日志，绝不进用户看的句子</b>——
    /// 面向普通玩家的告知里出现"Win32Error=1"只会让他更慌，且什么也解决不了。
    /// </param>
    public sealed record DeploymentDegradation(
        DeploymentDegradationKind Kind,
        string SourcePath,
        string TargetPath,
        string Detail);

    /// <summary>
    /// 同一原因的多条降级聚合成的一条。<see cref="FileCount"/> 是这次部署里因此受影响的文件数，
    /// 路径取该原因下的第一条作为样本（同一原因的降级是整批同因的，一条样本足以说明是哪两个盘）。
    /// </summary>
    public sealed record DeploymentDegradationSummary(
        DeploymentDegradationKind Kind,
        int FileCount,
        string SourcePath,
        string TargetPath,
        string Detail);

    /// <summary>
    /// 部署后端<b>可选</b>实现的能力接口：把"这次没按你选的方式做"上报出去。
    ///
    /// <para><b>为什么是独立的可选接口，而不是改 <see cref="IDeploymentBackend"/></b></para>
    /// <see cref="IDeploymentBackend"/> 是对外的扩展点，<c>samples/UEModManager.SampleBackend</c>
    /// 是照着它写的外部实现范例，文档里还教人"复制本项目到自己的 fork"。给
    /// <c>DeployFileAsync</c> 换返回类型会让所有已存在的第三方实现当场编译不过，
    /// 而绝大多数后端（复制、镜像）根本没有降级这回事。做成可选能力后：
    /// 不需要的后端一行不改，需要的后端多实现一个接口，
    /// <c>DeploymentService</c> 用 <c>is</c> 探测——扩展点保持原样，示例工程不受影响。
    ///
    /// <para><b>线程</b></para>
    /// 事件在<b>部署线程</b>上触发（后端的 <c>DeployFileAsync</c> 普遍跑在 <c>Task.Run</c> 里），
    /// 订阅方自己负责线程安全与 UI 调度。<see cref="DeploymentDegradationCollector"/> 就是为此提供的。
    /// </summary>
    public interface IDeploymentDegradationReporter
    {
        /// <summary>发生一次降级（单个文件）。</summary>
        event Action<DeploymentDegradation>? Degraded;
    }

    /// <summary>
    /// 降级记录的收集与聚合。
    ///
    /// <para>
    /// <b>聚合是这套告知的核心，不是顺手做的优化。</b>一次部署可能有上万个文件，
    /// 而跨盘降级是整批同因的：每个文件报一次，用户就会看到上万条一模一样的提示。
    /// 收进来的是"每文件一条"，交出去的是"每原因一条 + 文件数"。
    /// </para>
    ///
    /// <para>
    /// 拆到 Core 而不是揉在 <c>DeploymentService</c> 里，是因为这段是纯逻辑，
    /// 而构造 <c>DeploymentService</c> 要拖出 <c>OverwriteStore</c> → <c>PackageRepository</c>
    /// 一整条依赖链，"上万条同因记录只说一次"这条规则不该被那串依赖挡在测试之外。
    /// </para>
    /// </summary>
    public sealed class DeploymentDegradationCollector
    {
        private readonly object _gate = new();
        private readonly List<DeploymentDegradation> _items = new();

        /// <summary>记一条。可从任意线程调用。</summary>
        public void Add(DeploymentDegradation degradation)
        {
            if (degradation is null) throw new ArgumentNullException(nameof(degradation));

            lock (_gate) _items.Add(degradation);
        }

        /// <summary>这次一共记了多少条（每文件一条，未聚合）。</summary>
        public int Count
        {
            get { lock (_gate) return _items.Count; }
        }

        /// <summary>
        /// 按原因聚合。顺序取"第一次出现该原因"的顺序，不重排——
        /// 用户看到的第一句话应当对应最先发生的那件事。
        /// </summary>
        public IReadOnlyList<DeploymentDegradationSummary> Summarize()
        {
            lock (_gate) return Summarize(_items);
        }

        /// <inheritdoc cref="Summarize()"/>
        public static IReadOnlyList<DeploymentDegradationSummary> Summarize(
            IEnumerable<DeploymentDegradation>? degradations)
        {
            if (degradations is null)
                return Array.Empty<DeploymentDegradationSummary>();

            var summaries = degradations
                .Where(d => d is not null)
                .GroupBy(d => d.Kind)
                .Select(g =>
                {
                    var first = g.First();
                    return new DeploymentDegradationSummary(
                        g.Key, g.Count(), first.SourcePath, first.TargetPath, first.Detail);
                })
                .ToList();

            return new ReadOnlyCollection<DeploymentDegradationSummary>(summaries);
        }
    }
}
