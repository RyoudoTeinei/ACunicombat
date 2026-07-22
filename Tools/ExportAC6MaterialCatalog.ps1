param(
    [string]$MaterialArchive = "H:\ARMORED CORE VI FIRES OF RUBICON\Game\material\allmaterial.matbinbnd.dcx",
    [string]$WitchyBnd = "E:\UnityDev\Resources\AC6_ExtractedAssets\Extraction and conversion\WitchyBND-v1.0.7.4\WitchyBND.exe",
    [string]$DotnetRoot = "E:\UnityDev\Tools\dotnet9",
    [string]$OutputPath = "Assets\_Project\Data\Mechs\AC6Materials\ac6_frame_material_catalog.json"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$workRoot = Join-Path $projectRoot "Tools\LocalOut\AC6MaterialCatalog"
$archiveCopy = Join-Path $workRoot "allmaterial.matbinbnd.dcx"
$unpackedRoot = Join-Path $workRoot "allmaterial-matbinbnd-dcx"
$xmlRoot = Join-Path $workRoot "Xml"
$absoluteOutput = Join-Path $projectRoot $OutputPath

foreach ($requiredPath in @($MaterialArchive, $WitchyBnd, $DotnetRoot)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required path was not found: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $workRoot, $xmlRoot | Out-Null
if (-not (Test-Path -LiteralPath $archiveCopy)) {
    Copy-Item -LiteralPath $MaterialArchive -Destination $archiveCopy
}

$env:DOTNET_ROOT = $DotnetRoot
$env:DOTNET_ROLL_FORWARD = "Major"

if (-not (Test-Path -LiteralPath $unpackedRoot)) {
    Push-Location $workRoot
    try {
        & $WitchyBnd $archiveCopy | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "WitchyBND failed to unpack the material archive (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }
}

$partsRoot = Join-Path $unpackedRoot "Parts"
$materialPattern = '^(ACparts\[(AM|BD|HD|LG)_M_(1010|2010|3010).+|ACparts\[Prats_Marking\]|P\[BS_(1010|2010|3010)\]|P\[WP_(0010|0230|0250|1310|1410|1610|4210|4250|4290|4410)(?:_[^\]]+)?\]).*\.matbin$'
$materialFiles = @(Get-ChildItem -LiteralPath $partsRoot -File -Filter "*.matbin" |
    Where-Object { $_.Name -match $materialPattern } |
    Sort-Object Name)

if ($materialFiles.Count -eq 0) {
    throw "No matching frame MATBIN files were found under $partsRoot"
}

Push-Location $xmlRoot
try {
    foreach ($materialFile in $materialFiles) {
        $xmlPath = Join-Path $xmlRoot ($materialFile.Name + ".xml")
        if (Test-Path -LiteralPath $xmlPath) {
            continue
        }

        & $WitchyBnd $materialFile.FullName | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $xmlPath)) {
            throw "WitchyBND failed to decode $($materialFile.FullName)"
        }
    }
}
finally {
    Pop-Location
}

function Get-ParamText {
    param(
        [xml]$Document,
        [string]$Name,
        [string]$Fallback = ""
    )

    $node = @($Document.MATBIN.Params.Param | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)
    if ($node.Count -eq 0) {
        return $Fallback
    }

    return [string]$node[0].Value.InnerText
}

function Get-PrimarySamplerPath {
    param(
        [object[]]$Samplers,
        [string]$Semantic
    )

    $candidateSamplers = @($Samplers | Where-Object { $_.semantic -eq $Semantic -and $_.path })
    if ($candidateSamplers.Count -eq 0) {
        return ""
    }

    $preferredSuffix = switch ($Semantic) {
        "AlbedoMap" { '_a(?:\.tif)?$' }
        "NormalMap" { '_n(?:\.tif)?$' }
        "Mask3Map" { '_3m(?:\.tif)?$' }
        "MetallicMap" { '_m(?:\.tif)?$' }
        "EmissiveMap" { '_em(?:\.tif)?$' }
        default { '' }
    }
    if ($preferredSuffix) {
        $preferredSampler = @($candidateSamplers | Where-Object {
            $_.path -match $preferredSuffix
        } | Select-Object -First 1)
        if ($preferredSampler.Count -gt 0) {
            return [string]$preferredSampler[0]["path"]
        }
    }

    $partSpecific = @($candidateSamplers | Where-Object {
        $_.path -notmatch '^(AET|SYSTEX|c\d+_SYSTEX)'
    } | Select-Object -First 1)
    if ($partSpecific.Count -gt 0) {
        return [string]$partSpecific[0]["path"]
    }

    return [string]$candidateSamplers[0]["path"]
}

function Resolve-SamplerSemantic {
    param([string]$Type)

    foreach ($semantic in @("Mask3Map", "Mask1Map", "MetallicMap", "EmissiveMap", "NormalMap", "AlbedoMap")) {
        if ($Type -match ($semantic + '(_\d+)?$')) {
            return $semantic
        }
    }

    return "Unknown"
}

$records = foreach ($materialFile in $materialFiles) {
    $xmlPath = Join-Path $xmlRoot ($materialFile.Name + ".xml")
    [xml]$document = Get-Content -LiteralPath $xmlPath -Encoding UTF8
    $shaderPath = [string]$document.MATBIN.ShaderPath
    $paintEnabled = (Get-ParamText $document "GXFT_PaintDecal_Mat" "false") -eq "true"
    $emissiveEnabled = (Get-ParamText $document "GXFT_Emissive" "false") -eq "true"
    $samplers = @($document.MATBIN.Samplers.Sampler | ForEach-Object {
        [ordered]@{
            semantic = Resolve-SamplerSemantic ([string]$_.Type)
            type = [string]$_.Type
            path = [string]$_.Path
        }
    })
    $hasMetallicMap = @($samplers | Where-Object { $_.semantic -eq "MetallicMap" -and $_.path }).Count -gt 0

    $surfaceClass = if ($shaderPath -match "ColorMaterial" -and $paintEnabled) {
        "PaintedArmor"
    }
    elseif ($shaderPath -match "Emissive" -or $emissiveEnabled) {
        "Emissive"
    }
    elseif ($hasMetallicMap) {
        "MetallicPbr"
    }
    else {
        "Opaque"
    }

    $recommendedMetallic = switch ($surfaceClass) {
        "PaintedArmor" { 0.12 }
        "MetallicPbr" { 0.65 }
        default { 0.05 }
    }
    $recommendedSmoothness = switch ($surfaceClass) {
        "Emissive" { 0.45 }
        "MetallicPbr" { 0.42 }
        default { 0.30 }
    }

    [ordered]@{
        matbinFile = $materialFile.Name
        sourcePath = [string]$document.MATBIN.SourcePath
        shaderPath = $shaderPath
        surfaceClass = $surfaceClass
        blendMode = [int](Get-ParamText $document "g_BlendMode" "0")
        disableDepthWrite = (Get-ParamText $document "g_GX_bDisableDepthWrite" "false") -eq "true"
        cullNone = (Get-ParamText $document "g_GX_PrimCullNone" "false") -eq "true"
        paintDecal = $paintEnabled
        emissive = $emissiveEnabled
        materialId = [int](Get-ParamText $document "g_GX_MaterialID" "0")
        albedoMap = Get-PrimarySamplerPath $samplers "AlbedoMap"
        normalMap = Get-PrimarySamplerPath $samplers "NormalMap"
        mask3Map = Get-PrimarySamplerPath $samplers "Mask3Map"
        metallicMap = Get-PrimarySamplerPath $samplers "MetallicMap"
        emissionMap = Get-PrimarySamplerPath $samplers "EmissiveMap"
        mask3Role = if (@($samplers | Where-Object { $_.semantic -eq "Mask3Map" }).Count -gt 0) { "PaintAndMaterialZoneMask" } else { "None" }
        recommendedMetallic = $recommendedMetallic
        recommendedSmoothness = $recommendedSmoothness
        samplers = $samplers
    }
}

$catalog = [ordered]@{
    schemaVersion = 1
    sourceArchive = "Game/material/allmaterial.matbinbnd.dcx"
    scope = "AC6 frame sets 1010, 2010, 3010 plus shared marking, matching boosters and sample weapons"
    notes = @(
        "MATBIN identifies shader variants, render flags and sampler semantics.",
        "Mask3Map is a paint/material-zone selector and must not be interpreted as metallic, smoothness or ambient occlusion.",
        "Generated SPX parameter names do not preserve enough semantics to reproduce the complete original shader equation."
    )
    materials = @($records)
}

$outputDirectory = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$json = $catalog | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($absoluteOutput, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "Exported $($records.Count) AC6 material records to $absoluteOutput"
