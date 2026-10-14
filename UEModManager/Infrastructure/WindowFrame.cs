using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UEModManager.Infrastructure
{
    /// <summary>
    /// 让自绘窗口在深色桌面上也看得清边界：Win11 系统圆角 + DWM 浅色描边。
    ///
    /// <para>
    /// 本项目所有窗口都是 <c>WindowStyle=None</c> + 自绘标题栏（见 <c>CyberStyles.xaml</c> 的
    /// <c>CyberDarkWindow</c> / <c>CyberModalWindow</c> / <c>CyberBorderlessWindow</c>）。
    /// 系统阴影靠样式里的 <c>GlassFrameThickness="1"</c> 找回——设成 0 时 DWM 不画阴影，
    /// 深色窗口叠在深色窗口或壁纸上，边界完全融在一起。阴影 Win10/11 通用，这里补 Win11 才有的两项：
    /// </para>
    ///
    /// <para>
    /// <b>圆角。</b>样式里的 <c>WindowChrome.CornerRadius</c> 是 0，Win11 默认给窗口加的圆角被按掉了，
    /// 在一屏全是圆角窗口的 Win11 上显得格外生硬。不去改 <c>WindowChrome.CornerRadius</c>：
    /// 那个属性作用于 WPF 自己绘制的 chrome，对 <c>WindowStyle=None</c> 的窗口并不能让**系统**
    /// 把窗口区域裁成圆角；真正管这件事的是 DWM 的 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>，
    /// 由 DWM 裁剪还额外带上系统一致的边缘抗锯齿和投影。
    /// </para>
    ///
    /// <para>
    /// <b>描边。</b>让 DWM 在窗口外缘描一圈 <c>BorderLightColor</c>（#3f3f46），与样式里的
    /// <c>BorderBrush</c> 同色，圆角处也连续。
    /// </para>
    ///
    /// <para>
    /// Win10 及更早没有这两个属性，<c>DwmSetWindowAttribute</c> 会返回失败码；这里既做版本判断
    /// 也忽略返回值，任何情况下都不该因为"窗口没圆角"影响程序启动。
    /// <c>AllowsTransparency=True</c> 的分层窗口 DWM 不参与绘制，直接跳过——这类窗口
    /// （首次运行的存储向导）在自己的 XAML 里画投影。
    /// </para>
    /// </summary>
    internal static class WindowFrame
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;

        /// <summary>DWM 的圆角偏好取值。</summary>
        private enum CornerPreference
        {
            Default    = 0,
            DoNotRound = 1,
            Round      = 2,   // 大圆角，Win11 窗口的默认观感
            RoundSmall = 3,
        }

        /// <summary>COLORREF 是 0x00BBGGRR，对应主题的 BorderLightColor #3f3f46。</summary>
        private const int BorderColorRef = 0x00463f3f;

        /// <summary>Win11 的第一个正式版本号。</summary>
        private const int Windows11Build = 22000;

        private static readonly bool Supported =
            Environment.OSVersion.Platform == PlatformID.Win32NT &&
            Environment.OSVersion.Version.Build >= Windows11Build;

        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// 给窗口套上系统圆角和浅色描边。窗口句柄还没创建、系统不支持或是分层窗口时静默跳过。
        /// </summary>
        internal static void Apply(Window? window)
        {
            if (!Supported || window == null || window.AllowsTransparency) return;

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;   // 尚未 SourceInitialized

                int preference = (int)CornerPreference.Round;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                int color = BorderColorRef;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));
            }
            catch
            {
                // 纯观感增强，失败不影响任何功能，连日志都不值得记。
            }
        }
    }
}
