using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechAssemblyRecord
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string slotId;
        public string SlotId => string.IsNullOrEmpty(slotId) ? MechSlotGraph.LegacyId(slot) : slotId;
        public MechPartSlot slot;
        public MechPartDefinition part;
        public GameObject instance;
        public Transform parentMount;
    }
}
