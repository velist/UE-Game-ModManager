using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using UEModManager.Services;

namespace UEModManager.Localization;

/// <summary>
/// Opt-in localization for application-owned status labels and count formats.
/// The original value stays intact; changing language re-evaluates the display.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class ValueExtension(Binding value) : MarkupExtension
{
    [ConstructorArgument("value")]
    public Binding Value { get; set; } = value;
    public string? Format { get; set; }
    public bool CategoryName { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new ValueConverter(Format, CategoryName) };
        binding.Bindings.Add(Value);
        binding.Bindings.Add(new Binding(nameof(LanguageState.IsEnglish)) { Source = LanguageManager.State });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class ValueConverter(string? format, bool categoryName) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue) return string.Empty;
            var english = values[1] is true;
            if (categoryName) return CategoryDisplayNames.For(values[0]?.ToString() ?? string.Empty, english);
            return format == null ? UiText.Get(values[0]?.ToString() ?? string.Empty, english)
                : string.Format(CultureInfo.GetCultureInfo(english ? "en-US" : "zh-CN"), UiText.Get(format, english), values[0]);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
