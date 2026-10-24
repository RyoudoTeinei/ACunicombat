[CmdletBinding()]
param(
    [ValidateSet('Smoke','Regression','Replay','Baseline','Layout','Equipment','Damage','Defense')][string]$Mode='Smoke',
    [string]$ProjectPath=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$Scenario,
    [string]$ReplayFrom,
    [string]$UnityPath,
    [string]$PythonPath='python',
    [string]$OutputPath,
    [ValidateRange(30,3600)][int]$TimeoutSeconds=600,
    [ValidateSet("ProjectDefault","Direct3D11")][string]$GraphicsApi="ProjectDefault",
    [switch]$ContractOnly
)
$ErrorActionPreference='Stop'
$ProjectPath=[IO.Path]::GetFullPath($ProjectPath)
if(-not $OutputPath){$OutputPath=Join-Path $ProjectPath ('Artifacts/SensorBench/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath $OutputPath){throw 'Output directory already exists; runs are immutable. Choose a fresh directory.'}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
function Write-Json($name,$value){[IO.File]::WriteAllText((Join-Path $OutputPath $name),($value|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))}
$originalSettingsBytes=$null
$originalSettingsText=$null
$settingsPath=Join-Path $ProjectPath 'ProjectSettings/ProjectSettings.asset'
try {
    if($PythonPath -eq 'python'){
        $bundledPython=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
        $command=Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if(($null -eq $command -or $command.Source -like '*WindowsApps*') -and (Test-Path -LiteralPath $bundledPython)){$PythonPath=$bundledPython}
    }
    $originalSettingsBytes=[IO.File]::ReadAllBytes($settingsPath)
    $originalSettingsText=[IO.File]::ReadAllText($settingsPath)
    $versionFile=Join-Path $ProjectPath 'ProjectSettings/ProjectVersion.txt'
    $version=((Get-Content -LiteralPath $versionFile | Where-Object {$_ -match '^m_EditorVersion: '}) -split ': ',2)[1]
    if(-not $UnityPath){$UnityPath=Join-Path ${env:ProgramFiles} ('Unity/Hub/Editor/'+$version+'/Editor/Unity.exe')}
    if(-not (Test-Path -LiteralPath $UnityPath)){throw "Required Unity $version not found: $UnityPath"}
    $installedVersion=(Get-Item -LiteralPath $UnityPath).VersionInfo.ProductVersion
    if(-not $installedVersion.StartsWith($version)){throw "Unity mismatch: project=$version installed=$installedVersion"}
    $lock=Join-Path $ProjectPath 'Temp/UnityLockfile'
    if(Test-Path -LiteralPath $lock){
        try{$handle=[IO.File]::Open($lock,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$handle.Dispose()}
        catch{throw 'Project is open in another Unity editor. Use a separate complete checkout or close it yourself; this runner never closes your editor.'}
    }
    & $PythonPath -c 'import sys; assert sys.version_info >= (3, 9)' 
    if($LASTEXITCODE -ne 0){throw 'Python 3.9+ required; set -PythonPath explicitly.'}
    if($Mode -eq 'Replay'){
        if(-not $ReplayFrom){throw 'Replay requires -ReplayFrom run directory.'}
        $Scenario=Join-Path $ReplayFrom 'scenario.json'
        $oldManifest=Get-Content -Raw -LiteralPath (Join-Path $ReplayFrom 'manifest.json') | ConvertFrom-Json
        if((Get-FileHash -LiteralPath $Scenario).Hash -ne $oldManifest.scenarioHash){throw 'Replay scenario hash mismatch'}
    } elseif(-not $Scenario){$Scenario=Join-Path $ProjectPath 'Assets/_Project/Tests/SensorBench/Scenarios/radar-paired-observers.json'}
    $spec=Get-Content -Raw -LiteralPath $Scenario | ConvertFrom-Json
    if($Mode -eq 'Smoke'){$spec.seeds=@($spec.seeds | Select-Object -First 3)}
    Write-Json 'scenario.json' $spec
    $commit=git -C $ProjectPath rev-parse HEAD
    if($LASTEXITCODE -ne 0){throw 'Project must have Git provenance'}
    $dirty=@(git -C $ProjectPath -c core.quotepath=false status --porcelain=v1)
    $sources=@{}
    foreach($sub in @('Assets/_Project/Scripts','Assets/_Project/Resources/Shaders','Assets/_Project/Resources/DefenseExtensions','Assets/Resources/Audio/APS','Assets/_Project/Editor','Assets/_Project/Tests/SensorBench','Assets/_Project/Tests/Fixtures','Assets/_Project/Data/Mechs/Layouts','Assets/_Project/Data/Mechs/Samples','Tools/SensorBench','ProjectSettings','Packages')){
        foreach($file in Get-ChildItem -LiteralPath (Join-Path $ProjectPath $sub) -Recurse -File | Where-Object Extension -In '.cs','.asmdef','.json','.txt','.asset','.py','.ps1','.shader','.ogg','.wav'){
            $relative=$file.FullName.Substring($ProjectPath.Length+1).Replace('\','/')
            $sources[$relative]=(Get-FileHash -LiteralPath $file.FullName).Hash
        }
    }
    if($Mode -eq 'Replay'){
        $old=$oldManifest.sourceHashes
        if($sources.Count -ne $old.PSObject.Properties.Count){
            # Count through enumeration because scalar PSObject property collections vary by host.
            if($sources.Count -ne @($old.PSObject.Properties).Count){throw 'Replay source set changed'}
        }
        foreach($key in $sources.Keys){if($old.$key -ne $sources[$key]){throw "Replay source changed: $key"}}
    }
    $manifest=@{schemaVersion=1;mode=$Mode;executionMode='FullUnityProject';commit=$commit;worktreeStatus=$dirty;
        unityVersion=$version;graphicsApi=$GraphicsApi;scenarioHash=(Get-FileHash -LiteralPath (Join-Path $OutputPath 'scenario.json')).Hash;
        sourceHashes=$sources;startedUtc=[DateTime]::UtcNow.ToString('o');replayFrom=$ReplayFrom;contractOnly=[bool]$ContractOnly}
    Write-Json 'manifest.json' $manifest
    $filter='ACunicombat.SensorBench'
    if($Mode -eq 'Regression'){$filter+=';ACunicombat.Tests.EditMode;MechMissileTrackLifecycleTests;MechSceneEntityLifecycleTests'}
    if($Mode -eq 'Layout'){$filter+=';MechVariableLayoutTests;MechSceneEntityLifecycleTests;MechMissileTrackLifecycleTests;MechFlightReferenceFrameTests'}
    if($Mode -eq 'Equipment'){$filter+=';MechActionArbiterTests;MechEquipmentExecutionTests;MechVariableLayoutTests;MechSceneEntityLifecycleTests;MechMissileTrackLifecycleTests'}
    if($Mode -eq 'Damage'){$filter+=';MechPartDamageTests;MechEquipmentExecutionTests;MechActionArbiterTests;MechSceneEntityLifecycleTests'}
    if($Mode -eq 'Defense'){$filter+=';MechApsCombatTests;MechApsCollisionReplayTests;MechDefenseExtensionTests;MechSignalDefenseTests;MechPartDamageTests;MechEquipmentExecutionTests;MechActionArbiterTests'}
    if($Mode -eq 'Baseline'){$filter='ACunicombat;Mech;SolarSystem'}
    $arguments=@('-batchmode','-nographics','-projectPath',$ProjectPath,'-runTests','-testPlatform','EditMode',
        '-testFilter',$filter,'-testResults',(Join-Path $OutputPath 'test-results.xml'),'-logFile',(Join-Path $OutputPath 'unity.log'))
    if($Mode -in @('Baseline','Damage','Defense')){$arguments=@($arguments | Where-Object {$_ -ne '-nographics'})}
    if($GraphicsApi -eq "Direct3D11"){$arguments+="-force-d3d11"}
    $previousBaseline=$env:ACUNICOMBAT_PLATFORM_BASELINE
    $previous=$env:ACUNICOMBAT_SENSOR_BENCH_OUTPUT
    try{
        $env:ACUNICOMBAT_SENSOR_BENCH_OUTPUT=$OutputPath
        $env:ACUNICOMBAT_PLATFORM_BASELINE=if($Mode -eq 'Baseline'){'1'}else{'0'}
        $quoted=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
        $process=Start-Process -FilePath $UnityPath -ArgumentList $quoted -WindowStyle Hidden -PassThru
    }finally{$env:ACUNICOMBAT_SENSOR_BENCH_OUTPUT=$previous;$env:ACUNICOMBAT_PLATFORM_BASELINE=$previousBaseline}
    Write-Output "SensorBench running (PID $($process.Id)): $OutputPath"
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while(-not $process.WaitForExit(1000)){
        if([DateTime]::UtcNow -gt $deadline){
            Stop-Process -Id $process.Id -Force
            throw 'Spawned test process timed out; no success report will be produced.'
        }
    }
    $xmlPath=Join-Path $OutputPath 'test-results.xml'
    if(-not (Test-Path -LiteralPath $xmlPath)){throw 'Unity did not produce test results (check license, compilation and unity.log).'}
    [xml]$testResults=Get-Content -Raw -LiteralPath $xmlPath
    $manifest.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $manifest.runtimeProjectSettingsHash=(Get-FileHash -LiteralPath $settingsPath).Hash
    $manifest.unityResult=$testResults.'test-run'.result
    Write-Json 'manifest.json' $manifest
    if($testResults.'test-run'.result -ne 'Passed'){
        Write-Json 'status.json' @{status='Failed';reason='Unity test assertions or test setup failed';results='test-results.xml'}
        Write-Output "Tests failed; preserved full results in $OutputPath"
        exit 1
    }
    & $PythonPath (Join-Path $PSScriptRoot 'evaluate.py') $OutputPath
    $evaluationCode=$LASTEXITCODE
    if($evaluationCode -eq 1){
        Write-Json 'status.json' @{status='Failed';reason='Observer contract violation';details='evaluation-error.json'}
        exit 1
    }
    if($Mode -eq 'Replay' -and $evaluationCode -in 0,3){
        foreach($name in @('truth.jsonl','observations.jsonl','tracks.jsonl','decisions.jsonl')){
            if((Get-FileHash -LiteralPath (Join-Path $OutputPath $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $ReplayFrom $name)).Hash){
                Write-Json 'status.json' @{status='Failed';reason=('Deterministic replay differs: '+$name)}
                exit 1
            }
        }
        $manifest.replayMatches=$true
        Write-Json 'manifest.json' $manifest
    }
    if($evaluationCode -notin 0,3){throw 'Offline evaluator rejected the run; see evaluation-error.json.'}
    Write-Json 'status.json' @{status='Inconclusive';contractGate='Passed';capabilityGate='Inconclusive';reason='Performance thresholds await review';report='report.html'}
    Write-Output "Report: $(Join-Path $OutputPath 'report.html')"
    if($ContractOnly){exit 0}
    exit 3
} catch {
    Write-Json 'status.json' @{status='InfrastructureFailed';reason=$_.Exception.Message}
    Write-Error $_ -ErrorAction Continue
    exit 2
}
finally {
    # Unity's optional analytics package may remove its define during startup. Restore only
    # that exact editor-generated delta; never overwrite arbitrary concurrent setting edits.
    if($null -ne $originalSettingsBytes -and (Test-Path -LiteralPath $settingsPath)){
        $currentSettings=[IO.File]::ReadAllText($settingsPath)
        $normalized=$originalSettingsText.Replace(';SENTIS_ANALYTICS_ENABLED','').Replace('SENTIS_ANALYTICS_ENABLED;','')
        if($currentSettings -eq $normalized -and $currentSettings -ne $originalSettingsText){
            [IO.File]::WriteAllBytes($settingsPath,$originalSettingsBytes)
        }
    }
}