param(
    [string]$Bhv = 'E:\UnityDev\Resources\AC6_ExtractedAssets\chr\AC - c0000\basenormal.bhv',
    [string]$DebugMetadata = 'Tools/BhvDebug/c0000/basenormal_dbg.json',
    [string]$TaeDump = 'Tools/LocalOut/c0000_tae_dump.json',
    [string]$OutputJson = 'Tools/AssetManifests/c0000_bhv_state_map.json',
    [string]$OutputCsv = 'Tools/AssetManifests/c0000_bhv_state_map.csv'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepoPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return Join-Path $repoRoot $path
}

$dotnet = 'E:\UnityDev\Tools\dotnet9sdk\dotnet.exe'
$project = Join-Path $repoRoot 'Tools/DotNet/AC6BhvParser/AC6BhvParser.csproj'
$bhvPath = Resolve-RepoPath $Bhv
$debugPath = Resolve-RepoPath $DebugMetadata
$taeDumpPath = Resolve-RepoPath $TaeDump
$jsonPath = Resolve-RepoPath $OutputJson
$csvPath = Resolve-RepoPath $OutputCsv

if (-not (Test-Path -LiteralPath $dotnet)) { throw "Bundled .NET SDK not found: $dotnet" }
if (-not (Test-Path -LiteralPath $bhvPath)) { throw "BHV not found: $bhvPath" }
if (-not (Test-Path -LiteralPath $debugPath)) { throw "Debug metadata not found: $debugPath" }
if (-not (Test-Path -LiteralPath $taeDumpPath)) { throw "TAE dump not found: $taeDumpPath" }

$env:DOTNET_CLI_HOME = 'E:\UnityDev\Tools\dotnet-cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& $dotnet run --project $project --configuration Release -- `
    --bhv $bhvPath `
    --debug $debugPath `
    --tae $taeDumpPath `
    --tae-file-prefix 5000000 `
    --json $jsonPath `
    --csv $csvPath
if ($LASTEXITCODE -ne 0) {
    throw "AC6BhvParser failed with exit code $LASTEXITCODE."
}

$map = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not [bool]$map.alignment.isSafeToApplyOfficialNames) {
    throw 'BHV parser completed, but official debug metadata did not align with the binary.'
}

$expectedMappings = @(
    @{ State = 47; Action = 'a000_120200' },
    @{ State = 61; Action = 'a000_004200' },
    @{ State = 77; Action = 'a000_022400' },
    @{ State = 82; Action = 'a000_122400' },
    @{ State = 176; Action = 'a000_050100' }
)
foreach ($expected in $expectedMappings) {
    $state = $map.states[[int]$expected.State]
    if ([string]$state.primaryActionId -ne [string]$expected.Action -or
        [string]::IsNullOrWhiteSpace([string]$state.officialStateName)) {
        throw "Known mapping validation failed at state $($expected.State): expected $($expected.Action)."
    }
}

Write-Host "Validated $($expectedMappings.Count) known official state-to-animation mappings."

$physicalAliasChecks = @(
    @{ State = 177; Logical = 'a000_150100'; Physical = 'a000_050100' },
    @{ State = 106; Logical = 'a000_022525'; Physical = 'a000_022524' }
)
foreach ($expected in $physicalAliasChecks) {
    $state = $map.states[[int]$expected.State]
    if ([string]$state.primaryActionId -ne [string]$expected.Logical -or
        [string]$state.primaryPhysicalActionId -ne [string]$expected.Physical) {
        throw "TAE physical alias validation failed at state $($expected.State)."
    }
}

Write-Host "Validated $($physicalAliasChecks.Count) TAE logical-to-physical aliases."
