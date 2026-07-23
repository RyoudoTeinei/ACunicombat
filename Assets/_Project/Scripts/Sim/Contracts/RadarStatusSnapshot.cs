using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    public enum RadarOperatingMode : byte
    {
        Search = 0,
        TrackWhileScan = 1,
        SingleTargetTrack = 2,
    }

    public enum MissileGuidanceMode : byte
    {
        ActiveRadar = 0,
        SemiActiveRadar = 1,
        Infrared = 2,
        Command = 3,
    }

    [Flags]
    public enum RadarStatusFlags : byte
    {
        None = 0,
        Jammed = 1 << 0,
        NoiseLimited = 1 << 1,
        Overloaded = 1 << 2,
        TrackDegraded = 1 << 3,
    }

    /// <summary>
    /// Public, truth-free description of how the local radar is operating.
    /// </summary>
    [Serializable]
    public struct RadarStatusSnapshot
    {
        public RadarOperatingMode Mode;
        public float RangeMeters;
        public float ScanAzimuthDegrees;
        public float ScanElevationDegrees;
        public float BeamAzimuthDegrees;
        public float BeamElevationDegrees;
        public int TrackCount;
        public int TrackCapacity;
        public float RefreshRateHz;
        public float DwellLoad;
        public long DeferredDwellCount;
        public int GuidanceChannelsUsed;
        public int GuidanceChannelCapacity;
        public double3 OwnshipVelocity;
        public TrackId SttTrackId;
        public float SttProgress;
        public RadarStatusFlags Flags;
    }

    /// <summary>
    /// Short-lived anonymous detection used only for the transient echo display.
    /// Position is reconstructed from the radar measurement, never from target truth.
    /// </summary>
    [Serializable]
    public struct RadarEchoSnapshot
    {
        public double3 EstimatedPosition;
        public double Timestamp;
        public float SnrDb;
        public MeasurementFlags Flags;
        public TrackSourceMask Source;
    }
}
