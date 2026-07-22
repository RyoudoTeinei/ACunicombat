param(
    [string]$RawAssetsRoot = 'E:\UnityDev\Resources\AC6_ExtractedAssets',
    [string]$TexConvPath = '',
    [string]$OutputAssetRoot = 'Assets\_Project\LocalSamples\AC6SkinnedModular\Generated\ColorMasks'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($TexConvPath)) {
    $TexConvPath = Join-Path $projectRoot 'Tools\LocalOut\bin\texconv.exe'
}

foreach ($required in @($RawAssetsRoot, $TexConvPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required path is missing: $required"
    }
}

$outputRoot = Join-Path $projectRoot $OutputAssetRoot
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$codes = @('1010', '2010', '3010')
$slots = @('HD', 'BD', 'AM', 'LG')
$count = 0
foreach ($code in $codes) {
    foreach ($slot in $slots) {
        $stem = "${slot}_M_${code}_01_3m"
        $source = Join-Path $RawAssetsRoot ("parts\{0}_m_{1}\{2}.dds" -f $slot.ToLowerInvariant(), $code, $stem)
        if (-not (Test-Path -LiteralPath $source)) {
            throw "AC6 zone mask is missing: $source"
        }

        & $TexConvPath -nologo -y -m 1 -ft png -o $outputRoot $source
        if ($LASTEXITCODE -ne 0) {
            throw "texconv failed for $source (exit $LASTEXITCODE)"
        }
        $count++
    }
}

$materialCatalogPath = Join-Path $projectRoot 'Assets\_Project\Data\Mechs\AC6Materials\ac6_frame_material_catalog.json'
if (Test-Path -LiteralPath $materialCatalogPath) {
    $catalog = Get-Content -LiteralPath $materialCatalogPath -Raw | ConvertFrom-Json
    $weaponMasks = @($catalog.materials |
        Where-Object { $_.sourcePath -match 'WP_\d{4}' -and -not [string]::IsNullOrWhiteSpace($_.mask3Map) } |
        ForEach-Object { [string]$_.mask3Map } |
        Sort-Object -Unique)

    foreach ($maskPath in $weaponMasks) {
        if ($maskPath -notmatch 'WP_(?:E_)?(?<code>\d{4})') {
            Write-Warning "Cannot infer weapon code from mask path: $maskPath"
            continue
        }

        $code = $Matches.code
        $stem = [IO.Path]::GetFileNameWithoutExtension($maskPath)
        $source = Join-Path $RawAssetsRoot ("wp\wp_{0}\{1}.dds" -f $code, $stem)
        if (-not (Test-Path -LiteralPath $source)) {
            Write-Warning "AC6 weapon zone mask is missing: $source"
            continue
        }

        & $TexConvPath -nologo -y -m 1 -ft png -o $outputRoot $source
        if ($LASTEXITCODE -ne 0) {
            throw "texconv failed for $source (exit $LASTEXITCODE)"
        }
        $count++
    }
}

Write-Output "Imported $count AC6 frame and weapon color masks to $outputRoot"
