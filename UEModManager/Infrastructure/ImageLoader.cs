using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace UEModManager.Infrastructure;

public static class ImageLoader
{
    public static BitmapImage? LoadFrozen(string? path, int decodePixelWidth = 0, bool ignoreImageCache = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }
            if (ignoreImageCache)
            {
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}