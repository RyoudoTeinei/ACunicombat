using System;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public enum MechDefenseExtensionKind { None, ActiveProtection, ReactiveArmor }
    [Serializable]
    public sealed class MechDefenseExtensionProfile
    {
        public MechDefenseExtensionKind kind;
        [Min(0)] public int ammunition=8;
        [Min(0)] public float reactionSeconds=.15f;
        [Min(0)] public float cooldownSeconds=1;
        [Min(1)] public int maximumConcurrentShots=1;
        [Min(0)] public float minimumRangeMeters=8;
        [Min(1)] public float maximumRangeMeters=220;
        [Min(1)] public float detectionRangeMeters=1000;
        [Min(.02f)] public float scanIntervalSeconds=.04f;
        [Range(1,89)] public float fragmentHalfAngle=60;
        [Min(0)] public float burstLeadMeters=2;
        public Vector3 localFacing=Vector3.forward;
        [Range(0,180)] public float coverageHalfAngle=180;
        [Min(1)] public float projectileSpeed=900;
        [Min(0)] public float minimumBurstDistanceMeters=35;
        [Min(1)] public float smokeRingScale=20;
        [Min(.01f)] public float maximumFlightSeconds=1;
        [Min(0)] public float blastRadiusMeters=8;
        [Min(0)] public float blastDamage=160;
        [Min(0)] public float energyPerShotMj;
        [Min(.01f)] public float maximumEvidenceAgeSeconds=.5f;
        public MechReactiveArmorProfile armor=new MechReactiveArmorProfile{cells=2,coverageHalfAngle=180};
    }
}
