using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UEModManager.Converters
{
    /// <summary>
    /// 宽度够不够：不小于 <see cref="MinimumWidth"/> 时显示，否则收起。
    ///
    /// 给"图标 + 文字"的按钮用，绑在它所在那一排的实际宽度上：这一排放不下时只留图标
    /// （悬停提示里仍有完整说明），免得多出来的文字把同一排的其他控件挤出去。
    /// 主窗口最小宽度 1024、或右侧详情面板打开时，列表上方那一排已经没有余量。
    /// </summary>
    public sealed class MinimumWidthToVisibilityConverter : IValueConverter
    {
        /// <summary>显示文字所需的最小宽度（设备无关像素）。</summary>
        public double MinimumWidth { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            // 量不到宽度（还没布局）时按"够宽"处理：默认显示文字，不让按钮在第一帧缺一块
            => value is double width && !double.IsNaN(width) && width > 0 && width < MinimumWidth
                ? Visibility.Collapsed
                : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }
}
