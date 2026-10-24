using System;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public enum MechPartCondition { NormalOrLight, FunctionImpaired, SevereButUsable, Destroyed }
    public enum MechDamageSide { Center, Left, Right }
    [Serializable]
    public sealed class MechPartDamageProfile
    {
        [Tooltip("Functional damage tolerance, not a second life bar. Zero derives a default from structure and total AP.")]
        [Min(0)] public float functionalTolerance;
        [Min(0)] public float sensitivity = 1;
        [Tooltip("Support region for an internal device without geometry. None uses the head for radar, core for other internals.")]
        public MechPartSlot supportRegion = MechPartSlot.None;
        [Range(.01f,.9f)] public float impairmentThreshold = .25f;
        [Range(.02f,.99f)] public float severeThreshold = .65f;
        public float ResolveTolerance(MechPartDefinition part,float totalAP)
            => functionalTolerance > 0 ? functionalTolerance : Mathf.Max(400,Mathf.Max(part.Stats.structurePoints,totalAP*.30f));
        public MechPartCondition Condition(float stress,float tolerance)
        {
            float fraction=stress/Mathf.Max(1,tolerance);
            if(fraction>=1)return MechPartCondition.Destroyed;
            if(fraction>=Mathf.Clamp(severeThreshold,impairmentThreshold+.01f,.99f))return MechPartCondition.SevereButUsable;
            return fraction>=impairmentThreshold?MechPartCondition.FunctionImpaired:MechPartCondition.NormalOrLight;
        }
        public float Efficiency(float stress,float tolerance)
        {
            float f=stress/Mathf.Max(1,tolerance),first=Mathf.Clamp(impairmentThreshold,.01f,.9f),second=Mathf.Clamp(severeThreshold,first+.01f,.99f);
            if(f>=1)return 0;if(f<first)return 1;
            if(f<second)return Mathf.Lerp(1f,.75f,Mathf.InverseLerp(first,second,f));
            return Mathf.Lerp(.75f,.35f,Mathf.InverseLerp(second,1,f));
        }
    }
    public readonly struct MechPartDamageSnapshot
    {
        public readonly string RegionId,SlotId;
        public readonly Guid InstanceId;
        public readonly MechDamageSide Side;
        public readonly MechPartCondition Condition;
        public readonly float Efficiency;
        public MechPartDamageSnapshot(string region,string slot,Guid instance,MechDamageSide side,MechPartCondition condition,float efficiency)
        {RegionId=region;SlotId=slot;InstanceId=instance;Side=side;Condition=condition;Efficiency=efficiency;}
    }
}
