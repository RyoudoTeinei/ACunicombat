using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechWeaponPerformance
    {
        [Header("Identity")]
        public bool configured;
        public MechWeaponRole role;
        public string weaponType;
        public string weaponSeriesId;
        public string hudLabel;

        [Header("Projectile")]
        [Min(0f)] public float directHitDamage;
        [Min(0f)] public float projectileCaliberMillimeters;
        [Min(0f)] public float muzzleVelocityMetersPerSecond;
        [Min(0f)] public float projectileLifetimeSeconds;
        [Min(0f)] public float maximumRangeMeters;
        public bool calculateEffectiveRange;
        [Min(0f)] public float effectiveRangeMeters;
        [Min(0f)] public float roundsPerMinute;

        [Header("Dispersion and recoil")]
        [Min(0f)] public float baseDispersionMilliradians;
        [Min(0f)] public float maximumDispersionMilliradians;
        [Min(0)] public int lowRecoilShotCount;
        [Min(0f)] public float lowRecoilPerShotMilliradians;
        [Min(0f)] public float sustainedRecoilPerShotMilliradians;
        [Min(0f)] public float dispersionRecoveryMilliradiansPerSecond;
        [Min(0f)] public float dispersionRecoveryDelaySeconds;
        [Min(0f)] public float burstResetSeconds;
        [Min(0f)] public float recoilImpulseNewtonSeconds;

        [Header("Ammunition")]
        public bool beltFed;
        [Min(0)] public int magazineCapacity;
        [Min(0)] public int magazineCount;
        [Min(0)] public int missileGroupCapacity;
        [Min(0f)] public float missileLaunchCycleSeconds;
        [Min(0f)] public float reloadSeconds;

        [Header("Trigger behavior")]
        [Min(1)] public int tapBurstShotCount = 1;
        [Min(0)] public int heldBurstShotCount;
        [Min(0f)] public float heldBurstPauseSeconds;

        [Header("Fuze and warhead")]
        public bool targetProximityFuse;
        public string proximityFuseMode;
        [Min(0f)] public float proximityFuseRadiusMeters;
        [Min(0f)] public float proximityFuseArmSeconds;
        [Min(0f)] public float proximityFuseSafeDistanceMeters;
        [Min(0f)] public float warheadMassKg;
        [Min(0f)] public float warheadLethalRadiusMeters;

        [Header("Guided missile")]
        public MissileFlightProfile missileFlightProfile;
        public bool verticalColdLaunch;
        public bool coldLaunch;
        public bool supportsPassiveRfHoming;

        [Header("Guided missile presentation")]
        public string missileDisplayName;
        public string missileVisualResourcePath;
        [Min(0.1f)] public float missileVisualLengthMeters;
        public Vector3 missileVisualSourceForward;

        [Header("Point defense")]
        public bool automaticPointDefense;
        [Range(0f, 179f)] public float outboardYawLimitDegrees;
        [Range(0f, 179f)] public float inboardYawLimitDegrees;
        [Range(0f, 89f)] public float elevationLimitDegrees;
        [Range(0f, 89f)] public float depressionLimitDegrees;
        [Min(0f)] public float traverseRateDegreesPerSecond;
        [Min(0f)] public float traverseAccelerationDegreesPerSecondSquared;
        [Min(0f)] public float traverseDecelerationDegreesPerSecondSquared;
        [Min(0f)] public float aimSmoothingSeconds;
        [Min(0f)] public float maximumLeadSeconds;
        [Min(0f)] public float trackHoldSeconds;
        [Min(0f)] public float minimumHitProbability;
        [Min(0f)] public float maximumThreatTimeSeconds;
        public bool requiresWeaponsQualityTrack;
        public bool incomingTargetsOnly;
        [Min(0f)] public float pointDefenseRoundLethalRadiusMeters;
        [Min(0f)] public float pointDefenseRoundArmSeconds;
        [Min(0f)] public float pointDefenseRoundSafeDistanceMeters;

        [Header("Action resources")]
        [Min(0f)] public float energyPerShotMj;

        [Header("Heat")]
        [Min(0f)] public float heatPerShotMj;
        [Min(0f)] public float heatCapacityMj;
        [Min(0f)] public float coolingMw;
        [Min(0f)] public float resumeThresholdMj;
        [Min(0f)] public float spinUpSeconds;

        [Header("Presentation")]
        public AutomaticCannonAudioProfile automaticCannonAudioProfile;
        public CannonAudioProfile cannonAudioProfile;

        public bool IsConfigured => configured && role != MechWeaponRole.None;

        public bool IsGuidedMissile =>
            role == MechWeaponRole.InterceptorMissile ||
            role == MechWeaponRole.AirToAirMissile;

        public bool IsAutomatic =>
            role == MechWeaponRole.AutomaticCannon ||
            role == MechWeaponRole.PointDefenseCannon;

        public MechWeaponPerformance Clone()
        {
            var clone = (MechWeaponPerformance)MemberwiseClone();
            clone.RefreshMissileEnvelope();
            return clone;
        }
        public void RefreshMissileEnvelope()
        {
            if(!IsGuidedMissile || !missileFlightProfile.IsConfigured) return;
            var dynamics = missileFlightProfile.Kinematics;
            if(!hasCachedEnvelope || !dynamics.HasSameDynamics(cachedDynamics))
            {
                missileFlightProfile = missileFlightProfile.Sanitized();
                cachedDynamics = missileFlightProfile.Kinematics;
                cachedReferenceRange = missileFlightProfile.MaximumRangeMeters;
                hasCachedEnvelope = true;
            }
            missileFlightProfile.MaximumRangeMeters = cachedReferenceRange;
            maximumRangeMeters = cachedReferenceRange;
            effectiveRangeMeters = 0;
            calculateEffectiveRange = false;
            projectileLifetimeSeconds = missileFlightProfile.MaximumFlightTimeSeconds;
        }
        [NonSerialized] private bool hasCachedEnvelope;
        [NonSerialized] private ACunicombat.FireControl.MissileKinematicProfile cachedDynamics;
        [NonSerialized] private float cachedReferenceRange;
    }
}
