param(
    [string]$Version = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$InnoPath = "",
    [string]$PublishDirectory = "",
    [string]$OutputDirectory = ""
)

# Keep this script ASCII so Windows PowerShell 5.1 can read it without a BOM.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$versionSource = Join-Path $PSScriptRoot "UEModManager\Properties\VersionAssemblyInfo.cs"
if (!$Version) {
    $versionMatch = [regex]::Match((Get-Content -LiteralPath $versionSource -Raw),
        'AssemblyInformationalVersion\("([0-9]+\.[0-9]+\.[0-9]+)"\)')
    if (!$versionMatch.Success) { throw "Cannot read the release version from $versionSource" }
    $Version = $versionMatch.Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version must contain three or four numeric components."
}
Write-Host "UEModManager v$Version installer build ($Configuration, win-x64)" -ForegroundColor Green

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
if ($InnoPath) {
    $candidate = [System.IO.Path]::GetFullPath($InnoPath)
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $candidate = Join-Path $candidate "ISCC.exe"
    } elseif ([System.IO.Path]::GetFileName($candidate) -ieq "Compil32.exe") {
        # The GUI and command-line compilers are shipped in the same directory.
        $candidate = Join-Path (Split-Path -Parent $candidate) "ISCC.exe"
    }
    if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Inno Setup command-line compiler was not found alongside the specified path: $candidate"
    }
    if ([System.IO.Path]::GetFileName($candidate) -ine "ISCC.exe") {
        throw "InnoPath must be an Inno Setup directory, Compil32.exe, or ISCC.exe."
    }
    $compilerPath = (Resolve-Path -LiteralPath $candidate).Path
} else {
    $innoCandidates = @(
        (Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"),
        (Join-Path $programFiles "Inno Setup 6\ISCC.exe")
    )
    $pathCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($pathCompiler) { $innoCandidates += $pathCompiler.Source }
    $compilerPath = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}
if (!$compilerPath) {
    throw "Specify the Inno Setup 6.7+ directory, Compil32.exe, or ISCC.exe with -InnoPath."
}
Write-Host "Inno Setup: $compilerPath" -ForegroundColor Green

$projectPath = Join-Path $PSScriptRoot "UEModManager\UEModManager.csproj"
$issPath = Join-Path $PSScriptRoot "Setup\UEModManager.iss"
if (!(Test-Path -LiteralPath $issPath)) { throw "Inno Setup script not found: $issPath" }
foreach ($imageName in @('installer-background.png', 'installer-sidebar.png', 'installer-mark.png')) {
    $imagePath = Join-Path $PSScriptRoot "Setup\wizard-images\$imageName"
    if (!(Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "Missing wizard image: $imagePath. Run Setup\New-InstallerArtwork.ps1 first."
    }
}

if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "installer_output" }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$outputBaseFilename = "UEModManager_v${Version}_Setup"
$installer = Join-Path $OutputDirectory "$outputBaseFilename.exe"

# A new publish directory prevents old packages and user data from a previous
# build from entering the installer. Explicit publish directories are retained.
$keepPublishDirectory = ![string]::IsNullOrWhiteSpace($PublishDirectory)
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "UEModManager-Installer"
if ($keepPublishDirectory) {
    $publishDir = [System.IO.Path]::GetFullPath($PublishDirectory)
    if (Test-Path -LiteralPath $publishDir) {
        throw "PublishDirectory must be a new directory: $publishDir"
    }
} else {
    $publishDir = Join-Path $stagingRoot ([Guid]::NewGuid().ToString("N"))
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
$exePath = Join-Path $publishDir "UEModManager.exe"

Write-Host "Publishing to a fresh directory: $publishDir" -ForegroundColor Yellow
$publishArgs = @(
    'publish', $projectPath, '--configuration', $Configuration,
    '--runtime', 'win-x64', '--no-self-contained', '--output', $publishDir,
    '--nologo', '--verbosity', 'minimal', '-p:DebugType=None', '-p:DebugSymbols=false'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "$Configuration publish failed (exit $LASTEXITCODE)." }
if (!(Test-Path -LiteralPath $exePath)) { throw "Published executable not found: $exePath" }

$builtVersion = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
$expectedVersion = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
if ($builtVersion -ne $expectedVersion) {
    throw "Version mismatch: exe is $builtVersion, expected $expectedVersion. Check $versionSource"
}
Write-Host "Exe version verified: $builtVersion" -ForegroundColor Green

# Keep this independent from the exclusions in the .iss file. Fail visibly if a
# project edit starts publishing credentials or personal runtime data.
$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File -Force)
$secretHits = @($publishedFiles | Where-Object {
    $_.Name -match '\.(env|enc|pfx|p12|key)$' -or $_.Name -like '.env*' -or
    $_.Name -eq 'secrets.json' -or $_.Name -like 'appsettings.*.local.json'
})
if ($secretHits.Count -gt 0) {
    throw "Packaging aborted: secret-class files found: $($secretHits.FullName -join ', ')"
}
$privateData = @(
    @('Data', 'Backups', 'UserData', 'config.json', 'local.db', 'console.log') |
        ForEach-Object { Join-Path $publishDir $_ } |
        Where-Object { Test-Path -LiteralPath $_ }
)
if ($privateData.Count -gt 0) {
    throw "Packaging aborted: personal runtime data found: $($privateData -join ', ')"
}
Write-Host "Publish scan passed: no credentials or personal runtime data." -ForegroundColor Green

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$innoArgs = @(
    "/DMyAppVersion=$Version", "/DMyAppDisplayVer=v$Version",
    "/DMyOutputBaseFilename=$outputBaseFilename", "/DSourceDir=$publishDir",
    "/O$OutputDirectory", $issPath
)
Write-Host "Compiling the installer..." -ForegroundColor Yellow
& $compilerPath @innoArgs
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit $LASTEXITCODE)." }
if (!(Test-Path -LiteralPath $installer)) { throw "Installer output not found: $installer" }

$installerSize = [math]::Round((Get-Item -LiteralPath $installer).Length / 1MB, 2)
Write-Host "Installer: $installer ($installerSize MiB)" -ForegroundColor Cyan
Write-Host "Runtime required: Microsoft .NET 8 Desktop Runtime (x64)." -ForegroundColor Cyan
if ($keepPublishDirectory) {
    Write-Host "Release files retained: $publishDir" -ForegroundColor Cyan
} else {
    # Delete only this invocation's unique directory, after checking its boundary.
    $resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot).TrimEnd('\') + '\'
    $resolvedPublishDir = (Resolve-Path -LiteralPath $publishDir).Path
    if (!$resolvedPublishDir.StartsWith($resolvedStagingRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Staging cleanup target is outside the installer staging root: $resolvedPublishDir"
    }
    Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
}
