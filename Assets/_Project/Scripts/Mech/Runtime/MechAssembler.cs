using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechSlotRootOverride
    {
        public MechPartSlot slot = MechPartSlot.None;
        public Transform root;
    }

    [DisallowMultipleComponent]
    public sealed class MechAssembler : MonoBehaviour
    {
        [Header("Loadout")]
        [SerializeField] private MechLoadoutDefinition loadout;
        [SerializeField] private MechValidationOptions validationOptions = new MechValidationOptions();

        [Header("Assembly")]
        [SerializeField] private bool assembleOnAwake;
        [SerializeField] private bool clearExistingAssembly = true;
        [SerializeField] private bool createPlaceholdersForMissingModels = true;
        [SerializeField] private string generatedRootName = "_AssembledMech";
        [SerializeField] private Transform explicitAssemblyRoot;
        [SerializeField] private List<MechSlotRootOverride> slotRootOverrides = new List<MechSlotRootOverride>();

        [Header("Physics")]
        [SerializeField] private bool applyMassToRigidbody = true;
        [SerializeField] private Rigidbody targetRigidbody;

        [Header("Diagnostics")]
        [SerializeField] private bool logValidationIssues = true;

        public MechLoadoutDefinition Loadout
        {
            get => loadout;
            set => loadout = value;
        }

        public MechRuntimeState RuntimeState { get; private set; }

        private void Awake()
        {
            if (assembleOnAwake)
            {
                Assemble();
            }
        }

        [ContextMenu("Assemble Mech")]
        public void Assemble()
        {
            RuntimeState = GetComponent<MechRuntimeState>();
            if (RuntimeState == null)
            {
                RuntimeState = gameObject.AddComponent<MechRuntimeState>();
            }

            var validation = MechLoadoutValidator.Validate(loadout, validationOptions);
            var stats = MechStatsCalculator.Calculate(loadout);
            var records = new List<MechAssemblyRecord>();
            var mountPoints = new List<MechMountPoint>();
            var mountRegistry = new Dictionary<MechPartSlot, List<Transform>>();
            var assemblyRoot = PrepareAssemblyRoot();

            RegisterSlotOverrides(mountRegistry);

            if (loadout != null)
            {
                var sortedAssignments = loadout.CreateSortedAssignmentList();
                for (var i = 0; i < sortedAssignments.Count; i++)
                {
                    var assignment = sortedAssignments[i];
                    if (assignment.part == null)
                    {
                        continue;
                    }

                    var parentMount = ResolveParentMount(assignment.slot, mountRegistry, assemblyRoot);
                    var instance = CreatePartInstance(assignment.slot, assignment.part, parentMount);
                    var assembledPart = instance.GetComponent<MechAssembledPart>();
                    if (assembledPart == null)
                    {
                        assembledPart = instance.AddComponent<MechAssembledPart>();
                    }

                    assembledPart.Configure(assignment.slot, assignment.part);
                    ApplyMaterialOverrides(instance, assignment.part);
                    CreateMountPoints(instance.transform, assignment.part, mountRegistry, mountPoints);

                    records.Add(new MechAssemblyRecord
                    {
                        slot = assignment.slot,
                        part = assignment.part,
                        instance = instance,
                        parentMount = parentMount
                    });
                }
            }

            ApplyPhysics(stats);
            RuntimeState.Apply(loadout, stats, validation, records, mountPoints);
            LogValidation(validation);
        }

        public MechValidationResult ValidateCurrentLoadout()
        {
            return MechLoadoutValidator.Validate(loadout, validationOptions);
        }

        public MechAggregatedStats CalculateCurrentStats()
        {
            return MechStatsCalculator.Calculate(loadout);
        }

        private Transform PrepareAssemblyRoot()
        {
            if (explicitAssemblyRoot != null)
            {
                if (clearExistingAssembly)
                {
                    DestroyChildren(explicitAssemblyRoot);
                }

                return explicitAssemblyRoot;
            }

            var existing = transform.Find(generatedRootName);
            if (existing != null && clearExistingAssembly)
            {
                DestroyUnityObject(existing.gameObject);
                existing = null;
            }

            if (existing != null)
            {
                return existing;
            }

            var root = new GameObject(generatedRootName);
            root.transform.SetParent(transform, false);
            return root.transform;
        }

        private void RegisterSlotOverrides(Dictionary<MechPartSlot, List<Transform>> mountRegistry)
        {
            for (var i = 0; i < slotRootOverrides.Count; i++)
            {
                var slotRoot = slotRootOverrides[i];
                if (slotRoot == null || slotRoot.slot == MechPartSlot.None || slotRoot.root == null)
                {
                    continue;
                }

                RegisterMount(mountRegistry, slotRoot.slot, slotRoot.root);
            }
        }

        private Transform ResolveParentMount(
            MechPartSlot slot,
            Dictionary<MechPartSlot, List<Transform>> mountRegistry,
            Transform assemblyRoot)
        {
            if (mountRegistry.TryGetValue(slot, out var mounts) && mounts.Count > 0 && mounts[0] != null)
            {
                return mounts[0];
            }

            if (MechSlotRules.IsBaseAssemblySlot(slot))
            {
                return assemblyRoot;
            }

            var fallback = new GameObject(slot + "_FallbackMount");
            fallback.transform.SetParent(assemblyRoot, false);
            fallback.transform.localPosition = MechSlotRules.DefaultLocalPosition(slot);
            fallback.transform.localRotation = Quaternion.identity;
            fallback.transform.localScale = Vector3.one;
            RegisterMount(mountRegistry, slot, fallback.transform);
            return fallback.transform;
        }

        private GameObject CreatePartInstance(MechPartSlot assignedSlot, MechPartDefinition part, Transform parent)
        {
            GameObject instance = null;

            if (part.ModelPrefab != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    instance = PrefabUtility.InstantiatePrefab(part.ModelPrefab, parent) as GameObject;
                }
#endif
                if (instance == null)
                {
                    instance = Instantiate(part.ModelPrefab, parent);
                }
            }

            if (instance == null)
            {
                instance = createPlaceholdersForMissingModels
                    ? CreatePlaceholder(assignedSlot, parent)
                    : new GameObject(part.DisplayName);
                instance.transform.SetParent(parent, false);
            }

            instance.name = assignedSlot + "_" + part.DisplayName;
            instance.transform.localPosition = part.LocalPosition;
            instance.transform.localRotation = Quaternion.Euler(part.LocalEulerAngles);
            instance.transform.localScale = part.LocalScale;
            return instance;
        }

        private GameObject CreatePlaceholder(MechPartSlot assignedSlot, Transform parent)
        {
            var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            placeholder.name = assignedSlot + "_Placeholder";
            placeholder.transform.SetParent(parent, false);
            placeholder.transform.localPosition = Vector3.zero;
            placeholder.transform.localRotation = Quaternion.identity;
            placeholder.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);

            var collider = placeholder.GetComponent<Collider>();
            if (collider != null)
            {
                DestroyUnityObject(collider);
            }

            return placeholder;
        }

        private void ApplyMaterialOverrides(GameObject instance, MechPartDefinition part)
        {
            var overrides = part.MaterialOverrides;
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            var overrideMap = BuildMaterialOverrideMap(overrides);
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var materials = renderers[i].sharedMaterials;
                for (var m = 0; m < materials.Length; m++)
                {
                    var replacement = FindNamedMaterialOverride(materials[m], overrideMap);
                    if (replacement == null)
                    {
                        replacement = FindIndexedMaterialOverride(materials[m], overrides, m);
                    }

                    if (replacement != null)
                    {
                        materials[m] = replacement;
                    }
                }

                renderers[i].sharedMaterials = materials;
            }
        }

        private static Dictionary<string, Material> BuildMaterialOverrideMap(IReadOnlyList<Material> overrides)
        {
            var map = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < overrides.Count; i++)
            {
                var material = overrides[i];
                if (material == null)
                {
                    continue;
                }

                var key = CanonicalMaterialName(material.name);
                if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
                {
                    map.Add(key, material);
                }
            }

            return map;
        }

        private static Material FindNamedMaterialOverride(
            Material originalMaterial,
            IReadOnlyDictionary<string, Material> overrideMap)
        {
            if (originalMaterial == null || overrideMap == null || overrideMap.Count == 0)
            {
                return null;
            }

            return overrideMap.TryGetValue(CanonicalMaterialName(originalMaterial.name), out var replacement)
                ? replacement
                : null;
        }

        private static Material FindIndexedMaterialOverride(
            Material originalMaterial,
            IReadOnlyList<Material> overrides,
            int materialIndex)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return null;
            }

            if (overrides.Count == 1)
            {
                return overrides[0];
            }

            if (LooksLikeAc6ImportedMaterial(originalMaterial))
            {
                return null;
            }

            var overrideIndex = Mathf.Min(materialIndex, overrides.Count - 1);
            return overrides[overrideIndex];
        }

        private static bool LooksLikeAc6ImportedMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }

            var name = material.name;
            return !string.IsNullOrWhiteSpace(name) &&
                (name.IndexOf(".matxml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("ACparts[", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string CanonicalMaterialName(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
            {
                return string.Empty;
            }

            return materialName
                .Replace(" (Instance)", string.Empty)
                .Trim();
        }

        private void CreateMountPoints(
            Transform partRoot,
            MechPartDefinition part,
            Dictionary<MechPartSlot, List<Transform>> mountRegistry,
            List<MechMountPoint> mountPoints)
        {
            var definitions = part.MountPoints;
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (definition == null || definition.targetSlot == MechPartSlot.None)
                {
                    continue;
                }

                var mountObject = new GameObject(string.IsNullOrWhiteSpace(definition.mountId) ? definition.targetSlot + "_Mount" : definition.mountId);
                mountObject.transform.SetParent(partRoot, false);
                mountObject.transform.localPosition = definition.localPosition;
                mountObject.transform.localRotation = Quaternion.Euler(definition.localEulerAngles);
                mountObject.transform.localScale = definition.localScale == Vector3.zero ? Vector3.one : definition.localScale;

                var mountPoint = mountObject.AddComponent<MechMountPoint>();
                mountPoint.Configure(definition);
                mountPoints.Add(mountPoint);
                RegisterMount(mountRegistry, definition.targetSlot, mountObject.transform);
            }
        }

        private void RegisterMount(Dictionary<MechPartSlot, List<Transform>> mountRegistry, MechPartSlot slot, Transform mount)
        {
            if (!mountRegistry.TryGetValue(slot, out var list))
            {
                list = new List<Transform>();
                mountRegistry.Add(slot, list);
            }

            list.Add(mount);
        }

        private void ApplyPhysics(MechAggregatedStats stats)
        {
            if (!applyMassToRigidbody || stats == null)
            {
                return;
            }

            var body = targetRigidbody;
            if (body == null)
            {
                body = GetComponent<Rigidbody>();
            }

            if (body == null)
            {
                return;
            }

            body.mass = Mathf.Max(1f, stats.totalMassKg);
        }

        private void LogValidation(MechValidationResult validation)
        {
            if (!logValidationIssues || validation == null || validation.Issues.Count == 0)
            {
                return;
            }

            for (var i = 0; i < validation.Issues.Count; i++)
            {
                var issue = validation.Issues[i];
                var text = $"Mech loadout {issue.severity}: [{issue.code}] {issue.message}";
                if (issue.severity == MechValidationSeverity.Error)
                {
                    Debug.LogError(text, this);
                }
                else if (issue.severity == MechValidationSeverity.Warning)
                {
                    Debug.LogWarning(text, this);
                }
                else
                {
                    Debug.Log(text, this);
                }
            }
        }

        private void DestroyChildren(Transform root)
        {
            for (var i = root.childCount - 1; i >= 0; i--)
            {
                DestroyUnityObject(root.GetChild(i).gameObject);
            }
        }

        private void DestroyUnityObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}
