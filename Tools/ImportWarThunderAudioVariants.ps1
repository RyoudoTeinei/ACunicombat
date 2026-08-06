param(
    [string]$ProjectRoot = "E:\UnityDev\ACunicombat",
    [string]$DownloadRoot = "E:\UnityDev\tmp\warthunder-audio-variants",
    [switch]$ForceRefresh
)

$ErrorActionPreference = "Stop"

$repository = "GaijinEntertainment/fmod_studio_warthunder_for_modders"
$commit = "446cd5ec8eddff30bc15477413b7de1b89edcc7e"
$rawRoot = "https://raw.githubusercontent.com/$repository/$commit"
$audioRoot = Join-Path $ProjectRoot "Assets\Resources\Audio"

function New-Group {
    param(
        [string]$Name,
        [string]$SourceDirectory,
        [string]$SourcePrefix,
        [string]$SourceSeparator,
        [int]$SourceDigits,
        [string]$DestinationDirectory,
        [string]$DestinationPrefix,
        [string]$DestinationSeparator,
        [int]$DestinationDigits,
        [int]$First,
        [int]$Last,
        [string]$MetaTemplate,
        [int[]]$SourceIndices,
        [string]$SourceSuffix = ""
    )

    [PSCustomObject]@{
        Name = $Name
        SourceDirectory = $SourceDirectory
        SourcePrefix = $SourcePrefix
        SourceSeparator = $SourceSeparator
        SourceDigits = $SourceDigits
        DestinationDirectory = $DestinationDirectory
        DestinationPrefix = $DestinationPrefix
        DestinationSeparator = $DestinationSeparator
        DestinationDigits = $DestinationDigits
        First = $First
        Last = $Last
        MetaTemplate = $MetaTemplate
        SourceIndices = $SourceIndices
        SourceSuffix = $SourceSuffix
    }
}

$groups = @(
    New-Group "bullet_interior" "Assets/tanks/impacts/hit_mg" "bullet_interior" "-" 3 "Impacts" "bullet_interior" "_" 3 2 12 "Impacts\bullet_interior_001.ogg.meta"
    New-Group "bullet_rico" "Assets/tanks/impacts/hit_mg" "bullet_rico" "-" 3 "Impacts" "bullet_rico" "_" 3 2 16 "Impacts\bullet_rico_001.ogg.meta"
    New-Group "bullet_incoming" "Assets/tanks/impacts/hit_mg" "bullet_incoming" "-" 3 "Impacts" "bullet_incoming" "_" 3 1 16 "Impacts\bullet_hit_001.ogg.meta"
    New-Group "tank_impact_ext" "Assets/tanks/effects/projectile_whiz_rico" "tank_bullet_impact_ext" "-" 3 "Impacts" "tank_bullet_impact_ext" "_" 3 2 6 "Impacts\tank_bullet_impact_ext_001.ogg.meta"
    New-Group "tank_impact_int" "Assets/tanks/effects/projectile_whiz_rico" "tank_bullet_impact_int" "-" 3 "Impacts" "tank_bullet_impact_int" "_" 3 2 6 "Impacts\tank_bullet_impact_int_001.ogg.meta"

    New-Group "missile_attack_mid" "Assets/explosion/modules/missiles" "slow_attack_mid" "-" 3 "Impacts\Missile" "slow_attack_mid" "_" 3 1 5 "Impacts\bullet_hit_001.ogg.meta"
    New-Group "missile_explosion_close" "Assets/explosion/modules/air" "explosion_air_close" "-" 3 "Impacts\Missile" "explosion_air_close" "_" 3 1 10 "Impacts\bullet_hit_001.ogg.meta"
    New-Group "missile_metal_debris" "Assets/explosion/modules/metal_debris" "metal_debris" "-" 3 "Impacts\Missile" "metal_debris" "_" 3 1 35 "Impacts\bullet_hit_001.ogg.meta"

    New-Group -Name "flak_airburst_close" `
        -SourceDirectory "Assets/explosive" `
        -SourcePrefix "aa_explosion_" -SourceSeparator "" -SourceDigits 2 `
        -DestinationDirectory "Impacts\Flak" `
        -DestinationPrefix "flak_airburst_close" `
        -DestinationSeparator "_" -DestinationDigits 3 `
        -First 0 -Last 0 `
        -MetaTemplate "Impacts\bullet_hit_001.ogg.meta" `
        -SourceIndices @(1, 2, 3, 4, 5, 7, 8, 9, 10) `
        -SourceSuffix "_s"
    New-Group -Name "flak_airburst_far" `
        -SourceDirectory "Assets/explosive" `
        -SourcePrefix "aa_explosion_" -SourceSeparator "" -SourceDigits 2 `
        -DestinationDirectory "Impacts\Flak" `
        -DestinationPrefix "flak_airburst_far" `
        -DestinationSeparator "_" -DestinationDigits 3 `
        -First 0 -Last 0 `
        -MetaTemplate "Impacts\bullet_hit_001.ogg.meta" `
        -SourceIndices @(3, 4, 7, 8, 9, 10) `
        -SourceSuffix "_s_far"
    New-Group -Name "flak_debris" `
        -SourceDirectory "Assets/explosive" `
        -SourcePrefix "aa_explosion_debris_" `
        -SourceSeparator "" -SourceDigits 2 `
        -DestinationDirectory "Impacts\Flak" `
        -DestinationPrefix "flak_debris" `
        -DestinationSeparator "_" -DestinationDigits 3 `
        -First 0 -Last 0 `
        -MetaTemplate "Impacts\bullet_hit_001.ogg.meta" `
        -SourceIndices @(1, 2, 3)

    New-Group "turbine_ignition" "Assets/engines/jet_engines_constructor" "turbine_02_N1_10600_IGNITION" "-" 3 "Engine" "afterburner_ignition" "_" 3 2 4 "Engine\afterburner_ignition.wav.meta"
    New-Group "hydraulic_start" "Assets/tanks/effects/turret_move/update" "m60_turret_start_hydraulic" "-" 3 "Mechanical" "hydraulic_start" "_" 3 2 3 "Mechanical\hydraulic_start.wav.meta"
    New-Group "hydraulic_stop" "Assets/tanks/effects/turret_move/update" "m60_turret_stop_hydraulic" "-" 3 "Mechanical" "hydraulic_stop" "_" 3 2 2 "Mechanical\hydraulic_stop.wav.meta"
    New-Group "pneumatic" "Assets/fx" "pneumatic" "-" 3 "Mechanical" "pneumatic_release" "_" 3 2 8 "Mechanical\pneumatic_release.wav.meta"
    New-Group "joint_mechanical" "Assets/fx/turret_servo" "turret_move_mech" "-" 3 "Mechanical" "joint_mechanical" "_" 3 2 6 "Mechanical\joint_mechanical.wav.meta"

    New-Group "cold_ejection" "Assets/tanks/weapon/atgm/tor_m1" "tor_m1_launch" "-" 3 "Missile" "cold_ejection" "_" 3 2 3 "Missile\cold_ejection.ogg.meta"
    New-Group "cold_ignition" "Assets/tanks/weapon/atgm/tor_m1" "tor_m1_ignition" "-" 3 "Missile" "cold_ignition" "_" 3 2 3 "Missile\cold_ignition.ogg.meta"
    New-Group "hot_ignition" "Assets/weapons/rockets_missiles" "missile_start_med" "-" 3 "Missile" "hot_ignition_medium" "_" 3 2 3 "Missile\hot_ignition_medium.ogg.meta"
    New-Group "interior_launch" "Assets/weapons/rockets_missiles" "missile_start_interior" "-" 3 "Missile" "ownship_interior_launch" "_" 3 2 6 "Missile\ownship_interior_launch.ogg.meta"
    New-Group "flyby" "Assets/weapons" "rocket_fly_by" "-" 3 "Missile" "flyby_close" "_" 3 2 7 "Missile\flyby_close.ogg.meta"
    New-Group "launcher_mechanical" "Assets/fx/torpedoes" "torpedo_launch" "_" 1 "Missile" "launcher_mechanical" "_" 3 2 3 "Missile\launcher_mechanical.ogg.meta"

    New-Group "2a42_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "2a42_close" "-" 3 "Weapons" "2a42_close" "_" 3 2 8 "Weapons\2a42_close.ogg.meta"
    New-Group "2a42_first_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "2a42_first_close" "-" 3 "Weapons" "2a42_first_close" "_" 3 2 3 "Weapons\2a42_first_close.ogg.meta"
    New-Group "2a42_first_interior" "Assets/tanks/weapon/cannons/auto_cannons/grains" "2a42_first_interior" "-" 3 "Weapons" "2a42_first_interior" "_" 3 2 3 "Weapons\2a42_first_interior.ogg.meta"
    New-Group "2a42_low_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "2a42_low_close" "-" 3 "Weapons" "2a42_low_close" "_" 3 2 8 "Weapons\2a42_low_close.ogg.meta"
    New-Group "2a72_last_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "2a72_last_close" "-" 3 "Weapons" "2a72_last_close" "_" 3 2 3 "Weapons\2a72_last_close.ogg.meta"
    New-Group "flak38_first_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak38_first_close" "-" 3 "Weapons\Flak38" "flak38_first_close" "_" 3 1 3 "Weapons\2a42_close.ogg.meta"
    New-Group "flak38_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak38_close" "-" 3 "Weapons\Flak38" "flak38_close" "_" 3 1 12 "Weapons\2a42_close.ogg.meta"
    New-Group "flak38_bass" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak38_bass" "-" 3 "Weapons\Flak38" "flak38_bass" "_" 3 1 12 "Weapons\2a42_close.ogg.meta"
    New-Group "flak38_far" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak38_far" "-" 3 "Weapons\Flak38" "flak38_far" "_" 3 1 8 "Weapons\2a42_close.ogg.meta"
    New-Group "flak38_last_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak38_last_close" "-" 3 "Weapons\Flak38" "flak38_last_close" "_" 3 1 3 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_first_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_first_close" "-" 3 "Weapons\Flak43" "flak43_first_close" "_" 3 1 3 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_close" "-" 3 "Weapons\Flak43" "flak43_close" "_" 3 1 7 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_bass" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_bass" "-" 3 "Weapons\Flak43" "flak43_bass" "_" 3 1 7 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_far" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_far" "-" 3 "Weapons\Flak43" "flak43_far" "_" 3 1 7 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_interior" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_interior" "-" 3 "Weapons\Flak43" "flak43_interior" "_" 3 1 7 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_last_close" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_last_close" "-" 3 "Weapons\Flak43" "flak43_last_close" "_" 3 1 3 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_last_far" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_last_far" "-" 3 "Weapons\Flak43" "flak43_last_far" "_" 3 1 3 "Weapons\2a42_close.ogg.meta"
    New-Group "flak43_last_interior" "Assets/tanks/weapon/cannons/auto_cannons/grains" "flak43_last_interior" "-" 3 "Weapons\Flak43" "flak43_last_interior" "_" 3 1 3 "Weapons\2a42_close.ogg.meta"
    New-Group "cannon_57_shot" "Assets/tanks/weapon/cannons/cannon_57mm_qf6" "cannon_57mm_qf6_shot" "_" 2 "Weapons" "cannon_57mm_qf6_shot" "_" 3 2 3 "Weapons\cannon_57mm_qf6_shot.ogg.meta"
    New-Group "shell_drop" "Assets/tanks/weapon/cannons" "cannon_shell_drop" "_" 2 "Weapons" "cannon_shell_drop" "_" 3 2 7 "Weapons\cannon_shell_drop.ogg.meta"

    New-Group "bolt" "Assets/tanks/weapon/cannons/cannon_reloading/aa_mag" "bolt" "-" 3 "Weapons\Reload" "bolt" "_" 3 2 2 "Weapons\Reload\bolt.ogg.meta"
    New-Group "mag_in" "Assets/tanks/weapon/cannons/cannon_reloading/aa_mag" "mag_in" "-" 3 "Weapons\Reload" "mag_in" "_" 3 2 3 "Weapons\Reload\mag_in.ogg.meta"
    New-Group "mag_move" "Assets/tanks/weapon/cannons/cannon_reloading/aa_mag" "mag_move" "-" 3 "Weapons\Reload" "mag_move" "_" 3 2 3 "Weapons\Reload\mag_move.ogg.meta"
    New-Group "mag_out" "Assets/tanks/weapon/cannons/cannon_reloading/aa_mag" "mag_out" "-" 3 "Weapons\Reload" "mag_out" "_" 3 2 3 "Weapons\Reload\mag_out.ogg.meta"
    New-Group "auto_end_turn" "Assets/tanks/weapon/cannons/cannon_reloading/autoloader" "cannon_autoloader_end_turn" "-" 3 "Weapons\Reload" "cannon_autoloader_end_turn" "_" 3 2 4 "Weapons\Reload\cannon_autoloader_end_turn.ogg.meta"
    New-Group "auto_shell_push" "Assets/tanks/weapon/cannons/cannon_reloading/autoloader" "cannon_autoloader_sell_push" "-" 3 "Weapons\Reload" "cannon_autoloader_shell_push" "_" 3 2 4 "Weapons\Reload\cannon_autoloader_shell_push.ogg.meta"
    New-Group "auto_servo" "Assets/tanks/weapon/cannons/cannon_reloading/autoloader" "cannon_autoloader_servo" "-" 3 "Weapons\Reload" "cannon_autoloader_servo" "_" 3 2 2 "Weapons\Reload\cannon_autoloader_servo.ogg.meta"
    New-Group "auto_turn_noise" "Assets/tanks/weapon/cannons/cannon_reloading/autoloader" "cannon_autoloader_turn_noise" "-" 3 "Weapons\Reload" "cannon_autoloader_turn_noise" "_" 3 2 3 "Weapons\Reload\cannon_autoloader_turn_noise.ogg.meta"

    New-Group "con_scan_track" "Assets/gui" "con_scan_track" "-" 3 "RWR" "con_scan_track" "-" 3 2 8 "RWR\con_scan_track-001.wav.meta"
)

function Format-Index([int]$Index, [int]$Digits) {
    return $Index.ToString("D$Digits")
}

function Ensure-Meta([string]$TemplateRelativePath, [string]$DestinationMetaPath) {
    if (Test-Path -LiteralPath $DestinationMetaPath) {
        return
    }

    $templatePath = Join-Path $audioRoot $TemplateRelativePath
    if (-not (Test-Path -LiteralPath $templatePath)) {
        throw "Missing AudioImporter template: $templatePath"
    }

    $content = Get-Content -LiteralPath $templatePath -Raw
    $guid = [Guid]::NewGuid().ToString("N")
    $content = [Regex]::Replace($content, "(?m)^guid: .+$", "guid: $guid")
    [IO.File]::WriteAllText(
        $DestinationMetaPath,
        $content,
        [Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Force -Path $DownloadRoot | Out-Null

$expectedCount = 0
foreach ($group in $groups) {
    if ($group.SourceIndices.Count -gt 0) {
        $expectedCount += $group.SourceIndices.Count
    }
    else {
        $expectedCount += $group.Last - $group.First + 1
    }
}
if ($expectedCount -ne 286) {
    throw "Variant manifest expected 286 files but describes $expectedCount."
}

$completed = 0
$reused = 0
$downloaded = 0
$downloadedBytes = 0L
foreach ($group in $groups) {
    $groupDownloadRoot = Join-Path $DownloadRoot $group.Name
    $destinationRoot = Join-Path $audioRoot $group.DestinationDirectory
    New-Item -ItemType Directory -Force -Path $groupDownloadRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null

    $usesExplicitIndices = $group.SourceIndices.Count -gt 0
    $sourceIndices = if ($usesExplicitIndices) {
        @($group.SourceIndices)
    }
    else {
        @($group.First..$group.Last)
    }
    $destinationOrdinal = 0
    foreach ($index in $sourceIndices) {
        $destinationOrdinal++
        $sourceNumber = Format-Index $index $group.SourceDigits
        $destinationIndex = if ($usesExplicitIndices) {
            $destinationOrdinal
        }
        else {
            $index
        }
        $destinationNumber =
            Format-Index $destinationIndex $group.DestinationDigits
        $sourceName = (
            "$($group.SourcePrefix)$($group.SourceSeparator)" +
            "$sourceNumber$($group.SourceSuffix).wav")
        $destinationName = "$($group.DestinationPrefix)$($group.DestinationSeparator)$destinationNumber.ogg"
        $sourceRelativePath = "$($group.SourceDirectory)/$sourceName"
        $url = "$rawRoot/$sourceRelativePath"
        $downloadPath = Join-Path $groupDownloadRoot $sourceName
        $destinationPath = Join-Path $destinationRoot $destinationName

        if (-not $ForceRefresh -and
            (Test-Path -LiteralPath $destinationPath)) {
            $existingBytes = [IO.File]::ReadAllBytes($destinationPath)
            if ($existingBytes.Length -lt 4 -or
                [Text.Encoding]::ASCII.GetString(
                    $existingBytes, 0, 4) -ne "OggS") {
                throw "Existing file is not Ogg/Vorbis: $destinationPath"
            }

            Ensure-Meta $group.MetaTemplate "$destinationPath.meta"
            $reused++
            $completed++
            if ($completed % 10 -eq 0 -or $completed -eq $expectedCount) {
                Write-Host "Validated $completed / $expectedCount files"
            }
            continue
        }

        & curl.exe -L --fail --silent --show-error --output $downloadPath $url
        if ($LASTEXITCODE -ne 0) {
            throw "Download failed: $url"
        }

        $bytes = [IO.File]::ReadAllBytes($downloadPath)
        if ($bytes.Length -lt 4 -or
            [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne "OggS") {
            throw "Downloaded file is not Ogg/Vorbis: $url"
        }

        Copy-Item -LiteralPath $downloadPath -Destination $destinationPath -Force
        Ensure-Meta $group.MetaTemplate "$destinationPath.meta"
        $downloadedBytes += $bytes.Length
        $downloaded++
        $completed++
        if ($completed % 10 -eq 0 -or $completed -eq $expectedCount) {
            Write-Host "Validated $completed / $expectedCount files"
        }
    }
}

Write-Host (
    "Validated $completed files from commit $commit; " +
    "reused $reused, downloaded $downloaded ($downloadedBytes bytes).")
