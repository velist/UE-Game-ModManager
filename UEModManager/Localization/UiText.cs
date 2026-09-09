using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using UEModManager.Services;

namespace UEModManager.Localization;

/// <summary>Translations for application-owned UI text. User data and stored identifiers are never rewritten.</summary>
public static class UiText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(LoadEnglish);

    public static string Get(string source) => Get(source, LanguageManager.IsEnglish);

    public static string Get(string source, bool english)
        => english && English.Value.TryGetValue(source, out var translated) ? translated : source;

    public static string Format(string source, params object?[] values)
        => string.Format(LanguageManager.IsEnglish ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN"),
            Get(source), values);

    public static string Interpolate(FormattableString text)
        => Format(text.Format, text.GetArguments());

    public static bool HasEnglish(string source) => English.Value.ContainsKey(source);

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("UEModManager.Localization.en-US.json")
            ?? throw new InvalidOperationException("The English UI translation resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("The English UI translation resource is invalid.");
    }
}
