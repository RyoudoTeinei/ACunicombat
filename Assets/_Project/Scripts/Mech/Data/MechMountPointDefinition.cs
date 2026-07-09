using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechMountPointDefinition
    {
        public string mountId = "Mount";
        public MechPartSlot targetSlot = MechPartSlot.None;
        public MechHardpointType hardpointType = MechHardpointType.None;
        public float maxMassKg;
        public Vector3 localPosition;
        public Vector3 localEulerAngles;
        public Vector3 localScale = Vector3.one;
        public string[] requiredTags = Array.Empty<string>();
        public string[] blockedTags = Array.Empty<string>();
    }
}
