using System;
using UnityEngine;
namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechReactiveArmorProfile
    {
        [Min(0)] public int cells;
        public bool protectsMissiles = true;
        public bool protectsGunfire;
        public Vector3 localFacing = Vector3.forward;
        [Range(0,180)] public float coverageHalfAngle = 70;
        [Range(0,1)] public float damageReduction = .6f;
        [Min(0)] public float minimumTriggerDamage = 1;
        public bool Accepts(TestWeaponKind kind) => kind == TestWeaponKind.Missile ? protectsMissiles : protectsGunfire;
    }
}
