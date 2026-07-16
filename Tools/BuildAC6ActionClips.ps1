# Batch step A of the action-library pipeline: HKX -> full-sample pose JSON.
# Reads Tools/AssetManifests/c0000_action_manifest.json, locates each enabled
# entry's HKX under the extracted anibnd tree, and runs AC6HkxPoseExporter into
# the manifest's poseJsonRoot. Step B (pose JSON -> per-part clips + registry)
# is the Unity menu Tools > ACunicombat > AC6 > Build Action Library.
#
# Usage:  powershell -File Tools/BuildAC6ActionClips.ps1 [-Force]
# See Docs/AC6动作库管线_设计.md for the pipeline contract.

param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'Tools/AssetManifests/c0000_action_manifest.json'
$exporter = Join-Path $repoRoot 'Tools/DotNet/AC6HkxPoseExporter/bin/Release/net9.0/AC6HkxPoseExporter.exe'

if (-not (Test-Path $exporter)) {
    Write-Error "AC6HkxPoseExporter is not built. Run: dotnet build -c Release Tools/DotNet/AC6HkxPoseExporter"
}

$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$skeleton = Join-Path $repoRoot $manifest.skeleton
$anibndRoot = Join-Path $repoRoot $manifest.anibndRoot
$outRoot = Join-Path $repoRoot $manifest.poseJsonRoot
if (-not (Test-Path $outRoot)) { New-Item -ItemType Directory -Force $outRoot | Out-Null }

$done = 0; $skipped = 0; $missing = 0
foreach ($entry in $manifest.entries) {
    if (-not $entry.enabled) { $skipped++; continue }

    $out = Join-Path $outRoot ($entry.actionId + '_pose.json')
    if ((Test-Path $out) -and (-not $Force)) { $skipped++; continue }

    # HKX files are named <binderId>_<hkx>.hkx inside the extracted div folders.
    $hkxFile = Get-ChildItem -Path $anibndRoot -Recurse -Filter ('*_' + $entry.hkx + '.hkx') |
        Select-Object -First 1
    if ($null -eq $hkxFile) {
        Write-Warning ("HKX not found for {0} ({1}). Extract its anibnd div into {2} first " -f `
            $entry.actionId, $entry.hkx, $manifest.anibndRoot)
        $missing++
        continue
    }

    $compendium = Get-ChildItem -Path $hkxFile.DirectoryName -Filter '*.compendium' | Select-Object -First 1
    if ($null -eq $compendium) {
        Write-Warning ("No compendium next to {0}; skipping {1}." -f $hkxFile.FullName, $entry.actionId)
        $missing++
        continue
    }

    & $exporter --skeleton $skeleton --anim $hkxFile.FullName --compendium $compendium.FullName --out $out
    if ($LASTEXITCODE -ne 0) { Write-Error ("Exporter failed for {0}" -f $entry.actionId) }
    $done++
}

Write-Host ("BuildAC6ActionClips: exported={0} skipped={1} missing={2} -> {3}" -f $done, $skipped, $missing, $manifest.poseJsonRoot)
