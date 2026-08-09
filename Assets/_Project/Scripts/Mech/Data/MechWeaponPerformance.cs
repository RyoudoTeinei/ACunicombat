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
        public string hudLabel;

        [Header("Projectile")]
        [Min(0f)] public float directHitDamage;
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
        [Min(0)] public int magazineCapacity;
        [Min(0)] public int magazineCount;
        [Min(0f)] public float reloadSeconds;

        [Header("Fuze and warhead")]
        public bool targetProximityFuse;
        public string proximityFuseMode;
        [Min(0f)] public float proximityFuseRadiusMeters;
        [Min(0f)] public float proximityFuseArmSeconds;
        [Min(0f)] public float proximityFuseSafeDistanceMeters;
        [Min(0f)] public float warheadLethalRadiusMeters;

        [Header("Guided missile")]
        public MissileFlightProfile missileFlightProfile;

        [Header("Point defense")]
        public bool automaticPointDefense;
        [Range(0f, 179f)] public float maximumOffBoresightDegrees;
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
            return (MechWeaponPerformance)MemberwiseClone();
        }
    }
}
