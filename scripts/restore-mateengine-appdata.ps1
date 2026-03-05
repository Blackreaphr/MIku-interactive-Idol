param(
    [string]$BackupPath = "",
    [string]$RuntimeDataDir = "$env:USERPROFILE\AppData\LocalLow\Shinymoon\MateEngineX"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$backupRoot = Join-Path $repoRoot "sandbox\AppDataBackups"
$resolvedRuntimeDataDir = [System.IO.Path]::GetFullPath($RuntimeDataDir)
$runtimeParentDir = Split-Path -Parent $resolvedRuntimeDataDir

function Get-MateEngineProcesses {
    Get-CimInstance Win32_Process -Filter "Name='MateEngineX.exe'" |
        Where-Object { $_.ExecutablePath }
}

$runningProcesses = @(Get-MateEngineProcesses)
if ($runningProcesses.Count -gt 0) {
    $processPaths = $runningProcesses | Select-Object -ExpandProperty ExecutablePath
    throw "Close all MateEngine instances before restoring app data: $($processPaths -join ', ')"
}

if ($BackupPath) {
    $resolvedBackupPath = [System.IO.Path]::GetFullPath($BackupPath)
} else {
    $latestBackup = Get-ChildItem $backupRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latestBackup) {
        throw "No backup folders were found in '$backupRoot'."
    }
    $resolvedBackupPath = $latestBackup.FullName
}

if (!(Test-Path $resolvedBackupPath)) {
    throw "Backup folder not found at '$resolvedBackupPath'."
}

New-Item -ItemType Directory -Force -Path $runtimeParentDir | Out-Null

if (Test-Path $resolvedRuntimeDataDir) {
    $archivePath = Join-Path $backupRoot ("MateEngineX-before-restore-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    Move-Item -Path $resolvedRuntimeDataDir -Destination $archivePath
}

Copy-Item -Path $resolvedBackupPath -Destination $resolvedRuntimeDataDir -Recurse -Force
Write-Host "Restored MateEngine app data from $resolvedBackupPath to $resolvedRuntimeDataDir"
