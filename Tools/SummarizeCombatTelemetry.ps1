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
$eventCounts = @{}
$allocations = [System.Collections.Generic.List[object]]::new()
$latestMissiles = @{}
$previousAffiliations = @{}
$affiliationTransitions = @{}
$identityResolutions = @{}
$records = 0
$invalidLines = 0
$allocationFailures = 0

Get-Content -LiteralPath $resolved | ForEach-Object {
    try {
        $record = $_ | ConvertFrom-Json
    }
    catch {
        $invalidLines++
        return
    }

    $records++
    $eventKey = "$($record.system), $($record.event)"
    $eventCounts[$eventKey] = 1 + [int]($eventCounts[$eventKey])

    if ($record.system -eq 'radar_tracking') {
        if ($record.event -eq 'TrackAllocated') {
            $allocations.Add($record)
        }
        elseif ($record.event -eq 'TrackAllocationFailed') {
            $allocationFailures++
        }
    }

    if ($record.system -eq 'missile_fusion' -and
        $record.event -eq 'track_snapshot') {
        $trackKey = "$($record.source)|$($record.trackId)"
        $latestMissiles[$trackKey] = $record
        if ($previousAffiliations.ContainsKey($trackKey) -and
            $previousAffiliations[$trackKey] -ne $record.affiliation) {
            $transition = "$($previousAffiliations[$trackKey]) -> $($record.affiliation)"
            $affiliationTransitions[$transition] =
                1 + [int]($affiliationTransitions[$transition])
        }
        $previousAffiliations[$trackKey] = $record.affiliation
    }

    if ($record.system -eq 'identity' -and
        $record.event -eq 'friendly_missile_resolution') {
        $identityResolutions[$record.resolution] =
            1 + [int]($identityResolutions[$record.resolution])
    }
}

Write-Output "Combat telemetry: $resolved"
Write-Output "Records: $records, invalid JSON lines: $invalidLines"
Write-Output ''
Write-Output 'Event counts:'
$eventCounts.GetEnumerator() |
    Sort-Object Value -Descending |
    ForEach-Object { '{0,8}  {1}' -f $_.Value, $_.Key }

Write-Output ''
Write-Output "Radar track allocations: $($allocations.Count)"
Write-Output "Radar allocation failures: $allocationFailures"
Write-Output 'Suspected rapid duplicate allocations (same source/class, <=1 s, <=2 km):'
$previousAllocations = @{}
$suspects = 0
foreach ($current in ($allocations | Sort-Object simTime)) {
    $allocationKey = "$($current.source)|$($current.objectClass)"
    if ($previousAllocations.ContainsKey($allocationKey)) {
        $previous = $previousAllocations[$allocationKey]
        $dt = [double]$current.simTime - [double]$previous.simTime
        if ($dt -ge 0 -and $dt -le 1.0) {
            $dx = [double]$current.measurementPosition.x - [double]$previous.measurementPosition.x
            $dy = [double]$current.measurementPosition.y - [double]$previous.measurementPosition.y
            $dz = [double]$current.measurementPosition.z - [double]$previous.measurementPosition.z
            $distance = [Math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
            if ($distance -le 2000.0) {
                $suspects++
                '{0,8:F3}s  dt={1:F3}s  distance={2:F1}m  track={3}  gate={4}  source={5}' -f `
                    [double]$current.simTime, $dt, $distance, $current.trackId, `
                    $current.gateSquared, $current.source
            }
        }
    }
    $previousAllocations[$allocationKey] = $current
}
if ($suspects -eq 0) {
    Write-Output 'None.'
}

Write-Output ''
Write-Output 'Missile affiliation transitions:'
if ($affiliationTransitions.Count -eq 0) {
    Write-Output 'None.'
}
else {
    $affiliationTransitions.GetEnumerator() |
        Sort-Object Value -Descending |
        ForEach-Object { '{0,8}  {1}' -f $_.Value, $_.Key }
}

Write-Output ''
Write-Output 'Friendly missile identity resolutions:'
if ($identityResolutions.Count -eq 0) {
    Write-Output 'No structured identity events in this log.'
}
else {
    $identityResolutions.GetEnumerator() |
        Sort-Object Value -Descending |
        ForEach-Object { '{0,8}  {1}' -f $_.Value, $_.Key }
}

Write-Output ''
Write-Output 'Latest missile intent states:'
$latestMissiles.Values |
    Sort-Object source, trackId |
    Format-Table source, trackId, affiliation, quality, incoming, missionRole, `
        targetIntent, targetIntentConfidence, closestApproachDistanceMeters -AutoSize
