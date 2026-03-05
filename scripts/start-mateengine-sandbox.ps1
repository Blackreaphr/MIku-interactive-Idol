param(
    [string]$SandboxDir = "sandbox\MateEngineX"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedSandbox = Join-Path $repoRoot $SandboxDir
$sandboxExe = Join-Path $resolvedSandbox "MateEngineX.exe"

if (!(Test-Path $sandboxExe)) {
    throw "Sandbox executable not found at '$sandboxExe'. Run scripts\\sync-mateengine-sandbox.ps1 first."
}

$sandboxExePath = [System.IO.Path]::GetFullPath($sandboxExe)

function Get-SandboxProcesses {
    Get-CimInstance Win32_Process -Filter "Name='MateEngineX.exe'" |
        Where-Object {
            $_.ExecutablePath -and
            [string]::Equals(
                [System.IO.Path]::GetFullPath($_.ExecutablePath),
                $sandboxExePath,
                [System.StringComparison]::OrdinalIgnoreCase
            )
        }
}

$existingProcesses = @(Get-SandboxProcesses)

if ($existingProcesses.Count -gt 0) {
    foreach ($processInfo in $existingProcesses) {
        $process = Get-Process -Id $processInfo.ProcessId -ErrorAction SilentlyContinue
        if ($process -and $process.MainWindowHandle -ne 0) {
            $null = $process.CloseMainWindow()
        }
    }

    Start-Sleep -Seconds 3

    $remainingProcesses = @(Get-SandboxProcesses)
    foreach ($processInfo in $remainingProcesses) {
        Stop-Process -Id $processInfo.ProcessId -ErrorAction Stop
    }

    if ($remainingProcesses.Count -gt 0) {
        Start-Sleep -Seconds 1
    }
}

Start-Process -FilePath $sandboxExe -WorkingDirectory $resolvedSandbox
Write-Host "Started MateEngine sandbox from $sandboxExePath"
