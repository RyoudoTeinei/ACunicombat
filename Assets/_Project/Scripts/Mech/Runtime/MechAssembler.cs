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

        [Header("Synchronized Skinned Parts")]
        [SerializeField] private AnimationClip synchronizedAnimationClip;
        [SerializeField] private bool playSynchronizedAnimationOnAwake = true;
        [SerializeField] private bool loopSynchronizedAnimation = true;
        [SerializeField] private float synchronizedAnimationSpeed = 1f;

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
        public AnimationClip SynchronizedAnimationClip => synchronizedAnimationClip;

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
            var existingAnimationPlayer = GetComponent<MechSynchronizedAnimationPlayer>();
            if (existingAnimationPlayer != null)
            {
                existingAnimationPlayer.StopPlayback();
            }

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

                    var parentMounts = ResolveParentMounts(assignment.slot, assignment.part, mountRegistry, assemblyRoot);
                    var parentMount = parentMounts[0];
                    var instance = CreatePartInstance(assignment.slot, assignment.part, parentMount);
                    var assembledPart = instance.GetComponent<MechAssembledPart>();
                    if (assembledPart == null)
                    {
                        assembledPart = instance.AddComponent<MechAssembledPart>();
                    }

                    assembledPart.Configure(assignment.slot, assignment.part);
                    ApplyMaterialOverrides(instance, assignment.part);
                    CreateMountPoints(instance.transform, assignment.part, mountRegistry, mountPoints);

                    if (assignment.part.InstantiateVisualAtAllMounts)
                    {
                        for (var mountIndex = 1; mountIndex < parentMounts.Count; mountIndex++)
                        {
                            var replica = CreatePartInstance(assignment.slot, assignment.part, parentMounts[mountIndex]);
                            replica.name = $"{assignment.slot}_{assignment.part.DisplayName}_Visual_{mountIndex + 1}";
                            ApplyMaterialOverrides(replica, assignment.part);
                        }
                    }

                    records.Add(new MechAssemblyRecord
                    {
                        slot = assignment.slot,
                        part = assignment.part,
                        instance = instance,
                        parentMount = parentMount
                    });
                }
            }

            ConfigureSynchronizedSkinnedParts(records);

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

        private List<Transform> ResolveParentMounts(
            MechPartSlot slot,
            MechPartDefinition part,
            Dictionary<MechPartSlot, List<Transform>> mountRegistry,
            Transform assemblyRoot)
        {
            if (mountRegistry.TryGetValue(slot, out var mounts) && mounts.Count > 0)
            {
                var validMounts = new List<Transform>(mounts.Count);
                var acceptingMounts = new List<Transform>(mounts.Count);
                for (var i = 0; i < mounts.Count; i++)
                {
                    if (mounts[i] == null)
                    {
                        continue;
                    }

                    validMounts.Add(mounts[i]);

                    // Same slot can expose several hardpoints with different tag filters, e.g.
                    // AC6 left-hand firearms mount at the palm dummy while melee weapons mount
                    // at the forearm dummy. Mounts without a MechMountPoint accept everything.
                    var mountPoint = mounts[i].GetComponent<MechMountPoint>();
                    if (mountPoint == null || mountPoint.CanAccept(part))
                    {
                        acceptingMounts.Add(mounts[i]);
                    }
                }

                if (acceptingMounts.Count > 0)
                {
                    return acceptingMounts;
                }

                if (validMounts.Count > 0)
                {
                    return validMounts;
                }
            }

            if (MechSlotRules.IsBaseAssemblySlot(slot))
            {
                return new List<Transform> { assemblyRoot };
            }

            var fallback = new GameObject(slot + "_FallbackMount");
            fallback.transform.SetParent(assemblyRoot, false);
            fallback.transform.localPosition = MechSlotRules.DefaultLocalPosition(slot);
            fallback.transform.localRotation = Quaternion.identity;
            fallback.transform.localScale = Vector3.one;
            RegisterMount(mountRegistry, slot, fallback.transform);
            return new List<Transform> { fallback.transform };
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
            ConfigureMountedEquipmentAnimation(instance, part);
            return instance;
        }

        private static void ConfigureMountedEquipmentAnimation(GameObject instance, MechPartDefinition part)
        {
            if (instance == null || part == null || part.EquipmentAnimations == null ||
                part.EquipmentAnimations.Count == 0)
            {
                return;
            }

            var player = instance.GetComponent<MechEquipmentAnimationPlayer>();
            if (player == null)
            {
                player = instance.AddComponent<MechEquipmentAnimationPlayer>();
            }

            player.Configure(
                part.EquipmentAnimations,
                part.DefaultEquipmentActionId,
                part.PlayDefaultEquipmentActionOnAssemble);

            // Equipment that does not auto-play still needs its native resting pose: sample the
            // default action's first frame once so gimbals/brackets settle into the pose the
            // data defines. This drives the real animation target nodes inside the imported
            // model — never bake action-derived angles onto the wrapper instance, or any later
            // playback (whose curves carry absolute rest-composed values) doubles the rotation.
            if (!part.PlayDefaultEquipmentActionOnAssemble && !string.IsNullOrWhiteSpace(part.DefaultEquipmentActionId))
            {
                player.SampleActionAtTime(part.DefaultEquipmentActionId, 0f);
            }
        }

        private void ConfigureSynchronizedSkinnedParts(IReadOnlyList<MechAssemblyRecord> records)
        {
            var animationBindings = new List<MechSynchronizedAnimationBinding>();
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record?.part == null || record.instance == null || !record.part.UsesSynchronizedSkinnedRig)
                {
                    continue;
                }

                if (record.instance.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                {
                    if (logValidationIssues)
                    {
                        Debug.LogWarning(
                            $"Skinned mech part {record.part.DisplayName} is marked {MechVisualRigMode.SynchronizedSkinned} but contains no SkinnedMeshRenderer.",
                            record.instance);
                    }
                    continue;
                }

                var partClip = record.part.SkinnedAnimationClip != null
                    ? record.part.SkinnedAnimationClip
                    : synchronizedAnimationClip;
                if (partClip == null)
                {
                    if (logValidationIssues)
                    {
                        Debug.LogWarning($"Skinned mech part {record.part.DisplayName} has no animation clip.", record.instance);
                    }
                    continue;
                }

                animationBindings.Add(new MechSynchronizedAnimationBinding(
                    record.slot,
                    record.instance,
                    partClip,
                    record.part.AncestorDriverSlot,
                    record.part.AncestorDrivenBoneNames));
            }

            var player = GetComponent<MechSynchronizedAnimationPlayer>();
            if (animationBindings.Count == 0)
            {
                if (player != null)
                {
                    player.Configure(null, null, false, false, 0f);
                }
                return;
            }

            if (player == null)
            {
                player = gameObject.AddComponent<MechSynchronizedAnimationPlayer>();
            }

            player.Configure(
                animationBindings,
                playSynchronizedAnimationOnAwake,
                loopSynchronizedAnimation,
                synchronizedAnimationSpeed);
        }

#if UNITY_EDITOR
        public void EditorConfigureSynchronizedSkinnedParts(
            AnimationClip animationClip,
            bool shouldAssembleOnAwake = true,
            bool shouldCreatePlaceholders = false,
            bool shouldLoop = true,
            float playbackSpeed = 1f)
        {
            synchronizedAnimationClip = animationClip;
            assembleOnAwake = shouldAssembleOnAwake;
            createPlaceholdersForMissingModels = shouldCreatePlaceholders;
            loopSynchronizedAnimation = shouldLoop;
            synchronizedAnimationSpeed = playbackSpeed;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

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

                var sourceTransforms = FindMountSourceTransforms(partRoot, definition.sourceTransformNameSuffix);
                FilterMountSourceTransforms(partRoot, sourceTransforms, definition.sourceTransformRegion);
                if (sourceTransforms.Count == 0)
                {
                    if (definition.sourceTransformRegion != MechMountSourceRegion.Any)
                    {
                        if (logValidationIssues)
                        {
                            Debug.LogWarning(
                                $"Mount '{definition.mountId}' could not find a child transform ending with " +
                                $"'{definition.sourceTransformNameSuffix}' in the {definition.sourceTransformRegion} region " +
                                $"of part '{part.DisplayName}'. The mount was not created.",
                                partRoot);
                        }

                        continue;
                    }

                    sourceTransforms.Add(partRoot);
                    if (!string.IsNullOrWhiteSpace(definition.sourceTransformNameSuffix) && logValidationIssues)
                    {
                        Debug.LogWarning(
                            $"Mount '{definition.mountId}' could not find a child transform ending with " +
                            $"'{definition.sourceTransformNameSuffix}' on part '{part.DisplayName}'. Using the part root.",
                            partRoot);
                    }
                }

                var sourceCount = definition.matchAllSourceTransforms ? sourceTransforms.Count : 1;
                for (var sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
                {
                    var baseName = string.IsNullOrWhiteSpace(definition.mountId)
                        ? definition.targetSlot + "_Mount"
                        : definition.mountId;
                    var mountName = sourceCount > 1 ? $"{baseName}_{sourceIndex + 1}" : baseName;
                    var mountObject = new GameObject(mountName);
                    mountObject.transform.SetParent(sourceTransforms[sourceIndex], false);
                    mountObject.transform.localPosition = definition.localPosition;
                    mountObject.transform.localRotation = Quaternion.Euler(definition.localEulerAngles);
                    mountObject.transform.localScale = definition.localScale == Vector3.zero ? Vector3.one : definition.localScale;

                    var mountPoint = mountObject.AddComponent<MechMountPoint>();
                    mountPoint.Configure(definition);
                    mountPoints.Add(mountPoint);
                    RegisterMount(mountRegistry, definition.targetSlot, mountObject.transform);
                }
            }
        }

        private static List<Transform> FindMountSourceTransforms(Transform partRoot, string nameSuffix)
        {
            var matches = new List<Transform>();
            if (partRoot == null || string.IsNullOrWhiteSpace(nameSuffix))
            {
                return matches;
            }

            var transforms = partRoot.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                var candidate = transforms[i];
                if (candidate != null && MatchesImportedTransformName(candidate.name, nameSuffix))
                {
                    matches.Add(candidate);
                }
            }

            return matches;
        }

        private static void FilterMountSourceTransforms(
            Transform partRoot,
            List<Transform> sourceTransforms,
            MechMountSourceRegion region)
        {
            if (partRoot == null || sourceTransforms == null || region == MechMountSourceRegion.Any)
            {
                return;
            }

            const float regionEpsilon = 0.001f;
            sourceTransforms.RemoveAll(candidate =>
            {
                if (candidate == null)
                {
                    return true;
                }

                var localZ = partRoot.InverseTransformPoint(candidate.position).z;
                return region == MechMountSourceRegion.Rear
                    ? localZ <= regionEpsilon
                    : localZ >= -regionEpsilon;
            });
        }

        private static bool MatchesImportedTransformName(string transformName, string nameSuffix)
        {
            if (string.IsNullOrWhiteSpace(transformName) || string.IsNullOrWhiteSpace(nameSuffix))
            {
                return false;
            }

            var index = transformName.IndexOf(nameSuffix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var nextIndex = index + nameSuffix.Length;
            return nextIndex >= transformName.Length || !char.IsDigit(transformName[nextIndex]);
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
