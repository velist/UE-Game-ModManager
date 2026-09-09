using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using UEModManager.Localization;

namespace UEModManager.Infrastructure;

public static class GameIconPicker
{
    public static string? BrowseAndCopy(Window owner, string gameName)
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("选择游戏图标"),
            Filter = UiText.Get("图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.webp|所有文件|*.*")
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        var iconsDir = AppPaths.GameIconsDirectory;
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
