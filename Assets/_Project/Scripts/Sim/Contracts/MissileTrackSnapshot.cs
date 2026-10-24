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
        LaserRangefinder = 1 << 4,
        DataLink = 1 << 5,
        OwnLaunch = 1 << 6,
    }

    public enum MissileTrackQuality : byte
    {
        None = 0,
        Warning = 1,
        WeaponsQuality = 2,
    }

    public enum MissileTargetIntent : byte
    {
        Unknown = 0,
        Ownship = 1,
        FriendlyMissile = 2,
    }

    public enum MissileMissionRoleEvidence : byte
    {
        None = 0,
        KinematicIntent = 1,
        EsmWaveform = 2,
        AuthenticatedDataLink = 3,
        OwnLaunch = 4,
    }

    /// <summary>
    /// Fire-control-safe missile track. It contains only fused estimates and
    /// sensor evidence; no scene object or simulation entity identity escapes.
    /// </summary>
    [Serializable]
    public struct MissileTrackSnapshot
    {
        public Unity.Collections.FixedString128Bytes KnownModel;
        public TrackId TrackId;
        public TrackId AssociatedSensorTrackId;
        public LinkTerminalId ReportingTerminal;
        public Guid OwnLaunchIdentity;
        public TrackId AssignedTargetTrackId;
        public double3 Position;
        public double3 Velocity;
        public double3 Acceleration;
        public double3 Jerk;
        public float PositionUncertaintyMeters;
        public bool HasPositionCovariance;
        public double3x3 PositionCovariance;
        public float VelocityUncertaintyMetersPerSecond;
        public float RadialVelocityUncertaintyMetersPerSecond;
        public float3 ObserverLineOfSight;
        public float CpaUncertaintyMeters;
        public float Confidence;
        public float IdentityConfidence;
        public MissileMissionRole MissionRole;
        public float MissionRoleConfidence;
        public MissileMissionRoleEvidence MissionRoleEvidence;
        public MissileTargetIntent TargetIntent;
        public float TargetIntentConfidence;
        public float IntentOwnshipScore;
        public float IntentFriendlyMissileScore;
        public TrackId SuspectedTargetTrackId;
        /// <summary>
        /// RWR 对“该导引头主瓣正在照射本机”的融合证据。旁瓣接触恒为零。
        /// </summary>
        public float SeekerOwnshipEvidence;
        public double LastSeekerMainlobeTime;
        public float SeekerMainlobeContinuousSeconds;
        public float SeekerMainlobePowerTrendDbPerSecond;
        public MissileTrackSourceMask SourceMask;
        public TrackAffiliation Affiliation;
        public MissileTrackQuality Quality;
        public bool Confirmed;
        public bool Incoming;
        public float TimeToClosestApproachSeconds;
        public float ClosestApproachDistanceMeters;
        public double FirstObservationTime;
        public double LastEvidenceTime;
        public double KinematicUpdateTime;
        public double LastUpdateTime;

        public bool IsWarning =>
            Quality >= MissileTrackQuality.Warning;

        public bool IsWeaponsQuality =>
            Quality >= MissileTrackQuality.WeaponsQuality;

        public bool IsFriendly =>
            Affiliation == TrackAffiliation.Friendly &&
            IdentityConfidence >= 0.5f;

        public bool HasAuthenticatedMissionRole =>
            IsFriendly &&
            (SourceMask & MissileTrackSourceMask.DataLink) != 0 &&
            MissionRoleEvidence ==
                MissileMissionRoleEvidence.AuthenticatedDataLink &&
            MissionRole != MissileMissionRole.Unknown &&
            MissionRoleConfidence >= 0.5f;

        public bool IsLikelyTargetingOwnship =>
            !IsFriendly &&
            TargetIntent == MissileTargetIntent.Ownship &&
            TargetIntentConfidence >= 0.75f;

        public bool IsLikelyInterceptor =>
            !IsFriendly &&
            MissionRole == MissileMissionRole.Interceptor &&
            MissionRoleConfidence >= 0.65f;

        public bool IsLikelyAirToAir =>
            !IsFriendly &&
            MissionRole == MissileMissionRole.AirToAir &&
            MissionRoleConfidence >= 0.65f;

        public double3 PredictPosition(double time)
        {
            var age = math.max(0.0, time - LastUpdateTime);
            return Position +
                   Velocity * age +
                   0.5 * Acceleration * age * age +
                   (1.0 / 6.0) * Jerk * age * age * age;
        }

        public double3 PredictVelocity(double time)
        {
            var age = math.max(0.0, time - LastUpdateTime);
            return Velocity +
                   Acceleration * age +
                   0.5 * Jerk * age * age;
        }

        public double3 PredictAcceleration(double time)
        {
            var age = math.max(0.0, time - LastUpdateTime);
            return Acceleration + Jerk * age;
        }
    }
}
