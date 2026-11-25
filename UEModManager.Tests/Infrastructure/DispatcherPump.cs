using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace UEModManager.Tests.Infrastructure;

/// <summary>
/// 在测试线程上处理 WPF 的消息队列：计时器、布局、动画时钟、Unloaded 这些都靠它才会往前走。
///
/// <para>
/// 调用方必须已经在 STA 线程的消息循环里（<c>ThemeResourceLoader.RunOnSta</c> 或
/// <see cref="StaApplication.Run{T}"/>）。这里开的是嵌套的消息帧，条件成立或超时就退出。
/// </para>
/// </summary>
internal static class DispatcherPump
{
    /// <summary>处理消息队列，直到条件成立（返回 true）或超时（返回 false）。</summary>
    public static bool Until(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (done()) return true;
            if (DateTime.UtcNow >= deadline) return false;

            // 退出帧排在最低一档：比它优先的（计时器、渲染、布局、Unloaded）都处理完才返回
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (!done()) Thread.Sleep(5);
        }
    }

    /// <summary>
    /// 先至少处理 <paramref name="atLeast"/> 这么久的消息，之后再等条件成立（或超时）。
    ///
    /// <para>
    /// 用在「动画播完之后应该是什么样」上。WPF 的动画要到下一帧才生效：刚 <c>BeginAnimation</c> 完，
    /// 属性读出来还是原值。只等条件的话，动画还没开始就已经"符合终态"了，断言等于没验。
    /// <paramref name="atLeast"/> 给到动画时长再加一些余量。
    /// </para>
    /// </summary>
    public static bool Past(TimeSpan atLeast, Func<bool> done, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        return Until(() => clock.Elapsed >= atLeast && done(), atLeast + timeout);
    }

    /// <summary>
    /// 这个环境里 WPF 的动画时钟走不走。正常的桌面与 CI 都走；极少数没有合成器的宿主里不走，
    /// 那里「等动画播完」的断言无从验证，相关用例应直接返回，而不是报一个与被测代码无关的失败。
    /// </summary>
    public static bool AnimationClockTicks()
    {
        var probe = new TranslateTransform();
        var completed = false;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(30)) { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => completed = true;
        probe.BeginAnimation(TranslateTransform.XProperty, animation);
        return Until(() => completed, TimeSpan.FromSeconds(5));
    }
}
