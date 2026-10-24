using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public class MechAbmSimLauncherTests
{
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static MechPartDefinition Part(string n,string side)=>Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_"+n)??Resources.Load<MechPartDefinition>("AC6Parts/AC6_NATIVE_WP_"+side+"_"+n);
    [TestCase("4300",6,2)] [TestCase("4303",4,2)] [TestCase("4290",6,4)] [TestCase("4291",4,3)] [TestCase("4292",10,4)]
    public void SavedWarehouseCapacityCompatibilityAndAnimation(string n,int rounds,int variants)
    {
        foreach(string side in new[]{"L","R"})
        {
            var p=Part(n,side);Assert.IsNotNull(p);var d=MechMissileLauncherCatalog.Find(p.PartId);
            Assert.AreEqual(rounds,d.LoadedCapacity);Assert.AreEqual(2,d.TotalGroups);
            Assert.AreEqual(JsonUtility.ToJson(MechMissileLauncherCatalog.CreateDefaultPerformance(p.PartId)),JsonUtility.ToJson(p.WeaponPerformance));
            Assert.AreEqual(variants,MechMissileVariantCatalog.GetVariantCount(p.PartId,p.WeaponPerformance));
            Assert.IsTrue(p.EquipmentAnimations.Any(a=>a.ActionId=="a000_800210"));
            foreach(var ammo in d.CompatibleAmmo){var v=MechMissileVariantCatalog.ResolveVariant(p.PartId,p.WeaponPerformance,ammo);Assert.AreEqual(ammo,v.MissileId);Assert.AreEqual(rounds,v.Performance.magazineCapacity);Assert.AreEqual(2,v.Performance.magazineCount);}
        }
    }
    [Test] public void IllegalAmmoRejectedAndLegacySelectionMigrated()
    {
        var d=MechMissileLauncherCatalog.Find("WP_L_4291");Assert.IsNull(MechMissileLauncherCatalog.Compose(d,"SIM-57R"));
        var p=MechMissileLauncherCatalog.CreateDefaultPerformance("WP_L_4290");
        Assert.AreEqual("SIM-29",MechMissileVariantCatalog.ResolveVariant("WP_L_4290",p,"standard").MissileId);
        Assert.AreEqual("SIM-29M",MechMissileVariantCatalog.ResolveVariant("WP_L_4290",p,"dogfight").MissileId);
        Assert.AreEqual("SIM-57R",MechMissileVariantCatalog.ResolveVariant("WP_L_4290",p,"long-range").MissileId);
        Assert.AreNotEqual("SIM-57R",MechMissileVariantCatalog.ResolveVariant("WP_L_4291",p,"SIM-57R").MissileId);
    }
    [TestCase("standard","SIM-29")] [TestCase("dogfight","SIM-29M")] [TestCase("long-range","SIM-57R")]
    public void SavedLegacySelectionMatchesMenuAndActualAmmo(string oldId,string expected)
    {
        var slot=MechPartSlot.LeftBackWeapon;string key="acunicombat.hangar.missile-variant."+(int)slot;
        bool existed=PlayerPrefs.HasKey(key);string previous=PlayerPrefs.GetString(key,"");
        try
        {
            PlayerPrefs.SetString(key,"AC6_NATIVE_WP_L_4290|"+oldId);
            string selected=MechLoadoutSelectionStore.LoadMissileVariantId(slot,"AC6_NATIVE_WP_L_4290","SIM-29");
            Assert.AreEqual(expected,selected);
            var p=MechMissileLauncherCatalog.CreateDefaultPerformance("WP_L_4290");
            Assert.AreEqual(expected,MechMissileVariantCatalog.ResolveVariant("WP_L_4290",p,selected).MissileId);
        }
        finally{if(existed)PlayerPrefs.SetString(key,previous);else PlayerPrefs.DeleteKey(key);PlayerPrefs.Save();}
    }
    [UnityTest] public IEnumerator EveryCompatibleAmmoReallyDeploysFromBothSides()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float previous=Time.captureDeltaTime,scale=Time.timeScale;Time.timeScale=1;Time.captureDeltaTime=.04f;
        var fixture=new MechEquipmentExecutionTests();var extra=new List<GameObject>();
        var seed=typeof(MechEquipmentExecutionTests).GetMethod("SeedAirMeasurements",BindingFlags.NonPublic|BindingFlags.Static);
        try
        {
            foreach(string n in new[]{"4300","4303","4290","4291","4292","4340"})foreach(string side in new[]{"L","R"})foreach(string ammo in MechMissileLauncherCatalog.Find("WP_L_"+n).CompatibleAmmo)
            {
                var part=Part(n,side);var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private).Invoke(fixture,new object[]{1,part});
                a.Loadout.Assignments.First(x=>x.SlotId=="gun.0").missileVariantId=ammo;
                var own=a.gameObject.AddComponent<CombatTestTarget>();own.ConfigureIdentity("DEFENDER",1);own.ConfigureSignatures(1000,1,1);
                var stats=a.RuntimeState.AggregatedStats;stats.radarRangeKm=200;stats.sensorPrecision=80;stats.fireControlQuality=85;stats.guidanceChannels=8;stats.trackCapacity=32;stats.radarRefreshHz=10;
                var radar=a.gameObject.AddComponent<MechRadarController>();radar.SetTrackOperationMode(true);var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
                bool abm=n.StartsWith("430");
                if(abm)
                {
                    var enemy=new GameObject("Enemy launcher");extra.Add(enemy);enemy.transform.position=Vector3.forward*15000;enemy.AddComponent<CombatTestTarget>().ConfigureIdentity("ENEMY",2);
                    var incoming=new GameObject("Actual incoming missile");extra.Add(incoming);incoming.transform.position=Vector3.forward*10000;incoming.transform.rotation=Quaternion.LookRotation(Vector3.back);
                    incoming.AddComponent<MechTestProjectile>().Configure(enemy.transform,null,default,TestWeaponKind.Missile,Vector3.back*700,1,.2f,60,accelerationMetersPerSecondSquared:1,newSeekerActivationDelay:.1f,newSeekerMaximumRange:30000,hasInitialGuidanceEstimate:true,initialGuidancePosition:a.transform.position,initialGuidanceUncertaintyMeters:1);
                }
                yield return null;
                while(radar.SimTimeSeconds<.12)yield return null;
                var contacts=new MissileTrackSnapshot[64];MechTestProjectile shot=null;double until=radar.SimTimeSeconds+15;float deadline=Time.realtimeSinceStartup+35;
                while(shot==null&&radar.SimTimeSeconds<until&&Time.realtimeSinceStartup<deadline)
                {
                    if(abm){int count=radar.CopyMissileTracks(contacts);for(int i=0;i<count;i++)if(contacts[i].IsWeaponsQuality&&contacts[i].Incoming&&!contacts[i].IsFriendly){weapons.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(contacts[i].TrackId));break;}}
                    else{seed.Invoke(null,new object[]{radar});if(radar.TryGetBestTrack(out var track)){radar.TryAssignFireControlTrack(track.TrackId);weapons.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(track.TrackId));}}
                    yield return null;shot=MechTestProjectile.Registry.FirstOrDefault(p=>p!=null&&p.IsOwnedBy(a.transform));
                }
                var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);
                Assert.IsNotNull(shot,n+side+" "+ammo+": "+string.Join(";",feedback.Select(e=>e.Phase+":"+e.Reason)));
                Assert.AreEqual(MechMissileAmmunitionCatalog.Create(ammo).maximumRangeMeters,(float)typeof(MechTestProjectile).GetField("maximumTravelDistance",Private).GetValue(shot));
                Assert.Greater(shot.GetComponentsInChildren<Renderer>().Length,0);
                var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);Assert.AreEqual(part.WeaponPerformance.magazineCapacity-1,snapshots[0].Rounds);
                TestContext.WriteLine(n+side+" "+ammo+" actual native deployment/release OK");
                foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);
                foreach(var o in extra)if(o!=null)UnityEngine.Object.DestroyImmediate(o);extra.Clear();fixture.Cleanup();yield return null;
            }
        }
        finally{foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);foreach(var o in extra)if(o!=null)UnityEngine.Object.DestroyImmediate(o);fixture.Cleanup();Time.captureDeltaTime=previous;Time.timeScale=scale;}
        yield return new ExitPlayMode();
    }
}
