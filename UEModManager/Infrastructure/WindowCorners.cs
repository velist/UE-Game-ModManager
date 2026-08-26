using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UEModManager.Infrastructure
{
    /// <summary>
    /// 给窗口套上 Win11 的系统圆角。
    ///
    /// <para>
    /// 本项目所有窗口都是 <c>WindowStyle=None</c> + 自绘标题栏（见 <c>CyberStyles.xaml</c> 的
    /// <c>CyberDarkWindow</c> / <c>CyberModalWindow</c>），而那里的
    /// <c>WindowChrome.CornerRadius</c> 被设成 0，于是 Win11 默认会给窗口加的圆角被按掉了，
    /// 四个角是标准 90°——在一屏全是圆角窗口的 Win11 上，这一点让界面显得格外生硬。
    /// </para>
    ///
    /// <para>
    /// <b>为什么不去改 <c>WindowChrome.CornerRadius</c>：</b>那个属性作用于 WPF 自己绘制的 chrome，
    /// 对 <c>WindowStyle=None</c> 的窗口并不能让**系统**把窗口区域裁成圆角；真正管这件事的是
    /// DWM 的 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>。由 DWM 裁剪还额外带上系统一致的
    /// 边缘抗锯齿和投影，比自己画圆角边框更接近原生观感。
    /// </para>
    ///
    /// <para>
    /// Win10 及更早没有这个属性，<c>DwmSetWindowAttribute</c> 会返回失败码；这里既做版本判断
    /// 也忽略返回值，任何情况下都不该因为"窗口没圆角"影响程序启动。
    /// </para>
    /// </summary>
    internal static class WindowCorners
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        /// <summary>DWM 的圆角偏好取值。</summary>
        private enum CornerPreference
        {
            Default    = 0,
            DoNotRound = 1,
            Round      = 2,   // 大圆角，Win11 窗口的默认观感
            RoundSmall = 3,
        }

        /// <summary>Win11 的第一个正式版本号。</summary>
        private const int Windows11Build = 22000;

        private static readonly bool Supported =
            Environment.OSVersion.Platform == PlatformID.Win32NT &&
            Environment.OSVersion.Version.Build >= Windows11Build;

        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// 把窗口的四角交给 DWM 裁成圆角。窗口句柄还没创建、或系统不支持时静默跳过。
        /// </summary>
        internal static void ApplyRounded(Window window)
        {
            if (!Supported || window == null) return;

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;   // 尚未 SourceInitialized

                int preference = (int)CornerPreference.Round;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // 纯观感增强，失败不影响任何功能，连日志都不值得记。
            }
        }
    }
}
