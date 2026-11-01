using System;
using System.Collections.Generic;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public sealed partial class MechTestWeaponSystem
    {
        private sealed class InstanceWeaponState
        {
            public Guid InstanceId;
            public readonly HandWeaponState Hand = new HandWeaponState();
            public readonly ShaoWeiWeaponState Defense = new ShaoWeiWeaponState();
            public int Rounds, Magazines, MuzzleIndex;
            public string PartId;
            public float WeaponCooldownUntil;
            public MechWeaponDeployment Deployment;
            public MechActionId ActiveCommand;
            public MechMissileBanks MissileBanks;
            public int ActiveMissileBank=-1;
            public bool HasExternalAim;
            public Vector3 AimPosition, AimVelocity;
        }
        private readonly Dictionary<MechEquipmentSlot, InstanceWeaponState> instanceStates = new Dictionary<MechEquipmentSlot, InstanceWeaponState>();
        public bool IsEquipmentOverheated(MechEquipmentSlot slot){var state=GetInstanceState(slot);return state.Hand.Overheated||state.Defense.Overheated;}
        public bool IsBeltFed(MechEquipmentSlot slot)=>GetEquippedWeaponPerformance(slot)?.beltFed==true;
        public bool TryGetEquipmentHeatFraction(string slotId,out float fraction)
        {
            fraction=0;var slot=ResolveEquipmentSlot(new MechEquipmentSlot(slotId));
            if(instanceStates.TryGetValue(slot,out var handState)&&handState.Hand.Performance?.beltFed==true){fraction=Mathf.Clamp01(handState.Hand.HeatMj/Mathf.Max(.001f,handState.Hand.Performance.heatCapacityMj));return true;}
            if(!instanceStates.TryGetValue(slot,out var state)||state.Defense.Performance==null||state.Defense.Performance.role!=MechWeaponRole.PointDefenseCannon)return false;
            fraction=Mathf.Clamp01(state.Defense.HeatMj/Mathf.Max(.001f,state.Defense.Performance.heatCapacityMj));return true;
        }
        private IEnumerable<MechEquipmentSlot> InstalledWeaponSlots()
        {
            var slots = new List<MechEquipmentSlot>();
            if (runtimeState != null)
                foreach (var record in runtimeState.AssembledParts)
                    if (record?.part != null && record.part.Category == MechPartCategory.Weapon)
                        slots.Add(new MechEquipmentSlot(record.SlotId, record.slot));
            slots.Sort((a,b) => string.CompareOrdinal(a.Id,b.Id));
            return slots;
        }
        private MechAssemblyRecord FindEquipmentRecord(MechEquipmentSlot slot)
        {
            if (runtimeState == null) runtimeState = GetComponent<MechRuntimeState>();
            return runtimeState!=null?runtimeState.FindAssembledPart(slot):null;
        }
        private MechEquipmentSlot ResolveEquipmentSlot(MechEquipmentSlot slot)
        {
            var record = FindEquipmentRecord(slot);
            return record != null ? new MechEquipmentSlot(record.SlotId, record.slot) : slot;
        }
        private InstanceWeaponState GetInstanceState(MechEquipmentSlot slot)
        {
            var record = FindEquipmentRecord(slot);
            if(record!=null)slot=new MechEquipmentSlot(record.SlotId,record.slot);
            Guid id = record?.InstanceId ?? Guid.Empty;
            if (!instanceStates.TryGetValue(slot, out var state) || state.InstanceId != id)
            { state = new InstanceWeaponState { InstanceId = id }; instanceStates[slot] = state; muzzlesBySlot.Remove(slot); }
            return state;
        }
    }
}
