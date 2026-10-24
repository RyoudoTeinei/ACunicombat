using System;
using System.Collections.Generic;

namespace ACunicombat.Mechs
{
    public readonly struct MechEquipmentCapability
    {
        public readonly string SlotId, PartId;
        public readonly Guid InstanceId;
        public readonly bool Available;
        public readonly bool ExecutionStateKnown, LocallyReady, ActionPending, Reloading;
        public readonly int RoundsRemaining, MagazinesRemaining;
        public readonly float HeatMj;
        public readonly MechPartCategory Category;
        public readonly MechDefenseExtensionKind DefenseKind;
        public readonly int TrackCapacity, GuidanceChannels;
        public readonly float WeaponRangeMeters, RadarRangeKm, YawLimitDegrees, PitchLimitDegrees;
        public MechEquipmentCapability(MechAssemblyRecord record, MechSlotDefinition slot, bool parentAvailable, MechEquipmentWeaponSnapshot? weapon = null, MechDefenseExtensionSnapshot? defense = null)
        {
            SlotId = record.SlotId; PartId = record.part.PartId; InstanceId = record.InstanceId;
            Available = parentAvailable && record.instance != null && record.instance.activeInHierarchy;
            DefenseKind=record.part.DefenseExtension.kind;
            ExecutionStateKnown = weapon.HasValue || defense.HasValue;
            RoundsRemaining = defense?.Ammunition ?? weapon?.Rounds ?? 0; MagazinesRemaining = weapon?.Magazines ?? 0;
            Reloading = weapon?.Reloading ?? false; ActionPending = defense?.Pending ?? weapon?.Pending ?? false; HeatMj = weapon?.HeatMj ?? 0;
            LocallyReady = Available && weapon.HasValue && weapon.Value.Enabled && RoundsRemaining > 0 && !Reloading && !ActionPending &&
                weapon.Value.NextFireTime <= UnityEngine.Time.time &&
                (record.part.WeaponPerformance.heatCapacityMj <= 0 || HeatMj < record.part.WeaponPerformance.heatCapacityMj);
            if(defense.HasValue)
                LocallyReady=Available&&defense.Value.Enabled&&DefenseKind==MechDefenseExtensionKind.ActiveProtection&&RoundsRemaining>0&&!ActionPending&&
                    defense.Value.NextShot<=UnityEngine.Time.timeAsDouble&&defense.Value.ActiveShots<Math.Max(1,record.part.DefenseExtension.maximumConcurrentShots);
            Category = record.part.Category;
            bool isRadar = record.part.Slot == MechPartSlot.Radar;
            TrackCapacity = isRadar && record.part.RadarPerformance.HasData ? record.part.RadarPerformance.trackCapacity : record.part.Stats.trackCapacity;
            GuidanceChannels = record.part.Stats.guidanceChannels;
            WeaponRangeMeters = record.part.WeaponPerformance.maximumRangeMeters;
            RadarRangeKm = isRadar ? record.part.RadarPerformance.EstimatedSearchRangeKm : 0f;
            YawLimitDegrees = slot?.yawLimitDegrees ?? 180;
            PitchLimitDegrees = slot?.pitchLimitDegrees ?? 90;
        }
        public static void CopyFrom(MechRuntimeState state, List<MechEquipmentCapability> output)
        {
            output.Clear();
            var slots = state.CurrentLoadout?.GetSlots();
            var weapons = new List<MechEquipmentWeaponSnapshot>();
            state.GetComponent<MechTestWeaponSystem>()?.CopyEquipmentWeaponSnapshots(weapons);
            var defenses=new List<MechDefenseExtensionSnapshot>();state.GetComponent<MechDefenseExtensions>()?.CopySnapshots(defenses);
            var available = new Dictionary<string, bool>();
            foreach (var record in state.AssembledParts)
            {
                if (record.part == null) continue;
                var slot = slots?.Find(x => x.id == record.SlotId);
                bool parent = string.IsNullOrEmpty(slot?.parentSlotId) ||
                    (available.TryGetValue(slot.parentSlotId, out var live) && live);
                MechEquipmentWeaponSnapshot? weapon = null;
                foreach (var candidate in weapons) if (candidate.InstanceId == record.InstanceId) { weapon = candidate; break; }
                MechDefenseExtensionSnapshot? defense=null;
                foreach(var candidate in defenses)if(candidate.InstanceId==record.InstanceId){defense=candidate;break;}
                var value = new MechEquipmentCapability(record, slot, parent, weapon,defense);
                available[record.SlotId] = value.Available; output.Add(value);
            }
        }
    }
}
