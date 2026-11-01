using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public sealed class MechEquipmentExecutionTests
{
    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    private T Own<T>(T o) where T:UnityEngine.Object { owned.Add(o); return o; }
    [TearDown] public void Cleanup() { foreach(var o in owned) if(o!=null) UnityEngine.Object.DestroyImmediate(o); owned.Clear(); }
    private MechPartDefinition Gun(MechWeaponRole role=MechWeaponRole.AutomaticCannon, float cost=0)
    {
        var p=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        p.EditorConfigure("test.gun","Test Gun",MechPartSlot.LeftHandWeapon,MechPartCategory.Weapon,null,new MechPartStats{massKg=10},null,
            newWeaponPerformance:new MechWeaponPerformance { configured=true,role=role,magazineCapacity=5,magazineCount=3,roundsPerMinute=600,
                reloadSeconds=1,muzzleVelocityMetersPerSecond=500,projectileLifetimeSeconds=10,maximumRangeMeters=5000,directHitDamage=10,energyPerShotMj=cost });
        return p;
    }
    private MechAssembler Assemble(int count,MechPartDefinition p)
    {
        var layout=Own(ScriptableObject.CreateInstance<MechPlatformDefinition>());layout.platformId="execution-test";
        var loadout=Own(ScriptableObject.CreateInstance<MechLoadoutDefinition>());var assignments=new List<MechPartAssignment>();
        for(int i=0;i<count;i++)
        {layout.slots.Add(new MechSlotDefinition{id="gun."+i,weaponGroup="primary",hardpointType=p.MountClass,localPosition=new Vector3(i*2,0,0)});assignments.Add(new MechPartAssignment{slotId="gun."+i,part=p});}
        if(p.WeaponPerformance.IsGuidedMissile)
        {
            foreach(var role in new[]{MechPartSlot.FireControlSystem,MechPartSlot.DataLinkTerminal})
            {
                var support=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
                support.EditorConfigure("test."+role,role.ToString(),role,MechPartCategory.Internal,null,new MechPartStats{massKg=1},null,
                    newDataLinkPerformance:MechDataLinkPerformance.CreateStandardFallback());
                string id="support."+role;layout.slots.Add(new MechSlotDefinition{id=id,legacyRole=role,category=MechPartCategory.Internal,hardpointType=MechHardpointType.None});
                assignments.Add(new MechPartAssignment{slotId=id,part=support});
            }
        }
        loadout.ConfigureRuntime("test","Test",assignments);loadout.ConfigureLayout(layout);
        var owner=Own(new GameObject("EquipmentTest"));var a=owner.AddComponent<MechAssembler>();a.Loadout=loadout;a.Assemble();return a;
    }
    [Test] public void PlayerRejectedSlotAndRepeatedPendingRequestHaveVisibleReasons()
    {
        var a=Assemble(1,Gun());var w=a.gameObject.AddComponent<MechTestWeaponSystem>();typeof(MechTestWeaponSystem).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(w,null);
        Assert.IsFalse(w.CommandFireSlot("missing",MechActionSource.Player));var log=a.GetComponent<MechOwnshipEventLog>();Assert.IsNotNull(log);StringAssert.Contains("不可用",log.Entries.Last().Text);
        Assert.IsTrue(w.CommandFireSlot("gun.0",MechActionSource.Player));Assert.IsFalse(w.CommandFireSlot("gun.0",MechActionSource.Player));StringAssert.Contains("已有请求",log.Entries.Last().Text);
    }
    [Test] public void InventoryBatchDoesNotCacheDisabledOrReplacedEquipmentAcrossCalls()
    {
        var assembler=Assemble(2,Gun());var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        typeof(MechTestWeaponSystem).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(weapons,null);
        var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
        Assert.AreEqual(2,snapshots.Count);Assert.IsTrue(snapshots.All(s=>s.Enabled));
        var record=assembler.GetComponent<MechRuntimeState>().AssembledParts.First(r=>r.SlotId=="gun.0");
        var oldId=record.InstanceId;record.instance.SetActive(false);weapons.CopyEquipmentWeaponSnapshots(snapshots);
        Assert.IsFalse(snapshots.First(s=>s.SlotId=="gun.0").Enabled);Assert.IsTrue(snapshots.First(s=>s.SlotId=="gun.1").Enabled);
        record.instance.SetActive(true);assembler.Assemble();weapons.CopyEquipmentWeaponSnapshots(snapshots);
        Assert.IsTrue(snapshots.All(s=>s.Enabled));Assert.AreNotEqual(oldId,snapshots.First(s=>s.SlotId=="gun.0").InstanceId);
    }
    [UnityTest] public IEnumerator WeaponHeatUsesActualCommittedSlotAndRejectedFireDoesNotHeat()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Assemble(2,Gun());var heat=assembler.gameObject.AddComponent<MechThermalSignature>();
        var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        Assert.That(weapons.CommandFireSlot("gun.0",MechActionSource.Player),Is.True);weapons.Actions.Tick(Time.timeAsDouble);
        float fired=heat.GetEquipmentTemperature("gun.0");Assert.That(fired,Is.EqualTo(525));Assert.That(heat.GetEquipmentTemperature("gun.1"),Is.EqualTo(285));
        weapons.CommandFireSlot("gun.0",MechActionSource.Player);weapons.Actions.Tick(Time.timeAsDouble);
        Assert.That(heat.GetEquipmentTemperature("gun.0"),Is.EqualTo(fired));
        foreach(var projectile in UnityEngine.Object.FindObjectsByType<MechTestProjectile>())UnityEngine.Object.DestroyImmediate(projectile.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [Test] public void Deployment_UsesOriginalDelayAndSingleRelease()
    {
        var a=Assemble(1,Gun());var d=new MechWeaponDeployment(a.RuntimeState.AssembledParts[0]);
        Assert.That(d.Request(1),Is.True);d.Tick(1.17,false);Assert.That(d.Ready,Is.False);
        d.Tick(1.19,false);Assert.That(d.Ready,Is.True);d.Consume();Assert.That(d.Ready,Is.False);
        Assert.That(d.CanRequest,Is.True);
    }
    [Test] public void DeploymentCancellation_RemovesPendingRelease()
    {
        var a=Assemble(1,Gun());var d=new MechWeaponDeployment(a.RuntimeState.AssembledParts[0]);
        d.Request(0);d.Cancel();d.Tick(100,false);Assert.That(d.Ready,Is.False);Assert.That(d.CanRequest,Is.True);
    }
    [Test] public void CanonicalAnimation_OpensBeforeReleaseAndDoesNotRepeatMarker()
    {
        var p=Gun();var a=Assemble(1,p);var record=a.RuntimeState.AssembledParts[0];
        var player=record.instance.AddComponent<MechEquipmentAnimationPlayer>();
        var definitions=new List<MechEquipmentAnimationDefinition>();
        foreach(var id in new[]{"a000_800110","a000_800120","a000_800210","a000_800320","a000_800310"})
        {var clip=Own(new AnimationClip{legacy=true});clip.SetCurve("",typeof(Transform),"localPosition.x",AnimationCurve.Linear(0,0,1,0));definitions.Add(new MechEquipmentAnimationDefinition(id,clip));}
        player.Configure(definitions,"",false);var d=new MechWeaponDeployment(record);
        Assert.That(d.Request(0),Is.True);Assert.That(player.ActiveActionId,Is.EqualTo("a000_800110"));Assert.That(d.Ready,Is.False);
        player.StopPlayback();d.Tick(1,false);Assert.That(player.ActiveActionId,Is.EqualTo("a000_800120"));Assert.That(d.Ready,Is.False);
        player.StopPlayback();d.Tick(2,false);Assert.That(d.Ready,Is.True);Assert.That(player.ActiveActionId,Is.EqualTo("a000_800210"));
        d.Consume();player.StopPlayback();d.Tick(3,false);Assert.That(d.Ready,Is.False);Assert.That(player.ActiveActionId,Is.EqualTo("a000_800320"));
    }
    [UnityTest] public IEnumerator EightRealWeapons_SpendOnlyAvailableEnergyAndKeepIndependentAmmo()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var a=Assemble(8,Gun(cost:1));var motion=a.gameObject.AddComponent<MechFlightMotionController>();
        float initialize=motion.CurrentManeuverEnergy;
        typeof(MechFlightMotionController).GetField("currentManeuverEnergy",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(motion,3f);
        var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
        Assert.That(weapons.CommandFireGroup("primary",MechActionSource.Player),Is.EqualTo(8));
        weapons.Actions.Tick(Time.timeAsDouble);
        var states=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(states);
        var events=new List<MechActionFeedback>();weapons.Actions.CopyEvents(events);
        Assert.That(events.Count(x=>x.Phase==MechActionPhase.Succeeded),Is.EqualTo(3),string.Join(";",events.Select(x=>x.Reason)));
        Assert.That(states.Count(x=>x.Rounds==4),Is.EqualTo(3));Assert.That(states.Count(x=>x.Rounds==5),Is.EqualTo(5));
        Assert.That(motion.CurrentManeuverEnergy,Is.EqualTo(0).Within(0.0001));
        Assert.That(a,Is.Not.Null,"Assembler disappeared during fire");
        Assert.That(a.RuntimeState,Is.Not.Null,"Assembler lost runtime state during fire");
        Assert.That(a.RuntimeState.AssembledParts.All(x=>x!=null),Is.True,"Null assembly record");
        var ids=new HashSet<Guid>(states.Select(x=>x.InstanceId));
        var bullets=new List<MechTestProjectile>();
        foreach(var bullet in UnityEngine.Object.FindObjectsByType<MechTestProjectile>()) if(ids.Contains(bullet.SourceEquipmentId)) bullets.Add(bullet);
        Assert.That(bullets.Count,Is.EqualTo(3));Assert.That(bullets.Select(x=>x.SourceSlotId).Distinct().Count(),Is.EqualTo(3));
        foreach(var bullet in bullets) UnityEngine.Object.DestroyImmediate(bullet.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator PlayerAndAI_FireSameRealWeaponWithoutSharingAmmo()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var results=new List<int>();
        foreach(var source in new[]{MechActionSource.Player,MechActionSource.AI})
        {
            var a=Assemble(2,Gun());var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
            Assert.That(weapons.CommandFireSlot("gun.0",source),Is.True);weapons.Actions.Tick(Time.timeAsDouble);
            var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
            results.Add(snapshots.Single(x=>x.SlotId=="gun.0").Rounds);
            Assert.That(weapons.CommandReloadSlot("gun.0",source),Is.True);weapons.Actions.Tick(Time.timeAsDouble);
            Assert.That(weapons.CancelEquipmentAction("gun.0"),Is.True);weapons.CopyEquipmentWeaponSnapshots(snapshots);
            Assert.That(snapshots.Single(x=>x.SlotId=="gun.0").Rounds,Is.EqualTo(4));
            Assert.That(snapshots.Single(x=>x.SlotId=="gun.0").Magazines,Is.EqualTo(3));
            Assert.That(snapshots.Single(x=>x.SlotId=="gun.1").Rounds,Is.EqualTo(5));
            var events=new List<MechActionFeedback>();weapons.Actions.CopyEvents(events);Assert.That(events.Any(x=>x.Phase==MechActionPhase.Succeeded),Is.True,string.Join(";",events.Select(x=>x.Reason)));
        }
        CollectionAssert.AreEqual(new[]{4,4},results);
        foreach(var bullet in UnityEngine.Object.FindObjectsByType<MechTestProjectile>()) UnityEngine.Object.DestroyImmediate(bullet.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [Test] public void QueuedActionAgainstReplacedInstance_IsRejected()
    {
        var a=Assemble(1,Gun());var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
        Assert.That(weapons.CommandFireSlot("gun.0",MechActionSource.AI),Is.True);
        a.Assemble();weapons.Actions.Tick(Time.timeAsDouble);var events=new List<MechActionFeedback>();weapons.Actions.CopyEvents(events);
        Assert.That(events.Last().Phase,Is.EqualTo(MechActionPhase.Failed));Assert.That(weapons.Actions.ReservedEnergyMj,Is.Zero);
    }
    [Test] public void DisabledEquipment_CancelsBeforeSpawnWithoutDeductingAmmo()
    {
        var a=Assemble(1,Gun());var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
        weapons.CommandFireSlot("gun.0",MechActionSource.Player);a.RuntimeState.AssembledParts[0].instance.SetActive(false);
        weapons.Actions.Tick(Time.timeAsDouble);var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
        Assert.That(snapshots[0].Rounds,Is.EqualTo(5));Assert.That(snapshots[0].Pending,Is.False);
    }
    private static void SeedAirMeasurements(MechRadarController radar)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var driver=radar.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();
        var context=typeof(ACunicombat.Simulation.Driver.SimulationDriver).GetField("trackContext",flags).GetValue(driver);
        var manager=(ACunicombat.Tracking.RadarTrackManager)context.GetType().GetField("tracks",flags).GetValue(context);
        using var measurements=new Unity.Collections.NativeList<ACunicombat.Sensing.Contracts.Measurement>(1,Unity.Collections.Allocator.Temp);
        for(int i=0;i<3;i++)
        {
            double time=radar.SimTimeSeconds-(2-i)*.04;measurements.Clear();
            measurements.Add(new ACunicombat.Sensing.Contracts.Measurement {
                SensorId=driver.PrimaryRadarIdentity,Timestamp=time,Range=8000,Direction=new Unity.Mathematics.float3(0,0,1),
                RangeVar=4,AngleVar=0.000001f,VelocityVar=1,LosRateVar=0.000001f,SnrDb=30,
                ObjectClass=ACunicombat.Sensing.Contracts.TrackObjectClass.AirVehicle,ClassificationConfidence=1 });
            manager.Process(measurements,Unity.Mathematics.double3.zero,time);
        }
    }
    [UnityTest] public IEnumerator RealMissile_DeploysThenFiresAndSupportCancellationKeepsItAlive()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var part=Gun(MechWeaponRole.AirToAirMissile);part.WeaponPerformance.missileFlightProfile=MissileFlightProfile.AirToAirDefault;
        part.WeaponPerformance.missileVisualResourcePath="P3TestMissile";
        part.WeaponPerformance.missileVisualSourceForward=Vector3.forward;
        var a=Assemble(1,part);var stats=a.RuntimeState.AggregatedStats;
        stats.radarRangeKm=30;stats.guidanceChannels=2;stats.trackCapacity=16;stats.sensorPrecision=38;stats.fireControlQuality=65;stats.radarRefreshHz=2.5f;
        var radar=a.gameObject.AddComponent<MechRadarController>();radar.Configure(null,null);radar.SetTrackOperationMode(true);
        var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();yield return new WaitForSeconds(.2f);
        SeedAirMeasurements(radar);Assert.That(radar.TryGetBestTrack(out var track),Is.True);
        Assert.That(radar.TryAssignFireControlTrack(track.TrackId),Is.True);
        Assert.That(weapons.CommandFireSlot("gun.0",MechActionSource.AI,radar.Reference(track.TrackId)),Is.True);
        weapons.Actions.Tick(Time.timeAsDouble);
        var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
        Assert.That(snapshots[0].Rounds,Is.EqualTo(5),"Deployment must not spend a round");
        var events=new List<MechActionFeedback>();bool fired=false;float deadline=Time.realtimeSinceStartup+8;
        while(!fired && Time.realtimeSinceStartup<deadline)
        {
            yield return null;SeedAirMeasurements(radar);weapons.Actions.CopyEvents(events);
            foreach(var feedback in events) if(feedback.Phase==MechActionPhase.Succeeded) fired=true;
            if(events.Count>0 && events[events.Count-1].Phase==MechActionPhase.Failed) break;
        }
        Assert.That(fired,Is.True,string.Join(";",events.Select(x=>x.Phase+":"+x.Reason)));
        weapons.CopyEquipmentWeaponSnapshots(snapshots);Assert.That(snapshots[0].Rounds,Is.EqualTo(4));
        MechTestProjectile launched=null;
        foreach(var candidate in UnityEngine.Object.FindObjectsByType<MechTestProjectile>())
            if(candidate.SourceEquipmentId==snapshots[0].InstanceId) launched=candidate;
        Assert.That(launched,Is.Not.Null);Assert.That(launched.HasExternalGuidanceSupport,Is.True);
        Assert.That(weapons.CancelMissileSupport("gun.0"),Is.EqualTo(1));Assert.That(weapons.CancelMissileSupport("gun.0"),Is.Zero);
        Assert.That(launched.gameObject.activeSelf,Is.True);Assert.That(launched.HasExternalGuidanceSupport,Is.False);
        UnityEngine.Object.DestroyImmediate(launched.gameObject);Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator PassiveMissile_FiresWithOwnRadarAndUplinkOffThenSearchesAfterEmitterStops()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float oldStep=Time.captureDeltaTime;Time.captureDeltaTime=.04f;
        var part=Gun(MechWeaponRole.AirToAirMissile);part.WeaponPerformance.missileFlightProfile=MissileFlightProfile.AirToAirDefault;
        part.WeaponPerformance.missileFlightProfile.SeekerRangeMeters=1000;
        part.WeaponPerformance.supportsPassiveRfHoming=true;part.WeaponPerformance.missileVisualResourcePath="P3TestMissile";
        var a=Assemble(1,part);a.gameObject.AddComponent<CombatTestTarget>().ConfigureIdentity("PASSIVE-OWN",1);
        var radar=a.gameObject.AddComponent<MechRadarController>();radar.Configure(null,null);
        var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
        var emitter=Own(new GameObject("Observed radar emitter"));emitter.transform.position=new Vector3(0,0,8000);emitter.transform.rotation=Quaternion.LookRotation(Vector3.back);
        emitter.AddComponent<CombatTestTarget>().ConfigureIdentity("EMITTER",2);var enemyRadar=emitter.AddComponent<MechRadarController>();enemyRadar.SetTrackOperationMode(true);
        MechTestProjectile launched=null;
        try
        {
            float warm=Time.time;
            while(Time.time-warm<5)
            {yield return new WaitForFixedUpdate();if(enemyRadar.TryGetBestTrack(out var observed))enemyRadar.TryAssignFireControlTrack(observed.TrackId);}
            SeedAirMeasurements(radar);Assert.IsTrue(radar.TryGetBestTrack(out var target));
            radar.SetRadarPowerEnabled(false);while(radar.DataLinkPolicy!=MechDataLinkPolicy.Silent)radar.CycleDataLinkPolicy();
            var sensed=new ACunicombat.Sensing.Contracts.ThreatSnapshot[32];int sensedCount=radar.CopyThreats(sensed);
            TestContext.WriteLine("PASSIVE time="+radar.SimTimeSeconds+" target="+JsonUtility.ToJson(target)+" enemySTT="+enemyRadar.SttTrackId);
            for(int i=0;i<sensedCount;i++)TestContext.WriteLine("THREAT "+JsonUtility.ToJson(sensed[i]));
            Assert.IsTrue(radar.TryAssociatePassiveTarget(target,out _),"Actual received enemy radiation must identify a unique observed bearing.");
            Assert.IsTrue(weapons.CanReleaseMissileAt(target.TrackId,out var mode));Assert.AreEqual(MechTargetTrackingMode.Passive,mode);
            int before=radar.GetStatusSnapshot().GuidanceChannelsUsed;
            for(int i=0;i<20;i++)weapons.CanReleaseMissileAt(target.TrackId,out _);
            Assert.AreEqual(before,radar.GetStatusSnapshot().GuidanceChannelsUsed,"HUD peeks must not reserve channels.");
            Assert.IsTrue(weapons.CommandFireSlot("gun.0",MechActionSource.AI,radar.Reference(target.TrackId)));
            float deadline=Time.time+2;
            while(launched==null&&Time.time<deadline)
            {yield return null;foreach(var p in MechTestProjectile.Registry)if(p.IsOwnedBy(a.transform))launched=p;}
            Assert.IsNotNull(launched);Assert.IsTrue(launched.PassiveRfHomingEnabled);Assert.IsFalse(launched.HasExternalGuidanceSupport);
            Assert.IsFalse(radar.RadarPowerEnabled);Assert.AreEqual(0,radar.GuidanceUplinksUsed);
            var passiveTime=typeof(MechTestProjectile).GetField("passiveLastSeen",BindingFlags.Instance|BindingFlags.NonPublic);
            double firstSeen=(double)passiveTime.GetValue(launched),listeningUntil=radar.SimTimeSeconds+1;
            var launchAxis=(Vector3)typeof(MechTestProjectile).GetField("coldLaunchAxis",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(launched);
            var flightClock=typeof(MechTestProjectile).GetField("missileFlightTime",BindingFlags.Instance|BindingFlags.NonPublic);
            while(radar.SimTimeSeconds<listeningUntil){yield return new WaitForFixedUpdate();if((float)flightClock.GetValue(launched)<=1.3f)Assert.Less(Vector3.Angle(launchAxis,launched.transform.forward),.05f,"Listening must not bypass launch attitude hold");}
            Assert.Greater((double)passiveTime.GetValue(launched),firstSeen,"Missile's own passive receiver must continue observing real emissions after launch.");
            Assert.IsFalse(launched.ActiveSeekerEnabled);
            enemyRadar.SetRadarPowerEnabled(false);double stopUntil=radar.SimTimeSeconds+2;
            while(radar.SimTimeSeconds<stopUntil)yield return new WaitForFixedUpdate();
            Assert.IsNotNull(launched);Assert.IsTrue(launched.ActiveSeekerEnabled,"Lost radiation must allow autonomous search without waking the launcher's radar.");
            Assert.IsFalse(radar.RadarPowerEnabled);
        }
        finally{Time.captureDeltaTime=oldStep;if(launched!=null)UnityEngine.Object.DestroyImmediate(launched.gameObject);Cleanup();}
        yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator DataLinkOnlyMissile_UsesRemoteTrackForRealLaunchAndMidcourseUpdates()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float oldStep=Time.captureDeltaTime;Time.captureDeltaTime=.04f;
        var part=Gun(MechWeaponRole.AirToAirMissile);part.WeaponPerformance.missileFlightProfile=MissileFlightProfile.AirToAirDefault;
        part.WeaponPerformance.missileFlightProfile.SeekerRangeMeters=1000;part.WeaponPerformance.missileVisualResourcePath="P3TestMissile";
        var a=Assemble(1,part);a.RuntimeState.AggregatedStats.guidanceChannels=2;
        a.gameObject.AddComponent<CombatTestTarget>().ConfigureIdentity("LINK-OWN",1);
        var radar=a.gameObject.AddComponent<MechRadarController>();radar.Configure(null,null);radar.SetRadarPowerEnabled(false);
        var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
        var enemy=Own(new GameObject("Link target"));enemy.transform.position=new Vector3(0,0,8000);enemy.AddComponent<CombatTestTarget>().ConfigureIdentity("LINK-TARGET",2);
        var peer=Own(new GameObject("Link observer"));peer.transform.position=new Vector3(100,0,0);peer.AddComponent<CombatTestTarget>().ConfigureIdentity("LINK-PEER",1);var peerRadar=peer.AddComponent<MechRadarController>();peerRadar.SetTrackOperationMode(true);
        MechTestProjectile launched=null;
        try
        {
            var tracks=new ACunicombat.Sensing.Contracts.TrackSnapshot[96];var target=default(ACunicombat.Sensing.Contracts.TrackSnapshot);double start=radar.SimTimeSeconds;
            while(radar.SimTimeSeconds-start<15&&!target.TrackId.IsValid)
            {
                yield return new WaitForFixedUpdate();
                int sourceCount=peerRadar.CopyTracks(tracks);
                for(int i=0;i<sourceCount;i++)if(tracks[i].Affiliation!=ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly&&tracks[i].Position.z>4000)peerRadar.TryAssignFireControlTrack(tracks[i].TrackId);
                int count=radar.CopyTracks(tracks);
                for(int i=0;i<count;i++)if(tracks[i].Affiliation!=ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly&&
                    (tracks[i].SourceMask&ACunicombat.Sensing.Contracts.TrackSourceMask.DataLink)!=0&&weapons.CanReleaseMissileAt(tracks[i].TrackId,out var mode)&&mode==MechTargetTrackingMode.DataLink)target=tracks[i];
            }
            Assert.IsTrue(target.TrackId.IsValid,"A real received observation must support release.");
            var targetSharing=a.GetComponent<MechCooperativeTelemetry>();targetSharing.TraceTargetSharing=true;
            Assert.IsFalse(radar.SttTrackId.IsValid);Assert.IsFalse(radar.RadarPowerEnabled);
            Assert.IsTrue(weapons.CommandFireSlot("gun.0",MechActionSource.AI,radar.Reference(target.TrackId)));
            double deadline=radar.SimTimeSeconds+3;
            while(launched==null&&radar.SimTimeSeconds<deadline)
            {yield return new WaitForFixedUpdate();foreach(var p in MechTestProjectile.Registry)if(p.IsOwnedBy(a.transform))launched=p;}
            Assert.IsNotNull(launched);Assert.IsTrue(launched.HasExternalGuidanceSupport);
            var cache=typeof(MechTestProjectile).GetField("cachedGuidanceTrack",BindingFlags.Instance|BindingFlags.NonPublic);
            double initial=((ACunicombat.Sensing.Contracts.TrackSnapshot)cache.GetValue(launched)).LastUpdateTime;
            double until=radar.SimTimeSeconds+.8;while(radar.SimTimeSeconds<until)yield return new WaitForFixedUpdate();
            var updated=(ACunicombat.Sensing.Contracts.TrackSnapshot)cache.GetValue(launched);
            var sourceTracks=new ACunicombat.Sensing.Contracts.TrackSnapshot[96];int sourceTotal=peer.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>().CopyTracks(sourceTracks);
            TestContext.WriteLine("SOURCE now="+peerRadar.SimTimeSeconds+" STT="+peerRadar.SttTrackId+" radar="+peerRadar.RadarPowerEnabled);
            for(int i=0;i<sourceTotal;i++)TestContext.WriteLine("SOURCE TRACK "+sourceTracks[i].TrackId+" observed="+sourceTracks[i].LastUpdateTime+" z="+sourceTracks[i].Position.z+" state="+sourceTracks[i].Lifecycle);
            radar.TryGetTrack(target.TrackId,out var receiverTarget);
            int receiverCount=radar.CopyTracks(tracks);
            TestContext.WriteLine("REQUESTED "+target.TrackId);
            for(int i=0;i<receiverCount;i++)TestContext.WriteLine("RECEIVER TRACK "+tracks[i].TrackId+" observed="+tracks[i].LastUpdateTime+" z="+tracks[i].Position.z+" source="+tracks[i].SourceMask);
            foreach(var line in targetSharing.TargetShareDiagnostics)TestContext.WriteLine("SHARE "+line);
            TestContext.WriteLine("UPLINK now="+radar.SimTimeSeconds+" localObserved="+receiverTarget.LastUpdateTime+" initial="+initial+" received="+updated.LastUpdateTime+" seeker="+launched.ActiveSeekerEnabled+" support="+launched.HasExternalGuidanceSupport+" uplinks="+radar.GuidanceUplinksUsed+" seq="+typeof(MechTestProjectile).GetField("lastLinkSequence",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(launched));
            var linkDriver=a.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();TestContext.WriteLine("UPLINK PIPE pump="+radar.GuidanceUplinkSendAttempts+" accepted="+radar.GuidanceUplinkSendAccepted+" linkAccepted="+linkDriver.AcceptedLinkMessageCount+" rejected="+linkDriver.RejectedLinkMessageCount+" delivered="+linkDriver.DeliveredLinkMessageCount);
            var missileTerminal=(ACunicombat.Sensing.Contracts.LinkTerminalId)typeof(MechTestProjectile).GetField("linkTerminal",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(launched);TestContext.WriteLine("UPLINK TERMINAL "+missileTerminal+" attempts="+linkDriver.LinkDeliveryAttemptsFor(missileTerminal)+" delivered="+linkDriver.LinkDeliveriesFor(missileTerminal));
            Assert.Greater(updated.LastUpdateTime,initial,"Physical guidance uplink must deliver a newer observation.");
            Assert.IsFalse(radar.RadarPowerEnabled);Assert.IsFalse(radar.SttTrackId.IsValid);
        }
        finally{Time.captureDeltaTime=oldStep;if(launched!=null)UnityEngine.Object.DestroyImmediate(launched.gameObject);Cleanup();}
        yield return new ExitPlayMode();
    }
    [Test] public void BurstRequest_DoesNotCountQueuedShotsAndCancellationStopsRetry()
    {
        var p=Gun();p.WeaponPerformance.tapBurstShotCount=3;var a=Assemble(1,p);var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(MechTestWeaponSystem).GetMethod("UpdateHandWeaponInput",flags).Invoke(weapons,new object[]{new MechEquipmentSlot("gun.0"),true,true});
        var state=typeof(MechTestWeaponSystem).GetMethod("GetHandState",flags).Invoke(weapons,new object[]{new MechEquipmentSlot("gun.0")});
        Assert.That((int)state.GetType().GetField("QueuedTriggerShots").GetValue(state),Is.EqualTo(3));
        Assert.That((int)state.GetType().GetField("TriggerSequenceShotCount").GetValue(state),Is.Zero);
        Assert.That(weapons.CancelEquipmentAction("gun.0"),Is.True);
        Assert.That((int)state.GetType().GetField("QueuedTriggerShots").GetValue(state),Is.Zero);
    }
    [Test] public void RequestDuringAutomaticPredeployment_DoesNotRestartDeployment()
    {
        var a=Assemble(1,Gun());var record=a.RuntimeState.AssembledParts[0];
        var player=record.instance.AddComponent<MechEquipmentAnimationPlayer>();var definitions=new List<MechEquipmentAnimationDefinition>();
        foreach(var id in new[]{"a000_800110","a000_800120","a000_800210","a000_800320","a000_800310"})
        {var clip=Own(new AnimationClip{legacy=true});clip.SetCurve("",typeof(Transform),"localPosition.x",AnimationCurve.Linear(0,0,1,0));definitions.Add(new MechEquipmentAnimationDefinition(id,clip));}
        player.Configure(definitions,"",false);var deployment=new MechWeaponDeployment(record);
        deployment.Tick(0,true);Assert.That(player.ActiveActionId,Is.EqualTo("a000_800110"));
        Assert.That(deployment.Request(.1),Is.True);Assert.That(deployment.CanRequest,Is.False);
        player.StopPlayback();deployment.Tick(1,true);Assert.That(player.ActiveActionId,Is.EqualTo("a000_800120"));
        Assert.That(deployment.Ready,Is.False);deployment.Cancel();Assert.That(deployment.CanRequest,Is.True);
    }
    [UnityTest] public IEnumerator SixSlotAiInventoryAndReassemblyUseNewMuzzlesAndCommitFeedback()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Assemble(6,Gun());var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();var ai=assembler.gameObject.AddComponent<MechAiTargetController>();
        Assert.IsNotNull(assembler,"six-slot assembler");Assert.IsNotNull(weapons,"six-slot weapons");Assert.IsNotNull(ai,"six-slot AI");
        var copy=typeof(MechAiTargetController).GetMethod("CopyOffensiveWeaponStatuses",BindingFlags.Instance|BindingFlags.NonPublic);Assert.IsNotNull(copy,"inventory method");
        Assert.AreEqual(6,copy.Invoke(ai,null));
        var snapshots=new List<MechEquipmentWeaponSnapshot>();
        try{weapons.CopyEquipmentWeaponSnapshots(snapshots);}catch(Exception e){Assert.Fail("snapshot failed: "+e);}
        var old=snapshots.Select(x=>x.InstanceId).ToArray();
        // Populate real cached muzzle transforms, then rebuild the same six-slot loadout.
        Assert.AreEqual(6,weapons.CommandFireGroup("primary",MechActionSource.AI));weapons.Actions.Tick(Time.timeAsDouble);
        assembler.Assemble();yield return null;
        Assert.AreEqual(6,copy.Invoke(ai,null));weapons.CopyEquipmentWeaponSnapshots(snapshots);foreach(var equipment in snapshots)Assert.IsFalse(old.Contains(equipment.InstanceId));
        Assert.AreEqual(6,weapons.CommandFireGroup("primary",MechActionSource.AI));weapons.Actions.Tick(Time.timeAsDouble);
        var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);
        foreach(var equipment in snapshots)Assert.IsTrue(feedback.Any(f=>f.InstanceId==equipment.InstanceId&&f.Phase==MechActionPhase.Succeeded),string.Join(";",feedback.Select(f=>f.Reason)));
        var rules=(System.Collections.IDictionary)typeof(MechAiTargetController).GetField("weaponRuleStates",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ai);
        foreach(var equipment in snapshots)Assert.IsTrue(rules.Contains(equipment.InstanceId),"Actual commit did not reach AI timing state");
        assembler.RuntimeState.AssembledParts[0].instance.SetActive(false);Assert.AreEqual(5,copy.Invoke(ai,null));
        foreach(var projectile in UnityEngine.Object.FindObjectsByType<MechTestProjectile>())UnityEngine.Object.DestroyImmediate(projectile.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator FormationFollowingCannotOverrideObservedSelfDefenseEvasion()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Assemble(1,Gun());var input=assembler.gameObject.AddComponent<MechPlayerInputAdapter>();assembler.gameObject.AddComponent<MechFlightMotionController>();
        assembler.gameObject.AddComponent<MechTestWeaponSystem>();var radar=assembler.GetComponent<MechRadarController>()??assembler.gameObject.AddComponent<MechRadarController>();
        var ai=assembler.gameObject.AddComponent<MechAiTargetController>();var fleet=assembler.gameObject.AddComponent<MechFleetController>();var leader=Guid.NewGuid();var group=Guid.NewGuid();fleet.Configure(group,leader,false,false,1);
        fleet.State.Receive(new ACunicombat.Sensing.Contracts.FleetPacket{Kind=ACunicombat.Sensing.Contracts.FleetPacketKind.Heartbeat,Sender=leader,Fleet=group,ObservedAt=radar.SimTimeSeconds,ExpiresAt=radar.SimTimeSeconds+3,Position=new Unity.Mathematics.double3(0,0,2000),Available=true},radar.SimTimeSeconds);
        radar.enabled=false;
        typeof(MechAiTargetController).GetField("incomingThreat",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(ai,true);
        typeof(MechAiTargetController).GetField("incomingThreatTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(ai,1.5f);
        typeof(MechAiTargetController).GetField("lastThreatSeenTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(ai,Time.time);
        typeof(MechAiTargetController).GetField("nextInterceptorRadarSweepTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(ai,Time.time+1);
        typeof(MechAiTargetController).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(ai,null);
        Assert.AreEqual(-.25f,input.Current.MoveLocal.y);Assert.AreEqual(1,Mathf.Abs(input.Current.MoveLocal.x));Assert.IsFalse(input.Current.HasAutonomousFlightCommand,"Formation navigation replaced evasive input");
        Cleanup();yield return new ExitPlayMode();
    }
}
