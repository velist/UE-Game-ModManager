using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace UEModManager.Infrastructure;

public static class GameIconPicker
{
    public static string? BrowseAndCopy(Window owner, string gameName)
    {
        var dialog = new OpenFileDialog
        {
            Title = "\u9009\u62e9\u6e38\u620f\u56fe\u6807",
            Filter = "\u56fe\u7247\u6587\u4ef6|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.webp|\u6240\u6709\u6587\u4ef6|*.*"
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        var iconsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "GameIcons");
        Directory.CreateDirectory(iconsDir);

        var ext = Path.GetExtension(dialog.FileName);
        var safeName = SanitizeFileName(gameName);
        var destination = Path.Combine(iconsDir, $"{safeName}{ext}");
        File.Copy(dialog.FileName, destination, overwrite: true);
        return destination;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat([' ', '/']).ToHashSet();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "game" : sanitized;
    }
}