Write-Host "=== UEModManager v2.0.3 beta installer build ===" -ForegroundColor Green

$localInnoDir = "D:\" + [char]0x5B89 + [char]0x88C5
$innoCandidates = @(
    (Join-Path $localInnoDir "ISCC.exe"),
    (Join-Path $localInnoDir "Compil32.exe"),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)

$innoPath = $innoCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$innoPath) {
    Write-Host "ERROR: Inno Setup 6 compiler was not found." -ForegroundColor Red
    Write-Host "Install Inno Setup 6, then run this script again."
    exit 1
}
Write-Host "Inno Setup: $innoPath" -ForegroundColor Green

Write-Host ""
Write-Host "Building Release project..." -ForegroundColor Yellow
$buildResult = & dotnet build "UEModManager\UEModManager.csproj" --configuration Release --nologo --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Release build failed:" -ForegroundColor Red
    Write-Host $buildResult
    exit 1
}
Write-Host "Release build succeeded." -ForegroundColor Green

$exePath = "UEModManager\bin\Release\net8.0-windows\UEModManager.exe"
if (!(Test-Path $exePath)) {
    Write-Host "ERROR: Release exe not found: $exePath" -ForegroundColor Red
    exit 1
}
Write-Host "Main exe: $exePath" -ForegroundColor Green

$issPath = "Setup\UEModManager.iss"
if (!(Test-Path $issPath)) {
    Write-Host "ERROR: Inno Setup script not found: $issPath" -ForegroundColor Red
    exit 1
}
Write-Host "Setup script: $issPath" -ForegroundColor Green

if (Test-Path "Setup\wizard-images\banner_raw.png") {
    Write-Host "Converting wizard images..." -ForegroundColor Yellow
    $convertResult = & python -c "from PIL import Image; from pathlib import Path; p=Path('Setup/wizard-images'); items=[('banner_raw.png','banner.bmp',(384,772)),('step1_import_raw.png','step1.bmp',(1024,614)),('step2_deploy_raw.png','step2.bmp',(1024,614)),('step3_check_raw.png','step3.bmp',(1024,614))]; [Image.open(p/s).convert('RGB').resize(sz, Image.Resampling.LANCZOS).save(p/d) for s,d,sz in items]; logo=Image.open(p/'aichan-logo.png').convert('RGBA'); canvas=Image.new('RGBA',(110,110),(250,250,250,255)); logo=logo.crop(logo.getbbox()) if logo.getbbox() else logo; logo.thumbnail((86,86), Image.Resampling.LANCZOS); canvas.alpha_composite(logo,((110-logo.width)//2,(110-logo.height)//2)); canvas.convert('RGB').save(p/'small.bmp')" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Wizard image conversion failed:" -ForegroundColor Red
        Write-Host $convertResult
        exit 1
    }
    Write-Host "Wizard images converted." -ForegroundColor Green
}

if (!(Test-Path "installer_output")) {
    New-Item -ItemType Directory -Path "installer_output" | Out-Null
}

Write-Host ""
Write-Host "Compiling installer..." -ForegroundColor Yellow
$process = Start-Process -FilePath $innoPath -ArgumentList $issPath -Wait -PassThru -NoNewWindow
if ($process.ExitCode -ne 0) {
    Write-Host "ERROR: Inno Setup failed with exit code $($process.ExitCode)." -ForegroundColor Red
    exit 1
}

$installer = "installer_output\UEModManager_v2.0.3-beta_Setup.exe"
if (!(Test-Path $installer)) {
    Write-Host "ERROR: Output installer not found: $installer" -ForegroundColor Red
    exit 1
}

$size = [math]::Round((Get-Item $installer).Length / 1MB, 2)
Write-Host ""
Write-Host "Installer build succeeded." -ForegroundColor Green
Write-Host "File: $installer" -ForegroundColor Cyan
Write-Host "Size: $size MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "Features:" -ForegroundColor Yellow
Write-Host "  - Chinese Inno Setup wizard UI"
Write-Host "  - Custom banner and product logo"
Write-Host "  - License, intro, and after-install information pages"
Write-Host "  - Three custom illustrated guide pages"
Write-Host "  - One-click migration script included"
