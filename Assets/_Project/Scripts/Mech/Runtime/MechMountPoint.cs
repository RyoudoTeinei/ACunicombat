using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public sealed class MechMountPoint : MonoBehaviour
    {
        [SerializeField] private string mountId;
        [SerializeField] private MechPartSlot targetSlot;
        [SerializeField] private MechHardpointType hardpointType;
        [SerializeField] private float maxMassKg;
        [SerializeField] private string[] requiredTags = Array.Empty<string>();
        [SerializeField] private string[] blockedTags = Array.Empty<string>();

        public string MountId => mountId;
        public MechPartSlot TargetSlot => targetSlot;
        public MechHardpointType HardpointType => hardpointType;
        public float MaxMassKg => maxMassKg;

        public void Configure(MechMountPointDefinition definition)
        {
            if (definition == null)
            {
                return;
            }

            mountId = definition.mountId;
            targetSlot = definition.targetSlot;
            hardpointType = definition.hardpointType;
            maxMassKg = definition.maxMassKg;
            requiredTags = definition.requiredTags ?? Array.Empty<string>();
            blockedTags = definition.blockedTags ?? Array.Empty<string>();
        }

        public bool CanAccept(MechPartDefinition part)
        {
            if (part == null)
            {
                return false;
            }

            if (targetSlot != MechPartSlot.None && part.Slot != targetSlot)
            {
                var mirroredArmAllowed = part.Slot == MechPartSlot.Arms && MechSlotRules.IsMirroredArmSlot(targetSlot);
                if (!mirroredArmAllowed)
                {
                    return false;
                }
            }

            if (maxMassKg > 0f && part.Stats.massKg > maxMassKg)
            {
                return false;
            }

            for (var i = 0; i < requiredTags.Length; i++)
            {
                if (!part.HasTag(requiredTags[i]))
                {
                    return false;
                }
            }

            for (var i = 0; i < blockedTags.Length; i++)
            {
                if (part.HasTag(blockedTags[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
