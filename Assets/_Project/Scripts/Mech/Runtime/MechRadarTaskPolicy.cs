using UnityEngine;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    // Shared scheduling arithmetic, independent of frame rate. The sensor still enforces antenna time.
    public static class MechRadarTaskPolicy
    {
        public const float AcquisitionBeamMultiplier=2.5f, AcquisitionDwellSeconds=.004f, DeepDwellSeconds=.008f;
        public const int SearchCycle=3; // Two acquisition cells and one deep cell: equal antenna time per tier.
        public static bool DeepSearch(ulong sequence)=>sequence%SearchCycle==SearchCycle-1;
        public static float SearchDuty(bool tracking,bool stt,bool defense)=>defense||stt?.2f:tracking?.35f:.65f;
        public static float RevisitWeight(in TrackSnapshot t,double3 observer,double3 ownVelocity,double now)
        {
            if(t.ObjectClass!=TrackObjectClass.Missile)return t.Lifecycle==TrackLifecycle.Tentative?1.5f:1;
            if(t.Affiliation==TrackAffiliation.Friendly)return 1;
            var r=t.PredictPosition(now)-observer;var v=t.Velocity-ownVelocity;
            return math.dot(r,v)<0?3:1.5f;
        }
        public static double BatchInterval(int count,ulong start,double antennaDuty,double share,double minimum)
        {double cost=0;for(int i=0;i<count;i++)cost+=DeepSearch(start+(ulong)i)?DeepDwellSeconds:AcquisitionDwellSeconds;return math.max(minimum,cost/math.max(.01,antennaDuty*share));}
    }
}
