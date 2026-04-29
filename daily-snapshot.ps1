<# daily-snapshot.ps1 - Daily git snapshot script #>
<# Triggered by Windows Task Scheduler at 00:00 daily #>

$ErrorActionPreference = "Stop"
$RepoPath = "D:\modmangerpd\UE-Game-ModManager"
$LogFile = Join-Path $RepoPath "snapshot.log"
$Date = Get-Date -Format "yyyy-MM-dd"
$BranchName = "snapshot/$Date"

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$timestamp] $Message"
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    Write-Host $line
}

Set-Location $RepoPath
Write-Log "=== Daily snapshot started ==="

$status = git status --porcelain
if ($status) {
    Write-Log "Changes detected, committing..."
    git add -A
    git commit -m "snapshot: $Date daily auto snapshot"
    Write-Log "Commit done"
} else {
    Write-Log "No changes, skipping commit"
}

$branchExists = git branch --list $BranchName
if ($branchExists) {
    Write-Log "Branch $BranchName already exists, skipping"
} else {
    git branch $BranchName
    Write-Log "Created snapshot branch: $BranchName"
}

$genScript = Join-Path $RepoPath "generate-dashboard.ps1"
if (Test-Path $genScript) {
    Write-Log "Updating dashboard data..."
    & $genScript
    Write-Log "Dashboard data updated"
}

Write-Log "=== Daily snapshot completed ==="
