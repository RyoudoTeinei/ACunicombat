using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    [Flags]
    public enum MissileTrackSourceMask : byte
    {
        None = 0,
        Radar = 1 << 0,
        Rwr = 1 << 1,
        Maw = 1 << 2,
        ElectroOptical = 1 << 3,
    }

    public enum MissileTrackQuality : byte
    {
        None = 0,
        Warning = 1,
        WeaponsQuality = 2,
    }

    /// <summary>
    /// Fire-control-safe missile track. It contains only fused estimates and
    /// sensor evidence; no scene object or simulation entity identity escapes.
    /// </summary>
    [Serializable]
    public struct MissileTrackSnapshot
    {
        public TrackId TrackId;
        public double3 Position;
        public double3 Velocity;
        public float PositionUncertaintyMeters;
        public float CpaUncertaintyMeters;
        public float Confidence;
        public MissileTrackSourceMask SourceMask;
        public MissileTrackQuality Quality;
        public bool Confirmed;
        public bool Incoming;
        public float TimeToClosestApproachSeconds;
        public float ClosestApproachDistanceMeters;
        public double LastEvidenceTime;
        public double LastUpdateTime;

        public bool IsWarning =>
            Quality >= MissileTrackQuality.Warning;

        public bool IsWeaponsQuality =>
            Quality >= MissileTrackQuality.WeaponsQuality;

        public double3 PredictPosition(double time)
        {
            var age = math.max(0.0, time - LastUpdateTime);
            return Position + Velocity * age;
        }
    }
}
