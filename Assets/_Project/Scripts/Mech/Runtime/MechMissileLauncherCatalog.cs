using System;
using System.Collections.Generic;
namespace ACunicombat.Mechs
{
    public sealed class MechMissileLauncherDefinition
    {
        public readonly string Number, Purpose, DefaultAmmo;
        internal readonly string PartSuffix;
        public readonly int GroupCapacity, LoadedGroups, TotalGroups;
        public readonly string[] CompatibleAmmo;
        public float ReloadSeconds {get;set;} = 4f;
        public float LaunchCycleSeconds {get;set;}
        public int LoadedCapacity=>GroupCapacity*LoadedGroups;
        // Asset presentation families do not change ammunition compatibility.
        public string AssetFamilyId => Number switch
        {
            "4250" or "4251" or "4252" or "4301" or "4302" or "4310" => "4250-4251-4252-4301-4302-4310",
            "4290" or "4291" or "4292" or "4300" or "4303" or "4230" or "4340" => "4290-4291-4292-4300-4303-4230-4340",
            _ => string.Empty
        };
        public MechMissileLauncherDefinition(string number,string purpose,int groupCapacity,int loadedGroups,int totalGroups,string defaultAmmo,params string[] compatible)
        {PartSuffix="_"+number;LaunchCycleSeconds=number switch {"4250"=>1.3f,"4251" or "4252"=>1.3333334f,"4290"=>2.0666666f,"4291"=>1.1f,"4292"=>1.7666667f,"4300" or "4303"=>1.7f,"4301"=>.9333334f,"4302" or "4310"=>1.8666667f,"4230"=>1.9333334f,"4340"=>2f,_=>.18f};Number=number;Purpose=purpose;GroupCapacity=groupCapacity;LoadedGroups=loadedGroups;TotalGroups=totalGroups;DefaultAmmo=defaultAmmo;CompatibleAmmo=compatible.Length>0?compatible:new[]{defaultAmmo};}
        public bool Allows(string ammo)=>Array.IndexOf(CompatibleAmmo,ammo)>=0;
    }
    public static class MechMissileLauncherCatalog
    {
        static readonly MechMissileLauncherDefinition[] definitions={
            new("4250","重型垂直冷发射拦截仓",12,1,2,"JW7-ER","JW7","JW7-ER"),
            new("4251","垂直冷发射拦截仓",4,1,2,"JW7"),
            new("4252","改进型垂直冷发射拦截仓",4,2,3,"JW7","JW7","JW7-ER"),
            new("4300","近程拦截仓",6,1,2,"ABM-12","ABM-12","ABM-18"),
            new("4303","轻型近程拦截仓",4,1,2,"ABM-12","ABM-12","ABM-18"),
            new("4290","通用导弹仓",6,1,2,"SIM-29","SIM-29","SIM-29M","SIM-57","SIM-57R"),
            new("4291","轻型通用导弹仓",4,1,2,"SIM-29","SIM-29","SIM-29M","SIM-57"),
            new("4292","重型通用导弹仓",10,1,2,"SIM-29","SIM-29","SIM-29M","SIM-57","SIM-57R"),
            new("4301","近距格斗空空导弹发射器",8,1,4,"AAM-4301C"),
            new("4302","双脉冲远程空空导弹发射器",4,1,6,"DPL-15"),
            new("4310","长程双脉冲反辐射/空空导弹发射器",2,1,4,"DPL-17"),
            new("4230","常规反辐射/长程空空导弹发射器",2,1,6,"LRA-4230"),
            new("4340","可调流量反辐射/长程空空导弹发射器",2,1,4,"MTR-4340","MTR-4340","MTR-4340-ER")};
        static readonly MechMissileLauncherDefinition left4250 = new("4250","重型垂直冷发射拦截仓",12,1,2,"JW7-ER","JW7","JW7-ER"){ReloadSeconds=8f};
        public static MechMissileLauncherDefinition Find(string partId)
        {if(partId==null)return null;if(partId.EndsWith("_L_4250",StringComparison.OrdinalIgnoreCase))return left4250;foreach(var d in definitions)if(partId.EndsWith(d.PartSuffix,StringComparison.OrdinalIgnoreCase))return d;return null;}
        public static bool IsJianwei(string id)=>Find(id)?.Number is "4250" or "4251" or "4252";
        public static bool UsesAmmoSelection(string id)=>IsAbmOrSim(id)||Find(id)?.Number=="4340";
        public static bool IsAbmOrSim(string id)=>Find(id)?.Number is "4300" or "4303" or "4290" or "4291" or "4292";
        public static MechWeaponPerformance CreateDefaultPerformance(string partId)
        {var d=Find(partId);return d==null?null:Compose(d,d.DefaultAmmo);}
        public static MechWeaponPerformance Compose(MechMissileLauncherDefinition d,string ammoId)
        {
            if(d==null||!d.Allows(ammoId))return null;
            var p=MechMissileAmmunitionCatalog.Create(ammoId);if(p==null)return null;
            p.weaponType=d.Purpose;p.magazineCapacity=d.LoadedCapacity;p.magazineCount=d.TotalGroups;
            p.missileGroupCapacity=d.GroupCapacity;p.reloadSeconds=d.ReloadSeconds;p.missileLaunchCycleSeconds=d.LaunchCycleSeconds;
            return p;
        }
    }
}
