using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UEModManager.Infrastructure;
using UEModManager.Tests.Infrastructure;

namespace UEModManager.Tests.Views;

/// <summary>
/// 自绘标题栏的 × 按钮。
///
/// <para>
/// 事故：<c>CyberModalWindow</c> 模板里的关闭按钮发的是 <see cref="SystemCommands.CloseWindowCommand"/>，
/// 而 WPF 不会替窗口执行这条命令 —— 没有任何绑定时按钮的 CanExecute 为 false，点了毫无反应。
/// 只有在 XAML 里自己写了 <c>Window.CommandBindings</c> 的窗口能关；冲突面板、输入框、消息框、
/// 捐赠、MOD 详情这 5 个没写，用户点右上角 × 关不掉冲突检测弹窗。
/// 现在由 <see cref="WindowCommands.RegisterClassBindings"/> 在类级别注册一次。
/// </para>
///
/// <para>
/// 窗口必须真的显示出来（放在屏幕外、不抢焦点）：<see cref="SystemCommands.CloseWindow"/> 是往窗口句柄
/// Post 一条 WM_SYSCOMMAND，没有句柄就什么都不做，而且要等消息被处理才真正关闭。
/// 只 new 一个不显示的 Window，关与不关都测不出来。
/// </para>
/// </summary>
public sealed class WindowCommandsTests
{
    [Fact]
    public void 样式模板里的关闭按钮能关掉没写绑定的窗口()
        => StaApplication.Run(() =>
        {
            EnsureAppTheme();
            WindowCommands.RegisterClassBindings();

            // 与冲突面板同样的处境：套了 CyberModalWindow 样式，自己没写任何 CommandBindings。
            var window = new Window { Style = (Style)Application.Current.FindResource("CyberModalWindow") };
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                ShowOffscreen(window);
                var closeButton = FindDescendants<Button>(window)
                    .Single(b => b.Command == SystemCommands.CloseWindowCommand);

                // 从按钮本身发起，走的就是点击时那条从模板冒泡到窗口的路由。
                Assert.True(SystemCommands.CloseWindowCommand.CanExecute(null, closeButton));
                SystemCommands.CloseWindowCommand.Execute(null, closeButton);
                PumpUntil(() => closed, TimeSpan.FromSeconds(5));

                Assert.True(closed);
            }
            finally
            {
                if (!closed) window.Close();
            }
            return true;
        });

    [Fact]
    public void 窗口自己写的绑定仍然优先()
        => StaApplication.Run(() =>
        {
            WindowCommands.RegisterClassBindings();

            // 账户设置窗口这类已经自带绑定的窗口，行为不能被类级绑定抢走。
            var window = new Window();
            var ownHandlerRan = false;
            window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
                (_, e) => { ownHandlerRan = true; e.Handled = true; }));
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                ShowOffscreen(window);
                SystemCommands.CloseWindowCommand.Execute(null, window);
                // 类级绑定若也跑了，窗口会在消息处理后关掉 —— 给它足够的机会。
                PumpUntil(() => closed, TimeSpan.FromMilliseconds(500));

                Assert.True(ownHandlerRan);
                Assert.False(closed);
            }
            finally
            {
                if (!closed) window.Close();
            }
            return true;
        });

    [Fact]
    public void 启动时注册了类级绑定()
    {
        // 类级绑定只在 App 启动时注册一次；这一行被删掉，所有没写绑定的窗口又关不掉了。
        var app = File.ReadAllText(Path.Combine(LanguageIntegrationTests.AppSource, "App.xaml.cs"));
        Assert.Contains("WindowCommands.RegisterClassBindings()", app);
    }

    /// <summary>
    /// 把主题字典挂到常驻的 Application 上（与冲突面板接线用例的做法一致，重复调用无副作用）：
    /// <c>CyberModalWindow</c> 引用了主题里的画刷，单独加载样式字典会解析失败。
    /// </summary>
    private static void EnsureAppTheme()
    {
        var application = Application.Current;
        if (application.TryFindResource("CyberModalWindow") == null)
            foreach (var name in new[] { "CyberDarkTheme", "CyberStyles" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/UEModManager;component/Themes/{name}.xaml")
                });
    }

    private static void ShowOffscreen(Window window)
    {
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Width = 240;
        window.Height = 160;
        window.Show();
    }

    /// <summary>处理已 Post 的窗口消息，直到条件成立或超时。</summary>
    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (!done()) Thread.Sleep(10);
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }
}
