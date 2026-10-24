using System;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public sealed partial class MechTestWeaponSystem
    {
        private string SelectedMissileVariant(MechEquipmentSlot slot,MechPartDefinition part)
        {
            string fallback=MechMissileVariantCatalog.GetDefaultVariantId(part.PartId,part.WeaponPerformance);
            var resolved=ResolveEquipmentSlot(slot);
            if(runtimeState?.CurrentLoadout!=null)foreach(var a in runtimeState.CurrentLoadout.EnumerateEnabledAssignments())
                if(a.SlotId==resolved.Id&&!string.IsNullOrWhiteSpace(a.missileVariantId)){fallback=a.missileVariantId;break;}
            string selected=MechLoadoutSelectionStore.IsPlayerMech(gameObject)?MechLoadoutSelectionStore.LoadMissileVariantId(slot.LegacyRole,part.PartId,fallback):fallback;
            return MechMissileVariantCatalog.ResolveVariant(part.PartId,part.WeaponPerformance,selected).VariantId;
        }
        private MechMissileBanks EnsureMissileBanks(MechEquipmentSlot slot)
        {
            var part=GetEquippedPartDefinition(slot);if(part==null||!MechMissileLauncherCatalog.IsJianwei(part.PartId))return null;
            var state=GetInstanceState(slot);
            if(state.MissileBanks==null)state.MissileBanks=new MechMissileBanks(MechMissileLauncherCatalog.Find(part.PartId),SelectedMissileVariant(slot,part));
            state.MissileBanks.Tick(Time.timeAsDouble);SyncMissileBankCounts(state);return state.MissileBanks;
        }
        private static void SyncMissileBankCounts(InstanceWeaponState state)
        {var b=state.MissileBanks;if(b==null)return;state.Rounds=b.Loaded;state.Magazines=(b.Total+b.Launcher.GroupCapacity-1)/b.Launcher.GroupCapacity;}
        private double MissileTargetDistance(TrackId id)
        {
            if(radarController!=null&&id.IsValid&&radarController.TryGetMissileTrack(id,out var t))
                return Unity.Mathematics.math.distance(t.PredictPosition(radarController.SimTimeSeconds),(Unity.Mathematics.double3)(Unity.Mathematics.float3)transform.position);
            return 0;
        }
        private MechWeaponPerformance BankPerformance(MechEquipmentSlot slot,TrackId target=default)
        {
            var banks=EnsureMissileBanks(slot);if(banks==null)return null;
            var state=GetInstanceState(slot);int index=state.ActiveMissileBank;
            if(index<0)index=target.IsValid?banks.Select(MissileTargetDistance(target)):banks.LongestLoadedBank();
            // Empty/unsuitable inventory still has a role for HUD; release checks
            // explicitly reject it instead of confusing a fallback with ammunition.
            string ammo=index>=0?banks.Banks[index].Ammo:banks.Launcher.DefaultAmmo;
            return MechMissileLauncherCatalog.Compose(banks.Launcher,ammo);
        }
        private bool BankCanRelease(MechEquipmentSlot slot,TrackId target)
        {
            var banks=EnsureMissileBanks(slot);if(banks==null)return true;
            var state=GetInstanceState(slot);int index=state.ActiveMissileBank>=0?state.ActiveMissileBank:banks.Select(MissileTargetDistance(target));
            if(index<0||banks.Banks[index].Rounds<=0)return false;
            return MissileTargetDistance(target)<=MechMissileAmmunitionCatalog.Create(banks.Banks[index].Ammo).maximumRangeMeters;
        }
        public string GetMissileBankStatus(MechEquipmentSlot slot)
        {
            var banks=EnsureMissileBanks(slot);if(banks==null)return null;
            string result="";
            for(int i=0;i<banks.Banks.Length;i++)
            {var b=banks.Banks[i];if(i>0)result+=" / ";result+=(i==0?"A:":"B:")+(b.Ammo=="JW7-ER"?"C":"普")+b.Rounds;
                if(b.Loading>0)result+=" 装"+Mathf.Max(0,(float)(b.ReloadAt-Time.timeAsDouble)).ToString("0.0")+"s";}
            return result+" 备"+banks.Reserve+"/总"+banks.Total;
        }
        public string GetMissileBankStatus(string slotId)=>GetMissileBankStatus(new MechEquipmentSlot(slotId));
        public int CopyMissileBanks(string slotId,System.Collections.Generic.List<MechMissileBankSnapshot> destination,out int reserveRounds,out int totalRounds)
        {
            destination.Clear();var banks=EnsureMissileBanks(new MechEquipmentSlot(slotId));reserveRounds=banks?.Reserve??0;totalRounds=banks?.Total??0;
            if(banks!=null)for(int i=0;i<banks.Banks.Length;i++)destination.Add(new MechMissileBankSnapshot(i,banks.Banks[i],banks.ReserveAmmo));
            return destination.Count;
        }
    }
}
