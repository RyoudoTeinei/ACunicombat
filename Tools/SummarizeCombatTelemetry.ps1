param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$telemetryRoot = Join-Path $projectRoot 'Logs\CombatTelemetry'

if ([string]::IsNullOrWhiteSpace($Path)) {
    $latest = Get-ChildItem -LiteralPath $telemetryRoot -Filter '*.jsonl' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $latest) {
        throw "No combat telemetry files found under $telemetryRoot"
    }
    $Path = $latest.FullName
}

$resolved = (Resolve-Path -LiteralPath $Path).Path
$records = [System.Collections.Generic.List[object]]::new()
$invalidLines = 0
Get-Content -LiteralPath $resolved | ForEach-Object {
    try {
        $records.Add(($_ | ConvertFrom-Json))
    }
    catch {
        $invalidLines++
    }
}

Write-Output "Combat telemetry: $resolved"
Write-Output "Records: $($records.Count), invalid JSON lines: $invalidLines"
Write-Output ''
Write-Output 'Event counts:'
$records |
    Group-Object system, event |
    Sort-Object Count -Descending |
    ForEach-Object { '{0,8}  {1}' -f $_.Count, $_.Name }

$allocations = @($records | Where-Object {
    $_.system -eq 'radar_tracking' -and $_.event -eq 'TrackAllocated'
} | Sort-Object source, simTime)

Write-Output ''
Write-Output "Radar track allocations: $($allocations.Count)"
Write-Output 'Suspected rapid duplicate allocations (same source/class, <=1 s, <=2 km):'
$suspects = 0
for ($i = 1; $i -lt $allocations.Count; $i++) {
    $previous = $allocations[$i - 1]
    $current = $allocations[$i]
    if ($previous.source -ne $current.source -or
        $previous.objectClass -ne $current.objectClass) {
        continue
    }
    $dt = [double]$current.simTime - [double]$previous.simTime
    if ($dt -lt 0 -or $dt -gt 1.0) {
        continue
    }
    $dx = [double]$current.measurementPosition.x - [double]$previous.measurementPosition.x
    $dy = [double]$current.measurementPosition.y - [double]$previous.measurementPosition.y
    $dz = [double]$current.measurementPosition.z - [double]$previous.measurementPosition.z
    $distance = [Math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
    if ($distance -le 2000.0) {
        $suspects++
        '{0,8:F3}s  dt={1:F3}s  distance={2:F1}m  track={3}  gateRejects={4}  source={5}' -f `
            [double]$current.simTime, $dt, $distance, $current.trackId, `
            $current.gateRejected, $current.source
    }
}
if ($suspects -eq 0) {
    Write-Output 'None.'
}

Write-Output ''
Write-Output 'Latest missile intent states:'
$records |
    Where-Object { $_.system -eq 'missile_fusion' -and $_.event -eq 'track_snapshot' } |
    Group-Object source, trackId |
    ForEach-Object { $_.Group | Sort-Object simTime | Select-Object -Last 1 } |
    Sort-Object source, trackId |
    Format-Table source, trackId, affiliation, quality, incoming, targetIntent, `
        targetIntentConfidence, closestApproachDistanceMeters -AutoSize
