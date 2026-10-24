using System;
using UnityEngine;
namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechDamageRegion : MonoBehaviour
    {
        public string RegionId { get; private set; }
        public string SlotId { get; private set; }
        public Guid InstanceId { get; private set; }
        public MechPartDamageController Owner { get; private set; }
        public void Configure(MechPartDamageController owner,string region,string slot,Guid instance)
        {Owner=owner;RegionId=region;SlotId=slot;InstanceId=instance;}
    }
}
