using System;
using Unity.Mathematics;
namespace ACunicombat.Sensing.Contracts
{
    public enum FleetTaskGroup : byte { Strike, Support, Reconnaissance, Escort, AirSuperiority, AirDefenseSuppression, ElectronicWarfare, EarlyWarningCommand, Logistics }
    public enum FleetMissionGeometry : byte { Target, Area, Point, PointDirection }
    public enum FleetFormationShape : byte { Automatic, EchelonLeft, EchelonRight, LineAbreast, Trail, Wedge, FingerFour, Diamond, Custom }
    public enum FleetRadarControl : byte { RadiationSilence, Active, Intelligent }
    public enum FleetAttackMode : byte { Confirmed, Free }
    public enum FleetPacketKind : byte { Heartbeat, Capability, Order, Acknowledgement, Cancel, TargetUpdate, Threat, DefenseClaim, DefenseCommitted, DefenseRelease, TaskResult }
    public enum FleetOrderKind : byte { Attack, AttackWithWeapon, SetAttackMode, Return, Move, SetRadarControl, ConfigureFormation, AssignMission, SetCruiseSpeed }
    public enum FleetTaskPhase : byte { None, Requested, Acquiring, Executing, Returning, Completed, Rejected, Cancelled, Expired }
    public enum FleetTaskReason : byte { None, Unauthorized, PlayerIsNotSubordinate, Expired, StaleRevision, MissingWeapon, MissingObservation, IdentityDenied, UnsupportedMovement, LeaderUnavailable, Cancelled, LeaseExpired, LinkTimeout, WeaponUnavailable, TargetLost, EngagementCompleted, UnsupportedRadarMode, ActiveAttackProtected }
    // Network payload only: no scene/entity handle, transform, or foreign fire-control reference.
    [Serializable]
    public struct FleetPacket
    {
        // Budget the logical wire payload, not the in-memory union's unused target fields.
        public float EstimatedWireKilobits => (Kind==FleetPacketKind.Order||Kind==FleetPacketKind.TargetUpdate||Kind==FleetPacketKind.Threat||Threat.TrackId.IsValid||Target.TrackId.IsValid?8f:2f)+(HasPlatformStatus?.8f:0)+WeaponName.Length*.008f+FormationName.Length*.008f+(Order==FleetOrderKind.ConfigureFormation?.6f:0)+(HasNavigationGoal?.2f:0)+Waypoints.Length*.192f+MissionSectors.Length*.128f+(Order==FleetOrderKind.AssignMission?.9f:0)+(Kind==FleetPacketKind.Capability?.096f:0);
        public Unity.Collections.FixedList512Bytes<double3> Waypoints;
        public FleetPacketKind Kind;
        public Guid Sender, Recipient, Fleet, Leader, Operation, Contact, Weapon, ProtectedMember;
        public long Revision;
        public double ObservedAt, ExpiresAt, LeaseUntil, TaskDeadline;
        public FleetOrderKind Order;
        public FleetAttackMode Mode;
        public FleetRadarControl RadarControl;
        public FleetMissionGeometry MissionGeometry;
        public Unity.Collections.FixedList4096Bytes<int4> MissionSectors;
        public double3 MissionGridOrigin, MissionPoint;
        public quaternion MissionGridRotation;
        public float MissionCellSize;
        public float3 MissionDirection;
        public Guid MissionProtectedMember;
        public bool MissionProtectFormation;
        public Guid FormationRoot;
        public int FormationIndex, FormationSize;
        public FleetTaskGroup TaskGroup;
        public FleetFormationShape FormationShape;
        public float3 FormationOffset;
        public Unity.Collections.FixedString128Bytes FormationName;
        public bool AutomaticEngagement;
        public bool AuthorizedWeaponSelection; // Explicit player attack authorization; retained through delegation.
        public bool HasPlatformStatus, RadarPowered, HasDamageReport;
        public int ImpairedRegions, DisabledRegions;
        public float HealthFraction, HeadingDegrees;
        public Unity.Collections.FixedString512Bytes WeaponName;
        public FleetTaskPhase Phase;
        public FleetTaskReason Reason;
        public double3 Position, Velocity;
        public bool HasNavigationGoal;
        public double3 NavigationGoal;
        public TrackSnapshot Target;
        public MissileTrackSnapshot Threat;
        public int Role, Ammunition;
        public float MinimumRange, EffectiveRange, ProjectileSpeed, CruiseSpeed;
        public bool HasActionReport;
        public int CombatAction;
        public int WeaponMount; // Sender-reported MechPartSlot; zero means not reported.
        public float MaximumRange, EstimatedSeconds, ThreatTimeSeconds;
        public bool Available, IsPlayer, IsLeader, WholeFleet;
    }
}
