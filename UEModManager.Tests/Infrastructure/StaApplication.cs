using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace UEModManager.Tests.Infrastructure;

/// <summary>
/// 一个进程内常驻的 STA 线程 + <see cref="Application"/> 实例，供需要
/// <c>Application.Current.Dispatcher</c> 的集成测试借用。
///
/// <para>
/// <c>MainViewModel.RefreshFromRepositoryAsync</c> / <c>ApplyConflictBadges</c> 会把
/// <c>AllMods</c> 的重建放进 <c>Application.Current.Dispatcher.Invoke</c>：绑定集合有
/// 线程亲和性，后台线程改它会抛。这本是对的，但意味着任何想跑
/// <c>MainViewModel</c> 的测试都必须先有 <c>Application.Current</c>。
/// </para>
///
/// <para>
/// 只创建一次、且跑在专用 STA 线程上：<see cref="Application"/> 在一个 AppDomain 里
/// 只能有一个实例，而它必须在 STA 线程上创建，所以不能用
/// <c>ThemeResourceLoader.RunOnSta</c>（那是每次调用新起一个线程，第二次创建
/// <see cref="Application"/> 会抛）。
/// </para>
///
/// <para>
/// 测试体必须通过 <see cref="RunAsync{T}"/> 或 <see cref="Run{T}"/> 派发，
/// 不能在外部线程上直接调 <c>Dispatcher.Invoke</c> 再等异步结果 ——
/// 那会让异步续体与"正在阻塞等待的派发线程"互相等，直接死锁。
/// </para>
/// </summary>
internal static class StaApplication
{
    private static readonly Lazy<Dispatcher> LazyDispatcher = new(Start, isThreadSafe: true);

    /// <summary>常驻派发器（首次访问时启动专用 STA 线程）。</summary>
    public static Dispatcher Dispatcher => LazyDispatcher.Value;

    /// <summary>在 STA 线程上同步执行，并把结果带回调用线程。</summary>
    public static T Run<T>(Func<T> work) => Dispatcher.Invoke(work);

    /// <summary>
    /// 在 STA 线程上启动一段异步工作，并在调用线程上等待它完成。
    ///
    /// <c>InvokeAsync(...).Task.Unwrap()</c> 是关键：等待发生在调用线程上，
    /// 派发线程保持空闲以便继续处理异步续体。
    /// </summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> work)
        => Dispatcher.InvokeAsync(work).Task.Unwrap();

    /// <summary>无返回值的重载：委托本身返回 <see cref="Task"/>。</summary>
    public static Task RunAsync(Func<Task> work)
        => Dispatcher.InvokeAsync(work).Task.Unwrap();

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            // 必须在这里创建：Application 要求 STA，且全进程只能有一个。
            if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "UEModManager.Tests STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }
}