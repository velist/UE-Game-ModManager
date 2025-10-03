using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UEModManager.Converters
{
    public class ModConflictCountConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var realName = value as string ?? string.Empty;
                var count = UEModManager.Services.ModConflictRegistry.Lookup(realName);
                return count;
            }
            catch
            {
                return 0;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
