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
            try { BackgroundChanged?.Invoke(_settings); } catch { }
        }

        /// <summary>
        /// 实时预览背景效果（不持久化到磁盘）。
        /// 用于设置窗口中拖动滑块时的实时反馈。
        /// </summary>
        public static void Preview(BackgroundSettings settings)
        {
            _settings = settings;
            try { BackgroundChanged?.Invoke(_settings); } catch { }
        }

        /// <summary>
        /// 恢复到磁盘上保存的背景设置（取消预览时使用）。
        /// </summary>
        public static void RevertToSaved()
        {
            _settings = UiPreferences.LoadBackground();
            try { BackgroundChanged?.Invoke(_settings); } catch { }
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
            catch { }
        }
    }
}
