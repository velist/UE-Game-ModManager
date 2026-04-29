<# setup-task.ps1 - Register Windows scheduled task for daily snapshot #>
<# Requires administrator privileges #>

$ErrorActionPreference = "Stop"

$TaskName = "UEModManager-DailySnapshot"
$RepoPath = "D:\modmangerpd\UE-Game-ModManager"
$ScriptPath = Join-Path $RepoPath "daily-snapshot.ps1"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevating to admin..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed old task: $TaskName" -ForegroundColor Yellow
}

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-ExecutionPolicy Bypass -NoProfile -File `"$ScriptPath`"" `
    -WorkingDirectory $RepoPath

$trigger = New-ScheduledTaskTrigger -Daily -At "00:00"

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType S4U -RunLevel Limited

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "UEModManager daily git snapshot - auto commit and create snapshot branch"

Write-Host ""
Write-Host "Scheduled task registered!" -ForegroundColor Green
Write-Host "  Task:    $TaskName" -ForegroundColor Cyan
Write-Host "  Trigger: Daily at 00:00" -ForegroundColor Cyan
Write-Host "  Script:  $ScriptPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Management commands:" -ForegroundColor Gray
Write-Host "  View:   Get-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Gray
Write-Host "  Run:    Start-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Gray
Write-Host "  Delete: Unregister-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Gray
