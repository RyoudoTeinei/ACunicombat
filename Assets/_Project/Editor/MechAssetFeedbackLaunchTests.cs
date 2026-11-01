using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
public class MechAssetFeedbackLaunchTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Apply()
    {
        foreach(var guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            var expected=MechMissileLauncherCatalog.CreateDefaultPerformance(part.PartId);if(expected==null)continue;
            part.WeaponPerformance.reloadSeconds=expected.reloadSeconds;
            part.WeaponPerformance.coldLaunch=expected.coldLaunch;
            part.WeaponPerformance.warheadMassKg=expected.warheadMassKg;
            EditorUtility.SetDirty(part);
        }
        AssetDatabase.SaveAssets();
    }
    [Test] public void Left4250RequestDoesNotChangeOtherSide()
    {
        Assert.AreEqual(8,MechMissileLauncherCatalog.CreateDefaultPerformance("WP_L_4250").reloadSeconds);
        Assert.AreEqual(4,MechMissileLauncherCatalog.CreateDefaultPerformance("WP_R_4250").reloadSeconds);
        Assert.AreEqual(24,MechMissileAmmunitionCatalog.Create("DPL-15").warheadMassKg);
    }
    [UnityTest] public IEnumerator ActualLaunchersHonorColdEjectionAndHotStraightFlight()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float scale=Time.timeScale,capture=Time.captureDeltaTime;Time.timeScale=1;Time.captureDeltaTime=.02f;
        var fixture=new MechEquipmentExecutionTests();
        var assemble=typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private);
        var seed=typeof(MechEquipmentExecutionTests).GetMethod("SeedAirMeasurements",BindingFlags.NonPublic|BindingFlags.Static);
        try
        {
            foreach(string number in new[]{"4301","4302","4310","4230","4340","4290"})foreach(string side in new[]{"L","R"})
            {
                var part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_"+number)??Resources.Load<MechPartDefinition>("AC6Parts/AC6_NATIVE_WP_"+side+"_"+number);
                var a=(MechAssembler)assemble.Invoke(fixture,new object[]{1,part});
                var stats=a.RuntimeState.AggregatedStats;stats.radarRangeKm=30;stats.guidanceChannels=4;stats.trackCapacity=16;stats.sensorPrecision=38;stats.fireControlQuality=65;stats.radarRefreshHz=10;
                var radar=a.gameObject.AddComponent<MechRadarController>();radar.Configure(null,null);radar.SetTrackOperationMode(true);
                var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
                double deadline=Time.realtimeSinceStartupAsDouble+30;MechTestProjectile shot=null;
                while(shot==null&&Time.realtimeSinceStartupAsDouble<deadline)
                {
                    if(radar.SimTimeSeconds>.12){seed.Invoke(null,new object[]{radar});if(radar.TryGetBestTrack(out var t)){radar.TryAssignFireControlTrack(t.TrackId);weapons.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(t.TrackId));}}
                    yield return null;shot=MechTestProjectile.Registry.FirstOrDefault(p=>p!=null&&p.IsOwnedBy(a.transform));
                }
                Assert.IsNotNull(shot,part.PartId);
                bool cold=number=="4301"||number=="4302"||number=="4310";
                Assert.AreEqual(cold,(bool)Field(shot,"coldLaunchEnabled"),part.PartId);
                Vector3 axis=(Vector3)Field(shot,"coldLaunchAxis");
                if(cold)Assert.AreEqual(Mathf.Sin(15f*Mathf.Deg2Rad),Vector3.Dot(axis,a.transform.up),.002f,"Nonvertical cold launch must elevate 15 degrees");float mass=shot.CurrentMassKg,mainFuel=(float)Field(shot,"mainPropellantMassRemainingKg");bool ignited=false,turned=false;
                // Force an off-axis remembered observation so this exercises steering, not a straight-ahead special case.
                var remembered=(ACunicombat.Sensing.Contracts.TrackSnapshot)Field(shot,"cachedGuidanceTrack");
                remembered.Position+=new Unity.Mathematics.double3(3000,0,0);
                typeof(MechTestProjectile).GetField("cachedGuidanceTrack",Private).SetValue(shot,remembered);
                typeof(MechTestProjectile).GetField("externalGuidanceSuppressed",Private).SetValue(shot,true);
                deadline=Time.realtimeSinceStartupAsDouble+20;
                while(shot!=null&&(float)Field(shot,"missileFlightTime")<2.5f&&Time.realtimeSinceStartupAsDouble<deadline)
                {
                    float age=(float)Field(shot,"missileFlightTime");bool motor=(bool)Field(shot,"coldLaunchMotorIgnited");
                    if(cold&&!motor)Assert.AreEqual(mainFuel,(float)Field(shot,"mainPropellantMassRemainingKg"),.001f,"Cold ejection must not burn motor propellant");
                    if(!cold&&age<.98f){Assert.Less(Vector3.Angle(axis,shot.transform.forward),.05f,"Hot launch must retain tube axis for one second");if(age>.2f)Assert.Less(shot.CurrentMassKg,mass,"Hot motor burns during straight leg");}
                    if(age>1.1f&&Vector3.Angle(axis,shot.transform.forward)>.1f)turned=true;
                    ignited|=motor;yield return null;
                }
                Assert.IsNotNull(shot);Assert.IsTrue(ignited,part.PartId+" must ignite after separation");Assert.IsTrue(turned,part.PartId+" must resume guidance after clearance");
                TestContext.WriteLine(part.PartId+": "+(cold?"cold eject / align / ignite":"hot straight 1s / steer")+" verified");
                foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);fixture.Cleanup();yield return null;
            }
        }
        finally{fixture.Cleanup();Time.timeScale=scale;Time.captureDeltaTime=capture;}
        yield return new ExitPlayMode();
    }
    static object Field(object value,string name)=>value.GetType().GetField(name,Private).GetValue(value);
}
