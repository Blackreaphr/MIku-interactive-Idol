param(
    [string]$AddonRepoDir = "vendor\VRM-Addon-for-Blender",

    [string]$BlenderUserScriptsDir = ".blender-user-scripts"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedRepoDir = Join-Path $repoRoot $AddonRepoDir
$sourceAddonDir = Join-Path $resolvedRepoDir "src\io_scene_vrm"
$resolvedUserScripts = Join-Path $repoRoot $BlenderUserScriptsDir
$addonRoot = Join-Path $resolvedUserScripts "addons"
$destinationAddonDir = Join-Path $addonRoot "io_scene_vrm"
$manifestPath = Join-Path $resolvedUserScripts "vrm-addon.json"

if (!(Test-Path $sourceAddonDir)) {
    throw "VRM addon source not found at '$sourceAddonDir'. Clone the official repo into '$resolvedRepoDir' first."
}

New-Item -ItemType Directory -Force $addonRoot | Out-Null
if (Test-Path $destinationAddonDir) {
    Remove-Item -LiteralPath $destinationAddonDir -Recurse -Force
}

Copy-Item -LiteralPath $sourceAddonDir -Destination $destinationAddonDir -Recurse -Force

$manifest = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    source = $sourceAddonDir
    destination = $destinationAddonDir
}

$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "Synced VRM addon to $destinationAddonDir"
