using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [CreateAssetMenu(menuName = "ACunicombat/Mech/Part Definition", fileName = "MechPartDefinition")]
    public sealed class MechPartDefinition : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] private string partId;
        [SerializeField] private string displayName;
        [SerializeField] private MechPartSlot slot = MechPartSlot.None;
        [SerializeField] private MechPartCategory category = MechPartCategory.Frame;
        [SerializeField] private string manufacturer;
        [SerializeField] private string[] tags = Array.Empty<string>();

        [Header("Visual")]
        [SerializeField] private GameObject modelPrefab;
        [SerializeField] private Material[] materialOverrides = Array.Empty<Material>();
        [SerializeField] private Vector3 localPosition;
        [SerializeField] private Vector3 localEulerAngles;
        [SerializeField] private Vector3 localScale = Vector3.one;

        [Header("Gameplay")]
        [SerializeField] private MechPartStats stats = new MechPartStats();
        [SerializeField] private List<MechMountPointDefinition> mountPoints = new List<MechMountPointDefinition>();
        [SerializeField] private List<MechPartSlot> requiredInstalledSlots = new List<MechPartSlot>();
        [SerializeField] private string[] requiredLoadoutTags = Array.Empty<string>();
        [SerializeField] private string[] blockedLoadoutTags = Array.Empty<string>();

        public string PartId => string.IsNullOrWhiteSpace(partId) ? name : partId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? PartId : displayName;
        public MechPartSlot Slot => slot;
        public MechPartCategory Category => category;
        public string Manufacturer => manufacturer;
        public IReadOnlyList<string> Tags => tags;
        public GameObject ModelPrefab => modelPrefab;
        public IReadOnlyList<Material> MaterialOverrides => materialOverrides;
        public Vector3 LocalPosition => localPosition;
        public Vector3 LocalEulerAngles => localEulerAngles;
        public Vector3 LocalScale => localScale == Vector3.zero ? Vector3.one : localScale;
        public MechPartStats Stats => stats;
        public IReadOnlyList<MechMountPointDefinition> MountPoints => mountPoints;
        public IReadOnlyList<MechPartSlot> RequiredInstalledSlots => requiredInstalledSlots;
        public IReadOnlyList<string> RequiredLoadoutTags => requiredLoadoutTags;
        public IReadOnlyList<string> BlockedLoadoutTags => blockedLoadoutTags;

        public bool HasTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || tags == null)
            {
                return false;
            }

            for (var i = 0; i < tags.Length; i++)
            {
                if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(partId))
            {
                partId = name;
            }

            if (localScale == Vector3.zero)
            {
                localScale = Vector3.one;
            }
        }

#if UNITY_EDITOR
        public void EditorConfigure(
            string newPartId,
            string newDisplayName,
            MechPartSlot newSlot,
            MechPartCategory newCategory,
            GameObject newModelPrefab,
            MechPartStats newStats,
            IEnumerable<MechMountPointDefinition> newMountPoints,
            IEnumerable<string> newTags = null,
            IEnumerable<Material> newMaterialOverrides = null)
        {
            partId = newPartId;
            displayName = newDisplayName;
            slot = newSlot;
            category = newCategory;
            modelPrefab = newModelPrefab;
            materialOverrides = newMaterialOverrides == null
                ? Array.Empty<Material>()
                : new List<Material>(newMaterialOverrides).ToArray();
            if (stats == null)
            {
                stats = new MechPartStats();
            }
            stats.CopyFrom(newStats);
            mountPoints = newMountPoints == null
                ? new List<MechMountPointDefinition>()
                : new List<MechMountPointDefinition>(newMountPoints);
            tags = newTags == null ? Array.Empty<string>() : new List<string>(newTags).ToArray();
            localScale = Vector3.one;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
