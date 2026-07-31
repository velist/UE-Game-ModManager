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
# --no-incremental is deliberate: bumping only <AssemblyVersion>/<FileVersion> in the
# csproj leaves every source file untouched, so an incremental build happily reuses the
# previous assembly. The installer then ships an exe stamped with the OLD version while
# its filename and wizard say the new one -- and the update check would keep telling
# users on the newest build that an update is available.
$buildResult = & dotnet build $projectPath --configuration $Configuration --no-incremental --nologo --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: $Configuration build failed:" -ForegroundColor Red
    Write-Host $buildResult
    exit 1
}
Write-Host "$Configuration build succeeded." -ForegroundColor Green

if (!(Test-Path $exePath)) {
    throw "Build output exe not found: $exePath"
}

# Guard the mismatch described above: the exe must carry the version we were asked to
# ship. Catching it here beats discovering it after the installer is published.
$builtVersion = (Get-Item $exePath).VersionInfo.FileVersion
$expectedVersion = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
if ($builtVersion -ne $expectedVersion) {
    throw "Version mismatch: built exe reports $builtVersion but -Version asked for $Version " +
          "(expected FileVersion $expectedVersion). Update <AssemblyVersion>/<FileVersion> in " +
          "UEModManager\UEModManager.csproj to match."
}
Write-Host "Exe version verified: $builtVersion" -ForegroundColor Green
Write-Host "Main exe: $exePath" -ForegroundColor Green

# ---- [security] scan the publish directory before packaging -----------------
# This script hands the raw dotnet build output directory to ISCC (see
# $sourceDirForIss below); there is no staging copy. Anything a developer leaves
# in that directory ends up inside the installer.
# Setup\UEModManager.iss already excludes secret-class patterns, but that is a
# silent exclusion: the secret still sits in the publish directory, and any edit
# to the iss file re-exposes it. This is an independent hard gate so the problem
# becomes visible instead of being quietly worked around.
$publishDir = Split-Path $exePath -Parent

$secretHits = @(
    Get-ChildItem -LiteralPath $publishDir -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '\.(env|enc|pfx|p12|key)$' -or $_.Name -like '.env*' }
)
if ($secretHits.Count -gt 0) {
    Write-Host ""
    Write-Host "ERROR: secret-class files found in the publish directory. Packaging aborted." -ForegroundColor Red
    $secretHits | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    Write-Host "Move them out of the publish directory and retry. Secrets belong in Cloudflare Worker secrets only." -ForegroundColor Red
    exit 1
}

# Developer-private runtime data (own mod list, profiles, game paths, avatar).
# Already excluded by the iss file; not a build blocker, but the builder should know.
$privateData = @(
    @('Data', 'Backups', 'UserData', 'config.json') |
        ForEach-Object { Join-Path $publishDir $_ } |
        Where-Object { Test-Path -LiteralPath $_ }
)
if ($privateData.Count -gt 0) {
    Write-Host "NOTE: publish directory contains developer-private runtime data (excluded by the installer script):" -ForegroundColor Yellow
    $privateData | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
Write-Host "Publish dir scan passed: no secret-class files." -ForegroundColor Green

if (!(Test-Path $issPath)) {
    throw "Inno Setup script not found: $issPath"
}
Write-Host "Setup script: $issPath" -ForegroundColor Green

$wizardImageDir = Join-Path $PSScriptRoot "Setup\wizard-images"
$wizardRawImage = Join-Path $wizardImageDir "banner_raw.png"
if (Test-Path $wizardRawImage) {
    # Source-to-output mapping. Skip the whole step when every BMP is newer than its
    # source: these images change maybe once a year, while regenerating them costs
    # either a Python+Pillow dependency or the PowerShell path that distorts the logo.
    #
    # NOTE: keep this file ASCII-only. It has no BOM, so Windows PowerShell 5.1 decodes
    # it as the system ANSI codepage (GBK here); UTF-8 comments get mis-decoded and can
    # swallow following syntax characters, which shows up as bogus parser errors.
    $wizardImagePairs = @(
        @{ Raw = "banner_raw.png";       Bmp = "banner.bmp" },
        @{ Raw = "aichan-logo.png";      Bmp = "small.bmp"  },
        @{ Raw = "step1_import_raw.png"; Bmp = "step1.bmp"  },
        @{ Raw = "step2_deploy_raw.png"; Bmp = "step2.bmp"  },
        @{ Raw = "step3_check_raw.png";  Bmp = "step3.bmp"  }
    )

    $stale = @()
    foreach ($pair in $wizardImagePairs) {
        $raw = Join-Path $wizardImageDir $pair.Raw
        $bmp = Join-Path $wizardImageDir $pair.Bmp
        if (!(Test-Path $raw)) { continue }
        if (!(Test-Path $bmp) -or
            (Get-Item $bmp).LastWriteTimeUtc -lt (Get-Item $raw).LastWriteTimeUtc) {
            $stale += $pair.Bmp
        }
    }

    if ($stale.Count -eq 0) {
        Write-Host "Wizard images up to date (skipped conversion)." -ForegroundColor Green
    }
    else {
        Write-Host "Converting wizard images ($($stale -join ', '))..." -ForegroundColor Yellow

        # Prefer Python: it centres the logo on a white canvas, whereas the PowerShell
        # converter stretches it to 110x110 and distorts non-square logos. But
        # Python+Pillow is not a reasonable requirement for a build machine, so fall
        # back rather than fail -- a stretched small.bmp beats no installer at all.
        $converted = $false
        $python = Get-Command python -ErrorAction SilentlyContinue
        $pyConverter = Join-Path $PSScriptRoot "Setup\convert_wizard_images.py"
        if ($python -and (Test-Path $pyConverter)) {
            $convertResult = & $python.Source $pyConverter 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Wizard images converted (Python)." -ForegroundColor Green
                $converted = $true
            }
            else {
                Write-Host "Python converter failed, falling back to PowerShell:" -ForegroundColor Yellow
                Write-Host $convertResult
            }
        }

        if (!$converted) {
            $psConverter = Join-Path $PSScriptRoot "Setup\Convert-WizardImages.ps1"
            if (!(Test-Path $psConverter)) {
                throw "No usable wizard image converter found (tried Python and $psConverter)."
            }
            & $psConverter
            if ($LASTEXITCODE -ne 0) {
                throw "Wizard image conversion failed (PowerShell fallback)."
            }
            Write-Host "Wizard images converted (PowerShell fallback; small.bmp may be stretched)." -ForegroundColor Yellow
        }
    }
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
