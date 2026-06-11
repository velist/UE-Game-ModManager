using System;
using System.IO;

namespace UEModManager.Infrastructure;

public static class ImportWarningMessages
{
    public const string UnsupportedArchiveTitle = "请先手动解压 RAR/7z";

    public const string UnsupportedArchiveMessage =
        "检测到 RAR/7z 压缩包。\n\n" +
        "当前版本仅保证 ZIP 压缩包或 MOD 文件稳定导入。RAR/7z 受用户电脑解压环境影响，可能出现解压失败或中文文件名乱码。\n\n" +
        "请先用 WinRAR/7-Zip 手动解压，再把解压后的文件夹或其中的 MOD 文件重新导入。";

    public static bool IsUnsupportedArchive(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".rar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase);
    }
}