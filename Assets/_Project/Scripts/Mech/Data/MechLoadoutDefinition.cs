using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechPartAssignment
    {
        public MechPartSlot slot = MechPartSlot.None;
        public MechPartDefinition part;
        public bool enabled = true;
    }

    [CreateAssetMenu(menuName = "ACunicombat/Mech/Loadout Definition", fileName = "MechLoadoutDefinition")]
    public sealed class MechLoadoutDefinition : ScriptableObject
    {
        [SerializeField] private string loadoutId;
        [SerializeField] private string displayName;
        [SerializeField] private List<MechPartAssignment> assignments = new List<MechPartAssignment>();

        public string LoadoutId => string.IsNullOrWhiteSpace(loadoutId) ? name : loadoutId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? LoadoutId : displayName;
        public IReadOnlyList<MechPartAssignment> Assignments => assignments;

        public bool TryGetPart(MechPartSlot slot, out MechPartDefinition part)
        {
            part = null;
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                if (assignment == null || !assignment.enabled || assignment.slot != slot)
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
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                if (assignment != null && assignment.enabled)
                {
                    yield return assignment;
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

            sorted.Sort((a, b) => MechSlotRules.GetAssemblySortIndex(a.slot).CompareTo(MechSlotRules.GetAssemblySortIndex(b.slot)));
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

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(loadoutId))
            {
                loadoutId = name;
            }
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
