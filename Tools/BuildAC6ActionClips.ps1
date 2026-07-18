param(
    [switch]$Force,
    [switch]$AllPhysical,
    [string[]]$ActionId = @(),
    [string]$RawRoot = 'E:\UnityDev\Resources\AC6_ExtractedAssets\chr\AC - c0000'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot 'Tools/AssetManifests/c0000_action_catalog.json'
$exporterDll = Join-Path $repoRoot 'Tools/DotNet/AC6HkxPoseExporter/bin/Release/net9.0/AC6HkxPoseExporter.dll'
$dotnet = Join-Path (Split-Path -Parent $repoRoot) 'Tools/dotnet9sdk/dotnet.exe'
$outRoot = Join-Path $repoRoot 'Tools/LocalOut/actions'

function First-Existing([string[]]$paths) {
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) { return $path }
    }
    return $null
}

if (-not (Test-Path -LiteralPath $catalogPath)) {
    throw 'Action catalog is missing. Run Tools/BuildAC6ActionCatalog.ps1 first.'
}
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Bundled .NET 9 host is missing: $dotnet" }
if (-not (Test-Path -LiteralPath $exporterDll)) {
    throw "AC6HkxPoseExporter is not built: $exporterDll"
}
if (-not (Test-Path -LiteralPath $RawRoot)) { throw "Raw HKX root is missing: $RawRoot" }

$skeleton = First-Existing @(
    (Join-Path $repoRoot 'Tools/LocalOut/Melander1010Pipeline/anibnd/c0000/4000000_Skeleton.hkx'),
    'E:\UnityDev\tmp\ac6_anim_tests\from_game_c0000\4000000_Skeleton.hkx'
)
$compendium = First-Existing @(
    (Join-Path $repoRoot 'Tools/LocalOut/Melander1010Pipeline/anibnd/c0000_a000_00/7000000_a000_00.compendium'),
    'E:\UnityDev\tmp\ac6_anim_tests\from_game_c0000\7000000_a000_00.compendium'
)
if ($null -eq $skeleton) { throw 'Canonical c0000 Skeleton.hkx is missing.' }
if ($null -eq $compendium) { throw 'The real c0000 animation compendium is missing.' }
if (-not (Test-Path -LiteralPath $outRoot)) { New-Item -ItemType Directory -Force $outRoot | Out-Null }

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$selected = @(
    if ($ActionId.Count -gt 0) {
        $catalog.entries | Where-Object { $ActionId -contains $_.actionId }
    }
    elseif ($AllPhysical) {
        $catalog.entries | Where-Object sourceAvailable
    }
    else {
        $catalog.entries | Where-Object {
            $_.sourceAvailable -and $_.reviewStatus -eq 'Confirmed'
        }
    }
)

$done = 0
$skipped = 0
$failed = 0
foreach ($entry in $selected) {
    $animation = Join-Path $RawRoot $entry.hkxFile
    $output = Join-Path $outRoot ($entry.actionId + '_pose.json')
    if (-not (Test-Path -LiteralPath $animation)) {
        Write-Warning "Physical HKX missing for $($entry.actionId): $animation"
        $failed++
        continue
    }
    if ((Test-Path -LiteralPath $output) -and -not $Force) {
        $skipped++
        continue
    }

    & $dotnet $exporterDll --skeleton $skeleton --anim $animation --compendium $compendium --out $output
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Exporter failed for $($entry.actionId)."
        $failed++
        continue
    }
    $done++
}

Write-Host ("BuildAC6ActionClips: selected={0} exported={1} skipped={2} failed={3}" -f `
    $selected.Count, $done, $skipped, $failed)
if ($selected.Count -eq 0) {
    Write-Host 'No confirmed actions are selected. Use -ActionId <id> for review or -AllPhysical explicitly.'
}
if ($failed -gt 0) { exit 1 }
