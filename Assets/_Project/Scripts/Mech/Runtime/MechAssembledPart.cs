using UnityEngine;

namespace ACunicombat.Mechs
{
    public sealed class MechAssembledPart : MonoBehaviour
    {
        [SerializeField] private MechPartSlot slot;
        [SerializeField] private MechPartDefinition definition;

        public MechPartSlot Slot => slot;
        public MechPartDefinition Definition => definition;

        public void Configure(MechPartSlot assignedSlot, MechPartDefinition partDefinition)
        {
            slot = assignedSlot;
            definition = partDefinition;
        }
    }
}
