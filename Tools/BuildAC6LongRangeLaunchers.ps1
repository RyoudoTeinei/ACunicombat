param([string]$RawRoot='E:/UnityDev/Resources/AC6_ExtractedAssets/wp', [string[]]$Numbers=@('4310','4230'),[string[]]$Sides=@('R','L'))
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$dotnet='E:/UnityDev/Tools/dotnet9sdk/dotnet.exe'
$compendium=Join-Path $project 'Tools/LocalOut/AC6NativeEquipment/WP_L_4340/anibnd/7000000_WP_L_4340.compendium'
$exporter=Join-Path $project 'Tools/DotNet/AC6HkxPoseExporter/bin/Debug/net9.0/AC6HkxPoseExporter.dll'
$gltf=Join-Path $project 'Tools/DotNet/AC6PartToGltf/bin/Debug/net9.0/AC6PartToGltf.dll'
$texconv=Join-Path $project 'Tools/LocalOut/bin/texconv.exe'
function Checked($exe,$arguments){ & $exe @arguments; if($LASTEXITCODE -ne 0){throw "Export failed: $exe"} }
foreach($number in $Numbers) {
    $raw=Join-Path $RawRoot ('wp_'+$number)
    $work=Join-Path $project ('Tools/LocalOut/LongRangeLaunchers/'+$number)
    New-Item -ItemType Directory -Path $work,($work+'/png') -Force | Out-Null
    Checked $dotnet @($exporter,'--skeleton',($raw+'/skeleton.hkx'),'--compendium',$compendium,'--skeleton-only','--out',($work+'/skeleton.json'))
    $poses=@()
    foreach($anim in Get-ChildItem -LiteralPath $raw -Filter 'a*.hkx') {
        $pose=$work+'/'+$anim.BaseName+'.json'
        Checked $dotnet @($exporter,'--skeleton',($raw+'/skeleton.hkx'),'--anim',$anim.FullName,'--compendium',$compendium,'--out',$pose)
        $poses+=$pose
    }
    foreach($dds in Get-ChildItem -LiteralPath $raw -Filter '*.dds' | Where-Object {$_.BaseName -notmatch '_l$'}) {
        Checked $texconv @('-nologo','-y','-ft','png','-o',($work+'/png'),$dds.FullName)
    }
    foreach($side in $Sides) {
        $key='WP_'+$side+'_'+$number
        $sideDir=$work+'/'+$side;New-Item -ItemType Directory -Path $sideDir -Force | Out-Null
        # The extracted archive retains the right skeleton. Left FLVER bind-pose
        # alignment is verified by the exporter report; never mix bone names.
        foreach($source in @($work+'/skeleton.json')+$poses) {
            $text=[IO.File]::ReadAllText($source).Replace('WP_R_'+$number,$key)
            if($side -eq 'L') {
                $poseData=$text | ConvertFrom-Json
                $transforms=@($poseData.Bones | ForEach-Object {$_.ReferenceLocalTransform})
                foreach($frame in $poseData.Frames){$transforms+=@($frame.LocalTransforms)}
                foreach($t in $transforms){$t.Translation.X=-$t.Translation.X;$t.Rotation.Y=-$t.Rotation.Y;$t.Rotation.Z=-$t.Rotation.Z}
                $text=$poseData | ConvertTo-Json -Depth 20 -Compress
            }
            [IO.File]::WriteAllText(($sideDir+'/'+[IO.Path]::GetFileName($source)),$text)
        }
        $args=@($gltf,'--skeleton-json',($sideDir+'/skeleton.json'),'--flver',($raw+'/'+$key+'.flver'),'--tex-dir',($work+'/png'),'--report',($sideDir+'/report.json'),'--out',($project+'/Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment/'+$key+'_NATIVE.glb'))
        foreach($pose in $poses){$args+=@('--anim',($sideDir+'/'+[IO.Path]::GetFileName($pose)))}
        Checked $dotnet $args
        $report=Get-Content ($sideDir+'/report.json') -Raw | ConvertFrom-Json
        if($report.Parts[0].MaxPositionError -gt .002 -or $report.Parts[0].MaxRotationErrorDeg -gt .1) {
            $bind=@{};foreach($bone in $report.Parts[0].BindTranslations){$bind[$bone.Name]=$bone}
            foreach($file in Get-ChildItem -LiteralPath $sideDir -Filter '*.json' | Where-Object {$_.Name -ne 'report.json'}) {
                $data=Get-Content $file.FullName -Raw | ConvertFrom-Json
                for($i=0;$i -lt $data.Bones.Count;$i++) {
                    $bone=$data.Bones[$i];$target=$bind[$bone.Name];if($null -eq $target){continue}
                    $rest=$bone.ReferenceLocalTransform.Translation
                    $dx=$target.X-$rest.X;$dy=$target.Y-$rest.Y;$dz=$target.Z-$rest.Z
                    $q=$bone.ReferenceLocalTransform.Rotation;$desired=$target.Rotation
                    $old=[System.Numerics.Quaternion]::new($q.X,$q.Y,$q.Z,$q.W)
                    $new=[System.Numerics.Quaternion]::new($desired.X,$desired.Y,$desired.Z,$desired.W)
                    $delta=[System.Numerics.Quaternion]::Multiply($new,[System.Numerics.Quaternion]::Inverse($old))
                    foreach($frame in $data.Frames){
                        $t=$frame.LocalTransforms[$i].Translation;$t.X+=$dx;$t.Y+=$dy;$t.Z+=$dz
                        $r=$frame.LocalTransforms[$i].Rotation;$v=[System.Numerics.Quaternion]::Multiply($delta,[System.Numerics.Quaternion]::new($r.X,$r.Y,$r.Z,$r.W))
                        $r.X=$v.X;$r.Y=$v.Y;$r.Z=$v.Z;$r.W=$v.W
                    }
                    $q.X=$desired.X;$q.Y=$desired.Y;$q.Z=$desired.Z;$q.W=$desired.W
                    $rest.X=$target.X;$rest.Y=$target.Y;$rest.Z=$target.Z
                }
                [IO.File]::WriteAllText($file.FullName,($data|ConvertTo-Json -Depth 20 -Compress))
            }
            Checked $dotnet $args
            $report=Get-Content ($sideDir+'/report.json') -Raw | ConvertFrom-Json
        }
        foreach($part in $report.Parts){if($part.MaxPositionError -gt .002 -or $part.MaxRotationErrorDeg -gt .1){throw "Bind pose mismatch: $key"}}
    }
}

