using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
public class MechMissileMagazineTimingTests
{
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    public static void Apply()
    {
        foreach(var guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));var d=MechMissileLauncherCatalog.Find(part.PartId);if(d==null)continue;
            part.WeaponPerformance.reloadSeconds=d.ReloadSeconds;part.WeaponPerformance.missileLaunchCycleSeconds=d.LaunchCycleSeconds;EditorUtility.SetDirty(part);
        }
        AssetDatabase.SaveAssets();
    }
    [TestCase("4250")] [TestCase("4251")] [TestCase("4252")] [TestCase("4290")] [TestCase("4291")] [TestCase("4292")]
    [TestCase("4300")] [TestCase("4301")] [TestCase("4302")] [TestCase("4303")] [TestCase("4310")] [TestCase("4230")] [TestCase("4340")]
    public void EveryLauncherWaitsForReloadAndPreservesInventory(string n)
    {
        var d=MechMissileLauncherCatalog.Find("WP_L_"+n);var banks=new MechMissileBanks(d,MechMissileVariantCatalog.GetDefaultVariantId("WP_L_"+n,MechMissileLauncherCatalog.CreateDefaultPerformance("WP_L_"+n)));
        int total=banks.Total;for(int i=0;i<d.GroupCapacity;i++)Assert.IsTrue(banks.Consume(0,10));
        Assert.AreEqual(0,banks.Banks[0].Rounds);Assert.AreEqual(d.GroupCapacity,banks.Banks[0].Loading);Assert.AreEqual(total-d.GroupCapacity,banks.Total);
        banks.Tick(10+d.ReloadSeconds-.001);Assert.AreEqual(0,banks.Banks[0].Rounds);Assert.IsFalse(banks.Consume(0,10+d.ReloadSeconds-.001));
        banks.Tick(10+d.ReloadSeconds);Assert.AreEqual(d.GroupCapacity,banks.Banks[0].Rounds);Assert.AreEqual(total-d.GroupCapacity,banks.Total);
        double now=20;int consumed=d.GroupCapacity;
        while(banks.Total>0){banks.Tick(now);int index=banks.LongestLoadedBank();if(index>=0){Assert.IsTrue(banks.Consume(index,now));consumed++;}now+=5;Assert.Less(now,1000);}
        Assert.AreEqual(total,consumed);banks.Tick(2000);Assert.AreEqual(0,banks.Total);Assert.AreEqual(-1,banks.LongestLoadedBank());
        Assert.Greater(d.LaunchCycleSeconds,0);Assert.AreEqual(n=="4250"?8:4,d.ReloadSeconds);
    }
    [Test] public void PerLauncherReloadDurationIsActuallyUsed()
    {
        var d=new MechMissileLauncherDefinition("test","test",2,1,2,"SIM-29"){ReloadSeconds=7};var banks=new MechMissileBanks(d,"SIM-29");
        banks.Consume(0,1);banks.Consume(0,1);banks.Tick(7.99);Assert.AreEqual(0,banks.Loaded);banks.Tick(8);Assert.AreEqual(2,banks.Loaded);
    }
    [Test] public void LaunchCycleSettingControlsAnimationSpeedIndependentlyOfReload()
    {
        var fixture=new MechEquipmentExecutionTests();var d=MechMissileLauncherCatalog.Find("WP_L_4310");float old=d.LaunchCycleSeconds;
        try
        {
            var part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_L_4310");
            var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private).Invoke(fixture,new object[]{1,part});
            var record=a.RuntimeState.AssembledParts.First(r=>r.SlotId=="gun.0");
            var normal=new MechWeaponDeployment(record);
            float normalSpeed=(float)typeof(MechWeaponDeployment).GetField("cyclePlaybackSpeed",Private).GetValue(normal);
            Assert.AreEqual(1,normalSpeed,.001f,"Initial cycle should preserve native animation duration");
            d.LaunchCycleSeconds=old*2;var slower=new MechWeaponDeployment(record);
            var phase=typeof(MechWeaponDeployment).GetNestedType("Phase",BindingFlags.NonPublic);
            Assert.IsTrue((bool)typeof(MechWeaponDeployment).GetMethod("Play",Private).Invoke(slower,new object[]{"a000_800120",Enum.Parse(phase,"Opening")}));
            var player=record.instance.GetComponentInChildren<MechEquipmentAnimationPlayer>();
            Assert.AreEqual(normalSpeed*.5f,(float)typeof(MechEquipmentAnimationPlayer).GetField("playbackSpeed",Private).GetValue(player),.001f);
            Assert.AreEqual(4,d.ReloadSeconds,"Changing firing cycle must not change magazine reload");
        }
        finally{d.LaunchCycleSeconds=old;fixture.Cleanup();}
    }
    [UnityTest] public IEnumerator RealLauncherCannotFireDuringMagazineReload()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float scale=Time.timeScale,step=Time.captureDeltaTime;Time.timeScale=1;Time.captureDeltaTime=.02f;
        var fixture=new MechEquipmentExecutionTests();var seed=typeof(MechEquipmentExecutionTests).GetMethod("SeedAirMeasurements",BindingFlags.NonPublic|BindingFlags.Static);
        try
        {
            foreach(string side in new[]{"L","R"})
            {
                var part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_4310");
                var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private).Invoke(fixture,new object[]{1,part});
                var stats=a.RuntimeState.AggregatedStats;stats.radarRangeKm=30;stats.guidanceChannels=4;stats.trackCapacity=16;stats.sensorPrecision=38;stats.fireControlQuality=65;stats.radarRefreshHz=10;
                var radar=a.gameObject.AddComponent<MechRadarController>();radar.Configure(null,null);radar.SetTrackOperationMode(true);var w=a.gameObject.AddComponent<MechTestWeaponSystem>();
                while(radar.SimTimeSeconds<.12)yield return null;
                var banks=new List<MechMissileBankSnapshot>();double emptiedAt=0;int shots=0;
                float deadline=Time.realtimeSinceStartup+60;
                while(shots<2&&Time.realtimeSinceStartup<deadline)
                {
                    seed.Invoke(null,new object[]{radar});if(radar.TryGetBestTrack(out var t)){radar.TryAssignFireControlTrack(t.TrackId);w.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(t.TrackId));}
                    yield return null;
                    foreach(var shot in MechTestProjectile.Registry.ToArray())if(shot!=null&&shot.IsOwnedBy(a.transform)){shots++;emptiedAt=Time.timeAsDouble;UnityEngine.Object.DestroyImmediate(shot.gameObject);}
                }
                Assert.AreEqual(2,shots);w.CopyMissileBanks("gun.0",banks,out _,out int total);Assert.AreEqual(0,banks[0].Rounds);Assert.AreEqual(2,banks[0].LoadingRounds);Assert.AreEqual(6,total);
                double resume=banks[0].ReloadCompleteTime;Assert.That(resume-emptiedAt,Is.InRange(3.9,4.01));
                while(Time.timeAsDouble<resume-.06)
                {
                    seed.Invoke(null,new object[]{radar});if(radar.TryGetBestTrack(out var t))w.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(t.TrackId));
                    yield return null;Assert.IsFalse(MechTestProjectile.Registry.Any(p=>p!=null&&p.IsOwnedBy(a.transform)),"Cannot launch from empty/reloading chamber");
                }
                var events=new List<MechActionFeedback>();w.Actions.CopyEvents(events);Assert.IsTrue(events.Any(e=>e.Reason=="MissileMagazineReloading"));
                while(Time.timeAsDouble<resume+.02)yield return null;
                w.CopyMissileBanks("gun.0",banks,out _,out total);Assert.AreEqual(2,banks[0].Rounds);Assert.AreEqual(6,total);
                deadline=Time.realtimeSinceStartup+15;bool fired=false;
                while(!fired&&Time.realtimeSinceStartup<deadline)
                {
                    seed.Invoke(null,new object[]{radar});if(radar.TryGetBestTrack(out var t)){radar.TryAssignFireControlTrack(t.TrackId);w.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(t.TrackId));}yield return null;
                    fired=MechTestProjectile.Registry.Any(p=>p!=null&&p.IsOwnedBy(a.transform));
                }
                Assert.IsTrue(fired);w.CopyMissileBanks("gun.0",banks,out _,out total);Assert.AreEqual(5,total);
                TestContext.WriteLine(side+": real 2-shot depletion, 4-second blocked reload, third launch after reload passed");
                foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);fixture.Cleanup();yield return null;
            }
        }
        finally{foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);fixture.Cleanup();Time.timeScale=scale;Time.captureDeltaTime=step;}
        yield return new ExitPlayMode();
    }
}
