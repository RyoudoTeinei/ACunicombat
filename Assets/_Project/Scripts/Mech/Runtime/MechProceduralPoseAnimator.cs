using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechProceduralPoseMode
    {
        ReadyIdle = 0,
        AimForward = 10,
        ForwardBoost = 20,
        ReverseBrake = 30,
        LateralStrafe = 40
    }

    [DisallowMultipleComponent]
    public sealed class MechProceduralPoseAnimator : MonoBehaviour
    {
        [Header("Playback")]
        [SerializeField] private bool playOnStart = true;
        [SerializeField] private MechProceduralPoseMode poseMode = MechProceduralPoseMode.ReadyIdle;
        [SerializeField, Range(0f, 2f)] private float intensity = 1f;
        [SerializeField, Min(0f)] private float playbackSpeed = 1f;

        [Header("Motion")]
        [SerializeField, Range(0f, 0.25f)] private float idleBobMeters = 0.025f;
        [SerializeField, Range(0f, 10f)] private float bodySwayDegrees = 1.8f;
        [SerializeField, Range(0f, 20f)] private float armAimDegrees = 6f;
        [SerializeField, Range(0f, 20f)] private float weaponRecoilDegrees = 4f;
        [SerializeField, Range(0f, 0.25f)] private float weaponRecoilMeters = 0.04f;
        [SerializeField, Range(0f, 30f)] private float boostLeanDegrees = 8f;

        private readonly List<PosePart> parts = new List<PosePart>();
        private MechRuntimeState runtimeState;
        private bool isPlaying;
        private int cachedPartHash;

        public MechProceduralPoseMode PoseMode
        {
            get => poseMode;
            set => poseMode = value;
        }

        private void Awake()
        {
            runtimeState = GetComponent<MechRuntimeState>();
            RecachePoseParts();
        }

        private void OnEnable()
        {
            isPlaying = playOnStart;
        }

        private void OnDisable()
        {
            ResetPose();
        }

        private void LateUpdate()
        {
            if (!isPlaying || intensity <= 0f)
            {
                return;
            }

            if (NeedRecache())
            {
                RecachePoseParts();
            }

            var time = Time.time * Mathf.Max(0f, playbackSpeed);
            var breath = Mathf.Sin(time * 2.1f);
            var slow = Mathf.Sin(time * 0.85f);
            var recoil = Mathf.Max(0f, Mathf.Sin(time * 8f));

            for (var i = 0; i < parts.Count; i++)
            {
                ApplyPose(parts[i], breath, slow, recoil);
            }
        }

        [ContextMenu("Play Pose")]
        public void PlayPose()
        {
            isPlaying = true;
            RecachePoseParts();
        }

        [ContextMenu("Pause Pose")]
        public void PausePose()
        {
            isPlaying = false;
            ResetPose();
        }

        [ContextMenu("Recache Pose Parts")]
        public void RecachePoseParts()
        {
            ResetPose();
            parts.Clear();
            runtimeState = GetComponent<MechRuntimeState>();

            if (runtimeState != null && runtimeState.AssembledParts.Count > 0)
            {
                for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
                {
                    var record = runtimeState.AssembledParts[i];
                    if (record?.instance != null)
                    {
                        AddPart(record.slot, record.instance.transform);
                    }
                }
            }
            else
            {
                var assembledParts = GetComponentsInChildren<MechAssembledPart>(true);
                for (var i = 0; i < assembledParts.Length; i++)
                {
                    AddPart(assembledParts[i].Slot, assembledParts[i].transform);
                }
            }

            cachedPartHash = CalculatePartHash();
        }

        [ContextMenu("Reset Pose")]
        public void ResetPose()
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part.transform == null)
                {
                    continue;
                }

                part.transform.localPosition = part.baseLocalPosition;
                part.transform.localRotation = part.baseLocalRotation;
                part.transform.localScale = part.baseLocalScale;
            }
        }

        private void AddPart(MechPartSlot slot, Transform partTransform)
        {
            if (partTransform == null)
            {
                return;
            }

            parts.Add(new PosePart
            {
                slot = slot,
                transform = partTransform,
                baseLocalPosition = partTransform.localPosition,
                baseLocalRotation = partTransform.localRotation,
                baseLocalScale = partTransform.localScale
            });
        }

        private bool NeedRecache()
        {
            return parts.Count == 0 || cachedPartHash != CalculatePartHash();
        }

        private int CalculatePartHash()
        {
            var hash = 17;
            var assembledParts = GetComponentsInChildren<MechAssembledPart>(true);
            for (var i = 0; i < assembledParts.Length; i++)
            {
                hash = hash * 31 + (int)assembledParts[i].Slot;
                hash = hash * 31 + assembledParts[i].transform.GetSiblingIndex();
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(assembledParts[i].name);
            }

            return hash;
        }

        private void ApplyPose(PosePart part, float breath, float slow, float recoil)
        {
            var mode = poseMode;
            var amount = Mathf.Max(0f, intensity);
            var localPosition = part.baseLocalPosition;
            var localRotation = part.baseLocalRotation;

            switch (part.slot)
            {
                case MechPartSlot.Core:
                    localPosition += Vector3.up * idleBobMeters * breath * amount;
                    localRotation *= Quaternion.Euler(
                        BodyPitch(mode) + bodySwayDegrees * 0.35f * breath * amount,
                        bodySwayDegrees * 0.45f * slow * amount,
                        BodyRoll(mode) + bodySwayDegrees * 0.25f * slow * amount);
                    break;

                case MechPartSlot.Head:
                case MechPartSlot.Radar:
                    localRotation *= Quaternion.Euler(
                        -1.5f * breath * amount,
                        3f * slow * amount,
                        0f);
                    break;

                case MechPartSlot.Arms:
                case MechPartSlot.LeftArm:
                case MechPartSlot.RightArm:
                    localRotation *= Quaternion.Euler(
                        ArmPitch(mode) + armAimDegrees * 0.25f * breath * amount,
                        ArmYaw(part.slot, mode) * amount,
                        ArmRoll(part.slot, mode) + 1.5f * slow * amount);
                    break;

                case MechPartSlot.LeftHandWeapon:
                case MechPartSlot.RightHandWeapon:
                    localPosition += Vector3.back * weaponRecoilMeters * recoil * amount;
                    localRotation *= Quaternion.Euler(
                        WeaponPitch(mode) - weaponRecoilDegrees * recoil * amount,
                        WeaponYaw(part.slot, mode) * amount,
                        0f);
                    break;

                case MechPartSlot.LeftBackWeapon:
                case MechPartSlot.RightBackWeapon:
                    localRotation *= Quaternion.Euler(
                        BackWeaponPitch(mode) + 1.5f * breath * amount,
                        WeaponYaw(part.slot, mode) * 0.45f * amount,
                        BackWeaponRoll(part.slot, mode) * amount);
                    break;

                case MechPartSlot.Legs:
                    localPosition += Vector3.up * idleBobMeters * -0.35f * breath * amount;
                    localRotation *= Quaternion.Euler(
                        -BodyPitch(mode) * 0.35f,
                        0f,
                        -BodyRoll(mode) * 0.45f);
                    break;
            }

            part.transform.localPosition = localPosition;
            part.transform.localRotation = localRotation;
        }

        private float BodyPitch(MechProceduralPoseMode mode)
        {
            switch (mode)
            {
                case MechProceduralPoseMode.ForwardBoost:
                    return boostLeanDegrees * intensity;
                case MechProceduralPoseMode.ReverseBrake:
                    return -boostLeanDegrees * 0.75f * intensity;
                default:
                    return 0f;
            }
        }

        private float BodyRoll(MechProceduralPoseMode mode)
        {
            return mode == MechProceduralPoseMode.LateralStrafe ? -boostLeanDegrees * 0.5f * intensity : 0f;
        }

        private float ArmPitch(MechProceduralPoseMode mode)
        {
            switch (mode)
            {
                case MechProceduralPoseMode.AimForward:
                    return -armAimDegrees * intensity;
                case MechProceduralPoseMode.ForwardBoost:
                    return armAimDegrees * 0.35f * intensity;
                case MechProceduralPoseMode.ReverseBrake:
                    return -armAimDegrees * 0.5f * intensity;
                default:
                    return 0f;
            }
        }

        private float ArmYaw(MechPartSlot slot, MechProceduralPoseMode mode)
        {
            if (mode != MechProceduralPoseMode.AimForward)
            {
                return 0f;
            }

            return IsLeft(slot) ? 2f : -2f;
        }

        private float ArmRoll(MechPartSlot slot, MechProceduralPoseMode mode)
        {
            if (mode == MechProceduralPoseMode.LateralStrafe)
            {
                return IsLeft(slot) ? -3f : 3f;
            }

            return 0f;
        }

        private float WeaponPitch(MechProceduralPoseMode mode)
        {
            return mode == MechProceduralPoseMode.AimForward ? -armAimDegrees * 0.6f * intensity : 0f;
        }

        private float WeaponYaw(MechPartSlot slot, MechProceduralPoseMode mode)
        {
            if (mode != MechProceduralPoseMode.AimForward)
            {
                return 0f;
            }

            return IsLeft(slot) ? 1.5f : -1.5f;
        }

        private float BackWeaponPitch(MechProceduralPoseMode mode)
        {
            switch (mode)
            {
                case MechProceduralPoseMode.AimForward:
                    return -5f * intensity;
                case MechProceduralPoseMode.ForwardBoost:
                    return 3f * intensity;
                default:
                    return 0f;
            }
        }

        private float BackWeaponRoll(MechPartSlot slot, MechProceduralPoseMode mode)
        {
            if (mode != MechProceduralPoseMode.AimForward)
            {
                return 0f;
            }

            return IsLeft(slot) ? -4f : 4f;
        }

        private static bool IsLeft(MechPartSlot slot)
        {
            return slot == MechPartSlot.LeftArm ||
                slot == MechPartSlot.LeftHandWeapon ||
                slot == MechPartSlot.LeftBackWeapon;
        }

        [Serializable]
        private sealed class PosePart
        {
            public MechPartSlot slot;
            public Transform transform;
            public Vector3 baseLocalPosition;
            public Quaternion baseLocalRotation;
            public Vector3 baseLocalScale;
        }
    }
}
