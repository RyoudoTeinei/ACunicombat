using System;
using ACunicombat.Mechs;

internal static class AC6TestWeaponProfileFactory
{
    internal static MechWeaponPerformance Create(string partId)
    {
        if (Matches(partId, "WP_L_1310") || Matches(partId, "WP_R_1310"))
        {
            return new MechWeaponPerformance
            {
                configured = true,
                role = MechWeaponRole.HandCannon,
                weaponType = "57 mm 单发近炸火炮",
                hudLabel = "57MM",
                directHitDamage = 420f,
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
            return AutomaticCannon(
                95f, 1610f, 520f, 0.08f, 0.34f,
                4, 0.009f, 0.025f, 32, 12, 5.25f,
                AutomaticCannonAudioProfile.Flak38);
        }

        if (Matches(partId, "WP_L_1630") || Matches(partId, "WP_R_1630"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                132f, 2310f, 90f, 0.06f, 0.09f,
                1, 0.003f, 0.006f, 12, 12, 7f,
                AutomaticCannonAudioProfile.Flak43);
            profile.targetProximityFuse = true;
            profile.proximityFuseMode = "锁定目标时启用";
            profile.proximityFuseRadiusMeters = 5f;
            profile.warheadLethalRadiusMeters = 5f;
            return profile;
        }

        if (Matches(partId, "WP_L_1720") || Matches(partId, "WP_R_1720"))
        {
            return AutomaticCannon(
                85f, 1500f, 580f, 0.07f, 0.3f,
                4, 0.008f, 0.022f, 36, 12, 4.95f,
                AutomaticCannonAudioProfile.Legacy2A42);
        }

        if (Matches(partId, "WP_L_4250") || Matches(partId, "WP_R_4250"))
        {
            return Missile(
                MechWeaponRole.InterceptorMissile,
                "拦截导弹发射器",
                "INT",
                16,
                2,
                MissileFlightProfile.InterceptorDefault);
        }

        if (Matches(partId, "WP_L_4290") || Matches(partId, "WP_R_4290"))
        {
            return Missile(
                MechWeaponRole.AirToAirMissile,
                "空空导弹发射器",
                "MSL",
                4,
                6,
                MissileFlightProfile.AirToAirDefault);
        }

        if (Matches(partId, "WP_L_5110") || Matches(partId, "WP_R_5110"))
        {
            return new MechWeaponPerformance
            {
                configured = true,
                role = MechWeaponRole.PointDefenseCannon,
                weaponType = "肩载点防御加特林炮",
                hudLabel = "CIWS",
                directHitDamage = 45f,
                muzzleVelocityMetersPerSecond = 1800f,
                projectileLifetimeSeconds = 4f,
                maximumRangeMeters = 6000f,
                effectiveRangeMeters = 3000f,
                roundsPerMinute = 858f,
                baseDispersionMilliradians = 0.1f,
                maximumDispersionMilliradians = 0.35f,
                magazineCapacity = 200,
                magazineCount = 4,
                reloadSeconds = 4f,
                proximityFuseMode = "自动点防御破片云",
                pointDefenseRoundLethalRadiusMeters = 4f,
                pointDefenseRoundArmSeconds = 0.05f,
                pointDefenseRoundSafeDistanceMeters = 90f,
                automaticPointDefense = true,
                maximumOffBoresightDegrees = 100f,
                traverseRateDegreesPerSecond = 180f,
                traverseAccelerationDegreesPerSecondSquared = 540f,
                traverseDecelerationDegreesPerSecondSquared = 720f,
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

    private static MechWeaponPerformance Missile(
        MechWeaponRole role,
        string weaponType,
        string hudLabel,
        int magazineCapacity,
        int magazineCount,
        MissileFlightProfile flightProfile)
    {
        return new MechWeaponPerformance
        {
            configured = true,
            role = role,
            weaponType = weaponType,
            hudLabel = hudLabel,
            directHitDamage = 300f,
            muzzleVelocityMetersPerSecond = flightProfile.EjectionSpeedMps,
            projectileLifetimeSeconds = flightProfile.MaximumFlightTimeSeconds,
            maximumRangeMeters = flightProfile.MaximumRangeMeters,
            effectiveRangeMeters = flightProfile.MaximumRangeMeters,
            magazineCapacity = magazineCapacity,
            magazineCount = magazineCount,
            proximityFuseMode = "常规近炸",
            targetProximityFuse = true,
            proximityFuseRadiusMeters = flightProfile.ProximityFuseRadiusMeters,
            proximityFuseArmSeconds = flightProfile.ProximityFuseArmSeconds,
            proximityFuseSafeDistanceMeters = flightProfile.ProximityFuseSafeDistanceMeters,
            warheadLethalRadiusMeters = flightProfile.WarheadLethalRadiusMeters,
            missileFlightProfile = flightProfile
        };
    }

    private static bool Matches(string partId, string sourceKey)
    {
        return !string.IsNullOrWhiteSpace(partId) &&
            partId.EndsWith(sourceKey, StringComparison.OrdinalIgnoreCase);
    }
}
