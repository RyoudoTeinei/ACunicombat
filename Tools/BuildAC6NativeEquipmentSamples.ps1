param(
    [string]$RawAssetsRoot = 'E:\UnityDev\Resources\AC6_ExtractedAssets',
    [string]$GameRoot = 'E:\SteamLibrary\steamapps\common\ARMORED CORE VI FIRES OF RUBICON\Game',
    [string]$TexConvPath = '',
    [string]$OutputAssetRoot = 'Assets\_Project\LocalSamples\AC6SkinnedModular\Raw\NativeEquipment'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'E:\UnityDev\Tools\dotnet9sdk\dotnet.exe'
$probeProject = Join-Path $projectRoot 'Tools\DotNet\AC6AnimExportProbe\AC6AnimExportProbe.csproj'
$poseProject = Join-Path $projectRoot 'Tools\DotNet\AC6HkxPoseExporter\AC6HkxPoseExporter.csproj'
$gltfProject = Join-Path $projectRoot 'Tools\DotNet\AC6PartToGltf\AC6PartToGltf.csproj'
$probe = Join-Path $projectRoot 'Tools\DotNet\AC6AnimExportProbe\bin\Debug\net9.0\AC6AnimExportProbe.dll'
$poseExporter = Join-Path $projectRoot 'Tools\DotNet\AC6HkxPoseExporter\bin\Debug\net9.0\AC6HkxPoseExporter.dll'
$gltfExporter = Join-Path $projectRoot 'Tools\DotNet\AC6PartToGltf\bin\Debug\net9.0\AC6PartToGltf.dll'
$workRoot = Join-Path $projectRoot 'Tools\LocalOut\AC6NativeEquipment'
$assetOutput = Join-Path $projectRoot $OutputAssetRoot

if ([string]::IsNullOrWhiteSpace($TexConvPath)) {
    $TexConvPath = Join-Path $projectRoot 'Tools\LocalOut\bin\texconv.exe'
}

foreach ($required in @($dotnet, $RawAssetsRoot, $GameRoot, $TexConvPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required path is missing: $required"
    }
}

$env:DOTNET_CLI_HOME = 'E:\UnityDev\Tools\dotnet-cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = 'E:\UnityDev\Tools\nuget-packages'
$oodleDir = Join-Path $GameRoot 'AC6remake'
if (Test-Path -LiteralPath (Join-Path $oodleDir 'oo2core_8_win64.dll')) {
    $env:PATH = "$oodleDir;$env:PATH"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][object[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($ArgumentList -join ' ')"
    }
}

foreach ($project in @($probeProject, $poseProject, $gltfProject)) {
    Invoke-Checked -FilePath $dotnet -ArgumentList @('build', $project, '--no-restore', '--nologo')
}

New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
New-Item -ItemType Directory -Path $assetOutput -Force | Out-Null

$specs = @(
    [pscustomobject]@{ Key = 'WP_L_0010'; Binder = 'wp_l_0010.partsbnd.dcx'; TextureBinder = $null; RawDir = 'wp\wp_0010'; Flver = 'WP_L_0010.flver'; Kind = 'Weapon' },
    [pscustomobject]@{ Key = 'WP_L_0230'; Binder = 'wp_l_0230.partsbnd.dcx'; TextureBinder = $null; RawDir = 'wp\wp_0230'; Flver = 'WP_L_0230.flver'; Kind = 'Weapon' },
    [pscustomobject]@{ Key = 'WP_L_0250'; Binder = 'wp_l_0250.partsbnd.dcx'; TextureBinder = $null; RawDir = 'wp\wp_0250'; Flver = 'WP_L_0250.flver'; Kind = 'Weapon' },
    [pscustomobject]@{ Key = 'WP_L_4210'; Binder = 'wp_l_4210.partsbnd.dcx'; TextureBinder = 'wp_4210.tpf.dcx'; RawDir = $null; Flver = 'WP_L_4210.flver'; Kind = 'Weapon' },
    [pscustomobject]@{ Key = 'WP_R_4210'; Binder = 'wp_r_4210.partsbnd.dcx'; TextureBinder = 'wp_4210.tpf.dcx'; RawDir = $null; Flver = 'WP_R_4210.flver'; Kind = 'Weapon' },
    [pscustomobject]@{ Key = 'WP_R_4410'; Binder = 'wp_r_4410.partsbnd.dcx'; TextureBinder = 'wp_4410.tpf.dcx'; RawDir = $null; Flver = 'WP_R_4410.flver'; Kind = 'Weapon' },
    [pscustomobject]@{ Key = 'BS_A_1010'; Binder = 'bs_a_1010.partsbnd.dcx'; TextureBinder = $null; RawDir = 'parts\bs_a_1010'; Flver = 'BS_A_1010.flver'; Kind = 'Booster' },
    [pscustomobject]@{ Key = 'BS_A_2010'; Binder = 'bs_a_2010.partsbnd.dcx'; TextureBinder = $null; RawDir = 'parts\bs_a_2010'; Flver = 'BS_A_2010.flver'; Kind = 'Booster' },
    [pscustomobject]@{ Key = 'BS_A_3010'; Binder = 'bs_a_3010.partsbnd.dcx'; TextureBinder = $null; RawDir = 'parts\bs_a_3010'; Flver = 'BS_A_3010.flver'; Kind = 'Booster' }
)

$manifest = @()
foreach ($spec in $specs) {
    Write-Host "`n=== $($spec.Key) ==="
    $binder = Join-Path (Join-Path $GameRoot 'parts') $spec.Binder
    foreach ($required in @($binder)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Equipment source is missing: $required"
        }
    }

    $work = Join-Path $workRoot $spec.Key
    $outer = Join-Path $work 'outer'
    $inner = Join-Path $work 'anibnd'
    $poses = Join-Path $work 'poses'
    $png = Join-Path $work 'png'
    foreach ($dir in @($work, $outer, $inner, $poses, $png)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    if ([string]::IsNullOrWhiteSpace($spec.RawDir)) {
        $rawDir = Join-Path $work 'raw'
        New-Item -ItemType Directory -Path $rawDir -Force | Out-Null
        Invoke-Checked -FilePath $dotnet -ArgumentList @($probe, '--extract-bnd', $binder, $rawDir)
        $flver = Get-ChildItem -LiteralPath $rawDir -File | Where-Object {
            $_.Name.EndsWith('_' + $spec.Flver, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1 -ExpandProperty FullName
        if ([string]::IsNullOrWhiteSpace($flver)) {
            throw "FLVER $($spec.Flver) was not found in $binder"
        }

        if (-not [string]::IsNullOrWhiteSpace($spec.TextureBinder)) {
            $textureBinder = Join-Path (Join-Path $GameRoot 'parts') $spec.TextureBinder
            if (-not (Test-Path -LiteralPath $textureBinder)) {
                throw "Texture source is missing: $textureBinder"
            }
            Invoke-Checked -FilePath $dotnet -ArgumentList @($probe, '--extract-tpf', $textureBinder, $rawDir)
        }
    }
    else {
        $rawDir = Join-Path $RawAssetsRoot $spec.RawDir
        $flver = Join-Path $rawDir $spec.Flver
        foreach ($required in @($rawDir, $flver)) {
            if (-not (Test-Path -LiteralPath $required)) {
                throw "Equipment source is missing: $required"
            }
        }
    }

    Invoke-Checked -FilePath $dotnet -ArgumentList @($probe, '--extract-bnd', $binder, $outer, '400')
    $animationBinder = Get-ChildItem -LiteralPath $outer -File | Where-Object { $_.Name -match '\.anibnd$' } | Select-Object -First 1
    if ($null -eq $animationBinder) {
        throw "Nested animation binder was not found in $binder"
    }

    Invoke-Checked -FilePath $dotnet -ArgumentList @($probe, '--extract-bnd', $animationBinder.FullName, $inner)
    $skeleton = Get-ChildItem -LiteralPath $inner -File | Where-Object { $_.Name -match '_skeleton\.hkx$' } | Select-Object -First 1
    $compendium = Get-ChildItem -LiteralPath $inner -File -Filter '*.compendium' | Select-Object -First 1
    $animations = @(Get-ChildItem -LiteralPath $inner -File | Where-Object { $_.Name -match '_a\d{3}_\d+\.hkx$' } | Sort-Object Name)
    if ($null -eq $skeleton) {
        throw "Native skeleton is missing for $($spec.Key)"
    }

    $skeletonJson = Join-Path $work 'skeleton.json'
    $skeletonArgs = @(
        $poseExporter, '--skeleton', $skeleton.FullName, '--skeleton-only', '--out', $skeletonJson
    )
    if ($null -ne $compendium) {
        $skeletonArgs += @('--compendium', $compendium.FullName)
    }
    Invoke-Checked -FilePath $dotnet -ArgumentList $skeletonArgs

    $posePaths = @()
    if ($animations.Count -gt 0) {
        if ($null -eq $compendium) {
            throw "Animation compendium is missing for $($spec.Key)"
        }

        foreach ($animation in $animations) {
            $posePath = Join-Path $poses ($animation.BaseName + '.pose.json')
            Invoke-Checked -FilePath $dotnet -ArgumentList @(
                $poseExporter, '--skeleton', $skeleton.FullName, '--anim', $animation.FullName,
                '--compendium', $compendium.FullName, '--out', $posePath
            )
            $posePaths += $posePath
        }
    }

    $ddsFiles = @(Get-ChildItem -LiteralPath $rawDir -File -Filter '*.dds' | Where-Object {
        $_.BaseName -notmatch '_l$' -and $_.BaseName -notmatch '_L$' -and
        $_.BaseName -notmatch '_u$' -and $_.BaseName -notmatch '_U$'
    })
    foreach ($dds in $ddsFiles) {
        Invoke-Checked -FilePath $TexConvPath -ArgumentList @('-nologo', '-y', '-ft', 'png', '-o', $png, $dds.FullName)
    }

    $outputGlb = Join-Path $assetOutput ($spec.Key + '_NATIVE.glb')
    $reportPath = Join-Path $work 'export-report.json'
    $gltfArgs = @(
        $gltfExporter, '--skeleton-json', $skeletonJson, '--flver', $flver,
        '--tex-dir', $png, '--report', $reportPath, '--out', $outputGlb
    )
    foreach ($posePath in $posePaths) {
        $gltfArgs += @('--anim', $posePath)
    }
    Invoke-Checked -FilePath $dotnet -ArgumentList $gltfArgs

    $compendiumName = $null
    if ($null -ne $compendium) {
        $compendiumName = $compendium.Name
    }
    $animationNames = @($animations | ForEach-Object { $_.Name })
    $manifest += [pscustomobject]@{
        key = $spec.Key
        kind = $spec.Kind
        glb = [IO.Path]::GetFileName($outputGlb)
        sourceFlver = $flver
        sourceBinder = $binder
        skeleton = $skeleton.Name
        compendium = $compendiumName
        animationCount = $animations.Count
        animations = $animationNames
    }
}

$manifestPath = Join-Path $assetOutput 'AC6NativeEquipmentManifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "`nNative equipment pipeline complete: $assetOutput"
Write-Host "Manifest: $manifestPath"
