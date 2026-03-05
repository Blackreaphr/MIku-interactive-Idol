param(
    [string]$SandboxDir = "sandbox\MateEngineX",
    [string]$VrmPath = "artifacts\yyb-miku\yyb-miku-export.vrm",
    [string]$RuntimeDataDir = "$env:USERPROFILE\AppData\LocalLow\Shinymoon\MateEngineX"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedSandbox = Join-Path $repoRoot $SandboxDir
$sandboxExe = Join-Path $resolvedSandbox "MateEngineX.exe"
$resolvedVrmPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $VrmPath))
$resolvedRuntimeDataDir = [System.IO.Path]::GetFullPath($RuntimeDataDir)
$backupRoot = Join-Path $repoRoot "sandbox\AppDataBackups"
$sessionRoot = Join-Path $repoRoot "sandbox\ImportSession"
$stagedImportDir = Join-Path $resolvedSandbox "Imports"

if (!(Test-Path $sandboxExe)) {
    throw "Sandbox executable not found at '$sandboxExe'. Run scripts\sync-mateengine-sandbox.ps1 first."
}

if (!(Test-Path $resolvedVrmPath)) {
    throw "VRM artifact not found at '$resolvedVrmPath'. Run scripts\run-yyb-pipeline.ps1 export-vrm first."
}

$sandboxExePath = [System.IO.Path]::GetFullPath($sandboxExe)

function Get-MateEngineProcesses {
    Get-CimInstance Win32_Process -Filter "Name='MateEngineX.exe'" |
        Where-Object { $_.ExecutablePath }
}

function Stop-SandboxProcesses {
    $sandboxProcesses = @(Get-MateEngineProcesses | Where-Object {
        [string]::Equals(
            [System.IO.Path]::GetFullPath($_.ExecutablePath),
            $sandboxExePath,
            [System.StringComparison]::OrdinalIgnoreCase
        )
    })

    foreach ($processInfo in $sandboxProcesses) {
        $process = Get-Process -Id $processInfo.ProcessId -ErrorAction SilentlyContinue
        if ($process -and $process.MainWindowHandle -ne 0) {
            $null = $process.CloseMainWindow()
        }
    }

    if ($sandboxProcesses.Count -gt 0) {
        Start-Sleep -Seconds 3
    }

    $remainingProcesses = @(Get-MateEngineProcesses | Where-Object {
        [string]::Equals(
            [System.IO.Path]::GetFullPath($_.ExecutablePath),
            $sandboxExePath,
            [System.StringComparison]::OrdinalIgnoreCase
        )
    })

    foreach ($processInfo in $remainingProcesses) {
        Stop-Process -Id $processInfo.ProcessId -ErrorAction Stop
    }
}

$runningProcesses = @(Get-MateEngineProcesses)
$nonSandboxProcesses = @($runningProcesses | Where-Object {
    -not [string]::Equals(
        [System.IO.Path]::GetFullPath($_.ExecutablePath),
        $sandboxExePath,
        [System.StringComparison]::OrdinalIgnoreCase
    )
})

if ($nonSandboxProcesses.Count -gt 0) {
    $processPaths = $nonSandboxProcesses | Select-Object -ExpandProperty ExecutablePath
    throw "Refusing to prepare an import session while a non-sandbox MateEngine instance is running: $($processPaths -join ', ')"
}

Stop-SandboxProcesses

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stagedImportDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupPath = Join-Path $backupRoot "MateEngineX-$timestamp"
$sessionPath = Join-Path $sessionRoot "session-$timestamp.json"
$stagedImportPath = Join-Path $stagedImportDir ([System.IO.Path]::GetFileName($resolvedVrmPath))

if (Test-Path $resolvedRuntimeDataDir) {
    Copy-Item -Path $resolvedRuntimeDataDir -Destination $backupPath -Recurse -Force
}

Copy-Item -Path $resolvedVrmPath -Destination $stagedImportPath -Force
Set-Clipboard -Value $stagedImportPath

$session = [ordered]@{
    createdAt = (Get-Date).ToString("o")
    runtimeDataDir = $resolvedRuntimeDataDir
    backupPath = if (Test-Path $backupPath) { $backupPath } else { $null }
    sourceVrmPath = $resolvedVrmPath
    stagedVrmPath = $stagedImportPath
    sandboxExe = $sandboxExePath
}

$session | ConvertTo-Json -Depth 5 | Set-Content -Path $sessionPath -Encoding UTF8

& (Join-Path $PSScriptRoot "start-mateengine-sandbox.ps1") -SandboxDir $SandboxDir

Write-Host "Prepared MateEngine sandbox import session."
Write-Host "Backup: $($session.backupPath)"
Write-Host "Staged VRM: $stagedImportPath"
Write-Host "The staged VRM path has been copied to the clipboard."
