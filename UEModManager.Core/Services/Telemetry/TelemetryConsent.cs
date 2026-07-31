namespace UEModManager.Services.Telemetry;

/// <summary>本次不上报的原因。每一条都要能在日志里一眼看懂，排"数字怎么不涨"时全靠它。</summary>
public enum TelemetrySkipReason
{
    /// <summary>不适用（可以上报）。</summary>
    NotSkipped,

    /// <summary>还没告知过用户，也就还没取得同意。</summary>
    NotAskedYet,

    /// <summary>用户明确关掉了。</summary>
    OptedOut,
}

/// <summary>
/// 用户对"匿名统计"这件事的当前状态。两个布尔是<b>正交</b>的，不能合并成一个三态枚举，
/// 也不能用"Enabled 为 true 即代表问过"来省掉 <paramref name="Asked"/>。
/// </summary>
/// <param name="Asked">
/// 是否已经把这件事告诉过用户。<b>必须是独立的一个标记。</b>
/// 拿"Enabled 是不是默认值"当判据的话，一个主动选了"参与"的用户与一个从没被问过的用户
/// 长得一模一样，于是要么每次启动再问一遍，要么在没问过的时候就开始上报。
/// </param>
/// <param name="Enabled">用户的选择。未问过时这个值没有意义，判定一律以 <paramref name="Asked"/> 优先。</param>
public readonly record struct TelemetryConsentState(bool Asked, bool Enabled);

/// <summary>一次"能不能上报 / 要不要问"的结论，带可直接进日志的理由。</summary>
public readonly record struct TelemetryConsentDecision(
    bool ShouldAsk,
    bool ShouldReport,
    TelemetrySkipReason SkipReason,
    string Reason)
{
    /// <inheritdoc/>
    public override string ToString()
        => ShouldReport ? $"上报：{Reason}"
         : ShouldAsk ? $"先问用户：{Reason}"
         : $"不上报（{SkipReason}）：{Reason}";
}

/// <summary>
/// "这次该不该上报"的判定（纯函数，不碰 IO、不碰 UI）。
///
/// <para><b>唯一一条硬规则：没告知过就绝不上报。</b></para>
/// 用户主要在中国大陆，收集邮箱哈希 + 设备标识属于个人信息处理，《个人信息保护法》
/// 要求告知并取得同意。工程上把它落成一句话：<see cref="TelemetryConsentState.Asked"/>
/// 为 false 时 <see cref="TelemetryConsentDecision.ShouldReport"/> 恒为 false，
/// 无论 <see cref="TelemetryConsentState.Enabled"/> 是什么。
///
/// <para>
/// 这条规则有一个真实的副作用，是设计的一部分而不是缺陷：首次运行时登录窗口出现在主窗口
/// <b>之前</b>，那一次登录成功的账号上报会因为"还没问过"而被丢弃。兜底不在这里，
/// 而在客户端——心跳时若发现当前已登录且本次会话还没报过账号，就顺带补一次。
/// 那条兜底同时也解决了另一个更大的漏计：靠"记住我"自动登录、根本不走登录窗口的老用户。
/// </para>
///
/// <para><b>为什么默认值是"开"而不是"关"</b>（<c>Enabled</c> 的初值由调用方给，这里只记结论）：
/// 默认关会让愿意主动去设置里打开的用户成为样本，而那不是典型用户，数字会严重失真到没有
/// 参考价值——那还不如不做。所以取的是"默认开 + 首次运行明确告知一次 + 设置里随时能关"，
/// 把代价放在"必须真的告知到位"上，而不是放在"数字不可信"上。</para>
/// </summary>
public static class TelemetryConsent
{
    /// <summary>判定。</summary>
    public static TelemetryConsentDecision Decide(TelemetryConsentState state)
    {
        if (!state.Asked)
        {
            // 顺序即优先级：没问过时不看 Enabled。哪怕它是 true（默认值），也一样不上报。
            return new TelemetryConsentDecision(
                ShouldAsk: true,
                ShouldReport: false,
                TelemetrySkipReason.NotAskedYet,
                "还没告知过用户，先问一次再说；在此之前一个字节都不发");
        }

        if (!state.Enabled)
        {
            return new TelemetryConsentDecision(
                ShouldAsk: false,
                ShouldReport: false,
                TelemetrySkipReason.OptedOut,
                "用户关掉了匿名统计");
        }

        return new TelemetryConsentDecision(
            ShouldAsk: false,
            ShouldReport: true,
            TelemetrySkipReason.NotSkipped,
            "已告知且用户未关闭");
    }
}
