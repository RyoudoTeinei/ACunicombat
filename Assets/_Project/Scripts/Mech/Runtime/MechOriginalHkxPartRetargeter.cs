using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechOriginalHkxPartBinding
    {
        public MechPartSlot slot = MechPartSlot.None;
        public Transform part;
        public Transform sourceBone;
        [Range(0f, 1.5f)] public float rotationWeight = 1f;
        [Range(0f, 1f)] public float positionWeight;
        public Vector3 positionScale = Vector3.one;
        public Vector3 rotationEulerOffset;

        [SerializeField] private Vector3 baseLocalPosition;
        [SerializeField] private Quaternion baseLocalRotation = Quaternion.identity;
        [SerializeField] private Vector3 sourceBaseLocalPosition;
        [SerializeField] private Quaternion sourceBaseLocalRotation = Quaternion.identity;

        public bool IsValid => part != null && sourceBone != null;

        public void CaptureBasePose()
        {
            if (part != null)
            {
                baseLocalPosition = part.localPosition;
                baseLocalRotation = part.localRotation;
            }

            if (sourceBone != null)
            {
                sourceBaseLocalPosition = sourceBone.localPosition;
                sourceBaseLocalRotation = sourceBone.localRotation;
            }
        }

        public void ResetPose()
        {
            if (part == null)
            {
                return;
            }

            part.localPosition = baseLocalPosition;
            part.localRotation = baseLocalRotation;
        }

        public void Apply(float rotationMultiplier, float positionMultiplier)
        {
            if (!IsValid)
            {
                return;
            }

            var sourcePositionDelta = sourceBone.localPosition - sourceBaseLocalPosition;
            var scaledPositionDelta = Vector3.Scale(sourcePositionDelta, positionScale);
            part.localPosition = baseLocalPosition + scaledPositionDelta * positionWeight * positionMultiplier;

            var sourceRotationDelta = Quaternion.Inverse(sourceBaseLocalRotation) * sourceBone.localRotation;
            var weightedRotationDelta = Quaternion.Slerp(Quaternion.identity, sourceRotationDelta, rotationWeight * rotationMultiplier);
            part.localRotation = baseLocalRotation * weightedRotationDelta * Quaternion.Euler(rotationEulerOffset);
        }

        public void ConfigureRigidModuleDefaults()
        {
            positionWeight = 0f;
            positionScale = Vector3.zero;
            rotationEulerOffset = Vector3.zero;
        }
    }

    [DisallowMultipleComponent]
    public sealed class MechOriginalHkxPartRetargeter : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private Transform sourceSkeletonRoot;

        [Header("Target")]
        [SerializeField] private MechAssembler targetAssembler;
        [SerializeField] private Transform targetAssemblyRoot;

        [Header("Playback")]
        [SerializeField] private bool playOnStart = true;
        [SerializeField, Range(0f, 1.5f)] private float globalRotationWeight = 0.75f;
        [SerializeField] private bool applySourcePositionDeltas;
        [SerializeField, Range(0f, 1f)] private float globalPositionWeight;

        [Header("Bindings")]
        [SerializeField] private List<MechOriginalHkxPartBinding> bindings = new List<MechOriginalHkxPartBinding>();

        private bool isPlaying;
        private readonly Dictionary<string, Transform> sourceBones = new Dictionary<string, Transform>(StringComparer.Ordinal);

        public Transform SourceSkeletonRoot
        {
            get => sourceSkeletonRoot;
            set => sourceSkeletonRoot = value;
        }

        public MechAssembler TargetAssembler
        {
            get => targetAssembler;
            set => targetAssembler = value;
        }

        public Transform TargetAssemblyRoot
        {
            get => targetAssemblyRoot;
            set => targetAssemblyRoot = value;
        }

        public IReadOnlyList<MechOriginalHkxPartBinding> Bindings => bindings;

        private void Awake()
        {
            if (targetAssembler == null)
            {
                targetAssembler = GetComponent<MechAssembler>();
            }

            RebuildSourceBoneMap();
            isPlaying = playOnStart;
        }

        private void OnEnable()
        {
            isPlaying = playOnStart;
        }

        private void LateUpdate()
        {
            if (!isPlaying)
            {
                return;
            }

            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                if (binding == null)
                {
                    continue;
                }

                binding.Apply(globalRotationWeight, applySourcePositionDeltas ? globalPositionWeight : 0f);
            }
        }

        [ContextMenu("Play Retarget")]
        public void Play()
        {
            isPlaying = true;
        }

        [ContextMenu("Pause Retarget")]
        public void Pause()
        {
            isPlaying = false;
        }

        [ContextMenu("Reset Retarget Pose")]
        public void ResetPose()
        {
            for (var i = 0; i < bindings.Count; i++)
            {
                bindings[i]?.ResetPose();
            }
        }

        [ContextMenu("Capture Base Pose")]
        public void CaptureBasePose()
        {
            for (var i = 0; i < bindings.Count; i++)
            {
                bindings[i]?.CaptureBasePose();
            }
        }

        [ContextMenu("Auto Build Bindings")]
        public void AutoBuildBindings()
        {
            RebuildSourceBoneMap();
            bindings.Clear();

            if (targetAssembler == null)
            {
                targetAssembler = GetComponent<MechAssembler>();
            }

            if (targetAssemblyRoot == null)
            {
                targetAssemblyRoot = FindAssemblyRoot();
            }

            var assembledParts = GetTargetParts();
            for (var i = 0; i < assembledParts.Count; i++)
            {
                var part = assembledParts[i];
                if (part == null)
                {
                    continue;
                }

                var binding = BuildBinding(part.Slot, part.transform);
                if (binding != null)
                {
                    bindings.Add(binding);
                }
            }

            CaptureBasePose();
            ConfigureStableRigidModuleDefaults();
        }

        [ContextMenu("Configure Stable Rigid Module Defaults")]
        public void ConfigureStableRigidModuleDefaults()
        {
            applySourcePositionDeltas = false;
            globalPositionWeight = 0f;
            globalRotationWeight = 0.75f;

            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                if (binding == null)
                {
                    continue;
                }

                binding.ConfigureRigidModuleDefaults();
                binding.rotationWeight = StableRotationWeightForSlot(binding.slot);
            }
        }

        public int CountValidBindings()
        {
            var count = 0;
            for (var i = 0; i < bindings.Count; i++)
            {
                if (bindings[i] != null && bindings[i].IsValid)
                {
                    count++;
                }
            }

            return count;
        }

        private void RebuildSourceBoneMap()
        {
            sourceBones.Clear();
            if (sourceSkeletonRoot == null)
            {
                return;
            }

            var transforms = sourceSkeletonRoot.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                var current = transforms[i];
                if (!sourceBones.ContainsKey(current.name))
                {
                    sourceBones.Add(current.name, current);
                }
            }
        }

        private Transform FindAssemblyRoot()
        {
            if (targetAssembler == null)
            {
                return null;
            }

            var assembled = targetAssembler.transform.Find("_AssembledMech");
            return assembled != null ? assembled : targetAssembler.transform;
        }

        private List<MechAssembledPart> GetTargetParts()
        {
            var result = new List<MechAssembledPart>();
            if (targetAssembler != null && targetAssembler.RuntimeState != null && targetAssembler.RuntimeState.AssembledParts.Count > 0)
            {
                for (var i = 0; i < targetAssembler.RuntimeState.AssembledParts.Count; i++)
                {
                    var record = targetAssembler.RuntimeState.AssembledParts[i];
                    if (record?.instance == null)
                    {
                        continue;
                    }

                    var part = record.instance.GetComponent<MechAssembledPart>();
                    if (part != null)
                    {
                        result.Add(part);
                    }
                }
            }

            if (result.Count == 0)
            {
                var root = targetAssemblyRoot != null ? targetAssemblyRoot : transform;
                result.AddRange(root.GetComponentsInChildren<MechAssembledPart>(true));
            }

            return result;
        }

        private MechOriginalHkxPartBinding BuildBinding(MechPartSlot slot, Transform part)
        {
            var bone = FindBoneForSlot(slot);
            if (bone == null)
            {
                return null;
            }

            return new MechOriginalHkxPartBinding
            {
                slot = slot,
                part = part,
                sourceBone = bone,
                rotationWeight = RotationWeightForSlot(slot),
                positionWeight = 0f,
                positionScale = Vector3.zero,
                rotationEulerOffset = Vector3.zero
            };
        }

        private Transform FindBoneForSlot(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.Core:
                    return FirstBone("BD_Root", "Spine", "Spine1", "Root");
                case MechPartSlot.Head:
                    return FirstBone("Head", "Neck", "Spine1");
                case MechPartSlot.Arms:
                    return FirstBone("Spine1", "Offset_ClavicleLink", "Spine");
                case MechPartSlot.LeftArm:
                    return FirstBone("L_UpperArm", "Root_L_UpperArm", "L_Clavicle");
                case MechPartSlot.RightArm:
                    return FirstBone("R_UpperArm", "Root_R_UpperArm", "R_Clavicle");
                case MechPartSlot.Legs:
                    return FirstBone("Pelvis", "Root", "Master");
                case MechPartSlot.Booster:
                case MechPartSlot.Generator:
                case MechPartSlot.FireControlSystem:
                    return FirstBone("BD_Root", "Spine", "Spine1");
                case MechPartSlot.Radar:
                    return FirstBone("Head", "Neck", "Spine1");
                case MechPartSlot.LeftHandWeapon:
                    return FirstBone("L_Weapon_00", "L_Hand", "L_Wrist");
                case MechPartSlot.RightHandWeapon:
                    return FirstBone("R_Weapon_00", "R_Hand", "R_Wrist");
                case MechPartSlot.LeftBackWeapon:
                case MechPartSlot.RightBackWeapon:
                    return FirstBone("Spine1", "Spine", "BD_Root");
                default:
                    return FirstBone("Root", "Master");
            }
        }

        private Transform FirstBone(params string[] names)
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (sourceBones.TryGetValue(names[i], out var bone) && bone != null)
                {
                    return bone;
                }
            }

            return null;
        }

        private static float RotationWeightForSlot(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.Core:
                    return 0.35f;
                case MechPartSlot.Arms:
                    return 0.25f;
                case MechPartSlot.Legs:
                    return 0.25f;
                case MechPartSlot.Booster:
                case MechPartSlot.Generator:
                case MechPartSlot.FireControlSystem:
                    return 0.2f;
                default:
                    return 0.55f;
            }
        }

        private static float StableRotationWeightForSlot(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.Core:
                    return 0.28f;
                case MechPartSlot.Head:
                    return 0.5f;
                case MechPartSlot.Arms:
                    return 0.18f;
                case MechPartSlot.LeftArm:
                case MechPartSlot.RightArm:
                    return 0.45f;
                case MechPartSlot.Legs:
                    return 0.18f;
                case MechPartSlot.LeftHandWeapon:
                case MechPartSlot.RightHandWeapon:
                    return 0.6f;
                case MechPartSlot.LeftBackWeapon:
                case MechPartSlot.RightBackWeapon:
                    return 0.25f;
                case MechPartSlot.Booster:
                case MechPartSlot.Generator:
                case MechPartSlot.FireControlSystem:
                case MechPartSlot.Radar:
                    return 0.15f;
                default:
                    return RotationWeightForSlot(slot);
            }
        }
    }
}
