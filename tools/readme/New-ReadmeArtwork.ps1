# Generate the README banner from the existing product mark and UI palette.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw 'README artwork generation failed.' }
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

public static class ReadmeArtwork
{
    static Color Hex(string value) { return ColorTranslator.FromHtml(value); }
    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x+w-d, y, d, d, 270, 90);
        p.AddArc(x+w-d, y+h-d, d, d, 0, 90); p.AddArc(x, y+h-d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    static void Text(Graphics g, string text, float size, string color, float x, float y, bool bold)
    {
        using (var font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Hex(color)))
            g.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
    }
    public static void Generate(string brandPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        using (var bitmap = new Bitmap(1600, 520, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bitmap))
        using (var brand = Image.FromFile(brandPath))
        using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 1600, 520), Hex("#15171b"), Hex("#102a34"), 0f))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.FillRectangle(fill, 0, 0, 1600, 520);
            using (var line = new Pen(Color.FromArgb(38, 78, 178, 204), 1.5f))
            {
                g.DrawLines(line, new PointF[] {new PointF(1260,-60),new PointF(1650,330),new PointF(1650,520)});
                g.DrawLines(line, new PointF[] {new PointF(1320,-60),new PointF(1650,270),new PointF(1650,520)});
                g.DrawLine(line, 76, 377, 1000, 377);
            }
            using (var accent = new SolidBrush(Hex("#46d0e7"))) g.FillRectangle(accent,76,75,46,5);
            Text(g,"A I C H A N   /   M O D   M A N A G E R",20,"#94b5c0",76,101,false);
            Text(g,"\u7231\u9171 MOD \u7ba1\u7406\u5668",68,"#edf6f9",72,172,true);
            Text(g,"\u628a MOD \u6574\u7406\u597d\uff0c\u628a\u65f6\u95f4\u7559\u7ed9\u6e38\u620f\u3002",29,"#abbac4",76,282,false);
            Text(g,"\u591a\u5f15\u64ce\u9002\u914d",23,"#67e8f9",76,421,false);
            Text(g,"\u672c\u5730\u4f18\u5148",23,"#b9c7cf",312,421,false);
            Text(g,"\u79bb\u7ebf\u53ef\u7528",23,"#b9c7cf",500,421,false);
            Text(g,"v2.1.0",23,"#b9c7cf",874,421,false);
            using (var p = Round(1189,108,250,250,34))
            using (var pen = new Pen(Color.FromArgb(75,75,151,171),2f)) g.DrawPath(pen,p);
            using (var p = Round(1214,133,250,250,34))
            using (var b = new SolidBrush(Hex("#142c35")))
            using (var pen = new Pen(Color.FromArgb(120,75,155,177),2f))
            { g.FillPath(b,p); g.DrawPath(pen,p); }
            float scale = Math.Min(180f/brand.Width,180f/brand.Height);
            float w=brand.Width*scale,h=brand.Height*scale;
            g.DrawImage(brand,1339-w/2,258-h/2,w,h);
            Text(g,"WINDOWS  /  x64",19,"#7fa3b1",1238,421,false);
            bitmap.Save(outputPath,ImageFormat.Png);
        }
    }
}
'@
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$brandPath = Join-Path $repositoryRoot 'website\assets\brand.png'
$outputPath = Join-Path $repositoryRoot 'docs\assets\readme\hero.png'
[ReadmeArtwork]::Generate($brandPath, $outputPath)
Write-Output $outputPath
