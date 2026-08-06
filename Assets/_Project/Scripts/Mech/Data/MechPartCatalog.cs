using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechPartTestStatus
    {
        Untested = 0,
        NeedsReview = 5,
        Tested = 10
    }

    [Serializable]
    public sealed class MechPartCatalogEntry
    {
        [SerializeField] private string partId;
        [SerializeField] private string displayName;
        [SerializeField] private MechPartSlot slot;
        [SerializeField] private MechPartCategory category;
        [SerializeField] private string resourcePath;
        [SerializeField] private MechPartTestStatus testStatus;
        [SerializeField] private string statusNote;

        public string PartId => partId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? partId : displayName;
        public MechPartSlot Slot => slot;
        public MechPartCategory Category => category;
        public string ResourcePath => resourcePath;
        public MechPartTestStatus TestStatus => testStatus;
        public string StatusNote => statusNote;
        public bool IsTested => testStatus == MechPartTestStatus.Tested;
        public bool NeedsReview => testStatus == MechPartTestStatus.NeedsReview;
        public bool IsWeapon => MechSlotRules.IsWeaponSlot(slot);

        public MechPartDefinition LoadPart()
        {
            return string.IsNullOrWhiteSpace(resourcePath)
                ? null
                : Resources.Load<MechPartDefinition>(resourcePath);
        }

#if UNITY_EDITOR
        public MechPartCatalogEntry(
            string newPartId,
            string newDisplayName,
            MechPartSlot newSlot,
            MechPartCategory newCategory,
            string newResourcePath,
            MechPartTestStatus newTestStatus,
            string newStatusNote)
        {
            partId = newPartId;
            displayName = newDisplayName;
            slot = newSlot;
            category = newCategory;
            resourcePath = newResourcePath;
            testStatus = newTestStatus;
            statusNote = newStatusNote;
        }
#endif
    }

    [CreateAssetMenu(menuName = "ACunicombat/Mech/Part Catalog", fileName = "MechPartCatalog")]
    public sealed class MechPartCatalog : ScriptableObject
    {
        [SerializeField] private List<MechPartCatalogEntry> entries = new List<MechPartCatalogEntry>();

        public IReadOnlyList<MechPartCatalogEntry> Entries => entries;

        public MechPartCatalogEntry Find(string partId)
        {
            if (string.IsNullOrWhiteSpace(partId))
            {
                return null;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && string.Equals(entry.PartId, partId, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        public List<MechPartCatalogEntry> GetEntries(MechPartSlot slot)
        {
            var result = new List<MechPartCatalogEntry>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && entry.Slot == slot)
                {
                    result.Add(entry);
                }
            }

            result.Sort((a, b) =>
            {
                if (MechSlotRules.IsWeaponSlot(slot))
                {
                    var testedComparison = b.IsTested.CompareTo(a.IsTested);
                    if (testedComparison != 0)
                    {
                        return testedComparison;
                    }
                }

                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

#if UNITY_EDITOR
        public void EditorConfigure(IEnumerable<MechPartCatalogEntry> newEntries)
        {
            entries = newEntries == null
                ? new List<MechPartCatalogEntry>()
                : new List<MechPartCatalogEntry>(newEntries);
            entries.Sort((a, b) =>
            {
                var slotComparison = a.Slot.CompareTo(b.Slot);
                return slotComparison != 0
                    ? slotComparison
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
