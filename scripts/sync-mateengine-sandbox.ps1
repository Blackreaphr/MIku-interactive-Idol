param(
    [string]$SourceDir = "C:\Users\nicky\Downloads\Public.Release.X3.3.0-HOTFIX-1",

    [string]$SandboxDir = "sandbox\MateEngineX"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedSource = $SourceDir
$resolvedSandbox = Join-Path $repoRoot $SandboxDir
$sandboxManifest = Join-Path $resolvedSandbox "codex-sandbox.json"
$requiredExe = Join-Path $resolvedSource "MateEngineX.exe"
$requiredDataDir = Join-Path $resolvedSource "MateEngineX_Data"

if (!(Test-Path $resolvedSource)) {
    throw "MateEngine source directory not found at '$resolvedSource'."
}

if (!(Test-Path $requiredExe)) {
    throw "MateEngine executable not found at '$requiredExe'."
}

if (!(Test-Path $requiredDataDir)) {
    throw "MateEngine data directory not found at '$requiredDataDir'."
}

New-Item -ItemType Directory -Force $resolvedSandbox | Out-Null

$null = robocopy $resolvedSource $resolvedSandbox /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -gt 7) {
    throw "Robocopy failed with exit code $LASTEXITCODE while syncing the MateEngine sandbox."
}

$manifest = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    source = $resolvedSource
    sandbox = $resolvedSandbox
    executable = (Join-Path $resolvedSandbox "MateEngineX.exe")
}

$manifest | ConvertTo-Json | Set-Content -Path $sandboxManifest -Encoding utf8
Write-Host "Sandbox MateEngine copied to $resolvedSandbox"
