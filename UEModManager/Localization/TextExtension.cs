using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using UEModManager.Services;

namespace UEModManager.Localization;

/// <summary>An explicit binding keeps labels, templates and tooltips in sync with the global language.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension(string source) => Source = source;

    [ConstructorArgument("source")]
    public string Source { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding(nameof(LanguageState.IsEnglish))
        {
            Source = LanguageManager.State,
            Mode = BindingMode.OneWay,
            Converter = new TextConverter(Source)
        }.ProvideValue(serviceProvider);

    private sealed class TextConverter(string source) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => UiText.Get(source, value is true);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }
}
