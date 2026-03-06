param(
    [Parameter(Mandatory = $true)]
    [string]$ModelPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [int]$WaitSeconds = 12,

    [string]$SandboxDir = "sandbox\MateEngineX",

    [string]$RuntimeDataDir = "$env:USERPROFILE\AppData\LocalLow\Shinymoon\MateEngineX"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedSandbox = Join-Path $repoRoot $SandboxDir
$sandboxExe = Join-Path $resolvedSandbox "MateEngineX.exe"
$resolvedRuntimeDataDir = [System.IO.Path]::GetFullPath($RuntimeDataDir)
$resolvedOutputDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDir))
$resolvedSandboxExe = [System.IO.Path]::GetFullPath($sandboxExe)
$backupRoot = Join-Path $repoRoot "sandbox\AppDataBackups"
$stagedImportDir = Join-Path $resolvedSandbox "Imports"
$settingsPath = Join-Path $resolvedRuntimeDataDir "settings.json"
$liveLogPath = Join-Path $resolvedRuntimeDataDir "Player.log"
$captureWidth = 900
$captureHeight = 1400
$motionSampleDelaySeconds = 3

if (!(Test-Path $resolvedSandboxExe)) {
    throw "Sandbox executable not found at '$resolvedSandboxExe'. Run scripts\sync-mateengine-sandbox.ps1 first."
}

if (!(Test-Path $settingsPath)) {
    throw "Runtime settings file not found at '$settingsPath'. Launch MateEngine once before running validation."
}

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class MateEngineWindowNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
"@

function Get-MateEngineProcesses {
    Get-CimInstance Win32_Process -Filter "Name='MateEngineX.exe'" |
        Where-Object { $_.ExecutablePath }
}

function Stop-SandboxProcesses {
    $sandboxProcesses = @(Get-MateEngineProcesses | Where-Object {
        [string]::Equals(
            [System.IO.Path]::GetFullPath($_.ExecutablePath),
            $resolvedSandboxExe,
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
            $resolvedSandboxExe,
            [System.StringComparison]::OrdinalIgnoreCase
        )
    })

    foreach ($processInfo in $remainingProcesses) {
        Stop-Process -Id $processInfo.ProcessId -ErrorAction Stop
    }
}

function Resolve-ValidationTarget {
    param([string]$Value)

    $candidatePath = $null
    if ([System.IO.Path]::IsPathRooted($Value) -and (Test-Path -LiteralPath $Value)) {
        $candidatePath = [System.IO.Path]::GetFullPath($Value)
    } else {
        $repoCandidate = Join-Path $repoRoot $Value
        if (Test-Path -LiteralPath $repoCandidate) {
            $candidatePath = [System.IO.Path]::GetFullPath($repoCandidate)
        } elseif (Test-Path -LiteralPath $Value) {
            $candidatePath = [System.IO.Path]::GetFullPath($Value)
        }
    }

    if ($null -ne $candidatePath) {
        $candidateItem = Get-Item -LiteralPath $candidatePath -ErrorAction Stop
        if ($candidateItem.PSIsContainer) {
            $vrmFiles = @(Get-ChildItem -LiteralPath $candidatePath -Recurse -File -Filter *.vrm)
            if ($vrmFiles.Count -eq 1) {
                $candidatePath = [System.IO.Path]::GetFullPath($vrmFiles[0].FullName)
            } elseif ($vrmFiles.Count -eq 0) {
                throw "Validation target directory '$candidatePath' does not contain a .vrm file."
            } else {
                $matches = $vrmFiles | Select-Object -ExpandProperty FullName
                throw "Validation target directory '$candidatePath' contains multiple .vrm files: $($matches -join ', '). Pass an explicit file path."
            }
        }

        New-Item -ItemType Directory -Force -Path $stagedImportDir | Out-Null
        $stagedImportPath = Join-Path $stagedImportDir ([System.IO.Path]::GetFileName($candidatePath))
        Copy-Item -LiteralPath $candidatePath -Destination $stagedImportPath -Force

        return [ordered]@{
            mode = "file"
            input = $Value
            sourcePath = $candidatePath
            selectedModelPath = [System.IO.Path]::GetFullPath($stagedImportPath)
            displayName = [System.IO.Path]::GetFileNameWithoutExtension($candidatePath)
        }
    }

    return [ordered]@{
        mode = "builtin"
        input = $Value
        sourcePath = $null
        selectedModelPath = $Value
        displayName = $Value
    }
}

function Save-WindowCapture {
    param(
        [int]$ProcessId,
        [string]$DestinationPath
    )

    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    if ($process.MainWindowHandle -eq 0) {
        throw "MateEngine window handle is unavailable for process $ProcessId."
    }

    $rect = New-Object MateEngineWindowNative+RECT
    if (-not [MateEngineWindowNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) {
        throw "Unable to get MateEngine window bounds for process $ProcessId."
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "MateEngine window bounds are invalid: ${width}x${height}."
    }

    $rawBitmap = New-Object System.Drawing.Bitmap $width, $height
    $rawGraphics = [System.Drawing.Graphics]::FromImage($rawBitmap)
    $scaledBitmap = New-Object System.Drawing.Bitmap $captureWidth, $captureHeight
    $scaledGraphics = [System.Drawing.Graphics]::FromImage($scaledBitmap)
    try {
        $rawGraphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $rawBitmap.Size)
        $scaledGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaledGraphics.DrawImage($rawBitmap, 0, 0, $captureWidth, $captureHeight)
        $scaledBitmap.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $rawGraphics.Dispose()
        $rawBitmap.Dispose()
        $scaledGraphics.Dispose()
        $scaledBitmap.Dispose()
    }
}

$runningProcesses = @(Get-MateEngineProcesses)
$nonSandboxProcesses = @($runningProcesses | Where-Object {
    -not [string]::Equals(
        [System.IO.Path]::GetFullPath($_.ExecutablePath),
        $resolvedSandboxExe,
        [System.StringComparison]::OrdinalIgnoreCase
    )
})

if ($nonSandboxProcesses.Count -gt 0) {
    $processPaths = $nonSandboxProcesses | Select-Object -ExpandProperty ExecutablePath
    throw "Refusing to run validation while a non-sandbox MateEngine instance is running: $($processPaths -join ', ')"
}

$target = Resolve-ValidationTarget -Value $ModelPath
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $resolvedOutputDir ($timestamp + "-" + ($target.displayName -replace "[^A-Za-z0-9._-]", "_"))
$backupPath = Join-Path $backupRoot "MateEngineX-$timestamp"
$startScreenshotPath = Join-Path $runDir "window-start.png"
$endScreenshotPath = Join-Path $runDir "window-end.png"
$screenshotPath = Join-Path $runDir "window.png"
$logCopyPath = Join-Path $runDir "Player.log"
$metadataPath = Join-Path $runDir "validation.json"

New-Item -ItemType Directory -Force -Path $runDir | Out-Null
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

$originalSettingsRaw = Get-Content -Raw $settingsPath
$settings = $originalSettingsRaw | ConvertFrom-Json
$originalSelectedModelPath = $settings.selectedModelPath

try {
    Stop-SandboxProcesses

    if (Test-Path $resolvedRuntimeDataDir) {
        Copy-Item -Path $resolvedRuntimeDataDir -Destination $backupPath -Recurse -Force
    }

    $settings.selectedModelPath = $target.selectedModelPath
    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding UTF8

    & (Join-Path $PSScriptRoot "start-mateengine-sandbox.ps1") -SandboxDir $SandboxDir
    Start-Sleep -Seconds $WaitSeconds

    $sandboxProcess = Get-Process MateEngineX -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($null -eq $sandboxProcess) {
        throw "MateEngine window did not become available after $WaitSeconds seconds."
    }

    if (!(Test-Path $liveLogPath)) {
        throw "Live Player.log not found at '$liveLogPath'."
    }

    Copy-Item -Path $liveLogPath -Destination $logCopyPath -Force
    Save-WindowCapture -ProcessId $sandboxProcess.Id -DestinationPath $startScreenshotPath
    Start-Sleep -Seconds $motionSampleDelaySeconds
    Save-WindowCapture -ProcessId $sandboxProcess.Id -DestinationPath $endScreenshotPath
    Copy-Item -Path $endScreenshotPath -Destination $screenshotPath -Force

    $startHash = (Get-FileHash -Algorithm SHA256 $startScreenshotPath).Hash
    $endHash = (Get-FileHash -Algorithm SHA256 $endScreenshotPath).Hash

    $metadata = [ordered]@{
        capturedAt = (Get-Date).ToString("o")
        modelPath = $target.input
        mode = $target.mode
        selectedModelPath = $target.selectedModelPath
        sourcePath = $target.sourcePath
        waitSeconds = $WaitSeconds
        backupPath = if (Test-Path $backupPath) { $backupPath } else { $null }
        playerLog = $logCopyPath
        screenshot = $screenshotPath
        startScreenshot = $startScreenshotPath
        endScreenshot = $endScreenshotPath
        captureWidth = $captureWidth
        captureHeight = $captureHeight
        motionSampleDelaySeconds = $motionSampleDelaySeconds
        startFrameHash = $startHash
        endFrameHash = $endHash
        frameChanged = ($startHash -ne $endHash)
        sandboxExe = $resolvedSandboxExe
    }
    $metadata | ConvertTo-Json -Depth 6 | Set-Content -Path $metadataPath -Encoding UTF8
} finally {
    $restoredSettings = $originalSettingsRaw | ConvertFrom-Json
    $restoredSettings.selectedModelPath = $originalSelectedModelPath
    $restoredSettings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding UTF8
    Stop-SandboxProcesses
}

Write-Host "Validated MateEngine avatar import."
Write-Host "Output: $runDir"
Write-Host "Log: $logCopyPath"
Write-Host "Screenshot: $screenshotPath"
