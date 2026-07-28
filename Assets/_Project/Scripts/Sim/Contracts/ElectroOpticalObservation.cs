using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    /// <summary>
    /// Anonymous electro-optical bearing observation. A monocular channel does
    /// not publish range; fire control must associate it with another track.
    /// </summary>
    [Serializable]
    public struct ElectroOpticalObservation
    {
        public SensorId SensorId;
        public double Timestamp;
        public float3 Direction;
        public float AngleVar;
        public float ApparentDiameterRadians;
        public float SignalMarginDb;
        public TrackObjectClass ObjectClass;
        public float ClassificationConfidence;
    }
}
