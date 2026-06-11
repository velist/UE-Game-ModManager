# 把 wizard-images/*.png 转成 Inno Setup 用的 BMP（24 位）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File Convert-WizardImages.ps1

param(
  [string]$SrcDir = "$PSScriptRoot\wizard-images",
  [string]$DstDir = "$PSScriptRoot\wizard-images"
)

Add-Type -AssemblyName System.Drawing

function Convert-ToBmp {
  param(
    [string]$SrcPath,
    [string]$DstPath,
    [int]$W,
    [int]$H
  )
  if (!(Test-Path $SrcPath)) {
    Write-Host "  ! 跳过：找不到 $SrcPath" -ForegroundColor Yellow
    return
  }
  $src = [System.Drawing.Image]::FromFile((Resolve-Path $SrcPath))
  try {
    $bmp = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
      $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
      $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
      $g.Clear([System.Drawing.Color]::Black)
      $g.DrawImage($src, 0, 0, $W, $H)
    } finally {
      $g.Dispose()
    }
    $bmp.Save($DstPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
    Write-Host "  ✓ $([System.IO.Path]::GetFileName($DstPath)) ($W x $H)" -ForegroundColor Green
  } finally {
    $src.Dispose()
  }
}

Write-Host "正在转换安装向导图片为 BMP..." -ForegroundColor Cyan

# Inno Setup modern wizard 推荐尺寸（@2x 高分屏自适应）
Convert-ToBmp -SrcPath "$SrcDir\banner_raw.png"      -DstPath "$DstDir\banner.bmp"  -W 384 -H 772
Convert-ToBmp -SrcPath "$SrcDir\aichan-logo.png"     -DstPath "$DstDir\small.bmp"   -W 110 -H 110
Convert-ToBmp -SrcPath "$SrcDir\step1_import_raw.png" -DstPath "$DstDir\step1.bmp"   -W 1024 -H 614
Convert-ToBmp -SrcPath "$SrcDir\step2_deploy_raw.png" -DstPath "$DstDir\step2.bmp"   -W 1024 -H 614
Convert-ToBmp -SrcPath "$SrcDir\step3_check_raw.png"  -DstPath "$DstDir\step3.bmp"   -W 1024 -H 614

Write-Host "Done." -ForegroundColor Green
