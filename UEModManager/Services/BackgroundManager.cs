using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UEModManager.Models;

namespace UEModManager.Services
{
    public static class BackgroundManager
    {
        private static BackgroundSettings _settings = new();

        public static BackgroundSettings Settings => _settings;
        public static event Action<BackgroundSettings>? BackgroundChanged;

        public static void Initialize()
        {
            _settings = UiPreferences.LoadBackground();
        }

        public static void Apply(BackgroundSettings settings)
        {
            _settings = settings;
            UiPreferences.SaveBackground(settings);
            RaiseBackgroundChanged();
        }

        /// <summary>
        /// 实时预览背景效果（不持久化到磁盘）。
        /// 用于设置窗口中拖动滑块时的实时反馈。
        /// </summary>
        public static void Preview(BackgroundSettings settings)
        {
            _settings = settings;
            RaiseBackgroundChanged();
        }

        /// <summary>
        /// 恢复到磁盘上保存的背景设置（取消预览时使用）。
        /// </summary>
        public static void RevertToSaved()
        {
            _settings = UiPreferences.LoadBackground();
            RaiseBackgroundChanged();
        }

        /// <summary>
        /// 逐个订阅者派发，每个订阅者单独捕获异常。
        /// 不能直接 <c>BackgroundChanged?.Invoke(...)</c>：多播委托是串行调用，
        /// 只要某个 handler 抛异常，调用列表中排在它后面的 handler 全部不会被执行，
        /// 表现为"改了背景只有部分窗口跟着变"且毫无日志。
        /// </summary>
        private static void RaiseBackgroundChanged()
        {
            var handlers = BackgroundChanged;
            if (handlers == null) return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<BackgroundSettings>)handler)(_settings);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BackgroundManager] 订阅者 " +
                        $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name} 处理背景变更失败: {ex}");
                }
            }
        }

        /// <summary>
        /// 将背景效果应用到弹窗中的背景层。
        /// 调用方需在 XAML 中准备 Image (BgImage) 和 Border (BgOverlay) 元素。
        /// </summary>
        public static void ApplyToDialog(Image bgImage, Border bgOverlay)
        {
            var bg = _settings;
            if (bg.Mode != BackgroundMode.Image || !bg.ApplyToDialogs)
            {
                bgImage.Visibility = Visibility.Collapsed;
                bgOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            if (string.IsNullOrEmpty(bg.ImagePath) || !File.Exists(bg.ImagePath))
                return;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                // IgnoreImageCache：避免 WPF 按 URI 字符串缓存，切图后弹窗也能拿到最新内容
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(bg.ImagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                bgImage.Source = bitmap;
                bgImage.Opacity = bg.Opacity * 0.4; // 弹窗中背景更淡
                bgImage.Stretch = Stretch.UniformToFill;
                bgImage.Visibility = Visibility.Visible;
                bgOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackgroundManager] ApplyToDialog 失败: {ex.Message}");
            }
        }
    }
}
