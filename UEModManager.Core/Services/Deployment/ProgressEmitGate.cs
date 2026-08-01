using System;

namespace UEModManager.Services.Deployment
{
    /// <summary>
    /// 部署进度事件的发送闸门。
    ///
    /// <para>
    /// 起因：<c>DeploymentService</c> 每完成一个操作就 <c>ProgressChanged?.Invoke</c> 一次，
    /// 而订阅者 <c>DeployPreviewDialog.OnDeployProgress</c> 里是 <c>Dispatcher.Invoke</c>
    /// —— 同步阻塞地marshal到 UI 线程。上万个文件的整合包部署会因此产生上万次
    /// 跨线程同步调用，UI 线程被进度更新淹没，反而比不显示进度还卡。
    /// </para>
    ///
    /// <para>
    /// 判据是"时间 <b>或</b> 进度"任一达标就发，而不是两者都达标：
    /// 单文件很大时（拷贝一个 5GB 的 pak）靠时间保证界面不像卡死；
    /// 文件很多但很小时靠 1% 步长保证进度条平滑。两者取或，
    /// 发送次数仍从 N 降到约 200 以内，而任一单独使用都会在另一种场景下表现糟糕。
    /// </para>
    ///
    /// <para>
    /// <b>最后一次必发</b>：<c>completed >= total</c> 时无条件放行，
    /// 否则进度条会永远停在 99%。
    /// </para>
    ///
    /// <para>时间由调用方注入，故本类可完整测试，不依赖真实时钟。</para>
    /// </summary>
    public sealed class ProgressEmitGate
    {
        /// <summary>默认最小发送间隔。</summary>
        public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(100);

        private readonly int _total;
        private readonly TimeSpan _minInterval;
        private readonly int _stride;

        private DateTime _lastEmitAt;
        private int _lastEmitCount;

        /// <param name="totalOperations">操作总数。</param>
        /// <param name="startedAt">起始时刻，作为首次间隔判断的基准。</param>
        /// <param name="minInterval">最小发送间隔，默认 100ms。</param>
        public ProgressEmitGate(int totalOperations, DateTime startedAt, TimeSpan? minInterval = null)
        {
            _total = totalOperations;
            _minInterval = minInterval ?? DefaultMinInterval;

            // 1% 步长；总数少于 100 时退化为"每个操作都发"，此时 N 本来就不大
            _stride = Math.Max(1, totalOperations / 100);

            _lastEmitAt = startedAt;
            _lastEmitCount = 0;
        }

        /// <summary>每 1% 对应多少个操作。</summary>
        public int Stride => _stride;

        /// <summary>
        /// 是否应当发送本次进度。返回 true 时内部会记录本次发送，调用方必须真的发出去。
        /// </summary>
        /// <param name="completedOperations">已完成操作数。</param>
        /// <param name="now">当前时刻。</param>
        public bool ShouldEmit(int completedOperations, DateTime now)
        {
            // 最后一次必发，否则进度条停在 99%
            if (completedOperations >= _total)
            {
                Record(completedOperations, now);
                return true;
            }

            var enoughTime = now - _lastEmitAt >= _minInterval;
            var enoughProgress = completedOperations - _lastEmitCount >= _stride;

            if (!enoughTime && !enoughProgress) return false;

            Record(completedOperations, now);
            return true;
        }

        private void Record(int completedOperations, DateTime now)
        {
            _lastEmitAt = now;
            _lastEmitCount = completedOperations;
        }
    }
}
