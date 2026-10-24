using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechSlotDefinition
    {
        public string id;
        public string displayName;
        public string weaponGroup;
        // Optional compatibility role for existing body/weapon adapters. Not the slot identity.
        public MechPartSlot legacyRole;
        public MechPartCategory category = MechPartCategory.Weapon;
        public MechHardpointType hardpointType = MechHardpointType.HandWeapon;
        public bool required;
        public string parentSlotId;
        public string mountPath;
        public Vector3 localPosition;
        public Vector3 localEulerAngles;
        public float maxMassKg;
        public Vector3 maxSizeMeters;
        public float maxPowerKw;
        public float maxHeatKw;
        public float yawLimitDegrees = 180;
        public float pitchLimitDegrees = 90;
        public string[] requiredTags = Array.Empty<string>();
        public string[] blockedTags = Array.Empty<string>();
        public string Label => string.IsNullOrWhiteSpace(displayName) ? id : displayName;

        public bool Accepts(MechPartDefinition part)
        {
            if (part == null) return true;
            if (part.Category != category) return false;
            var type = part.MountClass;
            if (hardpointType != MechHardpointType.None && type != hardpointType) return false;
            if ((category == MechPartCategory.Frame || category == MechPartCategory.Internal) && legacyRole != MechPartSlot.None &&
                part.Slot != legacyRole && !(part.Slot == MechPartSlot.Arms && MechSlotRules.IsMirroredArmSlot(legacyRole))) return false;
            if (maxMassKg > 0 && part.Stats.massKg > maxMassKg) return false;
            var size = part.InstallationSizeMeters;
            if ((maxSizeMeters.x > 0 && size.x > maxSizeMeters.x) ||
                (maxSizeMeters.y > 0 && size.y > maxSizeMeters.y) ||
                (maxSizeMeters.z > 0 && size.z > maxSizeMeters.z)) return false;
            if (maxPowerKw > 0 && part.Stats.peakPowerDrawMw * 1000 > maxPowerKw) return false;
            if (maxHeatKw > 0 && part.Stats.heatGenerationMw * 1000 > maxHeatKw) return false;
            foreach (var tag in requiredTags ?? Array.Empty<string>()) if (!part.HasTag(tag)) return false;
            foreach (var tag in blockedTags ?? Array.Empty<string>()) if (part.HasTag(tag)) return false;
            return true;
        }

        public MechSlotDefinition Copy(string fullId = null, string parent = null)
        {
            var copy = (MechSlotDefinition)MemberwiseClone();
            copy.id = fullId ?? id;
            copy.parentSlotId = parent ?? parentSlotId;
            copy.requiredTags = (string[])(requiredTags ?? Array.Empty<string>()).Clone();
            copy.blockedTags = (string[])(blockedTags ?? Array.Empty<string>()).Clone();
            return copy;
        }
    }

    [CreateAssetMenu(menuName = "ACunicombat/Mech/Platform Layout", fileName = "MechPlatformDefinition")]
    public sealed class MechPlatformDefinition : ScriptableObject
    {
        public const int CurrentVersion = 1;
        public int version = CurrentVersion;
        public string platformId;
        public List<MechSlotDefinition> slots = new List<MechSlotDefinition>();
    }
}
