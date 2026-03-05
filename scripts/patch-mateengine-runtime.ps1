param(
    [string[]]$AssemblyPaths = @(
        "vendor\MateEngineX\Managed\Assembly-CSharp.dll",
        "sandbox\MateEngineX\MateEngineX_Data\Managed\Assembly-CSharp.dll"
    )
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

$operations = @(
    @{
        Name = "Restore AvatarHideHandler.OnDisable"
        Offset = 0xD700
        Desired = [byte[]](0xEE, 0x02, 0x16, 0x16, 0x28, 0x3D, 0x02, 0x00, 0x06)
        Expected = @(
            [byte[]](0xEE, 0x02, 0x16, 0x16, 0x28, 0x3D, 0x02, 0x00, 0x06),
            [byte[]](0xEE, 0x2A, 0x16, 0x16, 0x28, 0x3D, 0x02, 0x00, 0x06)
        )
    },
    @{
        Name = "Disable property setter copying in VRMLoader.CopyComponentValues"
        Offset = 0x335AC
        Desired = [byte[]](0x0A, 0x16, 0x2A)
        Expected = @(
            [byte[]](0x62, 0x03, 0x6F, 0xE2, 0x00, 0x00, 0x0A, 0x2C, 0x0E, 0x03, 0x17, 0x6F, 0xDB, 0x06, 0x00, 0x0A, 0x14, 0x28, 0xB9, 0x00, 0x00, 0x0A, 0x2A, 0x16, 0x2A)
        )
    }
)

function Resolve-AssemblyPath {
    param([string]$PathValue)

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PathValue))
}

function Test-Bytes {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [byte[]]$Pattern
    )

    if (($Offset + $Pattern.Length) -gt $Bytes.Length) {
        return $false
    }

    for ($i = 0; $i -lt $Pattern.Length; $i++) {
        if ($Bytes[$Offset + $i] -ne $Pattern[$i]) {
            return $false
        }
    }

    return $true
}

function Get-HexSlice {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [int]$Length
    )

    return (($Bytes[$Offset..($Offset + $Length - 1)] | ForEach-Object { $_.ToString("X2") }) -join " ")
}

foreach ($assemblyPathInput in $AssemblyPaths) {
    $resolvedAssemblyPath = Resolve-AssemblyPath $assemblyPathInput
    if (!(Test-Path $resolvedAssemblyPath)) {
        Write-Warning "Skipping missing assembly: $resolvedAssemblyPath"
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($resolvedAssemblyPath)
    $mutated = $false

    foreach ($operation in $operations) {
        $offset = [int]$operation.Offset
        $desired = [byte[]]$operation.Desired
        $expectedPatterns = @($operation.Expected)

        if (Test-Bytes -Bytes $bytes -Offset $offset -Pattern $desired) {
            Write-Host "Already applied: $($operation.Name) in $resolvedAssemblyPath"
            continue
        }

        $matchedExpected = $false
        foreach ($expectedPattern in $expectedPatterns) {
            if (Test-Bytes -Bytes $bytes -Offset $offset -Pattern ([byte[]]$expectedPattern)) {
                $matchedExpected = $true
                break
            }
        }

        if (-not $matchedExpected) {
            $maxPatternLength = ($expectedPatterns | ForEach-Object { ([byte[]]$_).Length } | Measure-Object -Maximum).Maximum
            $actualHex = Get-HexSlice -Bytes $bytes -Offset $offset -Length $maxPatternLength
            throw "Unexpected byte signature for '$($operation.Name)' in '$resolvedAssemblyPath': $actualHex"
        }

        [Array]::Copy($desired, 0, $bytes, $offset, $desired.Length)
        $mutated = $true
        Write-Host "Patched $($operation.Name) in $resolvedAssemblyPath"
    }

    if ($mutated) {
        [System.IO.File]::WriteAllBytes($resolvedAssemblyPath, $bytes)
    }
}
