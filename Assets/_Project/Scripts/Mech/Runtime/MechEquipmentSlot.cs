using System;
namespace ACunicombat.Mechs
{
    public readonly struct MechEquipmentSlot : IEquatable<MechEquipmentSlot>
    {
        public readonly string Id;
        public readonly MechPartSlot LegacyRole;
        public MechEquipmentSlot(string id, MechPartSlot legacyRole = MechPartSlot.None) { Id = id; LegacyRole = legacyRole; }
        public static implicit operator MechEquipmentSlot(MechPartSlot role) => new MechEquipmentSlot(MechSlotGraph.LegacyId(role), role);
        public bool Equals(MechEquipmentSlot other) => string.Equals(Id, other.Id, StringComparison.Ordinal);
        public override bool Equals(object other) => other is MechEquipmentSlot slot && Equals(slot);
        public override int GetHashCode() => Id == null ? 0 : StringComparer.Ordinal.GetHashCode(Id);
        public static bool operator ==(MechEquipmentSlot a, MechEquipmentSlot b) => a.Equals(b);
        public static bool operator !=(MechEquipmentSlot a, MechEquipmentSlot b) => !a.Equals(b);
        public static bool operator ==(MechEquipmentSlot a, MechPartSlot b) => a.LegacyRole == b;
        public static bool operator !=(MechEquipmentSlot a, MechPartSlot b) => a.LegacyRole != b;
        public override string ToString() => Id ?? "none";
    }
}
