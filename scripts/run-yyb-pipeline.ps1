param(
    [ValidateSet("inspect", "export", "export-fbx", "export-vrm")]
    [string]$Command = "inspect",

    [string]$BlendPath = "assets\YYB Miku\YYB Miku.blend",

    [string]$OutputDir = "artifacts\yyb-miku",

    [string]$WorkingRoot = ".tmp-miku-src\yyb-miku",

    [string]$BlenderUserScriptsDir = ".blender-user-scripts",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PipelineArgs = @()
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$blenderExe = "C:\Program Files\Blender Foundation\Blender 5.0\blender.exe"
$scriptPath = Join-Path $repoRoot "tools\blender\yyb_pipeline.py"
$resolvedBlend = Join-Path $repoRoot $BlendPath
$resolvedOutput = Join-Path $repoRoot $OutputDir
$resolvedUserScripts = Join-Path $repoRoot $BlenderUserScriptsDir
$resolvedVrmAddon = Join-Path $resolvedUserScripts "addons\io_scene_vrm"
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) "miku-yyb-pipeline\$runStamp"
$tempDir = Join-Path $runRoot "temp"
$userConfigDir = Join-Path $runRoot "config"

if (!(Test-Path $blenderExe)) {
    throw "Blender executable not found at '$blenderExe'."
}

if (!(Test-Path $resolvedBlend)) {
    throw "Blend file not found at '$resolvedBlend'."
}

New-Item -ItemType Directory -Force $resolvedOutput | Out-Null
New-Item -ItemType Directory -Force $tempDir | Out-Null
New-Item -ItemType Directory -Force $userConfigDir | Out-Null

$env:TEMP = $tempDir
$env:TMP = $tempDir
$env:BLENDER_USER_CONFIG = $userConfigDir

$blendToOpen = $resolvedBlend
if ($Command -ne "inspect") {
    $sourceDir = Split-Path -Parent $resolvedBlend
    $sourceDirName = Split-Path -Leaf $sourceDir
    $workingStamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $workingRunRoot = Join-Path (Join-Path $repoRoot $WorkingRoot) $workingStamp
    New-Item -ItemType Directory -Force $workingRunRoot | Out-Null
    Copy-Item -LiteralPath $sourceDir -Destination $workingRunRoot -Recurse -Force
    $workingSourceDir = Join-Path $workingRunRoot $sourceDirName
    $blendToOpen = Join-Path $workingSourceDir (Split-Path -Leaf $resolvedBlend)
}

$blenderArgs = @(
    "-b",
    "--factory-startup",
    $blendToOpen
)

if ($Command -eq "export-vrm") {
    if (!(Test-Path $resolvedVrmAddon)) {
        throw "Repo-local VRM addon not found at '$resolvedVrmAddon'. Sync the official addon before running export-vrm."
    }

    $env:BLENDER_USER_SCRIPTS = $resolvedUserScripts
    $blenderArgs += @("--addons", "io_scene_vrm")
}

$blenderArgs += @(
    "--python",
    $scriptPath,
    "--",
    $Command,
    "--output-dir",
    $resolvedOutput,
    "--source-blend",
    $resolvedBlend
)

$blenderArgs += $PipelineArgs

& $blenderExe @blenderArgs
