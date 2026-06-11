using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UEModManager.Models;

namespace UEModManager.Converters
{
    // ═══════════════════════════════════════════════════════
    //  v2.0 Package / Deploy / Conflict 转换器
    // ═══════════════════════════════════════════════════════

    /// <summary>PackageKind → 前景色画刷 (MOD=青/Plugin=紫/Config=橙)</summary>
    public class PackageKindToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush ModBrush = new(Color.FromRgb(0x06, 0xb6, 0xd4));
        private static readonly SolidColorBrush PluginBrush = new(Color.FromRgb(0xa8, 0x55, 0xf7));
        private static readonly SolidColorBrush ConfigBrush = new(Color.FromRgb(0xf5, 0x9e, 0x0b));

        static PackageKindToBrushConverter()
        {
            ModBrush.Freeze(); PluginBrush.Freeze(); ConfigBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is PackageKind kind ? kind switch
            {
                PackageKind.Plugin => PluginBrush,
                PackageKind.Config => ConfigBrush,
                _ => ModBrush
            } : ModBrush;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>PackageKind → 背景色画刷（20%透明度）</summary>
    public class PackageKindToBgBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush ModBg = new(Color.FromArgb(0x20, 0x06, 0xb6, 0xd4));
        private static readonly SolidColorBrush PluginBg = new(Color.FromArgb(0x20, 0xa8, 0x55, 0xf7));
        private static readonly SolidColorBrush ConfigBg = new(Color.FromArgb(0x20, 0xf5, 0x9e, 0x0b));

        static PackageKindToBgBrushConverter()
        {
            ModBg.Freeze(); PluginBg.Freeze(); ConfigBg.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is PackageKind kind ? kind switch
            {
                PackageKind.Plugin => PluginBg,
                PackageKind.Config => ConfigBg,
                _ => ModBg
            } : ModBg;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>PackageKind → 显示标签 (MOD/插件/配置)</summary>
    public class PackageKindToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is PackageKind kind ? kind switch
            {
                PackageKind.Plugin => "插件",
                PackageKind.Config => "配置",
                _ => "MOD"
            } : "MOD";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>PackageKind → 类型圆点色（用于卡片左上角小圆点）</summary>
    public class PackageKindToDotBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Cyan = new(Color.FromRgb(0x06, 0xb6, 0xd4));
        private static readonly SolidColorBrush Purple = new(Color.FromRgb(0xa8, 0x55, 0xf7));
        private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xf5, 0x9e, 0x0b));

        static PackageKindToDotBrushConverter()
        {
            Cyan.Freeze(); Purple.Freeze(); Amber.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is PackageKind kind ? kind switch
            {
                PackageKind.Plugin => Purple,
                PackageKind.Config => Amber,
                _ => Cyan
            } : Cyan;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>DeploymentOperationType → 前景色画刷 (Add=绿/Replace=橙/Remove=红)</summary>
    public class DeployOpTypeToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush AddBrush = new(Color.FromRgb(0x22, 0xc5, 0x5e));
        private static readonly SolidColorBrush ReplaceBrush = new(Color.FromRgb(0xf5, 0x9e, 0x0b));
        private static readonly SolidColorBrush RemoveBrush = new(Color.FromRgb(0xef, 0x44, 0x44));

        static DeployOpTypeToBrushConverter()
        {
            AddBrush.Freeze(); ReplaceBrush.Freeze(); RemoveBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is DeploymentOperationType t ? t switch
            {
                DeploymentOperationType.Replace => ReplaceBrush,
                DeploymentOperationType.Remove => RemoveBrush,
                _ => AddBrush
            } : AddBrush;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>DeploymentOperationType → 标签文本 (新增/修改/移除)</summary>
    public class DeployOpTypeToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is DeploymentOperationType t ? t switch
            {
                DeploymentOperationType.Replace => "修改",
                DeploymentOperationType.Remove => "移除",
                _ => "新增"
            } : "新增";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>bool (IsWinner) → 胜者(绿)/败者(红)画刷</summary>
    public class WinnerLoserBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush WinnerBrush = new(Color.FromRgb(0x22, 0xc5, 0x5e));
        private static readonly SolidColorBrush LoserBrush = new(Color.FromRgb(0xef, 0x44, 0x44));

        static WinnerLoserBrushConverter()
        {
            WinnerBrush.Freeze(); LoserBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool isWinner && isWinner ? WinnerBrush : LoserBrush;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>bool (IsWinner) → 胜者/败者背景画刷（半透明）</summary>
    public class WinnerLoserBgBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush WinnerBg = new(Color.FromArgb(0x18, 0x22, 0xc5, 0x5e));
        private static readonly SolidColorBrush LoserBg = new(Color.FromArgb(0x18, 0xef, 0x44, 0x44));

        static WinnerLoserBgBrushConverter()
        {
            WinnerBg.Freeze(); LoserBg.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool isWinner && isWinner ? WinnerBg : LoserBg;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && string.IsNullOrEmpty(s)) return Visibility.Collapsed;
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class InverseNullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && string.IsNullOrEmpty(s)) return Visibility.Visible;
            return value == null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class HandPlaceholderVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // value是分类名称（Binding Name）
            var categoryName = value as string;

            // ✅ 系统分类（全部、已启用、已禁用）显示占位符，其他情况隐藏
            if (categoryName == "全部" || categoryName == "已启用" || categoryName == "已禁用")
            {
                return Visibility.Visible;  // 显示占位符（此时DragHandle已隐藏）
            }

            return Visibility.Collapsed;  // 隐藏占位符（让DragHandle可交互）
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class HandColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Brushes.Gray; // Default color
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class ImagePathToSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return null;
            try
            {
                return new BitmapImage(new Uri(value.ToString()));
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class BooleanToStatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool status)
            {
                return status ? Brushes.Green : Brushes.Red;
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class BooleanToToggleTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isEnabled)
            {
                return isEnabled ? "点击禁用" : "点击启用";
            }
            return "点击切换";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class BooleanToToggleTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isEnabled)
            {
                return isEnabled ? "禁用" : "启用";
            }
            return "切换";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class LevelToMarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int level)
            {
                return new Thickness(level * 20, 0, 0, 0);
            }
            return new Thickness(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                return UEModManager.Core.Utils.FileSizeFormatter.Format(bytes);
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class MultiBooleanAndConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (value is bool boolValue && !boolValue)
                        return false;
                }
                return true;
            }
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class BoolToStatusTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? "已启用" : "已禁用";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToEnabledColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush EnabledBrush = new(Color.FromRgb(0x22, 0xc5, 0x5e));
        private static readonly SolidColorBrush DisabledBrush = new(Color.FromRgb(0x71, 0x71, 0x7a));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? EnabledBrush : DisabledBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>GeneratedArtifactType → 前景色画刷。</summary>
    public class GeneratedTypeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not GeneratedArtifactType type) return Brushes.Gray;
            return type switch
            {
                GeneratedArtifactType.DeploymentSnapshot => new SolidColorBrush(Color.FromRgb(0x06, 0xb6, 0xd4)),
                GeneratedArtifactType.MergedConfig => new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)),
                GeneratedArtifactType.ToolOutput => new SolidColorBrush(Color.FromRgb(0xa8, 0x55, 0xf7)),
                GeneratedArtifactType.Cache => new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7a)),
                GeneratedArtifactType.UserFix => new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
                _ => Brushes.Gray
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>GeneratedArtifactStatus → 状态色画刷。</summary>
    public class GeneratedStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not GeneratedArtifactStatus status) return Brushes.Gray;
            return status switch
            {
                GeneratedArtifactStatus.Active => new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
                GeneratedArtifactStatus.Stale => new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)),
                GeneratedArtifactStatus.Promoted => new SolidColorBrush(Color.FromRgb(0x06, 0xb6, 0xd4)),
                _ => Brushes.Gray
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class MultiBooleanOrConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (value is bool boolValue && boolValue)
                        return true;
                }
                return false;
            }
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 将图片文件路径转换为解码后的缩略图 ImageSource。
    /// 使用 DecodePixelWidth 控制解码尺寸，大幅减少内存占用。
    /// 内置 LRU 缓存，切换分类时避免重复加载同一张图片。
    /// </summary>
    public class AsyncImageConverter : IValueConverter
    {
        public int DecodePixelWidth { get; set; } = 400;

        /// <summary>
        /// 图片缓存：key = "path|decodeWidth"，value = 冻结的 BitmapImage。
        /// 切换分类/过滤时同一 MOD 对象会被反复绑定，缓存避免重复磁盘 I/O。
        /// </summary>
        private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new();

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return null;

            var cacheKey = $"{path}|{DecodePixelWidth}";

            // 命中缓存直接返回
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            if (!File.Exists(path))
                return null;

            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.DecodePixelWidth = DecodePixelWidth;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.EndInit();
                bi.Freeze();

                _cache.TryAdd(cacheKey, bi);
                return bi;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 清空图片缓存（切换游戏时调用）。
        /// </summary>
        public static void ClearCache()
        {
            _cache.Clear();
        }
    }
}
