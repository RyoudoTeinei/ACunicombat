using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechPartAssignment
    {
        public string slotId;
        public string SlotId => string.IsNullOrWhiteSpace(slotId) ? MechSlotGraph.LegacyId(slot) : slotId;
        public MechPartSlot slot = MechPartSlot.None;
        public MechPartDefinition part;
        public string missileVariantId;
        public bool enabled = true;
    }

    [CreateAssetMenu(menuName = "ACunicombat/Mech/Loadout Definition", fileName = "MechLoadoutDefinition")]
    public sealed class MechLoadoutDefinition : ScriptableObject
    {
        [SerializeField] private MechPlatformDefinition platform;
        [SerializeField] private int schemaVersion;
        [SerializeField] private ACunicombat.Sensing.Contracts.ObservedWeightClass tacticalWeight;
        [SerializeField] private ACunicombat.Sensing.Contracts.ObservedCombatRole tacticalRole;
        public ACunicombat.Sensing.Contracts.ObservedWeightClass TacticalWeight=>tacticalWeight;
        public ACunicombat.Sensing.Contracts.ObservedCombatRole TacticalRole=>tacticalRole;
        public MechPlatformDefinition Platform => platform;
        public int SchemaVersion => schemaVersion;
        public List<MechSlotDefinition> GetSlots() => MechSlotGraph.Resolve(this);

        [SerializeField] private string loadoutId;
        [SerializeField] private string displayName;
        [SerializeField] private List<MechPartAssignment> assignments = new List<MechPartAssignment>();

        public string LoadoutId => string.IsNullOrWhiteSpace(loadoutId) ? name : loadoutId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? LoadoutId : displayName;
        public IReadOnlyList<MechPartAssignment> Assignments => assignments;

        public bool TryGetPart(string slotId, out MechPartDefinition part)
        {
            foreach (var a in assignments)
                if (a != null && a.enabled && a.SlotId == slotId) { part = a.part; return part != null; }
            part = null; return false;
        }

        public bool TryGetPart(MechPartSlot slot, out MechPartDefinition part)
        {
            if (platform != null)
            {
                var matching = GetSlots().Find(x => x.legacyRole == slot);
                if (matching != null) return TryGetPart(matching.id, out part);
                part = null; return false;
            }
            part = null;
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                var role = assignment?.slot ?? MechPartSlot.None;
                if (assignment == null || !assignment.enabled || role != slot)
                {
                    continue;
                }

                part = assignment.part;
                return part != null;
            }

            return false;
        }

        public IEnumerable<MechPartAssignment> EnumerateEnabledAssignments()
        {
            var resolvedSlots = platform != null ? GetSlots() : null;
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                if (assignment != null && assignment.enabled)
                {
                    yield return new MechPartAssignment { slotId = assignment.SlotId, slot = resolvedSlots == null ? assignment.slot : (resolvedSlots.Find(x => x.id == assignment.SlotId)?.legacyRole ?? MechPartSlot.None), part = assignment.part, missileVariantId = assignment.missileVariantId, enabled = true };
                }
            }
        }

        public List<MechPartAssignment> CreateSortedAssignmentList()
        {
            var sorted = new List<MechPartAssignment>();
            foreach (var assignment in EnumerateEnabledAssignments())
            {
                sorted.Add(assignment);
            }

            if (platform == null)
                sorted.Sort((a, b) => MechSlotRules.GetAssemblySortIndex(a.slot).CompareTo(MechSlotRules.GetAssemblySortIndex(b.slot)));
            else
            {
                var slots = GetSlots();
                sorted.Sort((a, b) => slots.FindIndex(x => x.id == a.SlotId).CompareTo(slots.FindIndex(x => x.id == b.SlotId)));
            }
            return sorted;
        }

        public HashSet<string> BuildLoadoutTagSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in EnumerateEnabledAssignments())
            {
                if (assignment.part == null)
                {
                    continue;
                }

                var tags = assignment.part.Tags;
                for (var i = 0; i < tags.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(tags[i]))
                    {
                        set.Add(tags[i]);
                    }
                }
            }

            return set;
        }

        public void ConfigureLayout(MechPlatformDefinition layout)
        { platform = layout; schemaVersion = 1; }

        public bool TrySetPart(string slotId, MechPartDefinition part, out string reason)
        {
            var slot = GetSlots().Find(x => x.id == slotId);
            if (slot == null) { reason = "Unknown slot: " + slotId; return false; }
            if (platform != null && !slot.Accepts(part)) { reason = "Part is incompatible with " + slot.Label; return false; }
            if(!MechSpecialistKits.CandidateAllowed(this,slot,part)){reason="当前专用套件不允许此装备或槽位组合。";return false;}
            TryGetPart(slotId, out var previous);
            if (previous != part)
                foreach (var a in EnumerateEnabledAssignments())
                    if (a.part != null && (a.SlotId.StartsWith(slotId + "/", StringComparison.Ordinal) ||
                        GetSlots().Find(x => x.id == a.SlotId)?.parentSlotId == slotId))
                    { reason = "Remove dependent equipment first: " + a.SlotId; return false; }
            assignments.RemoveAll(a => a != null && a.SlotId == slotId);
            if (part != null) assignments.Add(new MechPartAssignment { slotId = slotId, slot = slot.legacyRole, part = part });
            MechSpecialistKits.NormalizeAssignments(assignments,slotId);
            if(MechSpecialistKits.Kind(this)==MechSpecialistKitKind.EarlyWarning&&!assignments.Exists(a=>a.part!=null&&a.part.Slot==MechPartSlot.Radar))
            {
                var radarSlot=GetSlots().Find(s=>s.legacyRole==MechPartSlot.Radar);var prototype=Resources.Load<MechPartDefinition>("SpecialistKits/EarlyWarningRadar01");
                if(radarSlot!=null&&prototype!=null)assignments.Add(new MechPartAssignment{slotId=radarSlot.id,slot=MechPartSlot.Radar,part=prototype});
            }
            reason = null; return true;
        }

        private void OnValidate()
        {
            if (platform != null && schemaVersion == 0) schemaVersion = 1;
            if (string.IsNullOrWhiteSpace(loadoutId))
            {
                loadoutId = name;
            }
        }

        public void ConfigureRuntime(
            string newLoadoutId,
            string newDisplayName,
            IEnumerable<MechPartAssignment> newAssignments)
        {
            loadoutId = newLoadoutId;
            displayName = newDisplayName;
            assignments = newAssignments == null
                ? new List<MechPartAssignment>()
                : new List<MechPartAssignment>(newAssignments);
        }

#if UNITY_EDITOR
        public void EditorConfigure(string newLoadoutId, string newDisplayName, IEnumerable<MechPartAssignment> newAssignments)
        {
            loadoutId = newLoadoutId;
            displayName = newDisplayName;
            assignments = newAssignments == null
                ? new List<MechPartAssignment>()
                : new List<MechPartAssignment>(newAssignments);
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
