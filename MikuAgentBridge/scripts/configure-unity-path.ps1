param(
    [string]$UnityManagedDir
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$localPropsPath = Join-Path $projectRoot "Unity.Local.props"

function Test-UnityManagedDir([string]$Path) {
    if (-not $Path) { return $false }
    $core = Join-Path $Path "UnityEngine.CoreModule.dll"
    $anim = Join-Path $Path "UnityEngine.AnimationModule.dll"
    return (Test-Path $core) -and (Test-Path $anim)
}

if (-not (Test-UnityManagedDir $UnityManagedDir)) {
    $hubEditorRoot = "C:\Program Files\Unity\Hub\Editor"
    if (Test-Path $hubEditorRoot) {
        $candidates = Get-ChildItem $hubEditorRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "Editor\Data\Managed\UnityEngine" } |
            Where-Object { Test-UnityManagedDir $_ }

        $UnityManagedDir = $candidates | Select-Object -First 1
    }
}

if (-not (Test-UnityManagedDir $UnityManagedDir)) {
    throw "Could not locate Unity managed assemblies. Install Unity Editor, or pass -UnityManagedDir to this script."
}

$xml = @"
<Project>
  <PropertyGroup>
    <UnityManagedDir>$UnityManagedDir</UnityManagedDir>
  </PropertyGroup>
</Project>
"@

Set-Content -Path $localPropsPath -Value $xml -Encoding UTF8
Write-Output "Wrote $localPropsPath"
Write-Output "UnityManagedDir = $UnityManagedDir"
