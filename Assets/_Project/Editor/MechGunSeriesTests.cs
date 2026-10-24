using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public class MechGunSeriesTests
{
    static MechPartDefinition Part(string n,string side)=>Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_"+n);
    [TestCase("1630","1630",23)] [TestCase("1710","1610",25)] [TestCase("1620","1630",23)] [TestCase("1730","1720",25)]
    public void SavedVariantsInheritFamilyPerformance(string n,string parent,int caliber)
    {
        foreach(string side in new[]{"L","R"})
        {
            var part=Part(n,side);Assert.IsNotNull(part);Assert.IsNotNull(part.ModelPrefab);
            var p=part.WeaponPerformance;var baseline=AC6TestWeaponProfileFactory.Create("WP_L_"+parent);
            Assert.IsTrue(p.IsConfigured);Assert.AreEqual(caliber,p.projectileCaliberMillimeters);
            Assert.AreEqual(baseline.weaponSeriesId,p.weaponSeriesId);
            Assert.AreEqual(JsonUtility.ToJson(AC6TestWeaponProfileFactory.Create(part.PartId)),JsonUtility.ToJson(p));
            if(n=="1620")
            {
                Assert.AreEqual(180,p.roundsPerMinute);Assert.Greater(p.roundsPerMinute,baseline.roundsPerMinute);
                Assert.AreEqual(14000,p.maximumRangeMeters);Assert.Less(p.maximumRangeMeters,baseline.maximumRangeMeters);
                Assert.AreEqual(baseline.baseDispersionMilliradians*1.5f,p.baseDispersionMilliradians);
                Assert.AreEqual(baseline.maximumDispersionMilliradians*1.5f,p.maximumDispersionMilliradians);
                Assert.AreEqual(p.maximumRangeMeters,p.projectileLifetimeSeconds*p.muzzleVelocityMetersPerSecond,.01f);
            }
            else
            {
                Assert.AreEqual(baseline.roundsPerMinute,p.roundsPerMinute);Assert.AreEqual(baseline.maximumRangeMeters,p.maximumRangeMeters);
                Assert.AreEqual(baseline.baseDispersionMilliradians,p.baseDispersionMilliradians);Assert.AreEqual(baseline.maximumDispersionMilliradians,p.maximumDispersionMilliradians);
            }
            if(n=="1620"||n=="1630"){Assert.AreEqual(24,p.magazineCapacity);Assert.AreEqual(8,p.magazineCount);}
        }
    }
    [UnityTest] public IEnumerator BothSidesAndCommandSourcesFireHitAndReload()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float scale=Time.timeScale,step=Time.captureDeltaTime;Time.timeScale=1;Time.captureDeltaTime=.02f;
        var fixture=new MechEquipmentExecutionTests();GameObject victimObject=null;
        try
        {
            foreach(string n in new[]{"1710","1620","1630","1730"})foreach(string side in new[]{"L","R"})foreach(var source in new[]{MechActionSource.Player,MechActionSource.AI})
            {
                var part=Part(n,side);var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(fixture,new object[]{1,part});
                var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();yield return null;
                victimObject=GameObject.CreatePrimitive(PrimitiveType.Cube);victimObject.transform.position=Vector3.forward*100;victimObject.transform.localScale=Vector3.one*20;
                var victim=victimObject.AddComponent<CombatTestTarget>();victim.ConfigureIdentity("TARGET",2);victim.Configure(null,100000,false,0,0);
                Physics.SyncTransforms();float health=victim.Health;
                Assert.IsTrue(weapons.CommandFireSlot("gun.0",source));weapons.Actions.Tick(Time.timeAsDouble);
                var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
                Assert.AreEqual(part.WeaponPerformance.magazineCapacity-1,snapshots.Single(x=>x.SlotId=="gun.0").Rounds,n+side+source);
                Assert.IsTrue(MechTestProjectile.Registry.Any(p=>p!=null&&p.IsOwnedBy(a.transform)),"Must spawn actual projectile");
                double deadline=Time.timeAsDouble+3;float realDeadline=Time.realtimeSinceStartup+10;
                while(victim.Health>=health&&Time.timeAsDouble<deadline&&Time.realtimeSinceStartup<realDeadline)yield return null;
                Assert.Less(victim.Health,health,n+side+source+" must physically hit target");
                Assert.IsTrue(weapons.CommandReloadSlot("gun.0",source));weapons.Actions.Tick(Time.timeAsDouble);
                deadline=Time.timeAsDouble+part.WeaponPerformance.reloadSeconds+1;realDeadline=Time.realtimeSinceStartup+20;
                while(Time.timeAsDouble<deadline&&Time.realtimeSinceStartup<realDeadline)yield return null;
                weapons.CopyEquipmentWeaponSnapshots(snapshots);var snapshot=snapshots.Single(x=>x.SlotId=="gun.0");
                Assert.AreEqual(part.WeaponPerformance.magazineCapacity,snapshot.Rounds);Assert.AreEqual(part.WeaponPerformance.magazineCount-1,snapshot.Magazines);
                TestContext.WriteLine(n+side+" "+source+": real shot, damage, reload OK");
                foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)Object.DestroyImmediate(p.gameObject);
                Object.DestroyImmediate(victimObject);fixture.Cleanup();yield return null;
            }
        }
        finally{if(victimObject!=null)Object.DestroyImmediate(victimObject);foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)Object.DestroyImmediate(p.gameObject);fixture.Cleanup();Time.timeScale=scale;Time.captureDeltaTime=step;}
        yield return new ExitPlayMode();
    }
}
