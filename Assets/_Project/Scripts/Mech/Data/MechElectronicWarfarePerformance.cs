using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechElectronicWarfarePerformance
    {
        [Tooltip("有向天线输出后的等效辐射功率，不是机体耗电。")]
        [Min(0f)] public float equivalentRadiatedPowerWatts = 50000f;
        [Min(1f)] public float noiseBandwidthMHz = 100f;
        [Range(2f, 120f)] public float beamWidthDegrees = 45f;
        [Min(1000f)] public float maximumRangeMeters = 150000f;
        [Min(.04f)] public float emissionIntervalSeconds = .04f;
        [Min(-20f)] public float sidelobeLevelDb = -20f;

        public bool HasData => equivalentRadiatedPowerWatts > 0f && noiseBandwidthMHz > 0f &&
                               beamWidthDegrees > 0f && maximumRangeMeters > 0f;

        public MechElectronicWarfarePerformance Clone() => (MechElectronicWarfarePerformance)MemberwiseClone();
    }
}
