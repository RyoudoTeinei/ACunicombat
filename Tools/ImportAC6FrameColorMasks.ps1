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

Write-Output "Imported $count AC6 frame color masks to $outputRoot"
