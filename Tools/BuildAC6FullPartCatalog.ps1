param(
    [string]$RawAssetsRoot = 'E:\UnityDev\Resources\AC6_ExtractedAssets',
    [string]$OutputAssetRoot = 'Assets\_Project\LocalSamples\AC6SkinnedModular\Raw\FullCatalog',
    [string[]]$Keys = @(),
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'E:\UnityDev\Tools\dotnet9sdk\dotnet.exe'
$poseExporter = Join-Path $projectRoot 'Tools\DotNet\AC6HkxPoseExporter\bin\Debug\net9.0\AC6HkxPoseExporter.dll'
$gltfExporter = Join-Path $projectRoot 'Tools\DotNet\AC6PartToGltf\bin\Debug\net9.0\AC6PartToGltf.dll'
$probe = Join-Path $projectRoot 'Tools\DotNet\AC6AnimExportProbe\bin\Debug\net9.0\AC6AnimExportProbe.dll'
$texconv = Join-Path $projectRoot 'Tools\LocalOut\bin\texconv.exe'
$pose = Join-Path $projectRoot 'Tools\LocalOut\ModularSkinned_20260710\pose_010900.json'
$workRoot = Join-Path $projectRoot 'Tools\LocalOut\AC6FullPartCatalog'
$assetRoot = Join-Path $projectRoot $OutputAssetRoot
$frameOutput = Join-Path $assetRoot 'Frames'
$equipmentOutput = Join-Path $assetRoot 'Equipment'
$baseSkeletonHkx = Join-Path $RawAssetsRoot 'chr\AC - c0000\Skeleton.hkx'
$baseSkeletonJson = Join-Path $workRoot 'c0000_skeleton.json'
$manifestPath = Join-Path $assetRoot 'AC6FullPartManifest.json'

foreach ($required in @($dotnet, $poseExporter, $gltfExporter, $probe, $texconv, $pose, $baseSkeletonHkx)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required full-catalog pipeline input is missing: $required"
    }
}

$env:DOTNET_CLI_HOME = 'E:\UnityDev\Tools\dotnet-cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = 'E:\UnityDev\Tools\nuget-packages'

foreach ($dir in @($workRoot, $assetRoot, $frameOutput, $equipmentOutput)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][object[]]$ArgumentList,
        [switch]$AllowFailure
    )

    & $FilePath @ArgumentList | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "Command failed with exit code $exitCode`: $FilePath $($ArgumentList -join ' ')"
    }
    return $exitCode
}

function Export-SkeletonJson {
    param(
        [Parameter(Mandatory = $true)][string]$Skeleton,
        [Parameter(Mandatory = $true)][string]$Output,
        [Parameter(Mandatory = $true)][string]$Work
    )

    $directExit = Invoke-Tool -FilePath $dotnet -ArgumentList @(
        $poseExporter, '--skeleton', $Skeleton, '--skeleton-only', '--out', $Output
    ) -AllowFailure
    if ($directExit -eq 0) {
        return
    }

    $compendium = Join-Path $Work 'skeleton.compendium'
    Invoke-Tool -FilePath $dotnet -ArgumentList @(
        $probe, '--make-compendium', $Skeleton, $baseSkeletonHkx, $compendium
    ) | Out-Null
    Invoke-Tool -FilePath $dotnet -ArgumentList @(
        $poseExporter, '--skeleton', $Skeleton, '--compendium', $compendium,
        '--skeleton-only', '--out', $Output
    ) | Out-Null
}

function Convert-Textures {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $textures = @(Get-ChildItem -LiteralPath $Source -File -Filter '*.dds' | Where-Object {
        $_.BaseName -notmatch '_(l|u)$'
    })
    foreach ($texture in $textures) {
        Invoke-Tool -FilePath $texconv -ArgumentList @(
            '-nologo', '-y', '-ft', 'png', '-w', '1024', '-h', '1024', '-m', '1',
            '-o', $Destination, $texture.FullName
        ) | Out-Null
    }
}

function Test-KeyRequested {
    param([string]$Key)
    return $Keys.Count -eq 0 -or $Keys -contains $Key
}

function Get-AlignmentMetrics {
    param([string]$ReportPath)

    $maxPositionError = 0.0
    $maxRotationErrorDeg = 0.0
    if (Test-Path -LiteralPath $ReportPath) {
        try {
            $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
            foreach ($part in @($report.Parts)) {
                $maxPositionError = [Math]::Max($maxPositionError, [double]$part.MaxPositionError)
                $maxRotationErrorDeg = [Math]::Max($maxRotationErrorDeg, [double]$part.MaxRotationErrorDeg)
            }
        } catch {
            Write-Warning "Alignment report could not be read: $ReportPath"
        }
    }

    return [pscustomobject]@{
        maxPositionError = $maxPositionError
        maxRotationErrorDeg = $maxRotationErrorDeg
    }
}

$sampleProvidedKeys = @(
    'HD_M_1010', 'HD_M_2010', 'HD_M_3010',
    'BD_M_1010', 'BD_M_2010', 'BD_M_3010',
    'AM_M_1010', 'AM_M_2010', 'AM_M_3010',
    'LG_M_1010', 'LG_M_2010', 'LG_M_3010',
    'BS_A_1010', 'BS_A_2010', 'BS_A_3010',
    'WP_L_0010', 'WP_L_0230', 'WP_L_0250', 'WP_L_1310', 'WP_L_1410',
    'WP_L_4210', 'WP_L_4250', 'WP_R_1310', 'WP_R_1410', 'WP_R_1610',
    'WP_R_4210', 'WP_R_4290', 'WP_R_4410'
)

if ($Force -or -not (Test-Path -LiteralPath $baseSkeletonJson)) {
    Export-SkeletonJson -Skeleton $baseSkeletonHkx -Output $baseSkeletonJson -Work $workRoot
}

$specs = [Collections.Generic.List[object]]::new()
$partsRoot = Join-Path $RawAssetsRoot 'parts'
$frameKinds = @{
    'HD_M' = [pscustomobject]@{ Kind = 'frame.head'; Slot = 10 }
    'BD_M' = [pscustomobject]@{ Kind = 'frame.core'; Slot = 20 }
    'AM_M' = [pscustomobject]@{ Kind = 'frame.arms'; Slot = 30 }
    'LG_M' = [pscustomobject]@{ Kind = 'frame.legs'; Slot = 40 }
}

foreach ($prefix in $frameKinds.Keys) {
    foreach ($dir in Get-ChildItem -LiteralPath $partsRoot -Directory | Where-Object { $_.Name -like ($prefix.ToLowerInvariant() + '_*') }) {
        $flver = Get-ChildItem -LiteralPath $dir.FullName -File -Filter ($prefix + '_*.flver') |
            Where-Object { $_.Name -notlike '*_L.flver' } |
            Select-Object -First 1
        $skeleton = Join-Path $dir.FullName 'skeleton.hkx'
        if ($null -eq $flver -or -not (Test-Path -LiteralPath $skeleton)) {
            continue
        }
        $specs.Add([pscustomobject]@{
            Key = $flver.BaseName
            Kind = $frameKinds[$prefix].Kind
            Slot = $frameKinds[$prefix].Slot
            SourceDir = $dir.FullName
            Flver = $flver.FullName
            Skeleton = $skeleton
            IsFrame = $true
        })
    }
}

foreach ($dir in Get-ChildItem -LiteralPath $partsRoot -Directory | Where-Object { $_.Name -like 'bs_a_*' }) {
    $flver = Get-ChildItem -LiteralPath $dir.FullName -File -Filter 'BS_A_*.flver' |
        Where-Object { $_.Name -notlike '*_L.flver' } |
        Select-Object -First 1
    $skeleton = Join-Path $dir.FullName 'skeleton.hkx'
    if ($null -ne $flver -and (Test-Path -LiteralPath $skeleton)) {
        $specs.Add([pscustomobject]@{
            Key = $flver.BaseName
            Kind = 'booster'
            Slot = 50
            SourceDir = $dir.FullName
            Flver = $flver.FullName
            Skeleton = $skeleton
            IsFrame = $false
        })
    }
}

$weaponsRoot = Join-Path $RawAssetsRoot 'wp'
foreach ($dir in Get-ChildItem -LiteralPath $weaponsRoot -Directory) {
    $skeleton = Join-Path $dir.FullName 'skeleton.hkx'
    if (-not (Test-Path -LiteralPath $skeleton)) {
        continue
    }
    foreach ($flver in Get-ChildItem -LiteralPath $dir.FullName -File -Filter 'WP_*.flver') {
        if ($flver.Name -like '*_L.flver' -or $flver.BaseName -notmatch '^WP_([LR])_(\d{4})$') {
            continue
        }
        $side = $Matches[1]
        $code = [int]$Matches[2]
        $isBack = $code -ge 4000
        $slot = if ($isBack) {
            if ($side -eq 'L') { 110 } else { 111 }
        } else {
            if ($side -eq 'L') { 100 } else { 101 }
        }
        $specs.Add([pscustomobject]@{
            Key = $flver.BaseName
            Kind = if ($isBack) { 'weapon.back' } else { 'weapon.hand' }
            Slot = $slot
            SourceDir = $dir.FullName
            Flver = $flver.FullName
            Skeleton = $skeleton
            IsFrame = $false
        })
    }
}

$existingManifest = @{}
if (Test-Path -LiteralPath $manifestPath) {
    try {
        foreach ($entry in @((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).entries)) {
            $existingManifest[$entry.key] = $entry
        }
    } catch {
        Write-Warning "Existing manifest could not be read and will be replaced: $($_.Exception.Message)"
    }
}

$results = [Collections.Generic.List[object]]::new()
$requestedSpecs = @($specs | Where-Object {
    (Test-KeyRequested $_.Key) -and ($Keys.Count -gt 0 -or $sampleProvidedKeys -notcontains $_.Key)
} | Sort-Object Key -Unique)
$index = 0
foreach ($spec in $requestedSpecs) {
    $index++
    Write-Host "[$index/$($requestedSpecs.Count)] $($spec.Key)"
    $subfolder = if ($spec.IsFrame) { 'Frames' } else { 'Equipment' }
    $outputRoot = if ($spec.IsFrame) { $frameOutput } else { $equipmentOutput }
    $output = Join-Path $outputRoot ($spec.Key + '_FULL.glb')
    $work = Join-Path $workRoot $spec.Key
    $reportPath = Join-Path $work 'report.json'
    $assetRelative = ($OutputAssetRoot.TrimEnd('\') + '\' + $subfolder + '\' + [IO.Path]::GetFileName($output)).Replace('\', '/')
    if (-not $Force -and (Test-Path -LiteralPath $output)) {
        $metrics = Get-AlignmentMetrics -ReportPath $reportPath
        $results.Add([pscustomobject]@{
            key = $spec.Key; kind = $spec.Kind; slot = $spec.Slot
            glbAssetPath = $assetRelative; success = $true; message = 'existing'
            maxPositionError = $metrics.maxPositionError
            maxRotationErrorDeg = $metrics.maxRotationErrorDeg
        })
        continue
    }

    $textures = Join-Path $work 'textures'
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $skeletonJson = Join-Path $work 'skeleton.json'
        Export-SkeletonJson -Skeleton $spec.Skeleton -Output $skeletonJson -Work $work
        Convert-Textures -Source $spec.SourceDir -Destination $textures
        $args = @($gltfExporter, '--skeleton-json')
        if ($spec.IsFrame) {
            $args += @($baseSkeletonJson, '--part-skeleton-json', $skeletonJson)
        } else {
            $args += @($skeletonJson)
        }
        $args += @('--flver', $spec.Flver, '--tex-dir', $textures)
        if ($spec.IsFrame) {
            $args += @('--anim', $pose)
        }
        $args += @('--out', $output, '--report', $reportPath)
        Invoke-Tool -FilePath $dotnet -ArgumentList $args | Out-Null
        $metrics = Get-AlignmentMetrics -ReportPath $reportPath
        $results.Add([pscustomobject]@{
            key = $spec.Key; kind = $spec.Kind; slot = $spec.Slot
            glbAssetPath = $assetRelative; success = $true; message = 'built'
            maxPositionError = $metrics.maxPositionError
            maxRotationErrorDeg = $metrics.maxRotationErrorDeg
        })
    } catch {
        Write-Warning "$($spec.Key) failed: $($_.Exception.Message)"
        $results.Add([pscustomobject]@{
            key = $spec.Key; kind = $spec.Kind; slot = $spec.Slot
            glbAssetPath = $assetRelative; success = $false; message = $_.Exception.Message
            maxPositionError = 0.0; maxRotationErrorDeg = 0.0
        })
    }
}

if ($Keys.Count -gt 0) {
    foreach ($pair in $existingManifest.GetEnumerator()) {
        if (-not ($results | Where-Object { $_.key -eq $pair.Key })) {
            $results.Add($pair.Value)
        }
    }
}

$manifest = [ordered]@{
    generatedAt = [DateTimeOffset]::Now.ToString('o')
    sourceRoot = $RawAssetsRoot
    entries = @($results | Sort-Object key)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$successCount = @($results | Where-Object success).Count
$failureCount = @($results | Where-Object { -not $_.success }).Count
Write-Host "Full part conversion complete: success=$successCount failure=$failureCount"
Write-Host "Manifest: $manifestPath"
