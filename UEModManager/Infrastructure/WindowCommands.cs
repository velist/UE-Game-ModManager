using System.Windows;
using System.Windows.Input;

namespace UEModManager.Infrastructure
{
    /// <summary>
    /// 给所有 <see cref="Window"/> 挂上标题栏那三个系统命令的类级处理。
    ///
    /// <c>CyberModalWindow</c> 样式的自绘标题栏里，关闭按钮发的是
    /// <see cref="SystemCommands.CloseWindowCommand"/>，但 WPF 不会替窗口执行这条命令 ——
    /// 每个窗口得自己在 XAML 里写一段 <c>Window.CommandBindings</c>。有 5 个窗口没写
    /// （冲突面板、输入框、捐赠、MOD 详情、消息框），它们右上角的 × 点了没有任何反应。
    /// 靠"新建窗口时记得抄一段"是靠不住的，这里在类级别注册一次，所有窗口都能关。
    /// 窗口自己写的实例级绑定优先级更高，已有的行为不受影响。
    /// </summary>
    public static class WindowCommands
    {
        private static bool _registered;

        public static void RegisterClassBindings()
        {
            if (_registered) return;
            _registered = true;

            CommandManager.RegisterClassCommandBinding(typeof(Window),
                new CommandBinding(SystemCommands.CloseWindowCommand,
                    (sender, _) => { if (sender is Window w) SystemCommands.CloseWindow(w); }));
            CommandManager.RegisterClassCommandBinding(typeof(Window),
                new CommandBinding(SystemCommands.MinimizeWindowCommand,
                    (sender, _) => { if (sender is Window w) SystemCommands.MinimizeWindow(w); }));
            CommandManager.RegisterClassCommandBinding(typeof(Window),
                new CommandBinding(SystemCommands.MaximizeWindowCommand,
                    (sender, _) => { if (sender is Window w) SystemCommands.MaximizeWindow(w); }));
            CommandManager.RegisterClassCommandBinding(typeof(Window),
                new CommandBinding(SystemCommands.RestoreWindowCommand,
                    (sender, _) => { if (sender is Window w) SystemCommands.RestoreWindow(w); }));
        }
    }
}
