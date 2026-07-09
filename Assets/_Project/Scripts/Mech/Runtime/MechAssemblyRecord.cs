using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechAssemblyRecord
    {
        public MechPartSlot slot;
        public MechPartDefinition part;
        public GameObject instance;
        public Transform parentMount;
    }
}
