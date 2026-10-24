using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace ACunicombat.Mechs
{
    public enum MechSpecialistKitKind { None, EarlyWarning, Support }

    public enum MechVisualRigMode
    {
        MountedPrefab = 0,
        SynchronizedSkinned = 10
    }

    [Serializable]
    public sealed class MechEquipmentAnimationDefinition
    {
        [SerializeField] private string actionId;
        [SerializeField] private AnimationClip clip;
        [SerializeField] private bool loop;

        public MechEquipmentAnimationDefinition()
        {
        }

        public MechEquipmentAnimationDefinition(string newActionId, AnimationClip newClip, bool shouldLoop = false)
        {
            actionId = newActionId;
            clip = newClip;
            loop = shouldLoop;
        }

        public string ActionId => string.IsNullOrWhiteSpace(actionId) && clip != null ? clip.name : actionId;
        public AnimationClip Clip => clip;
        public bool Loop => loop;
    }

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
        [Tooltip("Optional assembled model used by the standalone hangar preview. Falls back to Model Prefab.")]
        [SerializeField] private GameObject previewModelPrefab;
        [Tooltip("Optional static catalog image for internal parts that have no external model.")]
        [SerializeField] private Texture2D previewImage;
        [SerializeField] private MechVisualRigMode visualRigMode = MechVisualRigMode.MountedPrefab;
        [SerializeField] private AnimationClip skinnedAnimationClip;
        [SerializeField] private MechPartSlot ancestorDriverSlot = MechPartSlot.None;
        [FormerlySerializedAs("coreDrivenBoneNames")]
        [SerializeField] private string[] ancestorDrivenBoneNames = Array.Empty<string>();
        [SerializeField] private bool instantiateVisualAtAllMounts;
        [SerializeField] private Material[] materialOverrides = Array.Empty<Material>();
        [SerializeField] private Vector3 localPosition;
        [SerializeField] private Vector3 localEulerAngles;
        [SerializeField] private Vector3 localScale = Vector3.one;

        [Header("Mounted Equipment Animation")]
        [SerializeField] private List<MechEquipmentAnimationDefinition> equipmentAnimations =
            new List<MechEquipmentAnimationDefinition>();
        [SerializeField] private string defaultEquipmentActionId;
        [SerializeField] private bool playDefaultEquipmentActionOnAssemble;

        [Header("Gameplay")]
        [SerializeField] private MechPartStats stats = new MechPartStats();
        [SerializeField] private MechInternalPartPerformance internalPerformance = new MechInternalPartPerformance();
        [SerializeField] private MechHeadSensorPerformance headSensorPerformance = new MechHeadSensorPerformance();
        [SerializeField] private MechArmPerformance armPerformance = new MechArmPerformance();
        [SerializeField] private MechCorePerformance corePerformance = new MechCorePerformance();
        [SerializeField] private MechLegPerformance legPerformance = new MechLegPerformance();
        [SerializeField] private MechBoosterPerformance boosterPerformance = new MechBoosterPerformance();
        [SerializeField] private MechGeneratorPerformance generatorPerformance = new MechGeneratorPerformance();
        [SerializeField] private MechRadiatorPerformance radiatorPerformance = new MechRadiatorPerformance();
        [SerializeField] private MechRadarPerformance radarPerformance = new MechRadarPerformance();
        [SerializeField] private MechFireControlPerformance fireControlPerformance = new MechFireControlPerformance();
        [SerializeField] private MechDataLinkPerformance dataLinkPerformance = new MechDataLinkPerformance();
        [SerializeField] private MechWeaponPerformance weaponPerformance = new MechWeaponPerformance();
        [SerializeField] private List<MechMountPointDefinition> mountPoints = new List<MechMountPointDefinition>();
        [SerializeField] private List<MechPartSlot> requiredInstalledSlots = new List<MechPartSlot>();
        [SerializeField] private string[] requiredLoadoutTags = Array.Empty<string>();
        [SerializeField] private string[] blockedLoadoutTags = Array.Empty<string>();

        [Header("Specialist conversion and core expansion")]
        [SerializeField, Min(0)] private int extensionSlotCount = 2;
        [SerializeField] private MechSpecialistKitKind specialistKit;
        [SerializeField, Range(0,3)] private int earlyWarningRadarVariant;
        [SerializeField] private bool radarDefinitionPending;
        public int ExtensionSlotCount => slot==MechPartSlot.Core ? Mathf.Max(0,extensionSlotCount) : 0;
        public MechSpecialistKitKind SpecialistKit => specialistKit;
        public int EarlyWarningRadarVariant => earlyWarningRadarVariant;
        public bool RadarDefinitionPending => radarDefinitionPending;

        [Header("Optional defense extension")]
        [SerializeField] private MechDefenseExtensionProfile defenseExtension = new MechDefenseExtensionProfile();
        public MechDefenseExtensionProfile DefenseExtension => defenseExtension ?? (defenseExtension=new MechDefenseExtensionProfile());
        [Header("Functional damage")]
        [SerializeField] private MechPartDamageProfile damageProfile = new MechPartDamageProfile();
        public MechPartDamageProfile DamageProfile => damageProfile ?? (damageProfile = new MechPartDamageProfile());

        [Header("Installation interfaces")]
        [SerializeField] private MechHardpointType mountClass;
        [SerializeField] private Vector3 installationSizeMeters;
        [SerializeField] private List<MechSlotDefinition> childSlots = new List<MechSlotDefinition>();
        public IReadOnlyList<MechSlotDefinition> ChildSlots => childSlots;
        public Vector3 InstallationSizeMeters => installationSizeMeters;
        public MechHardpointType MountClass => mountClass != MechHardpointType.None ? mountClass : MechSlotRules.RequiredHardpointType(slot);

        public string PartId => string.IsNullOrWhiteSpace(partId) ? name : partId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? PartId : displayName;
        public MechPartSlot Slot => slot;
        public MechPartCategory Category => category;
        public string Manufacturer => manufacturer;
        public IReadOnlyList<string> Tags => tags;
        public GameObject ModelPrefab => modelPrefab;
        public GameObject PreviewModelPrefab => previewModelPrefab != null ? previewModelPrefab : modelPrefab;
        public Texture2D PreviewImage => previewImage;
        public MechVisualRigMode VisualRigMode => visualRigMode;
        public bool UsesSynchronizedSkinnedRig => visualRigMode == MechVisualRigMode.SynchronizedSkinned;
        public AnimationClip SkinnedAnimationClip => skinnedAnimationClip;
        public MechPartSlot AncestorDriverSlot => ancestorDriverSlot;
        public IReadOnlyList<string> AncestorDrivenBoneNames => ancestorDrivenBoneNames;
        public bool InstantiateVisualAtAllMounts => instantiateVisualAtAllMounts;
        public IReadOnlyList<Material> MaterialOverrides => materialOverrides;
        public Vector3 LocalPosition => localPosition;
        public Vector3 LocalEulerAngles => localEulerAngles;
        public Vector3 LocalScale => localScale == Vector3.zero ? Vector3.one : localScale;
        public IReadOnlyList<MechEquipmentAnimationDefinition> EquipmentAnimations => equipmentAnimations;
        public string DefaultEquipmentActionId => defaultEquipmentActionId;
        public bool PlayDefaultEquipmentActionOnAssemble => playDefaultEquipmentActionOnAssemble;
        public MechPartStats Stats => stats;
        public MechInternalPartPerformance InternalPerformance => internalPerformance;
        public MechHeadSensorPerformance HeadSensorPerformance => headSensorPerformance;
        public MechArmPerformance ArmPerformance => armPerformance;
        public MechCorePerformance CorePerformance => corePerformance;
        public MechLegPerformance LegPerformance => legPerformance;
        public MechBoosterPerformance BoosterPerformance => boosterPerformance;
        public MechGeneratorPerformance GeneratorPerformance => generatorPerformance;
        public MechRadiatorPerformance RadiatorPerformance => radiatorPerformance;
        public MechRadarPerformance RadarPerformance => radarPerformance;
        public MechFireControlPerformance FireControlPerformance => fireControlPerformance;
        public MechDataLinkPerformance DataLinkPerformance => dataLinkPerformance;
        public MechWeaponPerformance WeaponPerformance => weaponPerformance;
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
            IEnumerable<Material> newMaterialOverrides = null,
            MechVisualRigMode newVisualRigMode = MechVisualRigMode.MountedPrefab,
            AnimationClip newSkinnedAnimationClip = null,
            MechPartSlot newAncestorDriverSlot = MechPartSlot.None,
            IEnumerable<string> newAncestorDrivenBoneNames = null,
            bool shouldInstantiateVisualAtAllMounts = false,
            IEnumerable<MechEquipmentAnimationDefinition> newEquipmentAnimations = null,
            string newDefaultEquipmentActionId = null,
            bool shouldPlayDefaultEquipmentActionOnAssemble = false,
            Vector3? newLocalPosition = null,
            Vector3? newLocalEulerAngles = null,
            Vector3? newLocalScale = null,
            GameObject newPreviewModelPrefab = null,
            Texture2D newPreviewImage = null,
            MechInternalPartPerformance newInternalPerformance = null,
            MechHeadSensorPerformance newHeadSensorPerformance = null,
            MechArmPerformance newArmPerformance = null,
            MechCorePerformance newCorePerformance = null,
            MechLegPerformance newLegPerformance = null,
            MechBoosterPerformance newBoosterPerformance = null,
            MechGeneratorPerformance newGeneratorPerformance = null,
            MechRadarPerformance newRadarPerformance = null,
            MechFireControlPerformance newFireControlPerformance = null,
            MechWeaponPerformance newWeaponPerformance = null,
            MechDataLinkPerformance newDataLinkPerformance = null,
            MechRadiatorPerformance newRadiatorPerformance = null)
        {
            partId = newPartId;
            displayName = newDisplayName;
            slot = newSlot;
            category = newCategory;
            modelPrefab = newModelPrefab;
            previewModelPrefab = newPreviewModelPrefab;
            previewImage = newPreviewImage;
            visualRigMode = newVisualRigMode;
            skinnedAnimationClip = newSkinnedAnimationClip;
            ancestorDriverSlot = newAncestorDriverSlot;
            ancestorDrivenBoneNames = newAncestorDrivenBoneNames == null
                ? Array.Empty<string>()
                : new List<string>(newAncestorDrivenBoneNames).ToArray();
            instantiateVisualAtAllMounts = shouldInstantiateVisualAtAllMounts;
            materialOverrides = newMaterialOverrides == null
                ? Array.Empty<Material>()
                : new List<Material>(newMaterialOverrides).ToArray();
            equipmentAnimations = newEquipmentAnimations == null
                ? new List<MechEquipmentAnimationDefinition>()
                : new List<MechEquipmentAnimationDefinition>(newEquipmentAnimations);
            defaultEquipmentActionId = newDefaultEquipmentActionId ?? string.Empty;
            playDefaultEquipmentActionOnAssemble = shouldPlayDefaultEquipmentActionOnAssemble;
            if (newLocalPosition.HasValue)
            {
                localPosition = newLocalPosition.Value;
            }
            if (newLocalEulerAngles.HasValue)
            {
                localEulerAngles = newLocalEulerAngles.Value;
            }
            if (newLocalScale.HasValue)
            {
                localScale = newLocalScale.Value;
            }
            if (stats == null)
            {
                stats = new MechPartStats();
            }
            stats.CopyFrom(newStats);
			if (newCategory == MechPartCategory.Frame &&
				(newSlot == MechPartSlot.Head || newSlot == MechPartSlot.Core ||
				 newSlot == MechPartSlot.Arms || newSlot == MechPartSlot.Legs))
			{
				stats.PopulateMassBreakdownFromDurability();
			}
            internalPerformance = newInternalPerformance?.Clone() ?? new MechInternalPartPerformance();
            headSensorPerformance = newHeadSensorPerformance?.Clone() ?? new MechHeadSensorPerformance();
            armPerformance = newArmPerformance?.Clone() ?? new MechArmPerformance();
            corePerformance = newCorePerformance?.Clone() ?? new MechCorePerformance();
            legPerformance = newLegPerformance?.Clone() ?? new MechLegPerformance();
            boosterPerformance = newBoosterPerformance?.Clone() ?? new MechBoosterPerformance();
            generatorPerformance = newGeneratorPerformance?.Clone() ?? new MechGeneratorPerformance();
            radiatorPerformance = newRadiatorPerformance?.Clone() ?? new MechRadiatorPerformance();
            radarPerformance = newRadarPerformance?.Clone() ?? new MechRadarPerformance();
            fireControlPerformance = newFireControlPerformance?.Clone() ?? new MechFireControlPerformance();
            dataLinkPerformance = newDataLinkPerformance?.Clone() ?? new MechDataLinkPerformance();
            weaponPerformance = newWeaponPerformance?.Clone() ?? new MechWeaponPerformance();
            mountPoints = newMountPoints == null
                ? new List<MechMountPointDefinition>()
                : new List<MechMountPointDefinition>(newMountPoints);
            tags = newTags == null ? Array.Empty<string>() : new List<string>(newTags).ToArray();
            if (localScale == Vector3.zero)
            {
                localScale = Vector3.one;
            }
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
