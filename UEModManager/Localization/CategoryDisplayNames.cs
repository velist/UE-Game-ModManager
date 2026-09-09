using System;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Localization;

/// <summary>Translate built-in category identifiers without changing custom category names.</summary>
public static class CategoryDisplayNames
{
    public static string For(string name) => For(name, LanguageManager.IsEnglish);

    public static string For(string name, bool english)
        => Array.IndexOf(CategoryItem.DefaultOrder, name) >= 0 ? UiText.Get(name, english) : name;
}
