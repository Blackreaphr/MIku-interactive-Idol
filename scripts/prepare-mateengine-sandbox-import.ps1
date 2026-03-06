param(
    [string]$SandboxDir = "sandbox\MateEngineX",
    [string]$VrmPath = "",
    [string]$RuntimeDataDir = "$env:USERPROFILE\AppData\LocalLow\Shinymoon\MateEngineX"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedSandbox = Join-Path $repoRoot $SandboxDir
$sandboxExe = Join-Path $resolvedSandbox "MateEngineX.exe"
$resolvedRuntimeDataDir = [System.IO.Path]::GetFullPath($RuntimeDataDir)
$settingsPath = Join-Path $resolvedRuntimeDataDir "settings.json"
$backupRoot = Join-Path $repoRoot "sandbox\AppDataBackups"
$sessionRoot = Join-Path $repoRoot "sandbox\ImportSession"
$stagedImportDir = Join-Path $resolvedSandbox "Imports"

function Resolve-ExistingPath {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $candidates = @()
    if ([System.IO.Path]::IsPathRooted($Value)) {
        $candidates += $Value
    } else {
        $candidates += (Join-Path $repoRoot $Value)
        $candidates += $Value
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Resolve-VrmPath {
    param([string]$Value)

    $candidatePath = Resolve-ExistingPath -Value $Value
    if ($null -eq $candidatePath) {
        throw "VRM source '$Value' was not found. Provide a .vrm file path or a directory containing a single .vrm."
    }

    $candidateItem = Get-Item -LiteralPath $candidatePath -ErrorAction Stop
    if ($candidateItem.PSIsContainer) {
        $vrmFiles = @(Get-ChildItem -LiteralPath $candidatePath -Recurse -File -Filter *.vrm)
        if ($vrmFiles.Count -eq 1) {
            return [System.IO.Path]::GetFullPath($vrmFiles[0].FullName)
        }

        if ($vrmFiles.Count -eq 0) {
            throw "No .vrm files were found under '$candidatePath'."
        }

        $matches = $vrmFiles | Select-Object -ExpandProperty FullName
        throw "Multiple .vrm files were found under '$candidatePath': $($matches -join ', '). Pass an explicit file path."
    }

    if (-not [string]::Equals($candidateItem.Extension, ".vrm", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved input '$candidatePath' is not a .vrm file."
    }

    return [System.IO.Path]::GetFullPath($candidateItem.FullName)
}

function Set-SelectedModelPath {
    param(
        [string]$SettingsPath,
        [string]$ModelPath
    )

    if (!(Test-Path -LiteralPath $SettingsPath)) {
        return $false
    }

    $settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
    $settings.selectedModelPath = $ModelPath
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
    return $true
}

function Resolve-RequestedVrmPath {
    param(
        [string]$RequestedValue,
        [string]$SettingsPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedValue)) {
        return $RequestedValue
    }

    if (Test-Path -LiteralPath $SettingsPath) {
        $settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
        $configuredPath = $settings.selectedModelPath
        if (-not [string]::IsNullOrWhiteSpace($configuredPath) -and (Test-Path -LiteralPath $configuredPath)) {
            return $configuredPath
        }
    }

    return "artifacts\yyb-miku\yyb-miku-export.vrm"
}

$requestedVrmPath = Resolve-RequestedVrmPath -RequestedValue $VrmPath -SettingsPath $settingsPath
$resolvedVrmPath = Resolve-VrmPath -Value $requestedVrmPath

if (!(Test-Path $sandboxExe)) {
    throw "Sandbox executable not found at '$sandboxExe'. Run scripts\sync-mateengine-sandbox.ps1 first."
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
$sourceEqualsDestination = [string]::Equals(
    [System.IO.Path]::GetFullPath($resolvedVrmPath),
    [System.IO.Path]::GetFullPath($stagedImportPath),
    [System.StringComparison]::OrdinalIgnoreCase
)

if (Test-Path $resolvedRuntimeDataDir) {
    Copy-Item -LiteralPath $resolvedRuntimeDataDir -Destination $backupPath -Recurse -Force
}

if (-not $sourceEqualsDestination) {
    Copy-Item -LiteralPath $resolvedVrmPath -Destination $stagedImportPath -Force
}
Set-SelectedModelPath -SettingsPath $settingsPath -ModelPath $stagedImportPath | Out-Null
Set-Clipboard -Value $stagedImportPath

$session = [ordered]@{
    createdAt = (Get-Date).ToString("o")
    runtimeDataDir = $resolvedRuntimeDataDir
    backupPath = if (Test-Path $backupPath) { $backupPath } else { $null }
    settingsPath = $settingsPath
    selectedModelPath = $stagedImportPath
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
