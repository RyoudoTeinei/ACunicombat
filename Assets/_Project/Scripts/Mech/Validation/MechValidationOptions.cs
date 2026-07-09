using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechValidationOptions
    {
        public bool requireDefaultFrameSlots = true;
        public bool requireWeaponHardpoints = true;
        public bool enforcePowerBudget = true;
        public bool enforceHeatBudget = false;
        public bool enforceSupportedMass = true;
        public bool allowArmsSlotInsteadOfSeparateArms = true;
        public List<MechPartSlot> extraRequiredSlots = new List<MechPartSlot>();

        public IEnumerable<MechPartSlot> EnumerateRequiredSlots()
        {
            if (requireDefaultFrameSlots)
            {
                var defaults = MechSlotRules.GetDefaultRequiredSlots();
                for (var i = 0; i < defaults.Count; i++)
                {
                    yield return defaults[i];
                }
            }

            for (var i = 0; i < extraRequiredSlots.Count; i++)
            {
                if (extraRequiredSlots[i] != MechPartSlot.None)
                {
                    yield return extraRequiredSlots[i];
                }
            }
        }
    }
}
