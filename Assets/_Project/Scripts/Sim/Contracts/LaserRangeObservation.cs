using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    /// <summary>
    /// Anonymous active-laser range return. The sensor boundary keeps the
    /// simulation entity private; fire control associates the return by angle,
    /// range, time and classification.
    /// </summary>
    [Serializable]
    public struct LaserRangeObservation
    {
        public SensorId SensorId;
        public double Timestamp;
        public float RangeMeters;
        public float RangeVar;
        public float3 Direction;
        public float AngleVar;
        public float SignalMarginDb;
        public TrackObjectClass ObjectClass;
        public float ClassificationConfidence;
    }
}
