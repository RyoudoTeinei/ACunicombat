<#
.SYNOPSIS
Runs the saved playable scene with real AI and scheduled sensors, then judges HUD continuity.
.DESCRIPTION
Requires the project editor to be closed. Never closes a user-owned editor or saves/rebuilds
scenes. Player motion/aim/fire use normal controls. Truth logs are offline oracles only.
60/30 are simulated render time steps, not performance measurements. A successful run
requires player movement and firing, multiple launchers, sufficient actual HUD repaints
and screenshots, unchanged compiled assemblies/scene, and continuity/identity checks.
Failures, screenshots, trace data, build hashes and the HTML review remain in OutputRoot.
#>
[CmdletBinding()]
param(
 [string]$ProjectPath=(Join-Path $PSScriptRoot '../..'),
 [string]$UnityPath,
 [string]$PythonPath='python',
 [int[]]$FrameRates=@(60,30),
 [int]$DurationSeconds=120,
 [int]$Width=2560,
 [int]$Height=1440,
 [int]$MaximumWallSeconds=1800,
 [string]$OutputRoot,
 [switch]$FleetValidation,
 [switch]$FleetDefenseFixture
)
$ErrorActionPreference='Stop'
$ProjectPath=(Resolve-Path -LiteralPath $ProjectPath).Path.TrimEnd('\','/')
$versionFile=Join-Path $ProjectPath 'ProjectSettings/ProjectVersion.txt'
if(-not (Test-Path -LiteralPath $versionFile)){throw 'ProjectPath must be a complete Unity project.'}
$version=((Get-Content -LiteralPath $versionFile|Where-Object{$_ -match '^m_EditorVersion: '}) -split ': ',2)[1]
if(-not $UnityPath){$UnityPath=Join-Path $env:ProgramFiles "Unity/Hub/Editor/$version/Editor/Unity.exe"}
if(-not(Test-Path -LiteralPath $UnityPath)){throw "Unity $version not found: $UnityPath"}
foreach($instance in Get-CimInstance Win32_Process -Filter "name='Unity.exe'"){
 if($instance.CommandLine -match '-projectPath\s+(?:"([^"]+)"|(\S+))'){
  $other=if($Matches[1]){$Matches[1]}else{$Matches[2]}
  if([IO.Path]::GetFullPath($other).TrimEnd('\','/') -eq $ProjectPath){throw 'This project is already open in Unity. Save and close it, or provide a complete project copy. The runner will not close your editor.'}
 }
}
if($PythonPath -eq 'python'){
 $bundled=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
 if(Test-Path -LiteralPath $bundled){$PythonPath=$bundled}
}
if(-not $OutputRoot){$OutputRoot=Join-Path $ProjectPath ('Artifacts/PlayableCombat/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot);New-Item -ItemType Directory -Path $OutputRoot -Force|Out-Null
& $PythonPath -m unittest discover -s $PSScriptRoot -p test_playable_continuity.py
if($LASTEXITCODE -ne 0){throw 'Continuity judge self-tests failed; no gameplay result can be accepted.'}
$results=@()
$existingSuite=Join-Path $OutputRoot "suite.json"
if(Test-Path -LiteralPath $existingSuite){$results=@(Get-Content -Raw -LiteralPath $existingSuite | ConvertFrom-Json)}
foreach($fps in $FrameRates){
 if($fps -notin @(30,60)){throw 'FrameRates supports 30 and 60.'}
 $dir=Join-Path $OutputRoot "combat-$fps";if(Test-Path -LiteralPath $dir){throw "Refusing to overwrite a prior run: $dir"};New-Item -ItemType Directory -Path $dir|Out-Null
 $arguments=@('-batchmode','-force-d3d11','-projectPath',$ProjectPath,'-executeMethod','MechPlayableCombatContinuityReview.Run','-combatReviewOutput',$dir,'-combatReviewDuration',"$DurationSeconds",'-combatReviewFps',"$fps",'-combatReviewWidth',"$Width",'-combatReviewHeight',"$Height",'-combatReviewPlayerAam','1','-logFile',(Join-Path $dir 'unity.log'))
 if($FleetValidation){$arguments+=@('-fleetReview','1')}
 if($FleetDefenseFixture){$arguments+=@('-fleetDefenseReview','1')}
 $quoted=$arguments|ForEach-Object{'"'+$_.Replace('"','\"')+'"'}
 $priorCombatTelemetryOutput=$env:ACUNICOMBAT_COMBAT_TELEMETRY_OUTPUT
 try {
  $env:ACUNICOMBAT_COMBAT_TELEMETRY_OUTPUT=Join-Path $dir 'telemetry'
  $process=Start-Process -FilePath $UnityPath -ArgumentList $quoted -WindowStyle Hidden -PassThru
 } finally {$env:ACUNICOMBAT_COMBAT_TELEMETRY_OUTPUT=$priorCombatTelemetryOutput}
 if(-not $process.WaitForExit($MaximumWallSeconds*1000)){
  'timeout'|Set-Content -LiteralPath (Join-Path $dir 'stop.request')
  if(-not $process.WaitForExit(30000)){$process.Kill();$process.WaitForExit()}
  'Runner timed out; only its own Unity process was stopped.'|Set-Content -LiteralPath (Join-Path $dir 'timeout.txt')
 }
 $exit=$process.ExitCode;$judged=$false
 if(Test-Path -LiteralPath (Join-Path $dir 'continuity.jsonl')){
  & $PythonPath (Join-Path $PSScriptRoot 'analyze_playable_continuity.py') $dir
  $judged=$LASTEXITCODE -eq 0
 }
 $fleetPassed=$true
 if($FleetValidation){$fleetFile=Join-Path $dir 'fleet-acceptance.txt';$fleetPassed=(Test-Path -LiteralPath $fleetFile) -and ((Get-Content -Raw -LiteralPath $fleetFile) -match 'PASS=True')}
 if($FleetDefenseFixture){$battleLog=Get-Content -Raw -LiteralPath (Join-Path $dir 'unity.log');$fleetPassed=$fleetPassed -and $battleLog.Contains('Interceptor launched at') -and ($battleLog -match 'Warhead damaged missile:.*destroyed=True')}
 $replayPassed=$true
 foreach($replay in @(
  @{file='contacts-input.jsonl.gz';method='MechMissileContactReplayReview.Run';argument='-contactReplay'},
  @{file='radar-input';method='MechMissileContactReplayReview.RunRadar';argument='-radarReplay'})){
  $inputPath=Join-Path $dir $replay.file
  if(-not(Test-Path -LiteralPath $inputPath)){$replayPassed=$false;continue}
  $replayArgs=@('-batchmode','-force-d3d11','-projectPath',$ProjectPath,'-executeMethod',$replay.method,$replay.argument,$inputPath,'-logFile',($inputPath+'.unity.log'))
  $replayProcess=Start-Process -FilePath $UnityPath -ArgumentList ($replayArgs|ForEach-Object{'"'+$_.Replace('"','\"')+'"'}) -WindowStyle Hidden -PassThru
  if(-not $replayProcess.WaitForExit($MaximumWallSeconds*1000)){$replayProcess.Kill();$replayProcess.WaitForExit();$replayPassed=$false}
  $resultPath=$inputPath+'.result.json'
  if($replayProcess.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $resultPath)){$replayPassed=$false}
  elseif(-not((Get-Content -Raw -LiteralPath $resultPath|ConvertFrom-Json).passed)){$replayPassed=$false}
 }
 $results += [pscustomobject]@{frameRate=$fps;unityExit=$exit;acceptancePassed=$judged;fleetPassed=$fleetPassed;fixedInputReplayPassed=$replayPassed;passed=($exit -eq 0 -and $judged -and $fleetPassed -and $replayPassed);directory=$dir}
}
$results|ConvertTo-Json -Depth 4|Set-Content -Encoding utf8 -LiteralPath (Join-Path $OutputRoot 'suite.json')
& $PythonPath (Join-Path $PSScriptRoot 'build_playable_review.py') $OutputRoot
if($results.Where({-not $_.passed}).Count -gt 0){exit 2}
exit 0
