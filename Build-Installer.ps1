param(
    [string]$Version = "2.0.5",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$InnoPath = ""
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "=== UEModManager v$Version installer build ($Configuration) ===" -ForegroundColor Green

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)

if ($InnoPath) {
    $candidate = $InnoPath
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $candidate = Join-Path $candidate "ISCC.exe"
    }
    if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Specified Inno Setup compiler was not found: $candidate"
    }
    if ([System.IO.Path]::GetFileName($candidate) -ne "ISCC.exe") {
        throw "InnoPath must point to ISCC.exe, not Compil32.exe or another executable."
    }
    $innoPath = (Resolve-Path -LiteralPath $candidate).Path
} else {
    $innoCandidates = @(
        (Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"),
        (Join-Path $programFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ }

    $innoPath = $innoCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (!$innoPath) {
    throw "Inno Setup 6 command-line compiler (ISCC.exe) was not found. Install Inno Setup 6 or rerun with -InnoPath."
}
Write-Host "Inno Setup: $innoPath" -ForegroundColor Green

$projectPath = Join-Path $PSScriptRoot "UEModManager\UEModManager.csproj"
$exePath = Join-Path $PSScriptRoot "UEModManager\bin\$Configuration\net8.0-windows\UEModManager.exe"
$issPath = Join-Path $PSScriptRoot "Setup\UEModManager.iss"
$outputDirectory = Join-Path $PSScriptRoot "installer_output"
$outputBaseFilename = "UEModManager_v${Version}_Setup"
$installer = Join-Path $outputDirectory "$outputBaseFilename.exe"

Write-Host ""
Write-Host "Building $Configuration project..." -ForegroundColor Yellow
$buildResult = & dotnet build $projectPath --configuration $Configuration --nologo --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: $Configuration build failed:" -ForegroundColor Red
    Write-Host $buildResult
    exit 1
}
Write-Host "$Configuration build succeeded." -ForegroundColor Green

if (!(Test-Path $exePath)) {
    throw "Build output exe not found: $exePath"
}
Write-Host "Main exe: $exePath" -ForegroundColor Green

if (!(Test-Path $issPath)) {
    throw "Inno Setup script not found: $issPath"
}
Write-Host "Setup script: $issPath" -ForegroundColor Green

$wizardRawImage = Join-Path $PSScriptRoot "Setup\wizard-images\banner_raw.png"
if (Test-Path $wizardRawImage) {
    Write-Host "Converting wizard images..." -ForegroundColor Yellow

    $python = Get-Command python -ErrorAction SilentlyContinue
    if (!$python) {
        throw "Python was not found in PATH. Install Python and Pillow, then run this script again."
    }

    $converter = Join-Path $PSScriptRoot "Setup\convert_wizard_images.py"
    if (!(Test-Path $converter)) {
        throw "Wizard image converter not found: $converter"
    }

    $convertResult = & $python.Source $converter 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Wizard image conversion failed:" -ForegroundColor Red
        Write-Host $convertResult
        exit 1
    }
    Write-Host "Wizard images converted." -ForegroundColor Green
}

if (!(Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

Write-Host ""
Write-Host "Compiling installer..." -ForegroundColor Yellow
$sourceDirForIss = "..\UEModManager\bin\$Configuration\net8.0-windows"
$innoArgs = @(
    "/DMyAppVersion=$Version",
    "/DMyAppDisplayVer=v$Version",
    "/DMyOutputBaseFilename=$outputBaseFilename",
    "/DSourceDir=$sourceDirForIss",
    $issPath
)
$compileResult = & $innoPath @innoArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Inno Setup failed:" -ForegroundColor Red
    Write-Host $compileResult
    exit 1
}

if (!(Test-Path $installer)) {
    throw "Output installer not found: $installer"
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
