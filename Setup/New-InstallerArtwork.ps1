# 使用 Windows 自带 GDI+ 生成安装向导图片，无需额外依赖。
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    # 固定使用 Windows .NET Framework 中的 GDI+。
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw "安装图片生成失败（退出码 $LASTEXITCODE）。" }
    return
}
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public static class InstallerArtwork
{
    static Color Hex(string value) { return ColorTranslator.FromHtml(value); }

    static Graphics Canvas(Bitmap bitmap)
    {
        var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    // 青色仅作为低透明度的局部径向光晕。
    static void Surface(Graphics g, int width, int height, int opacity)
    {
        g.Clear(Hex("#0f0f11"));
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(-width * 0.45f, -height * 0.3f, width * 1.2f, height * 0.9f);
            using (var glow = new PathGradientBrush(path))
            {
                glow.CenterColor = Color.FromArgb(opacity, 6, 182, 212);
                glow.SurroundColors = new Color[] { Color.FromArgb(0, 6, 182, 212) };
                g.FillPath(glow, path);
            }
        }
    }

    static void Text(Graphics g, string text, float size, Color color,
        RectangleF bounds, bool centered, bool bold)
    {
        using (var font = new Font("Microsoft YaHei UI", size,
            bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat())
        {
            format.Alignment = centered ? StringAlignment.Center : StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString(text, font, brush, bounds, format);
        }
    }

    static void FitImage(Graphics g, Image source, RectangleF bounds)
    {
        float scale = Math.Min(bounds.Width / source.Width, bounds.Height / source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        g.DrawImage(source, bounds.X + (bounds.Width - w) / 2,
            bounds.Y + (bounds.Height - h) / 2, w, h);
    }

    public static void Generate(string brandPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using (var brand = Image.FromFile(brandPath))
        {
            // 保持 Inno Setup 的图片比例，三倍分辨率适配高 DPI。
            using (var bitmap = new Bitmap(1491, 1080, PixelFormat.Format32bppArgb))
            using (var g = Canvas(bitmap))
            {
                Surface(g, bitmap.Width, bitmap.Height, 5);
                bitmap.Save(Path.Combine(outputDirectory, "installer-background.png"), ImageFormat.Png);
            }

            // 保持 Inno Setup 的图片比例，三倍分辨率适配高 DPI。
            using (var bitmap = new Bitmap(492, 942, PixelFormat.Format32bppArgb))
            using (var g = Canvas(bitmap))
            {
                Surface(g, bitmap.Width, bitmap.Height, 9);
                FitImage(g, brand, new RectangleF(54, 78, 114, 114));
                Text(g, "爱酱 MOD 管理器", 30, Hex("#f4f4f5"),
                    new RectangleF(54, 222, 390, 54), false, true);
                using (var accent = new SolidBrush(Hex("#06b6d4")))
                    g.FillRectangle(accent, 54, 309, 48, 3);
                using (var line = new Pen(Hex("#27272a"), 3f))
                    g.DrawLine(line, 54, 813, 438, 813);
                Text(g, "modmanger.com", 27, Hex("#8b8b94"),
                    new RectangleF(54, 843, 384, 42), false, false);
                bitmap.Save(Path.Combine(outputDirectory, "installer-sidebar.png"), ImageFormat.Png);
            }

            using (var bitmap = new Bitmap(144, 144, PixelFormat.Format32bppArgb))
            using (var g = Canvas(bitmap))
            {
                g.Clear(Color.Transparent);
                FitImage(g, brand, new RectangleF(27, 27, 90, 90));
                bitmap.Save(Path.Combine(outputDirectory, "installer-mark.png"), ImageFormat.Png);
            }
        }
    }
}
'@

$brandPath = Join-Path $PSScriptRoot 'wizard-images\aichan-logo.png'
$artworkOutput = Join-Path $PSScriptRoot 'wizard-images'
[InstallerArtwork]::Generate($brandPath, $artworkOutput)
Get-ChildItem -LiteralPath $artworkOutput -Filter 'installer-*.png' | Select-Object Name, Length
