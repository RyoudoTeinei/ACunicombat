using System;
namespace ACunicombat.Sensing.Contracts
{
    public enum CombatBehaviorKind : byte { WeaponLaunch, MissileAttack, GunfireAttack, WeaponImpact, EquipmentAttack, ProtectedAssetAttack, FireControlLock, LaserIllumination, DirectedJamming, InvalidFriendlyAuthentication, CommandHostile }
    [Serializable]
    public struct CombatBehaviorReport
    {
        public Guid OriginalId;
        public CombatBehaviorKind Kind;
        public TrackSnapshot SourceTrack;
        public float Quality, Association;
        public double ObservedAt, ExpiresAt;
    }
}
