using System;
using ACunicombat.Mechs;
using UnityEngine;

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
                4, 0.009f, 0.025f, 32, 12, 5.25f,
                AutomaticCannonAudioProfile.Flak38);
            profile.projectileCaliberMillimeters = 20f;
            return profile;
        }

        if (Matches(partId, "WP_L_1630") || Matches(partId, "WP_R_1630"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                132f, 2310f, 90f, 0.06f, 0.09f,
                1, 0.003f, 0.006f, 12, 12, 7f,
                AutomaticCannonAudioProfile.Flak43);
            profile.targetProximityFuse = true;
            profile.projectileCaliberMillimeters = 37f;
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

        if (Matches(partId, "WP_L_1810") || Matches(partId, "WP_R_1810"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                28f, 820f, 960f, 0.24f, 2.74f,
                3, 0.025f, 0.14f, 45, 12, 3.1f,
                AutomaticCannonAudioProfile.Mg81);
            profile.weaponType = "11 mm 重型冲锋枪";
            profile.hudLabel = "H-SMG";
            profile.projectileCaliberMillimeters = 11f;
            profile.tapBurstShotCount = 3;
            return profile;
        }

        if (Matches(partId, "WP_L_1820") || Matches(partId, "WP_R_1820"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                16f, 790f, 1420f, 0.37f, 2.95f,
                2, 0.06f, 0.12f, 50, 12, 2.8f,
                AutomaticCannonAudioProfile.VickersK);
            profile.weaponType = "9 mm 轻型冲锋枪";
            profile.hudLabel = "L-SMG";
            profile.projectileCaliberMillimeters = 9f;
            return profile;
        }

        if (Matches(partId, "WP_L_1830") || Matches(partId, "WP_R_1830"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                32f, 880f, 700f, 0.49f, 3.82f,
                2, 0.08f, 0.17f, 80, 10, 4.2f,
                AutomaticCannonAudioProfile.Shkas);
            profile.weaponType = "12.7 mm 轻机枪";
            profile.hudLabel = "LMG";
            profile.projectileCaliberMillimeters = 12.7f;
            return profile;
        }

        if (Matches(partId, "WP_L_1910") || Matches(partId, "WP_R_1910"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                28f, 1550f, 2400f, 0.14f, 0.85f,
                12, 0.015f, 0.028f, 240, 6, 6.8f,
                AutomaticCannonAudioProfile.Gau8A);
            profile.weaponType = "20 mm 转轮机枪";
            profile.hudLabel = "GAT";
            profile.projectileCaliberMillimeters = 20f;
            profile.recoilImpulseNewtonSeconds = 780f;
            profile.spinUpSeconds = 0.55f;
            profile.heatPerShotMj = 0.035f;
            profile.heatCapacityMj = 18f;
            profile.coolingMw = 2.8f;
            profile.resumeThresholdMj = 7f;
            return profile;
        }

        if (Matches(partId, "WP_L_2020") || Matches(partId, "WP_R_2020"))
        {
            MechWeaponPerformance profile = AutomaticCannon(
                14f, 720f, 780f, 0.17f, 1.72f,
                3, 0.025f, 0.065f, 21, 14, 2.3f,
                AutomaticCannonAudioProfile.Mg17);
            profile.weaponType = "8 mm 连发精确手枪";
            profile.hudLabel = "B-PST";
            profile.projectileCaliberMillimeters = 8f;
            profile.tapBurstShotCount = 3;
            profile.heldBurstShotCount = 3;
            profile.heldBurstPauseSeconds = 0.28f;
            return profile;
        }

        if (Matches(partId, "WP_L_4250") || Matches(partId, "WP_R_4250"))
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.InterceptorMissile,
                "拦截导弹发射器",
                "INT",
                16,
                2,
                300f,
                MissileFlightProfile.InterceptorDefault);
            profile.verticalColdLaunch = true;
            return profile;
        }

        if (Matches(partId, "WP_L_4290") || Matches(partId, "WP_R_4290"))
        {
            return Missile(
                MechWeaponRole.AirToAirMissile,
                "空空导弹发射器",
                "MSL",
                4,
                6,
                3120f,
                MissileFlightProfile.AirToAirDefault,
                "AAM-4290",
                "Projectiles/aam4290_white_blackband",
                3.7f,
                warheadMassKg: 24f);
        }

        if (Matches(partId, "WP_L_4300") || Matches(partId, "WP_R_4300"))
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.InterceptorMissile,
                "近程拦截导弹发射器",
                "INT",
                8,
                4,
                300f,
                CompactInterceptorMissile(),
                "CIM-4300",
                "Projectiles/wp4300_interceptor_hellfire",
                1.65f,
                warheadMassKg: 8f);
            profile.effectiveRangeMeters = 8_000f;
            return profile;
        }

        if (Matches(partId, "WP_L_4301") || Matches(partId, "WP_R_4301"))
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.AirToAirMissile,
                "近距格斗空空导弹发射器",
                "WVR",
                8,
                4,
                2200f,
                CloseCombatAirToAirMissile(),
                "AAM-4301C",
                "Projectiles/wp4301_dogfight_aim7",
                2.35f,
                warheadMassKg: 12f);
            profile.effectiveRangeMeters = 16_000f;
            return profile;
        }

        if (Matches(partId, "WP_L_4302") || Matches(partId, "WP_R_4302"))
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.AirToAirMissile,
                "双脉冲远程空空导弹发射器",
                "DPL15",
                4,
                6,
                3500f,
                DualPulseLongRangeMissile(),
                "DPL-15",
                "Projectiles/r77_gridfin",
                3.6f);
            profile.effectiveRangeMeters = 65_000f;
            return profile;
        }

        if (Matches(partId, "WP_L_4340") || Matches(partId, "WP_R_4340"))
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.AirToAirMissile,
                "可调流量冲压远程空空导弹发射器",
                "MTR",
                2,
                4,
                4200f,
                DuctedLongRangeMissile(),
                "MTR-4340",
                "Projectiles/kh38mt_meteor_proxy",
                4.2f,
                Vector3.right);
            profile.effectiveRangeMeters = 100_000f;
            return profile;
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
                projectileCaliberMillimeters = 7.62f,
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

    private static MechWeaponPerformance Missile(
        MechWeaponRole role,
        string weaponType,
        string hudLabel,
        int magazineCapacity,
        int magazineCount,
        float directHitDamage,
        MissileFlightProfile flightProfile,
        string missileDisplayName = null,
        string missileVisualResourcePath = null,
        float missileVisualLengthMeters = 3.6f,
        Vector3? missileVisualSourceForward = null,
        float warheadMassKg = 0f)
    {
        return new MechWeaponPerformance
        {
            configured = true,
            role = role,
            weaponType = weaponType,
            hudLabel = hudLabel,
            directHitDamage = directHitDamage,
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
            warheadMassKg = warheadMassKg,
            warheadLethalRadiusMeters = flightProfile.WarheadLethalRadiusMeters,
            missileFlightProfile = flightProfile,
            missileDisplayName = missileDisplayName,
            missileVisualResourcePath = missileVisualResourcePath,
            missileVisualLengthMeters = missileVisualLengthMeters,
            missileVisualSourceForward = missileVisualSourceForward ?? Vector3.down
        };
    }

    private static MissileFlightProfile DualPulseLongRangeMissile()
    {
        MissileFlightProfile profile = MissileFlightProfile.AirToAirDefault;
        profile.PropulsionMode = MissilePropulsionMode.DualPulseSolid;
        profile.DryMassKg = 125f;
        profile.MainPropellantMassKg = 48f;
        profile.MainEngineThrustNewtons = 42_000f;
        profile.MainMassFlowKgPerSecond = 12f;
        profile.SecondStagePropellantMassKg = 54f;
        profile.SecondStageThrustNewtons = 28_000f;
        profile.SecondStageMassFlowKgPerSecond = 9f;
        profile.SecondStageIgnitionDelaySeconds = 6f;
        profile.SecondStageMinimumThrottle = 1f;
        profile.RcsPropellantMassKg = 16f;
        profile.RcsThrustNewtons = 15_000f;
        profile.MaximumAngularRateDegreesPerSecond = 220f;
        profile.MaximumFlightTimeSeconds = 360f;
        profile.MaximumRangeMeters = 90_000f;
        profile.SeekerActivationDelaySeconds = 4f;
        profile.SeekerRangeMeters = 18_000f;
        profile.SeekerSearchHalfAngleDegrees = 32f;
        profile.SeekerGimbalDegrees = 60f;
        profile.ProximityFuseRadiusMeters = 14f;
        profile.WarheadLethalRadiusMeters = 22f;
        return profile;
    }

    private static MissileFlightProfile CompactInterceptorMissile()
    {
        MissileFlightProfile profile = MissileFlightProfile.InterceptorDefault;
        profile.DryMassKg = 32f;
        profile.MainPropellantMassKg = 18f;
        profile.MainEngineThrustNewtons = 42_000f;
        profile.MainMassFlowKgPerSecond = 9f;
        profile.RcsPropellantMassKg = 8f;
        profile.RcsThrustNewtons = 28_000f;
        profile.RcsMassFlowKgPerSecond = 1.1f;
        profile.PitchYawInertiaKgM2 = 12f;
        profile.RcsMomentArmMeters = 0.8f;
        profile.MaximumAngularRateDegreesPerSecond = 720f;
        profile.EjectionSpeedMps = 60f;
        profile.MaximumFlightTimeSeconds = 32f;
        profile.MaximumRangeMeters = 12_000f;
        profile.SeekerActivationDelaySeconds = 0.08f;
        profile.SeekerRangeMeters = 15_000f;
        profile.SeekerSearchHalfAngleDegrees = 55f;
        profile.SeekerGimbalDegrees = 80f;
        profile.ProximityFuseRadiusMeters = 10f;
        profile.ProximityFuseArmSeconds = 0.16f;
        profile.ProximityFuseSafeDistanceMeters = 7f;
        profile.WarheadLethalRadiusMeters = 18f;
        return profile;
    }

    private static MissileFlightProfile CloseCombatAirToAirMissile()
    {
        MissileFlightProfile profile = MissileFlightProfile.AirToAirDefault;
        profile.DryMassKg = 72f;
        profile.MainPropellantMassKg = 38f;
        profile.MainEngineThrustNewtons = 54_000f;
        profile.MainMassFlowKgPerSecond = 11f;
        profile.RcsPropellantMassKg = 11f;
        profile.RcsThrustNewtons = 26_000f;
        profile.RcsMassFlowKgPerSecond = 1.05f;
        profile.PitchYawInertiaKgM2 = 55f;
        profile.RcsMomentArmMeters = 0.9f;
        profile.MaximumAngularRateDegreesPerSecond = 480f;
        profile.EjectionSpeedMps = 50f;
        profile.MaximumFlightTimeSeconds = 90f;
        profile.MaximumRangeMeters = 24_000f;
        profile.SeekerActivationDelaySeconds = 0.15f;
        profile.SeekerRangeMeters = 10_000f;
        profile.SeekerSearchHalfAngleDegrees = 50f;
        profile.SeekerGimbalDegrees = 78f;
        profile.ProximityFuseRadiusMeters = 9f;
        profile.ProximityFuseArmSeconds = 0.35f;
        profile.ProximityFuseSafeDistanceMeters = 12f;
        profile.WarheadLethalRadiusMeters = 13f;
        return profile;
    }

    private static MissileFlightProfile DuctedLongRangeMissile()
    {
        MissileFlightProfile profile = MissileFlightProfile.AirToAirDefault;
        profile.PropulsionMode = MissilePropulsionMode.VariableFlowDuctedRocket;
        profile.DryMassKg = 118f;
        profile.MainPropellantMassKg = 35f;
        profile.MainEngineThrustNewtons = 45_000f;
        profile.MainMassFlowKgPerSecond = 10f;
        profile.SecondStagePropellantMassKg = 72f;
        profile.SecondStageThrustNewtons = 13_500f;
        profile.SecondStageMassFlowKgPerSecond = 2.4f;
        profile.SecondStageIgnitionDelaySeconds = 0.15f;
        profile.SecondStageMinimumThrottle = 0.42f;
        profile.RcsPropellantMassKg = 18f;
        profile.RcsThrustNewtons = 14_000f;
        profile.MaximumAngularRateDegreesPerSecond = 200f;
        profile.MaximumFlightTimeSeconds = 420f;
        profile.MaximumRangeMeters = 140_000f;
        profile.SeekerActivationDelaySeconds = 6f;
        profile.SeekerRangeMeters = 25_000f;
        profile.SeekerSearchHalfAngleDegrees = 30f;
        profile.SeekerGimbalDegrees = 58f;
        profile.ProximityFuseRadiusMeters = 16f;
        profile.WarheadLethalRadiusMeters = 26f;
        return profile;
    }

    private static bool Matches(string partId, string sourceKey)
    {
        return !string.IsNullOrWhiteSpace(partId) &&
            partId.EndsWith(sourceKey, StringComparison.OrdinalIgnoreCase);
    }
}
