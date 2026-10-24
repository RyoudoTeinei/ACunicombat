using System;
using ACunicombat.Mechs;
using UnityEngine;

internal static class AC6TestWeaponProfileFactory
{
    internal static MechWeaponPerformance Create(string partId)
    {
        var profile=CreateCore(partId);
        string number=partId?.Length>=4?partId.Substring(partId.Length-4):"";
        switch(number)
        {
            case "1810":case "1610":case "1710":case "1830":profile.weaponSeriesId="1610-1710-1810-1830";break;
            case "5110":case "1620":case "1630":case "2030":case "2040":profile.weaponSeriesId="1620-1630-2030-2040-5110";break;
            case "1720":case "1730":case "1820":case "1910":case "2020":profile.weaponSeriesId="1720-1730-1820-1910-2020";break;
        }
        if(profile.IsConfigured && !profile.IsGuidedMissile)
        {
            profile.baseDispersionMilliradians *= 2f;
            profile.maximumDispersionMilliradians *= 2f;
            profile.lowRecoilPerShotMilliradians *= 2f;
            profile.sustainedRecoilPerShotMilliradians *= 2f;
            profile.dispersionRecoveryMilliradiansPerSecond *= 2f;
        }
        return profile;
    }
    private static MechWeaponPerformance CreateCore(string partId)
    {
        var missile = MechMissileLauncherCatalog.CreateDefaultPerformance(partId);
        if(missile != null)return missile;
        if (Matches(partId, "WP_L_1310") || Matches(partId, "WP_R_1310"))
        {
            return new MechWeaponPerformance
            {
                configured = true,
                role = MechWeaponRole.HandCannon,
                weaponType = "57 mm 单发近炸火炮",
                hudLabel = "57MM",
                directHitDamage = 420f,
                projectileCaliberMillimeters = 57f,
                muzzleVelocityMetersPerSecond = 450f,
                projectileLifetimeSeconds = 10f,
                maximumRangeMeters = 4500f,
                roundsPerMinute = 60f / 4.25f,
                magazineCapacity = 1,
                magazineCount = 32,
                reloadSeconds = 4.25f,
                targetProximityFuse = true,
                proximityFuseMode = "锁定目标时启用",
                proximityFuseRadiusMeters = 7.5f,
                proximityFuseArmSeconds = 0.12f,
                proximityFuseSafeDistanceMeters = 45f,
                warheadLethalRadiusMeters = 10f,
                cannonAudioProfile = CannonAudioProfile.Cannon57Mm
            };
        }

        if (Matches(partId, "WP_L_1610") || Matches(partId, "WP_R_1610"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                95f, 1610f, 520f, 0.08f, 0.34f,
                4, 0.009f, 0.025f, 38, 10, 5.25f,
                AutomaticCannonAudioProfile.Flak38);
            profile.projectileCaliberMillimeters = 25f;
            profile.weaponType = "25 mm 弹匣式自动炮";
            profile.hudLabel = "25MM";
            return profile;
        }

        if (Matches(partId, "WP_L_1630") || Matches(partId, "WP_R_1630"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                132f, 2310f, 90f, 0.06f, 0.09f,
                1, 0.003f, 0.006f, 24, 8, 7f,
                AutomaticCannonAudioProfile.Flak43);
            profile.targetProximityFuse = true;
            profile.proximityFuseMode = "锁定目标时启用";
            profile.proximityFuseRadiusMeters = 5f;
            profile.warheadLethalRadiusMeters = 5f;
            profile.projectileCaliberMillimeters = 23f;
            profile.weaponType = "23 mm 精准自动炮";
            profile.hudLabel = "23MM";
            return profile;
        }

        if (Matches(partId, "WP_L_1720") || Matches(partId, "WP_R_1720"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                85f, 1500f, 580f, 0.07f, 0.3f,
                4, 0.008f, 0.022f, 46, 6, 4.95f,
                AutomaticCannonAudioProfile.Legacy2A42);
            profile.projectileCaliberMillimeters = 20f;
            profile.weaponType = "20 mm 弹匣式自动炮";
            profile.hudLabel = "20MM";
            return profile;
        }

        if (Matches(partId, "WP_L_1810") || Matches(partId, "WP_R_1810"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                28f, 820f, 960f, 0.24f, 2.74f,
                3, 0.025f, 0.14f, 48, 10, 3.1f,
                AutomaticCannonAudioProfile.Mg81);
            profile.hudLabel = "20MM";
            profile.tapBurstShotCount = 3;
            profile.projectileCaliberMillimeters = 20f;
            profile.weaponType = "20 mm 连发自动炮";
            return profile;
        }

        if (Matches(partId, "WP_L_1820") || Matches(partId, "WP_R_1820"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                16f, 790f, 1420f, 0.37f, 2.95f,
                2, 0.06f, 0.12f, 82, 6, 2.8f,
                AutomaticCannonAudioProfile.VickersK);
            profile.hudLabel = "L-SMG";
            profile.projectileCaliberMillimeters = 12.7f;
            profile.weaponType = "12.7 mm 突击冲锋枪";
            return profile;
        }

        if (Matches(partId, "WP_L_1830") || Matches(partId, "WP_R_1830"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                32f, 880f, 682f, 0.49f, 3.82f,
                2, 0.08f, 0.17f, 180, 2, 4.2f,
                AutomaticCannonAudioProfile.Shkas);
            profile.hudLabel = "LMG";
            profile.projectileCaliberMillimeters = 14.5f;
            profile.weaponType = "14.5 mm 轻机枪";
            return profile;
        }

        if (Matches(partId, "WP_L_1910") || Matches(partId, "WP_R_1910"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                28f, 1550f, 2400f, 0.14f, 0.85f,
                12, 0.015f, 0.028f, 1440, 1, 0f,
                AutomaticCannonAudioProfile.Gau8A);
            profile.hudLabel = "GAT";
            profile.recoilImpulseNewtonSeconds = 780f;
            profile.spinUpSeconds = 0.55f;
            profile.heatPerShotMj = 0.035f;
            profile.heatCapacityMj = 18f;
            profile.coolingMw = 0.35f;
            profile.beltFed = true;
            profile.resumeThresholdMj = 7f;
            profile.projectileCaliberMillimeters = 30f;
            profile.weaponType = "30 mm 转轮机炮";
            return profile;
        }

        if (Matches(partId, "WP_L_2020") || Matches(partId, "WP_R_2020"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                14f, 720f, 780f, 0.17f, 1.72f,
                3, 0.025f, 0.065f, 36, 6, 2.3f,
                AutomaticCannonAudioProfile.Mg17);
            profile.tapBurstShotCount = 3;
            profile.heldBurstShotCount = 3;
            profile.heldBurstPauseSeconds = 0.28f;
            profile.projectileCaliberMillimeters = 20f;
            profile.weaponType = "20 mm 连发精确炮";
            profile.hudLabel = "B-CAN";
            return profile;
        }

        if (Matches(partId, "WP_L_5110") || Matches(partId, "WP_R_5110"))
        {
            return new MechWeaponPerformance
            {
                configured = true,
                role = MechWeaponRole.PointDefenseCannon,
                weaponType = "12.7 mm 肩载点防御加特林机枪",
                hudLabel = "CIWS",
                directHitDamage = 45f,
                projectileCaliberMillimeters = 12.7f,
                muzzleVelocityMetersPerSecond = 1800f,
                projectileLifetimeSeconds = 4f,
                maximumRangeMeters = 6000f,
                effectiveRangeMeters = 3000f,
                roundsPerMinute = 1858f,
                baseDispersionMilliradians = 0.1f,
                maximumDispersionMilliradians = 0.35f,
                magazineCapacity = 1200,
                magazineCount = 1,
                beltFed = true,
                reloadSeconds = 0f,
                proximityFuseMode = "自动点防御破片云",
                pointDefenseRoundLethalRadiusMeters = 4f,
                pointDefenseRoundArmSeconds = 0.05f,
                pointDefenseRoundSafeDistanceMeters = 90f,
                automaticPointDefense = true,
                outboardYawLimitDegrees = 110f,
                inboardYawLimitDegrees = 45f,
                elevationLimitDegrees = 80f,
                depressionLimitDegrees = 25f,
                traverseRateDegreesPerSecond = 120f,
                traverseAccelerationDegreesPerSecondSquared = 300f,
                traverseDecelerationDegreesPerSecondSquared = 420f,
                aimSmoothingSeconds = 0.06f,
                maximumLeadSeconds = 4f,
                trackHoldSeconds = 0.3f,
                minimumHitProbability = 0.02f,
                maximumThreatTimeSeconds = 12f,
                requiresWeaponsQualityTrack = true,
                incomingTargetsOnly = true,
                heatPerShotMj = 0.22f,
                heatCapacityMj = 6f,
                coolingMw = 1.4f,
                resumeThresholdMj = 2f,
                spinUpSeconds = 0.5f
            };
        }

        if (Matches(partId, "WP_L_1710") || Matches(partId, "WP_R_1710"))
        {
            var profile=CreateCore("WP_L_1610").Clone();
            profile.projectileCaliberMillimeters=25f;
            profile.weaponType="25 mm 弹匣式自动炮";
            profile.hudLabel="25MM";
            return profile;
        }
        if (Matches(partId, "WP_L_1620") || Matches(partId, "WP_R_1620"))
        {
            var profile=CreateCore("WP_L_1630").Clone();
            profile.roundsPerMinute=180f;
            profile.maximumRangeMeters=14000f;
            profile.projectileLifetimeSeconds=profile.maximumRangeMeters/profile.muzzleVelocityMetersPerSecond;
            profile.baseDispersionMilliradians*=1.5f;
            profile.maximumDispersionMilliradians*=1.5f;
            profile.lowRecoilPerShotMilliradians*=1.5f;
            profile.sustainedRecoilPerShotMilliradians*=1.5f;
            profile.dispersionRecoveryMilliradiansPerSecond*=1.5f;
            profile.projectileCaliberMillimeters=23f;
            profile.weaponType="23 mm 精准自动炮";
            profile.hudLabel="23MM";
            return profile;
        }
        if (Matches(partId, "WP_L_1730") || Matches(partId, "WP_R_1730"))
        {
            var profile=CreateCore("WP_L_1720").Clone();
            profile.magazineCapacity=42;
            profile.projectileCaliberMillimeters=25f;
            profile.weaponType="25 mm 弹匣式自动炮";
            profile.hudLabel="25MM";
            return profile;
        }
        if (Matches(partId,"WP_L_2030") || Matches(partId,"WP_R_2030") || Matches(partId,"WP_L_2040") || Matches(partId,"WP_R_2040"))
            return new MechWeaponPerformance { configured=false, projectileCaliberMillimeters=20f, weaponType="20 mm 炮", hudLabel="20MM" };
        return new MechWeaponPerformance();
    }

    private static MechWeaponPerformance AutomaticCannon(
        float damage,
        float muzzleVelocity,
        float roundsPerMinute,
        float baseDispersion,
        float maximumDispersion,
        int lowRecoilShotCount,
        float lowRecoilPerShot,
        float sustainedRecoilPerShot,
        int magazineCapacity,
        int magazineCount,
        float reloadSeconds,
        AutomaticCannonAudioProfile audioProfile)
    {
        return new MechWeaponPerformance
        {
            configured = true,
            role = MechWeaponRole.AutomaticCannon,
            weaponType = "30 mm 弹匣式自动炮",
            hudLabel = "30MM",
            directHitDamage = damage,
            projectileCaliberMillimeters = 30f,
            muzzleVelocityMetersPerSecond = muzzleVelocity,
            projectileLifetimeSeconds = 8f,
            calculateEffectiveRange = true,
            maximumRangeMeters = muzzleVelocity * 8f,
            roundsPerMinute = roundsPerMinute,
            baseDispersionMilliradians = baseDispersion,
            maximumDispersionMilliradians = maximumDispersion,
            lowRecoilShotCount = lowRecoilShotCount,
            lowRecoilPerShotMilliradians = lowRecoilPerShot,
            sustainedRecoilPerShotMilliradians = sustainedRecoilPerShot,
            dispersionRecoveryMilliradiansPerSecond = 0.4f,
            dispersionRecoveryDelaySeconds = 0.16f,
            burstResetSeconds = 0.45f,
            magazineCapacity = magazineCapacity,
            magazineCount = magazineCount,
            reloadSeconds = reloadSeconds,
            automaticCannonAudioProfile = audioProfile
        };
    }

    private static bool Matches(string partId, string sourceKey)
    {
        return !string.IsNullOrWhiteSpace(partId) &&
            partId.EndsWith(sourceKey, StringComparison.OrdinalIgnoreCase);
    }
}
