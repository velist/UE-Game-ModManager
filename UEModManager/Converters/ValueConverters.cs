using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UEModManager.Models;

namespace UEModManager.Converters
{
    /// <summary>
    /// 从主题资源解析画刷，解析不到时回落到给定颜色。
    ///
    /// 存在的理由：本文件此前硬编码了 26 个 SolidColorBrush，与
    /// Themes/CyberDarkTheme.xaml 的设计令牌各自演化，已经漂移——
    /// 同为"配置/替换"语义色，XAML 是一种橙、这里是另一种橙。
    /// 现在统一从主题取，令牌就真的是唯一事实来源。
    ///
    /// 两点必须成立：
    /// - **不能抛异常**。设计期（XAML 设计器）和测试宿主里 Application.Current 为 null。
    /// - **延迟解析**。静态构造若早于 App.xaml 合并主题字典执行，会把回落值缓存一辈子；
    ///   用 Lazy 推迟到第一次真正取用，那时资源一定已就位（Lazy 默认线程安全）。
    /// </summary>
    internal static class ThemeBrush
    {
        /// <summary>声明一个延迟解析的主题画刷。</summary>
        public static Lazy<SolidColorBrush> Lazy(string resourceKey, Color fallback)
            => new(() => Resolve(resourceKey, fallback));

        private static SolidColorBrush Resolve(string resourceKey, Color fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(resourceKey) is SolidColorBrush brush)
                    return brush;
            }
            catch
            {
                // 设计期或资源尚未加载 —— 走回落，绝不让转换器把界面搞崩
            }

            var fallbackBrush = new SolidColorBrush(fallback);
            fallbackBrush.Freeze();
            return fallbackBrush;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  v2.0 Package / Deploy / Conflict 转换器
    // ═══════════════════════════════════════════════════════

    /// <summary>PackageKind → 前景色画刷 (MOD=青/Plugin=紫/Config=橙)</summary>
    public class PackageKindToBrushConverter : IValueConverter
    {
        private static readonly Lazy<SolidColorBrush> ModBrush =
            ThemeBrush.Lazy("PrimaryBrush", Color.FromRgb(0x06, 0xb6, 0xd4));
        private static readonly Lazy<SolidColorBrush> PluginBrush =
            ThemeBrush.Lazy("PluginPurpleBrush", Color.FromRgb(0xa8, 0x55, 0xf7));
        private static readonly Lazy<SolidColorBrush> ConfigBrush =
            ThemeBrush.Lazy("ConfigAmberBrush", Color.FromRgb(0xf5, 0x9e, 0x0b));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is PackageKind kind ? kind switch
            {
                PackageKind.Plugin => PluginBrush.Value,
                PackageKind.Config => ConfigBrush.Value,
                _ => ModBrush.Value
            } : ModBrush.Value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>PackageKind → 背景色画刷（20%透明度）</summary>
    public class PackageKindToBgBrushConverter : IValueConverter
    {
        private static readonly Lazy<SolidColorBrush> ModBg =
            ThemeBrush.Lazy("ModCyanBgBrush", Color.FromArgb(0x20, 0x06, 0xb6, 0xd4));
        private static readonly Lazy<SolidColorBrush> PluginBg =
            ThemeBrush.Lazy("PluginPurpleBgBrush", Color.FromArgb(0x20, 0xa8, 0x55, 0xf7));
        private static readonly Lazy<SolidColorBrush> ConfigBg =
            ThemeBrush.Lazy("ConfigAmberBgBrush", Color.FromArgb(0x20, 0xf5, 0x9e, 0x0b));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is PackageKind kind ? kind switch
            {
                PackageKind.Plugin => PluginBg.Value,
                PackageKind.Config => ConfigBg.Value,
                _ => ModBg.Value
            } : ModBg.Value;

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
    /// <summary>
    /// 将图片文件路径转换为解码后的缩略图 ImageSource。
    /// 使用 DecodePixelWidth 控制解码尺寸，大幅减少内存占用。
    /// 内置按字节数限容的 LRU 缓存，切换分类时避免重复加载同一张图片。
    /// </summary>
    public class AsyncImageConverter : IValueConverter
    {
        public int DecodePixelWidth { get; set; } = 400;

        /// <summary>
        /// 缓存容量上限（字节）。
        /// 依据：DecodePixelWidth 默认 400，一张 4:3 预览图解码后约 400×300×4 ≈ 480 KB，
        /// 128 MB 约可容纳 270 张——足以覆盖单个游戏下绝大多数 MOD 列表的实际浏览量，
        /// 同时把最坏情况的常驻内存钉死在 128 MB（这些位图 Freeze 后会进 LOH，不参与压缩）。
        /// </summary>
        public const long CacheCapacityBytes = 128L * 1024 * 1024;

        /// <summary>
        /// 图片缓存：key 见 <see cref="BuildCacheKey"/>，value = 冻结的 BitmapImage。
        /// 切换分类/过滤时同一 MOD 对象会被反复绑定，缓存避免重复磁盘 I/O。
        /// </summary>
        private static readonly SizeLimitedLruCache<BitmapImage> _cache = new(CacheCapacityBytes);

        /// <summary>
        /// 生成缓存 key。
        /// 必须带上文件最后写入时间：ObjectStore.StorePreviewImage 会把新预览图写到
        /// 确定性路径 {packageDir}/preview{ext}，用户把一张 .png 换成另一张 .png 时路径逐字节相同，
        /// 只按路径做 key 会命中旧的冻结位图，界面上永远显示老图。
        /// 带上写入时间同时覆盖了"用户在资源管理器里直接替换文件"的场景。
        /// </summary>
        public static string BuildCacheKey(string path, int decodePixelWidth, DateTime lastWriteTimeUtc)
            => $"{path}|{decodePixelWidth}|{lastWriteTimeUtc.Ticks}";

        /// <summary>
        /// 估算一张解码后位图占用的字节数。以实际像素尺寸和像素格式为准，
        /// 不同尺寸的图片占用差异很大，按条目数限容会严重失真。
        /// </summary>
        public static long EstimateBitmapBytes(int pixelWidth, int pixelHeight, int bitsPerPixel)
        {
            var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
            return (long)Math.Max(0, pixelWidth) * Math.Max(0, pixelHeight) * bytesPerPixel;
        }

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return null;

            // FileInfo 一次元数据查询同时拿到存在性和写入时间，替代原来的 File.Exists
            DateTime lastWriteUtc;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return null;
                lastWriteUtc = info.LastWriteTimeUtc;
            }
            catch
            {
                return null;
            }

            var cacheKey = BuildCacheKey(path, DecodePixelWidth, lastWriteUtc);

            // 命中缓存直接返回
            if (_cache.TryGet(cacheKey, out var cached))
                return cached;

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

                _cache.Set(cacheKey, bi, EstimateBitmapBytes(bi.PixelWidth, bi.PixelHeight, bi.Format.BitsPerPixel));
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

    /// <summary>
    /// 按字节数限容的 LRU 缓存。
    /// 采用「哈希表定位 + 双向链表维护访问顺序」的经典实现：链表头是最近使用的，
    /// 写入导致超限时从链表尾逐个淘汰，直到总字节数回到上限内。
    /// 不含任何 WPF 类型，可脱离 UI 线程直接单元测试。
    /// </summary>
    /// <typeparam name="TValue">缓存值类型（本项目为冻结的 BitmapImage）。</typeparam>
    public sealed class SizeLimitedLruCache<TValue> where TValue : class
    {
        private sealed class Entry
        {
            public string Key = string.Empty;
            public TValue Value = default!;
            public long SizeBytes;
        }

        private readonly long _capacityBytes;
        private readonly Dictionary<string, LinkedListNode<Entry>> _index = new();
        private readonly LinkedList<Entry> _order = new();   // First = 最近使用，Last = 最久未用
        private readonly object _gate = new();
        private long _currentBytes;

        public SizeLimitedLruCache(long capacityBytes)
        {
            if (capacityBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacityBytes), "容量上限必须为正数");
            _capacityBytes = capacityBytes;
        }

        /// <summary>容量上限（字节）。</summary>
        public long CapacityBytes => _capacityBytes;

        /// <summary>当前占用（字节）。</summary>
        public long CurrentBytes { get { lock (_gate) return _currentBytes; } }

        /// <summary>当前条目数。</summary>
        public int Count { get { lock (_gate) return _index.Count; } }

        /// <summary>按最近使用顺序返回全部 key（首个 = 最近使用）。仅用于诊断和测试。</summary>
        public IReadOnlyList<string> KeysMostRecentFirst()
        {
            lock (_gate)
            {
                var result = new List<string>(_order.Count);
                for (var node = _order.First; node != null; node = node.Next)
                    result.Add(node.Value.Key);
                return result;
            }
        }

        /// <summary>读取并把命中项提升为最近使用。</summary>
        public bool TryGet(string key, out TValue value)
        {
            lock (_gate)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// 写入。已存在的 key 会被覆盖（并按新大小重新计账）。
        /// 单个条目本身就超过总容量时不予缓存——否则它一进来就会把其余所有条目挤干净。
        /// </summary>
        public void Set(string key, TValue value, long sizeBytes)
        {
            if (sizeBytes < 0) sizeBytes = 0;

            lock (_gate)
            {
                if (_index.TryGetValue(key, out var existing))
                {
                    _currentBytes -= existing.Value.SizeBytes;
                    _order.Remove(existing);
                    _index.Remove(key);
                }

                if (sizeBytes > _capacityBytes)
                    return;

                var node = _order.AddFirst(new Entry { Key = key, Value = value, SizeBytes = sizeBytes });
                _index[key] = node;
                _currentBytes += sizeBytes;

                while (_currentBytes > _capacityBytes && _order.Last != null)
                {
                    var victim = _order.Last;
                    _order.RemoveLast();
                    _index.Remove(victim.Value.Key);
                    _currentBytes -= victim.Value.SizeBytes;
                }
            }
        }

        /// <summary>清空。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _index.Clear();
                _order.Clear();
                _currentBytes = 0;
            }
        }
    }
}
