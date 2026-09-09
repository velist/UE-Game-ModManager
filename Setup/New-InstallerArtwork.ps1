# Rebuild the checked-in wizard PNGs using Windows' built-in drawing library.
# No Python, web service, or additional graphics package is required.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    # GDI+ is part of .NET Framework on Windows; use that runtime consistently.
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw "Artwork generation failed (exit $LASTEXITCODE)." }
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

    static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
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
            // Inno Setup preserves a 497:360 aspect ratio for full backgrounds.
            using (var bitmap = new Bitmap(1491, 1080, PixelFormat.Format32bppArgb))
            using (var g = Canvas(bitmap))
            using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 1491, 1080),
                Hex("#1a1e24"), Hex("#15171b"), 40f))
            {
                g.FillRectangle(fill, 0, 0, bitmap.Width, bitmap.Height);
                bitmap.Save(Path.Combine(outputDirectory, "installer-background.png"), ImageFormat.Png);
            }

            // The welcome/completion illustration uses Inno's fixed 164:314 ratio.
            using (var bitmap = new Bitmap(492, 942, PixelFormat.Format32bppArgb))
            using (var g = Canvas(bitmap))
            using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 492, 942),
                Hex("#122a34"), Hex("#101a21"), 90f))
            {
                g.FillRectangle(fill, 0, 0, bitmap.Width, bitmap.Height);
                using (var line = new Pen(Color.FromArgb(32, 67, 170, 197), 1.5f))
                {
                    g.DrawLines(line, new PointF[] {
                        new PointF(255, -50), new PointF(530, 220), new PointF(530, 460)
                    });
                    g.DrawLines(line, new PointF[] {
                        new PointF(300, -50), new PointF(530, 177), new PointF(530, 460)
                    });
                    g.DrawLines(line, new PointF[] {
                        new PointF(-60, 660), new PointF(226, 942), new PointF(420, 942)
                    });
                }
                using (var accent = new SolidBrush(Hex("#46d0e7")))
                    g.FillRectangle(accent, 54, 54, 46, 5);
                Text(g, "A I C H A N", 20, Hex("#91b7c3"),
                    new RectangleF(54, 80, 384, 32), false, false);

                using (var halo = new GraphicsPath())
                {
                    halo.AddEllipse(32, 206, 428, 428);
                    using (var glow = new PathGradientBrush(halo))
                    {
                        glow.CenterColor = Color.FromArgb(35, 0, 192, 230);
                        glow.SurroundColors = new Color[] { Color.FromArgb(0, 0, 192, 230) };
                        g.FillPath(glow, halo);
                    }
                }
                using (var back = Rounded(110, 293, 248, 248, 34))
                using (var backPen = new Pen(Color.FromArgb(80, 65, 132, 151), 2f))
                    g.DrawPath(backPen, back);
                using (var plate = Rounded(128, 311, 248, 248, 34))
                using (var plateBrush = new SolidBrush(Hex("#142831")))
                using (var platePen = new Pen(Color.FromArgb(100, 73, 155, 177), 2f))
                {
                    g.FillPath(plateBrush, plate);
                    g.DrawPath(platePen, plate);
                }
                FitImage(g, brand, new RectangleF(158, 342, 184, 184));
                Text(g, "\u7231\u9171", 43, Hex("#e7f6fa"),
                    new RectangleF(48, 603, 396, 62), true, true);
                Text(g, "MOD MANAGER", 22, Hex("#91b7c3"),
                    new RectangleF(48, 674, 396, 36), true, false);
                Text(g, "modmanger.com", 22, Hex("#6f94a1"),
                    new RectangleF(48, 853, 396, 40), true, false);
                bitmap.Save(Path.Combine(outputDirectory, "installer-sidebar.png"), ImageFormat.Png);
            }

            using (var bitmap = new Bitmap(144, 144, PixelFormat.Format32bppArgb))
            using (var g = Canvas(bitmap))
            {
                g.Clear(Color.Transparent);
                FitImage(g, brand, new RectangleF(14, 14, 116, 116));
                bitmap.Save(Path.Combine(outputDirectory, "installer-mark.png"), ImageFormat.Png);
            }
        }
    }
}
'@

$brandPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'website\assets\brand.png'
$artworkOutput = Join-Path $PSScriptRoot 'wizard-images'
[InstallerArtwork]::Generate($brandPath, $artworkOutput)
Get-ChildItem -LiteralPath $artworkOutput -Filter 'installer-*.png' | Select-Object Name, Length
