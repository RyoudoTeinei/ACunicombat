using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
public class MechJianweiTests
{
    static List<InterceptorEngagementReport> outcomes;
    static void RecordOutcome(InterceptorEngagementReport report)=>outcomes?.Add(report);
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static MechMissileBanks Banks(string n,string selection)=>new(MechMissileLauncherCatalog.Find("WP_L_"+n),selection);
    [Test] public void AmmunitionIsIndependentOfLauncherCapacity()
    {
        Assert.AreEqual(47000,MechMissileAmmunitionCatalog.Create("JW7").maximumRangeMeters);
        Assert.AreEqual(64000,MechMissileAmmunitionCatalog.Create("JW7-ER").maximumRangeMeters);
        Assert.AreEqual(0,MechMissileAmmunitionCatalog.Create("JW7").magazineCapacity);
        Assert.AreEqual(24,Banks("4250","jw7-er").Total);Assert.AreEqual(8,Banks("4251","jw7").Total);
        var b=Banks("4252","jw7-mixed");Assert.AreEqual(8,b.Loaded);Assert.AreEqual(4,b.Reserve);Assert.AreEqual(12,b.Total);
    }
    [Test] public void IncompatibleExtendedRangeAmmunitionCannotBeLoaded()
    {
        var d=MechMissileLauncherCatalog.Find("WP_L_4251");Assert.IsNull(MechMissileLauncherCatalog.Compose(d,"JW7-ER"));
        Assert.AreEqual("JW7",Banks("4251","jw7-er").Banks[0].Ammo);
        Assert.AreEqual(1,MechMissileVariantCatalog.GetVariantCount("WP_L_4251",MechMissileLauncherCatalog.CreateDefaultPerformance("WP_L_4251")));
    }
    [Test] public void SelectionUsesShortestSufficientRangeAndNeverReloadsErFromStandardReserve()
    {
        var b=Banks("4252","jw7-mixed");Assert.AreEqual(1,b.Select(20000));Assert.AreEqual(0,b.Select(50000));Assert.AreEqual(-1,b.Select(65000));
        for(int i=0;i<4;i++)Assert.IsTrue(b.Consume(0,i));
        Assert.AreEqual(4,b.Loaded);Assert.AreEqual(4,b.Loading);Assert.AreEqual(0,b.Reserve);Assert.AreEqual(8,b.Total);
        Assert.AreEqual(-1,b.Select(50000));Assert.AreEqual(1,b.Select(20000));
        Assert.IsTrue(b.Consume(1,3.2));Assert.AreEqual(3,b.Banks[1].Rounds,"Other group keeps firing while the first reloads.");
        b.Tick(6.9);Assert.AreEqual(0,b.Banks[0].Rounds);
        b.Tick(7);Assert.AreEqual(4,b.Banks[0].Rounds);Assert.AreEqual("JW7",b.Banks[0].Ammo);Assert.AreEqual(7,b.Total);
        for(int i=0;i<4;i++)b.Consume(0,8+i);for(int i=0;i<3;i++)b.Consume(1,12+i);
        b.Tick(100);Assert.AreEqual(0,b.Total);Assert.AreEqual(-1,b.Select(1));
    }
    [Test] public void OrdinaryGroupMayReloadWithoutTouchingLoadedErGroup()
    {
        var b=Banks("4252","jw7-mixed");for(int i=0;i<4;i++)b.Consume(1,0);
        Assert.AreEqual(4,b.Banks[0].Rounds);Assert.AreEqual("JW7-ER",b.Banks[0].Ammo);
        b.Tick(4);Assert.AreEqual(4,b.Banks[1].Rounds);Assert.AreEqual(8,b.Total);
    }
    [Test] public void RepeatedReadAndTickCannotCreateExtraRounds()
    {
        var b=Banks("4252","jw7-mixed");for(int i=0;i<4;i++)b.Consume(0,0);
        for(int i=0;i<100;i++){b.Select(30000);b.Tick(10);}
        Assert.AreEqual(8,b.Total);Assert.AreEqual(0,b.Reserve);Assert.AreEqual(0,b.Loading);
    }
    [Test] public void CorrectedModelMappingRestores4320()
    {
        Assert.AreEqual("WP_L_4310",AC6LauncherVisualMapping.SourceKey("WP_L_4302"));
        Assert.AreEqual("WP_R_4302",AC6LauncherVisualMapping.SourceKey("WP_R_4310"));
        Assert.AreEqual("WP_L_4320",AC6LauncherVisualMapping.SourceKey("WP_L_4320"));
    }
    [Test] public void SixSavedJianweiLaunchersHaveMatchingAnimationsAndCapacity()
    {
        foreach(string n in new[]{"4250","4251","4252"})foreach(string side in new[]{"L","R"})
        {
            var part=Load(n,side);Assert.IsNotNull(part);
            Assert.AreEqual(JsonUtility.ToJson(MechMissileLauncherCatalog.CreateDefaultPerformance(part.PartId)),JsonUtility.ToJson(part.WeaponPerformance));
            Assert.IsTrue(part.EquipmentAnimations.Any(a=>a.ActionId=="a000_800210"));Assert.IsTrue(part.WeaponPerformance.verticalColdLaunch);
        }
    }
    static MechPartDefinition Load(string n,string side="L") =>Resources.Load<MechPartDefinition>((n=="4250"&&side=="L"?"AC6Parts/AC6_NATIVE_WP_":"AC6Parts/AC6_FULL_WP_")+side+"_"+n);

    [TestCase("JW7",30)] [TestCase("JW7",60)] [TestCase("JW7-ER",30)] [TestCase("JW7-ER",60)]
    public void PhysicalAmmoEnvelope(string ammo,int hz)=>MechLongRangeMissileTests.AssertFlightEnvelope(MechMissileAmmunitionCatalog.Create(ammo),ammo,hz);

    [Test] public void HangarSelectionRoundTripsWithoutChangingUserPreferences()
    {
        var saved=new Dictionary<string,string>();
        foreach(MechPartSlot s in Enum.GetValues(typeof(MechPartSlot))){string k="acunicombat.hangar.missile-variant."+(int)s;if(PlayerPrefs.HasKey(k))saved[k]=PlayerPrefs.GetString(k);}
        try
        {
            var launchers=new Dictionary<MechPartSlot,string>{{MechPartSlot.LeftBackWeapon,"AC6_NATIVE_WP_L_4250"},{MechPartSlot.RightBackWeapon,"AC6_FULL_WP_R_4252"}};
            MechLoadoutSelectionStore.SaveMissileVariants(launchers,new Dictionary<MechPartSlot,string>{{MechPartSlot.LeftBackWeapon,"jw7"},{MechPartSlot.RightBackWeapon,"jw7-mixed"}});
            foreach(var e in launchers)
            {string v=MechLoadoutSelectionStore.LoadMissileVariantId(e.Key,e.Value,"missing");Assert.AreEqual(e.Key==MechPartSlot.LeftBackWeapon?"jw7":"jw7-mixed",v);}
            var invalid=MechMissileVariantCatalog.ResolveVariant("WP_L_4251",MechMissileLauncherCatalog.CreateDefaultPerformance("WP_L_4251"),"jw7-er");
            Assert.AreEqual("jw7",invalid.VariantId);Assert.AreEqual(47000,invalid.Performance.maximumRangeMeters);
        }
        finally
        {
            foreach(MechPartSlot s in Enum.GetValues(typeof(MechPartSlot))){string k="acunicombat.hangar.missile-variant."+(int)s;if(saved.TryGetValue(k,out string value))PlayerPrefs.SetString(k,value);else PlayerPrefs.DeleteKey(k);}
            PlayerPrefs.Save();
        }
    }

    [UnityTest] public IEnumerator AllSixJianweiLaunchersUseRealColdLaunch()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float previous=Time.captureDeltaTime,scale=Time.timeScale;Time.timeScale=1;Time.captureDeltaTime=.04f;
        var fixture=new MechEquipmentExecutionTests();var extra=new List<GameObject>();
        try
        {
            foreach(string n in new[]{"4250","4251","4252"})foreach(string side in new[]{"L","R"})
            {
                var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private).Invoke(fixture,new object[]{1,Load(n,side)});
                var own=a.gameObject.AddComponent<CombatTestTarget>();own.ConfigureIdentity("DEFENDER",1);own.ConfigureSignatures(1000,1,1);
                var stats=a.RuntimeState.AggregatedStats;stats.radarRangeKm=200;stats.sensorPrecision=80;stats.fireControlQuality=85;stats.guidanceChannels=8;stats.trackCapacity=32;stats.radarRefreshHz=10;
                var radar=a.gameObject.AddComponent<MechRadarController>();var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
                var enemy=new GameObject("Enemy launcher");extra.Add(enemy);enemy.transform.position=Vector3.forward*15000;enemy.AddComponent<CombatTestTarget>().ConfigureIdentity("ENEMY",2);
                var incoming=new GameObject("Actual incoming missile");extra.Add(incoming);incoming.transform.position=Vector3.forward*12000;incoming.transform.rotation=Quaternion.LookRotation(Vector3.back);
                incoming.AddComponent<MechTestProjectile>().Configure(enemy.transform,null,default,TestWeaponKind.Missile,Vector3.back*700,1,.2f,60,accelerationMetersPerSecondSquared:1,newSeekerActivationDelay:.1f,newSeekerMaximumRange:30000,hasInitialGuidanceEstimate:true,initialGuidancePosition:a.transform.position,initialGuidanceUncertaintyMeters:1);
                var contacts=new MissileTrackSnapshot[64];MechTestProjectile shot=null;double until=radar.SimTimeSeconds+12;float deadline=Time.realtimeSinceStartup+30;bool silenceChecked=false;
                while(shot==null&&radar.SimTimeSeconds<until&&Time.realtimeSinceStartup<deadline)
                {
                    int count=radar.CopyMissileTracks(contacts);
                    for(int i=0;i<count;i++)if(contacts[i].IsWeaponsQuality&&contacts[i].Incoming&&!contacts[i].IsFriendly)
                    {
                        if(n=="4251"&&side=="L"&&!silenceChecked)
                        {
                            var policy=radar.DataLinkPolicy;while(radar.DataLinkPolicy!=MechDataLinkPolicy.Silent)radar.CycleDataLinkPolicy();
                            weapons.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(contacts[i].TrackId));weapons.Actions.Tick(Time.timeAsDouble);
                            var check=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(check);Assert.AreEqual(4,check[0].Rounds);
                            var events=new List<MechActionFeedback>();weapons.Actions.CopyEvents(events);Assert.IsTrue(events.Any(e=>e.Reason=="InterceptorGuidanceUnavailable"));
                            while(radar.DataLinkPolicy!=policy)radar.CycleDataLinkPolicy();silenceChecked=true;
                        }
                        weapons.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(contacts[i].TrackId));break;
                    }
                    yield return null;foreach(var p in MechTestProjectile.Registry)if(p!=null&&p.IsOwnedBy(a.transform)){shot=p;break;}
                }
                Assert.IsNotNull(shot,n+side+" must actually deploy and launch");
                Assert.IsTrue((bool)typeof(MechTestProjectile).GetField("coldLaunchEnabled",Private).GetValue(shot));
                Assert.AreEqual(n=="4250"?64000:47000,(float)typeof(MechTestProjectile).GetField("maximumTravelDistance",Private).GetValue(shot));
                var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
                Assert.AreEqual(MechMissileLauncherCatalog.Find("WP_L_"+n).LoadedCapacity-1,snapshots[0].Rounds);
                TestContext.WriteLine(n+side+" cold launch OK | "+weapons.GetMissileBankStatus("gun.0"));
                foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);
                foreach(var o in extra)if(o!=null)UnityEngine.Object.DestroyImmediate(o);extra.Clear();fixture.Cleanup();yield return null;
            }
        }
        finally
        {foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);foreach(var o in extra)if(o!=null)UnityEngine.Object.DestroyImmediate(o);fixture.Cleanup();Time.captureDeltaTime=previous;Time.timeScale=scale;}
        yield return new ExitPlayMode();
    }

    [UnityTest] public IEnumerator RealRadarIncomingMissilesChooseErThenStandardFrom4252()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float previous=Time.captureDeltaTime,scale=Time.timeScale;Time.timeScale=1;Time.captureDeltaTime=.04f;
        var fixture=new MechEquipmentExecutionTests();var objects=new List<GameObject>();
        outcomes=new List<InterceptorEngagementReport>();MechTestProjectile.InterceptorEngagementResolved+=RecordOutcome;
        try
        {
            var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private).Invoke(fixture,new object[]{1,Load("4252")});
            var own=a.gameObject.AddComponent<CombatTestTarget>();own.ConfigureIdentity("DEFENDER",1);own.ConfigureSignatures(1000,1,1);own.Configure(null,100000,false,0,0);
            var stats=a.RuntimeState.AggregatedStats;stats.radarRangeKm=200;stats.sensorPrecision=80;stats.fireControlQuality=85;stats.guidanceChannels=8;stats.trackCapacity=32;stats.radarRefreshHz=10;
            var radar=a.gameObject.AddComponent<MechRadarController>();radar.SetTrackOperationMode(true);
            var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
            var enemy=new GameObject("Threat launcher");objects.Add(enemy);enemy.transform.position=Vector3.forward*60000;enemy.AddComponent<CombatTestTarget>().ConfigureIdentity("ENEMY",2);
            var threats=new MechTestProjectile[2];
            for(int i=0;i<2;i++)
            {
                var go=new GameObject(i==0?"Far real incoming":"Near real incoming");objects.Add(go);go.transform.position=new Vector3(i*1000,0,i==0?63000:35000);go.transform.rotation=Quaternion.LookRotation(-go.transform.position);
                var p=go.AddComponent<MechTestProjectile>();threats[i]=p;
                p.Configure(enemy.transform,null,default,TestWeaponKind.Missile,go.transform.forward*1000,1,.2f,150,accelerationMetersPerSecondSquared:1,newSeekerActivationDelay:.1f,newSeekerMaximumRange:100000,
                    hasInitialGuidanceEstimate:true,initialGuidancePosition:a.transform.position,initialGuidanceUncertaintyMeters:1);
            }
            var contacts=new MissileTrackSnapshot[64];TrackId far=default,near=default;
            double start=radar.SimTimeSeconds;float deadline=Time.realtimeSinceStartup+30;
            while((!far.IsValid||!near.IsValid)&&radar.SimTimeSeconds-start<12&&Time.realtimeSinceStartup<deadline)
            {
                yield return null;int count=radar.CopyMissileTracks(contacts);
                for(int i=0;i<count;i++)if(contacts[i].IsWeaponsQuality&&contacts[i].Incoming&&!contacts[i].IsFriendly)
                {double range=Unity.Mathematics.math.length(contacts[i].Position);if(range>47000)far=contacts[i].TrackId;else near=contacts[i].TrackId;}
            }
            Assert.IsTrue(far.IsValid&&near.IsValid,"Normal radar must establish both real incoming missile tracks.");
            foreach(var id in new[]{far,far,far,far,near})
            {
                var before=MechTestProjectile.Registry.ToHashSet();bool accepted=false;MechTestProjectile shot=null;deadline=Time.realtimeSinceStartup+30;double until=radar.SimTimeSeconds+10;
                while(shot==null&&radar.SimTimeSeconds<until&&Time.realtimeSinceStartup<deadline)
                {
                    var current=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(current);
                    if(current.Count>0&&!current[0].Pending)accepted=weapons.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(id));
                    yield return null;shot=MechTestProjectile.Registry.FirstOrDefault(p=>p!=null&&!before.Contains(p)&&p.IsOwnedBy(a.transform)&&p.GuidanceTrackId.Equals(id));
                }
                var events=new List<MechActionFeedback>();weapons.Actions.CopyEvents(events);
                Assert.IsNotNull(shot,string.Join(";",events.Select(e=>e.Phase+":"+e.Reason)));
                float range=(float)typeof(MechTestProjectile).GetField("maximumTravelDistance",Private).GetValue(shot);
                Assert.AreEqual(id.Equals(far)?64000:47000,range,"Actual spawned missile must use selected ammunition.");
                TestContext.WriteLine("FIRED "+range+" "+weapons.GetMissileBankStatus("gun.0"));
            }
            var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
            Assert.That(snapshots[0].Rounds,Is.InRange(3,7));Assert.That(weapons.GetMissileBankStatus("gun.0"),Does.Contain("总7"));
            deadline=Time.realtimeSinceStartup+60;double end=radar.SimTimeSeconds+60;
            while(outcomes.Count(r=>r.Outcome==InterceptorEngagementOutcome.Destroyed)<2&&radar.SimTimeSeconds<end&&Time.realtimeSinceStartup<deadline)yield return null;
            foreach(var r in outcomes)TestContext.WriteLine("OUTCOME "+r.TargetTrackId+" "+r.Outcome+" "+r.Reason);
            Assert.GreaterOrEqual(outcomes.Count(r=>r.Outcome==InterceptorEngagementOutcome.Destroyed),2,"Real interceptor warheads must destroy both incoming missiles.");
        }
        finally
        {
            MechTestProjectile.InterceptorEngagementResolved-=RecordOutcome;
            foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);
            foreach(var o in objects)if(o!=null)UnityEngine.Object.DestroyImmediate(o);fixture.Cleanup();Time.captureDeltaTime=previous;Time.timeScale=scale;
        }
        yield return new ExitPlayMode();
    }
}
