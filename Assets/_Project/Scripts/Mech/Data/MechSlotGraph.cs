using System;
using System.Collections.Generic;

namespace ACunicombat.Mechs
{
    public static class MechSlotGraph
    {
        public const int MaxDepth = 4;
        public const int MaxSlots = 256;
        private static readonly Dictionary<MechPartSlot,string> legacyIds=BuildLegacyIds();
        private static Dictionary<MechPartSlot,string> BuildLegacyIds()
        {var result=new Dictionary<MechPartSlot,string>();foreach(MechPartSlot role in Enum.GetValues(typeof(MechPartSlot)))result[role]="legacy."+(int)role;return result;}
        public static string LegacyId(MechPartSlot role) => legacyIds.TryGetValue(role,out var id)?id:"legacy."+(int)role;
        private static void Error(MechValidationResult result, string code, string message)
            => result?.Add(MechValidationSeverity.Error, MechPartSlot.None, code, message);

        public static List<MechSlotDefinition> Resolve(MechLoadoutDefinition loadout, MechValidationResult result = null)
        {
            var slots = new List<MechSlotDefinition>();
            var byId = new Dictionary<string, MechSlotDefinition>(StringComparer.Ordinal);
            var assigned = new Dictionary<string, MechPartAssignment>(StringComparer.Ordinal);
            foreach (var a in loadout.Assignments)
            {
                if (a == null || !a.enabled) continue;
                if (string.IsNullOrWhiteSpace(a.SlotId) || assigned.ContainsKey(a.SlotId))
                    Error(result, "DUPLICATE_SLOT", "Duplicate or empty assignment: " + a.SlotId);
                else assigned.Add(a.SlotId, a);
            }
            if (loadout.Platform == null)
            {
                foreach (var role in MechSlotRules.AssemblyOrder)
                {
                    if(role==MechPartSlot.Extension&&MechSpecialistKits.CoreCapacity(loadout)<1||role==MechPartSlot.Extension2&&MechSpecialistKits.CoreCapacity(loadout)<2)continue;
                    slots.Add(new MechSlotDefinition { id = LegacyId(role), legacyRole = role, displayName = MechSlotRules.DisplayName(role) });
                }
                return slots;
            }
            if (string.IsNullOrWhiteSpace(loadout.Platform.platformId)) Error(result, "PLATFORM_ID", "Platform requires a stable ID.");
            if (loadout.Platform.version != MechPlatformDefinition.CurrentVersion)
                Error(result, "LAYOUT_VERSION", "Unsupported platform layout version.");
            Action<MechSlotDefinition> add = slot =>
            {
                if (slot == null || string.IsNullOrWhiteSpace(slot.id) || byId.ContainsKey(slot.id))
                { Error(result, "LAYOUT_SLOT_ID", "Missing or duplicate slot ID: " + slot?.id); return; }
                if (slots.Count >= MaxSlots) { Error(result, "LAYOUT_CAPACITY", "Slot count exceeds " + MaxSlots); return; }
                var copy = slot.Copy(); slots.Add(copy); byId.Add(copy.id, copy);
            };
            if (loadout.Platform.slots == null) { Error(result, "LAYOUT_EMPTY", "Layout slot list is missing."); return slots; }
            foreach (var slot in loadout.Platform.slots)
            {
                if (slot != null && slot.id != null && slot.id.Contains("/")) Error(result, "ROOT_SLOT_ID", "Root slot IDs cannot contain '/'.");
                add(slot);
            }
            var coreSlot=slots.Find(s=>s.legacyRole==MechPartSlot.Core);
            if(coreSlot!=null&&MechSpecialistKits.CoreCapacity(loadout)>=2&&!slots.Exists(s=>s.legacyRole==MechPartSlot.Extension2))
                add(new MechSlotDefinition{id=coreSlot.id+"/extension-2",parentSlotId=coreSlot.id,displayName="扩展槽 2",legacyRole=MechPartSlot.Extension2,category=MechPartCategory.Utility,hardpointType=MechHardpointType.Utility});
            // Child identity depends on installation path, never the list order.
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (!assigned.TryGetValue(slot.id, out var a) || a.part == null) continue;
                if (slot.id.Split('/').Length >= MaxDepth && a.part.ChildSlots.Count > 0)
                { Error(result, "LAYOUT_DEPTH", "Mount hierarchy is too deep: " + slot.id); continue; }
                foreach (var child in a.part.ChildSlots)
                {
                    if (child == null || string.IsNullOrWhiteSpace(child.id) || child.id.Contains("/"))
                    { Error(result, "CHILD_SLOT_ID", "Child slots require a nonempty local ID without '/'."); continue; }
                    add(child.Copy(slot.id + "/" + child.id, slot.id));
                }
            }
            foreach (var a in assigned.Values)
                if (!byId.ContainsKey(a.SlotId)) Error(result, "UNKNOWN_SLOT", "Assignment has no slot or installed parent: " + a.SlotId);
            var sorted = new List<MechSlotDefinition>();
            var state = new Dictionary<string, int>();
            Action<MechSlotDefinition, int> visit = null;
            visit = (slot, depth) =>
            {
                if (state.TryGetValue(slot.id, out var mark))
                { if (mark == 1) Error(result, "LAYOUT_CYCLE", "Cyclic parent dependency: " + slot.id); return; }
                state[slot.id] = 1;
                if (depth > MaxDepth) Error(result, "LAYOUT_DEPTH", "Mount hierarchy is too deep: " + slot.id);
                if (!string.IsNullOrEmpty(slot.parentSlotId))
                {
                    if (byId.TryGetValue(slot.parentSlotId, out var parent)) visit(parent, depth + 1);
                    else Error(result, "MISSING_PARENT_SLOT", "Unknown parent: " + slot.parentSlotId);
                    if (!assigned.TryGetValue(slot.parentSlotId, out var owner) || owner.part == null)
                        Error(result, "MISSING_PARENT_PART", "Parent equipment is absent: " + slot.parentSlotId);
                }
                state[slot.id] = 2; sorted.Add(slot);
            };
            // The legacy mount adapter still creates mounts while assembling its owning frame.
            // Respect its original ordering even when layout rows are rearranged.
            var assemblyOrder = new List<MechSlotDefinition>(slots);
            assemblyOrder.Sort((a, b) =>
            {
                int compare = MechSlotRules.GetAssemblySortIndex(a.legacyRole).CompareTo(MechSlotRules.GetAssemblySortIndex(b.legacyRole));
                return compare != 0 ? compare : slots.IndexOf(a).CompareTo(slots.IndexOf(b));
            });
            foreach (var slot in assemblyOrder) visit(slot, 1);
            return sorted;
        }
    }
}
