param(
    [string]$GameRoot = 'H:\ARMORED CORE VI FIRES OF RUBICON\Game',
    [int[]]$EffectIds = @(
        6011100, 6011101, 6011102, 6013101,
        6031100, 6031101,
        6082100, 6082101, 6083100, 6083101
    ),
    [switch]$ForceExtract
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$localOut = Join-Path $PSScriptRoot 'LocalOut'
$commonOut = Join-Path $localOut 'AC6NativeSfx\Common'
$jsonOut = Join-Path $localOut 'fxr_json'
$textureOut = Join-Path $localOut 'fxtex'
$textureScratch = Join-Path $localOut 'fxtex_dds'
$modelOut = Join-Path $localOut 'fxmodels'
$modelTextureOut = Join-Path $localOut 'fxmodel_tex'
$modelTextureScratch = Join-Path $localOut 'fxmodel_dds'
$binder = Join-Path $GameRoot 'sfx\sfxbnd_commoneffects.ffxbnd.dcx'
$probe = Join-Path $PSScriptRoot 'DotNet\AC6AnimExportProbe\bin\Debug\net9.0\AC6AnimExportProbe.dll'
$gltfExporter = Join-Path $PSScriptRoot 'DotNet\AC6PartToGltf\bin\Debug\net9.0\AC6PartToGltf.dll'
$sharedTools = Join-Path (Split-Path -Parent $repo) 'Tools'
$dotnetCandidates = @(
    (Join-Path $PSScriptRoot 'dotnet9sdk\dotnet.exe'),
    (Join-Path $sharedTools 'dotnet9sdk\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
)
$nodeCandidates = @(
    (Get-Command node -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe')
)
$dotnet = $dotnetCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
$node = $nodeCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
$translator = Join-Path $PSScriptRoot 'FxrTranslator\extract-fxr.mjs'
$texconv = Join-Path $localOut 'bin\texconv.exe'
$dsasCandidates = @(
    (Join-Path $PSScriptRoot 'DSAnimStudio\DSAS-V5-RC6.1'),
    (Join-Path $sharedTools 'DSAnimStudio\DSAS-V5-RC6.1')
)
$dsas = $dsasCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$skeletonCandidates = @(
    (Join-Path $localOut 'Melander1010Pipeline\c0000_skeleton.json'),
    (Join-Path $localOut 'Melander1010Rerun_20260710\c0000_skeleton.json'),
    (Join-Path $localOut 'ModularSkinned_20260710\c0000_skeleton.json')
)
$baseSkeleton = $skeletonCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

foreach ($required in @($binder, $probe, $gltfExporter, $dotnet, $node, $translator, $texconv, $dsas, $baseSkeleton)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required AC6 effect pipeline input is missing: $required"
    }
}

$env:AC6_DSAS_DIR = $dsas
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'dotnet-cli-home'
New-Item -ItemType Directory -Force -Path `
    $commonOut, $jsonOut, $textureOut, $textureScratch, `
    $modelOut, $modelTextureOut, $modelTextureScratch | Out-Null

if ($ForceExtract -or -not (Get-ChildItem -LiteralPath $commonOut -Filter '*.fxr' -ErrorAction SilentlyContinue)) {
    & $dotnet $probe --extract-bnd $binder $commonOut
    if ($LASTEXITCODE -ne 0) { throw "Common SFX binder extraction failed: $LASTEXITCODE" }
}

$fxrFiles = foreach ($effectId in $EffectIds) {
    $match = Get-ChildItem -LiteralPath $commonOut -Filter "*_f$($effectId.ToString('D9')).fxr" |
        Select-Object -First 1
    if ($null -eq $match) {
        Write-Warning "FXR $effectId was not found in the common SFX binder."
        continue
    }
    $match.FullName
}

$fxrFiles = @($fxrFiles)
if ($fxrFiles.Count -eq 0) { throw 'No selected FXR files were found.' }

# FXR proxy nodes are how AC6 aliases and composes effects. Resolve them recursively so
# effects such as vertical missiles do not appear empty merely because their root FXR is a proxy.
$translatedIds = [System.Collections.Generic.HashSet[int]]::new()
$pendingFiles = @($fxrFiles)
while ($pendingFiles.Count -gt 0) {
    & $node $translator $jsonOut @pendingFiles
    if ($LASTEXITCODE -ne 0) { throw "FXR translation failed: $LASTEXITCODE" }

    $nextFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($pendingFile in $pendingFiles) {
        $name = [IO.Path]::GetFileNameWithoutExtension($pendingFile)
        if ($name -notmatch 'f(?<id>\d{9})$') { continue }
        $effectId = [int]$Matches.id
        [void]$translatedIds.Add($effectId)
        $jsonPath = Join-Path $jsonOut "ffx_$effectId.json"
        $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
        foreach ($proxyId in @($json.proxyReferences)) {
            $proxyId = [int]$proxyId
            if ($translatedIds.Contains($proxyId)) { continue }
            $proxyFxr = Get-ChildItem -LiteralPath $commonOut -Filter "*_f$($proxyId.ToString('D9')).fxr" |
                Select-Object -First 1
            if ($null -eq $proxyFxr) {
                Write-Warning "Proxy FXR $proxyId referenced by $effectId was not found."
                continue
            }
            $nextFiles.Add($proxyFxr.FullName)
        }
    }
    $pendingFiles = @($nextFiles | Select-Object -Unique)
}

$textureIds = [System.Collections.Generic.HashSet[int]]::new()
$modelIds = [System.Collections.Generic.HashSet[int]]::new()
$modelTextureNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$translatedEffects = 0
$unityEmitterCount = 0
foreach ($effectId in $translatedIds) {
    $jsonPath = Join-Path $jsonOut "ffx_$effectId.json"
    if (-not (Test-Path -LiteralPath $jsonPath)) { continue }
    $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $translatedEffects++
    $unityEmitterCount += @($json.unityEmitters).Count
    foreach ($emitter in $json.unityEmitters) {
        if ($emitter.texture -gt 0) { [void]$textureIds.Add([int]$emitter.texture) }
        if ($emitter.kind -eq 'Model' -and $emitter.model -gt 0) {
            [void]$modelIds.Add([int]$emitter.model)
        }
    }
}

if ($translatedEffects -ne $translatedIds.Count) {
    throw "FXR translation was incomplete: expected $($translatedIds.Count), got $translatedEffects JSON files."
}

foreach ($textureId in $textureIds) {
    $tpf = Get-ChildItem -LiteralPath $commonOut -Filter "*_s$($textureId.ToString('D5'))_a.tpf" |
        Select-Object -First 1
    if ($null -eq $tpf) {
        Write-Warning "Texture s$($textureId.ToString('D5'))_a was not found."
        continue
    }

    & $dotnet $probe --extract-tpf $tpf.FullName $textureScratch
    if ($LASTEXITCODE -ne 0) { throw "TPF extraction failed for $($tpf.Name): $LASTEXITCODE" }
}

Get-ChildItem -LiteralPath $textureScratch -Filter '*.dds' | ForEach-Object {
    & $texconv -ft png -y -o $textureOut $_.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "texconv failed for $($_.Name): $LASTEXITCODE" }
}

# FXR Model/RichModel nodes reference small FLVER meshes in the same common binder.
# Export those meshes through the established native FLVER -> glTF path so Unity uses
# the original projectile/flash geometry instead of a hand-authored placeholder.
foreach ($modelId in $modelIds) {
    $modelToken = "s$($modelId.ToString('D5'))"
    $flver = Get-ChildItem -LiteralPath $commonOut -Filter "*_$modelToken.flver" |
        Select-Object -First 1
    if ($null -eq $flver) {
        Write-Warning "FXR model $modelToken was not found."
        continue
    }

    $modelTpfs = @(Get-ChildItem -LiteralPath $commonOut -Filter "*_$modelToken`_*.tpf")
    foreach ($tpf in $modelTpfs) {
        & $dotnet $probe --extract-tpf $tpf.FullName $modelTextureScratch
        if ($LASTEXITCODE -ne 0) { throw "TPF extraction failed for $($tpf.Name): $LASTEXITCODE" }
    }

    Get-ChildItem -LiteralPath $modelTextureScratch -Filter "$modelToken`_*.dds" | ForEach-Object {
        & $texconv -ft png -y -o $modelTextureOut $_.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "texconv failed for $($_.Name): $LASTEXITCODE" }
    }

    $outputGlb = Join-Path $modelOut "$modelToken.glb"
    $report = Join-Path $modelOut "$modelToken.report.json"
    & $dotnet $gltfExporter `
        --skeleton-json $baseSkeleton `
        --flver $flver.FullName `
        --tex-dir $modelTextureOut `
        --report $report `
        --out $outputGlb
    if ($LASTEXITCODE -ne 0) { throw "FLVER export failed for $modelToken`: $LASTEXITCODE" }

    # Many common-effect FLVERs use shared textures whose stems do not match the
    # model (for example s81032.flver -> s34010_a.tif). The first export report is
    # the authoritative material lookup; extract those TPFs and export once more.
    $reportJson = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $referencedTextureNames = @(
        $reportJson.Parts |
            ForEach-Object { $_.Materials } |
            ForEach-Object { $_.Textures } |
            ForEach-Object { $_.Path } |
            Where-Object { $_ } |
            ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_) } |
            Select-Object -Unique
    )

    $extractedSharedTexture = $false
    foreach ($textureName in $referencedTextureNames) {
        [void]$modelTextureNames.Add($textureName)
        $sharedTpf = Get-ChildItem -LiteralPath $commonOut -Filter "*_$textureName.tpf" |
            Select-Object -First 1
        if ($null -eq $sharedTpf) {
            Write-Warning "Shared FXR model texture $textureName referenced by $modelToken was not found."
            continue
        }

        & $dotnet $probe --extract-tpf $sharedTpf.FullName $modelTextureScratch
        if ($LASTEXITCODE -ne 0) { throw "TPF extraction failed for $($sharedTpf.Name): $LASTEXITCODE" }

        Get-ChildItem -LiteralPath $modelTextureScratch -Filter "$textureName*.dds" | ForEach-Object {
            & $texconv -ft png -y -o $modelTextureOut $_.FullName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "texconv failed for $($_.Name): $LASTEXITCODE" }
            $extractedSharedTexture = $true
        }
    }

    if ($extractedSharedTexture) {
        & $dotnet $gltfExporter `
            --skeleton-json $baseSkeleton `
            --flver $flver.FullName `
            --tex-dir $modelTextureOut `
            --report $report `
            --out $outputGlb
        if ($LASTEXITCODE -ne 0) { throw "FLVER re-export failed for $modelToken`: $LASTEXITCODE" }
    }
}

$profiles = @(
    [ordered]@{ slot='RightHandWeapon'; equipment='MA-J-200 RANSETSU-RF'; equipParamId=10000000; bulletParamId=10000000; launchFfxId=6011100; projectileFfxId=6013101; impactFfxId=6011101 },
    [ordered]@{ slot='LeftHandWeapon'; equipment='DF-GR-07 GOU-CHEN'; equipParamId=15060000; bulletParamId=10600000; launchFfxId=6031100; projectileFfxId=6031101; impactFfxId=0 },
    [ordered]@{ slot='LeftBackWeapon'; equipment='BML-G1/P07VTC-12'; equipParamId=35040000; bulletParamId=30400000; launchFfxId=6082100; projectileFfxId=6082101; impactFfxId=0 },
    [ordered]@{ slot='RightBackWeapon'; equipment='BML-G2/P03MLT-06'; equipParamId=30020000; bulletParamId=30020000; launchFfxId=6083100; projectileFfxId=6083101; impactFfxId=0 }
)
$manifest = [ordered]@{
    schemaVersion = 1
    sourceBinder = $binder
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    effectIds = $EffectIds
    resolvedEffectIds = @($translatedIds | Sort-Object)
    textureIds = @($textureIds | Sort-Object)
    modelIds = @($modelIds | Sort-Object)
    modelTextureNames = @($modelTextureNames | Sort-Object)
    weaponProfiles = $profiles
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $localOut 'AC6NativeSfx\native_effect_manifest.json') -Encoding utf8

Write-Host "AC6 native effect pipeline complete."
Write-Host "  FXR JSON: $jsonOut"
Write-Host "  PNG textures: $textureOut"
Write-Host "  FXR models: $modelOut"
Write-Host "  Requested effects: $($EffectIds.Count); resolved effects: $($translatedIds.Count); textures: $($textureIds.Count); models: $($modelIds.Count)"
Write-Host "  Unity emitters: $unityEmitterCount"
Write-Host 'Next: in Unity run Tools > ACunicombat > AC6 > Build Playable Player Scene.'
