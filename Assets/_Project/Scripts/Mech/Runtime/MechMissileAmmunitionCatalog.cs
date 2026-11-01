using System;
using UnityEngine;
namespace ACunicombat.Mechs {
public static class MechMissileAmmunitionCatalog {
public const string Jianwei="JW7", JianweiEr="JW7-ER";
public static MechWeaponPerformance Create(string id) {
var result=CreateProfile(id);result?.RefreshMissileEnvelope();return result;
}
private static MechWeaponPerformance CreateProfile(string id) {
if(id=="ABM-12"||id=="ABM-18")return BuildAbm(id=="ABM-18");
if(id=="SIM-29"||id=="SIM-29M"||id=="SIM-57"||id=="SIM-57R")return BuildSim(id);
if(id=="AAM-4290C"||id=="AAM-4290R")return MechMissileVariantCatalog.CreateLegacy4290Variant(Create("AAM-4290"),id=="AAM-4290C"?1:2).Performance;
if(id==Jianwei||id==JianweiEr)return BuildJianwei(id==JianweiEr);
        if (id == "AAM-4290")
        {
            return Missile(
                MechWeaponRole.AirToAirMissile,
                "空空导弹发射器",
                "MSL",
                3120f,
                MissileFlightProfile.AirToAirDefault,
                "AAM-4290",
                "Projectiles/aam4290_white_blackband",
                3.7f,
                warheadMassKg: 24f);
        }

        if (id == "CIM-4300")
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.InterceptorMissile,
                "近程拦截导弹发射器",
                "INT",
                300f,
                CompactInterceptorMissile(),
                "CIM-4300",
                "Projectiles/wp4300_interceptor_hellfire",
                1.65f,
                warheadMassKg: 8f);

            return profile;
        }

        if (id == "AAM-4301C")
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.AirToAirMissile,
                "近距格斗空空导弹发射器",
                "天空之手",
                2200f,
                CloseCombatAirToAirMissile(),
                "天空之手",
                "Projectiles/wp4301_dogfight_aim7",
                2.35f,
                warheadMassKg: 12f);
            profile.coldLaunch = true;

            return profile;
        }

        if (id == "DPL-15")
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.AirToAirMissile,
                "双脉冲远程空空导弹发射器",
                "火石15",
                3500f,
                DualPulseLongRangeMissile(),
                "火石 15",
                "Projectiles/r77_gridfin",
                3.6f, warheadMassKg: 24f);
            profile.coldLaunch = true;

            return profile;
        }

        if (id == "DPL-17")
        {
            var profile = Missile(MechWeaponRole.AirToAirMissile,
                "长程双脉冲反辐射/空空导弹发射器", "火石17", 4600f,
                ExtendedDualPulseMissile(), "火石 17", "Projectiles/r77_gridfin", 5.5f,
                warheadMassKg: 35f);
            profile.supportsPassiveRfHoming = true;
            profile.coldLaunch = true;

            return profile;
        }

        if (id == "LRA-4230")
        {
            var profile = Missile(MechWeaponRole.AirToAirMissile,
                "常规反辐射/长程空空导弹发射器", "SIM240", 5000f,
                HeavySinglePulseMissile(), "SIM-240 LR", "Projectiles/r27_white", 5.0f,
                warheadMassKg: 45f);
            profile.supportsPassiveRfHoming = true;

            return profile;
        }

        if (id == "MTR-4340" || id == "MTR-4340-ER")
        {
            MechWeaponPerformance profile = Missile(
                MechWeaponRole.AirToAirMissile,
                "可调流量反辐射/长程空空导弹发射器",
                id == "MTR-4340-ER" ? "流星-ER" : "流星",
                4200f,
                DuctedLongRangeMissile(id == "MTR-4340-ER"),
                id == "MTR-4340-ER" ? "流星-ER" : "流星",
                "Projectiles/kh38mt_meteor_proxy",
                4.2f,
                Vector3.right, warheadMassKg: 24f);
            profile.supportsPassiveRfHoming = true;

            return profile;
        }

return null;
}
private static MechWeaponPerformance BuildJianwei(bool er) {
 var f=MissileFlightProfile.InterceptorDefault;
 f.DryMassKg=48;f.MainPropellantMassKg=er?64:48;f.MainEngineThrustNewtons=48000;f.MainMassFlowKgPerSecond=12;
 f.RcsPropellantMassKg=er?18:16;f.MaximumFlightTimeSeconds=er?26.05f:22.63f;f.SeekerRangeMeters=16000;
 var p=Missile(MechWeaponRole.InterceptorMissile,"拦截导弹","INT",300,f,er?"剑卫7C":"剑卫7","Projectiles/r27_white",4f,warheadMassKg:9f);
 p.verticalColdLaunch=true;return p;
}
private static MechWeaponPerformance BuildAbm(bool extended)
{
    var f=CompactInterceptorMissile();

    if(extended){f.MainPropellantMassKg=28;f.RcsPropellantMassKg=10;f.MaximumFlightTimeSeconds=9.02f;}
    return Missile(MechWeaponRole.InterceptorMissile,"近程拦截导弹","INT",300,f,
        extended?"ABM-18":"ABM-12","Projectiles/wp4300_interceptor_hellfire",1.65f,warheadMassKg:8);
}
private static MechWeaponPerformance BuildSim(string id)
{
    var f=MissileFlightProfile.AirToAirDefault;
    f.SeekerRangeMeters=10000;
    switch(id)
    {
        case "SIM-29":
            f.DryMassKg=108;f.MainPropellantMassKg=58;f.MainMassFlowKgPerSecond=14;
            f.RcsPropellantMassKg=16;f.RcsThrustNewtons=16000;f.PitchYawInertiaKgM2=120;
            f.MaximumFlightTimeSeconds=19.95f;break;
        case "SIM-29M":
            f.DryMassKg=104;f.MainPropellantMassKg=68;f.MainMassFlowKgPerSecond=14;
            f.RcsPropellantMassKg=22;f.RcsThrustNewtons=24000;f.RcsMassFlowKgPerSecond=1;
            f.PitchYawInertiaKgM2=100;f.MaximumAngularRateDegreesPerSecond=330;
            f.MaximumFlightTimeSeconds=21.7f;break;
        case "SIM-57":
            f.DryMassKg=120;f.MainPropellantMassKg=110;f.MainMassFlowKgPerSecond=14;
            f.RcsPropellantMassKg=18;f.PitchYawInertiaKgM2=165;f.MaximumAngularRateDegreesPerSecond=200;
            f.MaximumFlightTimeSeconds=27.34f;break;
        case "SIM-57R":
            f.DryMassKg=120;f.MainPropellantMassKg=145;f.MainMassFlowKgPerSecond=13.5f;
            f.RcsPropellantMassKg=18;f.PitchYawInertiaKgM2=180;f.MaximumAngularRateDegreesPerSecond=190;
            f.MaximumFlightTimeSeconds=30.57f;break;
    }
    return Missile(MechWeaponRole.AirToAirMissile,"通用空空导弹","MSL",3120,f,id,
        "Projectiles/aam4290_white_blackband",3.7f,warheadMassKg:24);
}
    private static MechWeaponPerformance Missile(
        MechWeaponRole role,
        string weaponType,
        string hudLabel,
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
            coldLaunch = role == MechWeaponRole.InterceptorMissile,
            role = role,
            weaponType = weaponType,
            hudLabel = hudLabel,
            directHitDamage = directHitDamage,
            muzzleVelocityMetersPerSecond = flightProfile.EjectionSpeedMps,
            projectileLifetimeSeconds = flightProfile.MaximumFlightTimeSeconds,
            maximumRangeMeters = flightProfile.MaximumRangeMeters,
            effectiveRangeMeters = 0,
            magazineCapacity = 0,
            magazineCount = 0,
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
        profile.MaximumFlightTimeSeconds = 57.7f;

        profile.SeekerActivationDelaySeconds = 4f;
        profile.SeekerRangeMeters = 18_000f;
        profile.SeekerSearchHalfAngleDegrees = 32f;
        profile.SeekerGimbalDegrees = 60f;
        profile.ProximityFuseRadiusMeters = 14f;
        profile.WarheadLethalRadiusMeters = 22f;
        return profile;
    }

    private static MissileFlightProfile ExtendedDualPulseMissile()
    {
        var p = DualPulseLongRangeMissile();
        p.DryMassKg = 220f;
        p.MainPropellantMassKg = 110f; p.MainEngineThrustNewtons = 90_000f; p.MainMassFlowKgPerSecond = 15f;
        p.SecondStagePropellantMassKg = 110f; p.SecondStageThrustNewtons = 60_000f; p.SecondStageMassFlowKgPerSecond = 10f;
        p.AdaptiveSecondPulse = true;
        p.SecondStageIgnitionDelaySeconds = 2f; // Minimum inter-pulse coast, not a fixed ignition timer.
        p.RcsPropellantMassKg = 35f; p.RcsThrustNewtons = 24_000f;
        p.PitchYawInertiaKgM2 = 420f; p.RcsMomentArmMeters = 2f;
        p.MaximumAngularRateDegreesPerSecond = 170f;
        p.MaximumFlightTimeSeconds = 95.09f;
        p.SeekerRangeMeters = 35_000f;
        p.PassiveMemorySeconds = 12f;
        p.WarheadLethalRadiusMeters = 28f;
        return p;
    }

    private static MissileFlightProfile HeavySinglePulseMissile()
    {
        var p = MissileFlightProfile.AirToAirDefault;
        p.DryMassKg = 260f;
        p.MainPropellantMassKg = 210f; p.MainEngineThrustNewtons = 100_000f; p.MainMassFlowKgPerSecond = 14f;
        p.RcsPropellantMassKg = 28f; p.RcsThrustNewtons = 22_000f;
        p.PitchYawInertiaKgM2 = 480f; p.RcsMomentArmMeters = 1.8f;
        p.MaximumAngularRateDegreesPerSecond = 150f;
        p.MaximumFlightTimeSeconds = 68.84f;
        p.SeekerActivationDelaySeconds = 4f; p.SeekerRangeMeters = 30_000f;
        p.PassiveMemorySeconds = 12f;
        p.ProximityFuseRadiusMeters = 16f; p.WarheadLethalRadiusMeters = 32f;
        return p;
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
        profile.MaximumFlightTimeSeconds = 7.72f;

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
        profile.MaximumFlightTimeSeconds = 14.42f;

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

    private static MissileFlightProfile DuctedLongRangeMissile(bool extended = false)
    {
        MissileFlightProfile profile = MissileFlightProfile.AirToAirDefault;
        profile.PropulsionMode = MissilePropulsionMode.VariableFlowDuctedRocket;
        profile.DryMassKg = 118f;
        profile.MainPropellantMassKg = 35f;
        profile.MainEngineThrustNewtons = 45_000f;
        profile.MainMassFlowKgPerSecond = 10f;
        profile.SecondStagePropellantMassKg = extended ? 172f : 144f;
        profile.SecondStageThrustNewtons = 13_500f;
        profile.SecondStageMassFlowKgPerSecond = 2.4f;
        profile.SecondStageIgnitionDelaySeconds = 0.15f;
        profile.SecondStageMinimumThrottle = 0.42f;
        profile.RcsPropellantMassKg = 30f;
        profile.RcsThrustNewtons = 14_000f;
        profile.MaximumAngularRateDegreesPerSecond = 200f;
        profile.MaximumFlightTimeSeconds = extended ? 109.79f : 101.2f;

        profile.SeekerActivationDelaySeconds = 6f;
        profile.SeekerRangeMeters = 25_000f;
        profile.PassiveMemorySeconds = 12f;
        profile.SeekerSearchHalfAngleDegrees = 30f;
        profile.SeekerGimbalDegrees = 58f;
        profile.ProximityFuseRadiusMeters = 16f;
        profile.WarheadLethalRadiusMeters = 26f;
        return profile;
    }
}
}
