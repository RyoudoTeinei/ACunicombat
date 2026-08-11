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
        public double3 Acceleration;
        public double3 Jerk;
        public float PositionUncertaintyMeters;
        public float CpaUncertaintyMeters;
        public float Confidence;
        public float IdentityConfidence;
        public MissileMissionRole MissionRole;
        public float MissionRoleConfidence;
        public MissileTargetIntent TargetIntent;
        public float TargetIntentConfidence;
        public TrackId SuspectedTargetTrackId;
        public MissileTrackSourceMask SourceMask;
        public TrackAffiliation Affiliation;
        public MissileTrackQuality Quality;
        public bool Confirmed;
        public bool Incoming;
        public float TimeToClosestApproachSeconds;
        public float ClosestApproachDistanceMeters;
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
            MissionRole != MissileMissionRole.Unknown &&
            MissionRoleConfidence >= 0.5f;

        public bool IsLikelyTargetingOwnship =>
            !IsFriendly &&
            TargetIntent == MissileTargetIntent.Ownship &&
            TargetIntentConfidence >= 0.75f;

        public bool IsLikelyInterceptor =>
            !IsFriendly &&
            TargetIntent == MissileTargetIntent.FriendlyMissile &&
            TargetIntentConfidence >= 0.9f;

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
