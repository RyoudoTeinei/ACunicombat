using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MissilePropulsionMode : byte
    {
        SinglePulseSolid = 0,
        DualPulseSolid = 1,
        VariableFlowDuctedRocket = 2,
    }

    [Serializable]
    public struct MissileFlightProfile
    {
        [Header("Mass and main propulsion")]
        public MissilePropulsionMode PropulsionMode;
        [Min(0.1f)] public float DryMassKg;
        [Min(0f)] public float MainPropellantMassKg;
        [Min(0f)] public float MainEngineThrustNewtons;
        [Min(0.001f)] public float MainMassFlowKgPerSecond;

        [Header("Second pulse or sustain stage")]
        [Min(0f)] public float SecondStagePropellantMassKg;
        [Min(0f)] public float SecondStageThrustNewtons;
        [Min(0.001f)] public float SecondStageMassFlowKgPerSecond;
        [Min(0f)] public float SecondStageIgnitionDelaySeconds;
        [Range(0.05f, 1f)] public float SecondStageMinimumThrottle;

        [Header("Reaction control system")]
        [Min(0f)] public float RcsPropellantMassKg;
        [Min(0f)] public float RcsThrustNewtons;
        [Min(0.001f)] public float RcsMassFlowKgPerSecond;
        [Min(0.001f)] public float PitchYawInertiaKgM2;
        [Min(0.01f)] public float RcsMomentArmMeters;
        [Min(1f)] public float MaximumAngularRateDegreesPerSecond;

        [Header("Envelope and seeker")]
        [Min(0f)] public float EjectionSpeedMps;
        [Min(0.1f)] public float MaximumFlightTimeSeconds;
        [Min(100f)] public float MaximumRangeMeters;
        [Min(0.01f)] public float SeekerActivationDelaySeconds;
        [Min(100f)] public float SeekerRangeMeters;
        [Range(1f, 89f)] public float SeekerSearchHalfAngleDegrees;
        [Range(1f, 89f)] public float SeekerGimbalDegrees;

        [Header("Fuze and warhead")]
        [Min(0.1f)] public float ProximityFuseRadiusMeters;
        [Min(0f)] public float ProximityFuseArmSeconds;
        [Min(0f)] public float ProximityFuseSafeDistanceMeters;
        [Min(0.1f)] public float WarheadLethalRadiusMeters;

        public float InitialMassKg =>
            DryMassKg +
            MainPropellantMassKg +
            SecondStagePropellantMassKg +
            RcsPropellantMassKg;

        public float MainBurnDurationSeconds =>
            MainMassFlowKgPerSecond > 0f
                ? MainPropellantMassKg / MainMassFlowKgPerSecond
                : 0f;

        public float SecondStageBurnDurationSeconds =>
            SecondStageMassFlowKgPerSecond > 0f
                ? SecondStagePropellantMassKg / SecondStageMassFlowKgPerSecond
                : 0f;

        public bool HasSecondStage =>
            PropulsionMode != MissilePropulsionMode.SinglePulseSolid &&
            SecondStagePropellantMassKg > 0f &&
            SecondStageThrustNewtons > 0f &&
            SecondStageMassFlowKgPerSecond > 0f;

        public bool IsConfigured =>
            DryMassKg > 0f &&
            MainPropellantMassKg > 0f &&
            MainEngineThrustNewtons > 0f &&
            MainMassFlowKgPerSecond > 0f &&
            PitchYawInertiaKgM2 > 0f &&
            MaximumFlightTimeSeconds > 0f &&
            MaximumRangeMeters > 0f;

        public MissileFlightProfile Sanitized()
        {
            var profile = this;
            profile.DryMassKg = Mathf.Max(0.1f, profile.DryMassKg);
            profile.MainPropellantMassKg =
                Mathf.Max(0f, profile.MainPropellantMassKg);
            profile.MainEngineThrustNewtons =
                Mathf.Max(0f, profile.MainEngineThrustNewtons);
            profile.MainMassFlowKgPerSecond =
                Mathf.Max(0.001f, profile.MainMassFlowKgPerSecond);
            profile.SecondStagePropellantMassKg =
                Mathf.Max(0f, profile.SecondStagePropellantMassKg);
            profile.SecondStageThrustNewtons =
                Mathf.Max(0f, profile.SecondStageThrustNewtons);
            profile.SecondStageMassFlowKgPerSecond =
                Mathf.Max(0.001f, profile.SecondStageMassFlowKgPerSecond);
            profile.SecondStageIgnitionDelaySeconds =
                Mathf.Max(0f, profile.SecondStageIgnitionDelaySeconds);
            profile.SecondStageMinimumThrottle = Mathf.Clamp(
                profile.SecondStageMinimumThrottle > 0f
                    ? profile.SecondStageMinimumThrottle
                    : 0.4f,
                0.05f,
                1f);
            profile.RcsPropellantMassKg =
                Mathf.Max(0f, profile.RcsPropellantMassKg);
            profile.RcsThrustNewtons =
                Mathf.Max(0f, profile.RcsThrustNewtons);
            profile.RcsMassFlowKgPerSecond =
                Mathf.Max(0.001f, profile.RcsMassFlowKgPerSecond);
            profile.PitchYawInertiaKgM2 =
                Mathf.Max(0.001f, profile.PitchYawInertiaKgM2);
            profile.RcsMomentArmMeters =
                Mathf.Max(0.01f, profile.RcsMomentArmMeters);
            profile.MaximumAngularRateDegreesPerSecond =
                Mathf.Max(1f, profile.MaximumAngularRateDegreesPerSecond);
            profile.EjectionSpeedMps =
                Mathf.Max(0f, profile.EjectionSpeedMps);
            profile.MaximumFlightTimeSeconds =
                Mathf.Max(0.1f, profile.MaximumFlightTimeSeconds);
            profile.MaximumRangeMeters =
                Mathf.Max(100f, profile.MaximumRangeMeters);
            profile.SeekerActivationDelaySeconds =
                Mathf.Max(0.01f, profile.SeekerActivationDelaySeconds);
            profile.SeekerRangeMeters =
                Mathf.Max(100f, profile.SeekerRangeMeters);
            profile.SeekerSearchHalfAngleDegrees = Mathf.Clamp(
                profile.SeekerSearchHalfAngleDegrees,
                1f,
                89f);
            profile.SeekerGimbalDegrees = Mathf.Clamp(
                profile.SeekerGimbalDegrees,
                1f,
                89f);
            profile.ProximityFuseRadiusMeters =
                Mathf.Max(0.1f, profile.ProximityFuseRadiusMeters);
            profile.ProximityFuseArmSeconds =
                Mathf.Max(0f, profile.ProximityFuseArmSeconds);
            profile.ProximityFuseSafeDistanceMeters =
                Mathf.Max(0f, profile.ProximityFuseSafeDistanceMeters);
            profile.WarheadLethalRadiusMeters = Mathf.Max(
                profile.ProximityFuseRadiusMeters,
                profile.WarheadLethalRadiusMeters);
            return profile;
        }

        public static MissileFlightProfile AirToAirDefault =>
            new MissileFlightProfile
            {
                PropulsionMode = MissilePropulsionMode.SinglePulseSolid,
                DryMassKg = 120f,
                MainPropellantMassKg = 90f,
                MainEngineThrustNewtons = 58_000f,
                MainMassFlowKgPerSecond = 15f,
                RcsPropellantMassKg = 16f,
                RcsThrustNewtons = 14_000f,
                RcsMassFlowKgPerSecond = 0.9f,
                PitchYawInertiaKgM2 = 140f,
                RcsMomentArmMeters = 1.4f,
                MaximumAngularRateDegreesPerSecond = 240f,
                EjectionSpeedMps = 40f,
                MaximumFlightTimeSeconds = 300f,
                MaximumRangeMeters = 50_000f,
                SeekerActivationDelaySeconds = 0.5f,
                SeekerRangeMeters = 8_000f,
                SeekerSearchHalfAngleDegrees = 35f,
                SeekerGimbalDegrees = 60f,
                ProximityFuseRadiusMeters = 12f,
                ProximityFuseArmSeconds = 0.75f,
                ProximityFuseSafeDistanceMeters = 20f,
                WarheadLethalRadiusMeters = 18f,
            };

        public static MissileFlightProfile InterceptorDefault =>
            new MissileFlightProfile
            {
                PropulsionMode = MissilePropulsionMode.SinglePulseSolid,
                DryMassKg = 48f,
                MainPropellantMassKg = 32f,
                MainEngineThrustNewtons = 48_000f,
                MainMassFlowKgPerSecond = 16f,
                RcsPropellantMassKg = 10f,
                RcsThrustNewtons = 22_000f,
                RcsMassFlowKgPerSecond = 1.2f,
                PitchYawInertiaKgM2 = 20f,
                RcsMomentArmMeters = 1.1f,
                MaximumAngularRateDegreesPerSecond = 600f,
                EjectionSpeedMps = 55f,
                MaximumFlightTimeSeconds = 40f,
                MaximumRangeMeters = 15_000f,
                SeekerActivationDelaySeconds = 0.1f,
                SeekerRangeMeters = 12_000f,
                SeekerSearchHalfAngleDegrees = 50f,
                SeekerGimbalDegrees = 75f,
                ProximityFuseRadiusMeters = 12f,
                ProximityFuseArmSeconds = 0.2f,
                ProximityFuseSafeDistanceMeters = 8f,
                WarheadLethalRadiusMeters = 22f,
            };
    }
}
