using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public sealed class MechDefenseExtensionTests
{
    private readonly List<UnityEngine.Object> owned=new();
    private T Own<T>(T value)where T:UnityEngine.Object{owned.Add(value);return value;}
    [TearDown] public void Cleanup(){for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);owned.Clear();}
    private MechAssembler Fixture(MechDefenseExtensionKind kind)
    {
        var part=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        part.EditorConfigure("test.extension","Extension",MechPartSlot.Extension,MechPartCategory.Utility,null,new MechPartStats{massKg=10},null);
        part.DefenseExtension.kind=kind;
        var platform=Own(ScriptableObject.CreateInstance<MechPlatformDefinition>());platform.platformId="defense.test";
        platform.slots.Add(new MechSlotDefinition{id="ext",legacyRole=MechPartSlot.Extension,category=MechPartCategory.Utility,hardpointType=MechHardpointType.Utility});
        var loadout=Own(ScriptableObject.CreateInstance<MechLoadoutDefinition>());
        loadout.ConfigureRuntime("defense.test","Defense",new[]{new MechPartAssignment{slotId="ext",slot=MechPartSlot.Extension,part=part}});loadout.ConfigureLayout(platform);
        var go=Own(new GameObject("Defender"));var assembler=go.AddComponent<MechAssembler>();assembler.Loadout=loadout;assembler.Assemble();return assembler;
    }
    private static MissileTrackSnapshot Threat(double now)=>new MissileTrackSnapshot
    {TrackId=new TrackId(new Handle(7,1)),Position=new Unity.Mathematics.double3(0,0,50),Velocity=new Unity.Mathematics.double3(0,0,-20),
        Quality=MissileTrackQuality.WeaponsQuality,Incoming=true,Confidence=1,PositionUncertaintyMeters=.1f,LastEvidenceTime=now,LastUpdateTime=now,TimeToClosestApproachSeconds=2.5f};
    private static void Seed(MechRadarController radar,MissileTrackSnapshot track)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(MechRadarController).GetMethod("EnsureInitialized",flags).Invoke(radar,null);
        track.LastEvidenceTime=track.LastUpdateTime=radar.SimTimeSeconds;
        var cache=(MissileTrackSnapshot[])typeof(MechRadarController).GetField("missileTrackCache",flags).GetValue(radar);cache[0]=track;
        typeof(MechRadarController).GetField("missileTrackCacheCount",flags).SetValue(radar,1);
        typeof(MechRadarController).GetField("missileTrackCacheTime",flags).SetValue(radar,radar.SimTimeSeconds);
        typeof(MechRadarController).GetField("missileTrackCacheRevision",flags).SetValue(radar,radar.GetComponent<SimulationDriver>().ObservationRevision);
    }
    [UnityTest] public IEnumerator UrgentThreatPreemptsDistantPreparationWithoutSpendingAmmo()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Fixture(MechDefenseExtensionKind.ActiveProtection);var radar=assembler.gameObject.AddComponent<MechRadarController>();var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        var slow=Threat(10);slow.Position=new Unity.Mathematics.double3(0,0,800);slow.TimeToClosestApproachSeconds=40;
        Seed(radar,slow);weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);Assert.That(snapshots[0].Pending,Is.True);
        var urgent=Threat(10);urgent.TrackId=new TrackId(new Handle(8,1));urgent.Position=new Unity.Mathematics.double3(0,0,400);urgent.Velocity=new Unity.Mathematics.double3(0,0,-2020);urgent.TimeToClosestApproachSeconds=.2f;
        urgent.LastEvidenceTime=urgent.LastUpdateTime=radar.SimTimeSeconds;
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;var cache=(MissileTrackSnapshot[])typeof(MechRadarController).GetField("missileTrackCache",flags).GetValue(radar);cache[1]=urgent;typeof(MechRadarController).GetField("missileTrackCacheCount",flags).SetValue(radar,2);
        weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);Assert.That(feedback.Exists(e=>e.Phase==MechActionPhase.Cancelled),Is.True);
        weapons.DefenseExtensions.CopySnapshots(snapshots);Assert.That(snapshots[0].Pending,Is.True);Assert.That(snapshots[0].Ammunition,Is.EqualTo(8));
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator NewObservationRevisionInvalidatesSameTimeMissileCache()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Fixture(MechDefenseExtensionKind.ActiveProtection);var radar=assembler.gameObject.AddComponent<MechRadarController>();Seed(radar,Threat(10));
        var output=new MissileTrackSnapshot[4];Assert.That(radar.CopyMissileTracks(output),Is.EqualTo(1));
        var driver=radar.GetComponent<SimulationDriver>();typeof(SimulationDriver).GetProperty("ObservationRevision").SetValue(driver,driver.ObservationRevision+1);
        Assert.That(radar.CopyMissileTracks(output),Is.Zero,"A pre-sensor cache must not hide a later update with the same clock time");
        Cleanup();yield return new ExitPlayMode();
    }
    [Test] public void PreparationStartsOutsideReleaseRangeButCannotFireThere()
    {
        var p=new MechDefenseExtensionProfile();var t=Threat(10);t.Quality=MissileTrackQuality.Warning;t.Position=new Unity.Mathematics.double3(0,0,400);t.Velocity=new Unity.Mathematics.double3(0,0,-1200);t.TimeToClosestApproachSeconds=.333f;
        Assert.That(MechDefenseExtensions.TryPlan(in t,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,.15f,out _,out _,out _,true),Is.True);
        Assert.That(MechDefenseExtensions.TryPlan(in t,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out _,out var reason),Is.False);
        Assert.That(reason,Is.EqualTo("TrackQuality"));t.Quality=MissileTrackQuality.WeaponsQuality;
        Assert.That(MechDefenseExtensions.TryPlan(in t,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out _,out reason),Is.False);Assert.That(reason,Is.EqualTo("OutsideLaunchRange"));
        t.Position=new Unity.Mathematics.double3(0,0,200);t.TimeToClosestApproachSeconds=.166f;
        Assert.That(MechDefenseExtensions.TryPlan(in t,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out float flight,out reason),Is.True,reason);
        Assert.That(flight*p.projectileSpeed,Is.GreaterThan(35));
    }
    [Test] public void FragmentSolutionCoversCrossingThreatWithPlatformVelocity()
    {
        var p=new MechDefenseExtensionProfile();var t=Threat(10);t.Position=new Unity.Mathematics.double3(20,0,180);t.Velocity=new Unity.Mathematics.double3(150,20,-600);t.TimeToClosestApproachSeconds=.3f;
        var ownVelocity=new Vector3(90,0,20);
        Assert.That(MechDefenseExtensions.TryPlan(in t,10,Vector3.zero,ownVelocity,Quaternion.identity,p,0,out var direction,out float flight,out var reason),Is.True,reason);
        var missile=new Vector3(20,0,180)+new Vector3(150,20,-600)*flight;
        var charge=(ownVelocity+direction*p.projectileSpeed)*flight;var delta=missile-charge;
        Assert.That(delta.magnitude,Is.LessThan(p.blastRadiusMeters-t.PositionUncertaintyMeters));
        Assert.That(Vector3.Angle(direction,delta),Is.LessThan(p.fragmentHalfAngle));
    }
    [Test] public void TurningIncomingTrackCanHaveASolutionWhenItsFrozenVelocityDoesNot()
    {
        var p=new MechDefenseExtensionProfile();var t=Threat(10);t.Position=new Unity.Mathematics.double3(0,0,100);t.Velocity=new Unity.Mathematics.double3(950,0,-600);t.TimeToClosestApproachSeconds=.05f;
        Assert.That(MechDefenseExtensions.TryPlan(in t,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out _,out var reason),Is.False);
        Assert.That(reason,Is.EqualTo("NoInterceptSolution"));
        t.Acceleration=new Unity.Mathematics.double3(-500,0,0);
        Assert.That(MechDefenseExtensions.TryPlan(in t,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out var direction,out float flight,out reason),Is.True,reason);
        var predicted=t.PredictPosition(10+flight);var delta=new Vector3((float)predicted.x,(float)predicted.y,(float)predicted.z)-direction*p.projectileSpeed*flight;
        Assert.That(delta.magnitude,Is.LessThan(p.blastRadiusMeters));Assert.That(Vector3.Angle(direction,delta),Is.LessThan(p.fragmentHalfAngle));
    }
    [Test] public void CatalogContainsOptionalExtensionsAndCombiningDoesNotModifySource()
    {
        var source=Resources.Load<MechPartCatalog>(MechLoadoutSelectionStore.CatalogResourcePath);int before=source!=null?source.Entries.Count:0;
        var catalog=MechLoadoutSelectionStore.LoadCatalog();
        Assert.That(catalog.Find("DEFENSE_APS").LoadPart().DefenseExtension.kind,Is.EqualTo(MechDefenseExtensionKind.ActiveProtection));
        Assert.That(catalog.Find("DEFENSE_REACTIVEARMOR").LoadPart().DefenseExtension.kind,Is.EqualTo(MechDefenseExtensionKind.ReactiveArmor));
        if(source!=null)Assert.That(source.Entries.Count,Is.EqualTo(before));
    }
    [Test] public void UnequippedHasNoDefenseAndRemovalClearsWholeBodyCoverage()
    {
        var assembler=Fixture(MechDefenseExtensionKind.ReactiveArmor);var extensions=assembler.gameObject.AddComponent<MechDefenseExtensions>();
        Assert.That(extensions.ArmorCells("head"),Is.EqualTo(2));Assert.That(extensions.ArmorCells("legs/right"),Is.EqualTo(2));
        float residual=extensions.Absorb("head",100,Vector3.back,TestWeaponKind.Missile,out bool applied);
        Assert.That(applied,Is.True);Assert.That(residual,Is.EqualTo(40).Within(.01));Assert.That(extensions.ArmorCells("head"),Is.EqualTo(1));Assert.That(extensions.ArmorCells("legs/right"),Is.EqualTo(2));
        Assert.That(assembler.Loadout.TrySetPart("ext",null,out _),Is.True);assembler.Assemble();
        Assert.That(extensions.ArmorCells("head"),Is.Zero);Assert.That(extensions.ArmorCells("legs/right"),Is.Zero);
    }
    [Test] public void ApsPlannerRejectsOutOfRangeRearLateStaleAndImpreciseTracks()
    {
        var p=new MechDefenseExtensionProfile{kind=MechDefenseExtensionKind.ActiveProtection};var track=Threat(10);
        Assert.That(MechDefenseExtensions.TryPlan(in track,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,.1f,out _,out _),Is.True);
        p.maximumRangeMeters=20;Assert.That(MechDefenseExtensions.TryPlan(in track,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out _),Is.False);
        p.maximumRangeMeters=120;p.coverageHalfAngle=30;Assert.That(MechDefenseExtensions.TryPlan(in track,10,Vector3.zero,Vector3.zero,Quaternion.Euler(0,180,0),p,0,out _,out _),Is.False);
        p.coverageHalfAngle=180;track.TimeToClosestApproachSeconds=.01f;Assert.That(MechDefenseExtensions.TryPlan(in track,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,.1f,out _,out _),Is.False);
        track=Threat(10);Assert.That(MechDefenseExtensions.TryPlan(in track,11,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out _),Is.False);
        track.PositionUncertaintyMeters=100;Assert.That(MechDefenseExtensions.TryPlan(in track,10,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out _),Is.False);
    }
    [UnityTest] public IEnumerator EquippedApsUsesArbiterSpendsAmmoAndPhysicalBlastIntercepts()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Fixture(MechDefenseExtensionKind.ActiveProtection);var p=assembler.RuntimeState.AssembledParts[0].part.DefenseExtension;
        p.reactionSeconds=0;p.ammunition=2;p.cooldownSeconds=100;p.blastDamage=10000;
        var radar=assembler.gameObject.AddComponent<MechRadarController>();var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        var enemy=Own(new GameObject("Launcher"));var missileObject=Own(new GameObject("Incoming"));missileObject.transform.position=Vector3.forward*50;missileObject.transform.rotation=Quaternion.LookRotation(Vector3.back);
        var missile=missileObject.AddComponent<MechTestProjectile>();missile.Configure(enemy.transform,null,default,TestWeaponKind.Missile,Vector3.back*20,10,.1f,10,accelerationMetersPerSecondSquared:1,newSeekerActivationDelay:20);
        Seed(radar,Threat(radar.SimTimeSeconds));weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);
        var events=new List<MechActionFeedback>();weapons.Actions.CopyEvents(events);
        Assert.That(snapshots[0].Ammunition,Is.EqualTo(1),string.Join(";",events.ConvertAll(e=>e.Reason)));
        Assert.That(snapshots[0].ActiveShots,Is.EqualTo(1));Assert.That(missile.IsInterceptableMissile,Is.True,"Scheduling must not delete the target");
        Seed(radar,Threat(radar.SimTimeSeconds));weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);weapons.DefenseExtensions.CopySnapshots(snapshots);Assert.That(snapshots[0].Ammunition,Is.EqualTo(1));
        float until=Time.fixedTime+.7f,deadline=Time.realtimeSinceStartup+10;
        while(Time.fixedTime<until&&Time.realtimeSinceStartup<deadline&&missile!=null&&missile.IsInterceptableMissile)yield return null;
        Assert.That(missile==null||!missile.IsInterceptableMissile,Is.True,"Countermunition must physically damage the missile; target="+(missile!=null?missile.transform.position.ToString():"destroyed")+" shots="+UnityEngine.Object.FindObjectsByType<MechApsProjectile>().Length);
        var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");
        if(!string.IsNullOrEmpty(output))System.IO.File.WriteAllText(System.IO.Path.Combine(output,"defense-extension-demo.json"),"{\"input\":\"controlled local weapons-quality track\",\"initialAmmunition\":2,\"ammunitionAfterLaunch\":1,\"targetSurvivedScheduling\":true,\"physicalInterception\":true}");
        foreach(var shot in UnityEngine.Object.FindObjectsByType<MechApsProjectile>())UnityEngine.Object.DestroyImmediate(shot.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator MultipleApsSlotsKeepIndependentAmmoAndEnforceConcurrencyAndExhaustion()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Fixture(MechDefenseExtensionKind.ActiveProtection);var part=assembler.RuntimeState.AssembledParts[0].part;
        part.DefenseExtension.reactionSeconds=0;part.DefenseExtension.cooldownSeconds=0;part.DefenseExtension.ammunition=2;
        assembler.Loadout.Platform.slots.Add(new MechSlotDefinition{id="ext2",legacyRole=MechPartSlot.None,category=MechPartCategory.Utility,hardpointType=MechHardpointType.Utility});
        Assert.That(assembler.Loadout.TrySetPart("ext2",part,out _),Is.True);assembler.Assemble();
        var radar=assembler.gameObject.AddComponent<MechRadarController>();var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        Seed(radar,Threat(radar.SimTimeSeconds));weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);Assert.That(snapshots.Count,Is.EqualTo(2));
        foreach(var snapshot in snapshots){Assert.That(snapshot.Ammunition,Is.EqualTo(1));Assert.That(snapshot.ActiveShots,Is.EqualTo(1));}
        var capabilities=new List<MechEquipmentCapability>();MechEquipmentCapability.CopyFrom(assembler.RuntimeState,capabilities);
        foreach(var capability in capabilities){Assert.That(capability.ExecutionStateKnown,Is.True);Assert.That(capability.LocallyReady,Is.False);Assert.That(capability.RoundsRemaining,Is.EqualTo(1));}
        weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);Assert.That(UnityEngine.Object.FindObjectsByType<MechApsProjectile>().Length,Is.EqualTo(2));
        foreach(var shot in UnityEngine.Object.FindObjectsByType<MechApsProjectile>())UnityEngine.Object.DestroyImmediate(shot.gameObject);
        Seed(radar,Threat(radar.SimTimeSeconds));weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        weapons.DefenseExtensions.CopySnapshots(snapshots);foreach(var snapshot in snapshots)Assert.That(snapshot.Ammunition,Is.Zero);
        Assert.That(UnityEngine.Object.FindObjectsByType<MechApsProjectile>().Length,Is.EqualTo(2));
        foreach(var shot in UnityEngine.Object.FindObjectsByType<MechApsProjectile>())UnityEngine.Object.DestroyImmediate(shot.gameObject);
        weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);Assert.That(UnityEngine.Object.FindObjectsByType<MechApsProjectile>().Length,Is.Zero);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator PositiveReactionTimeMustElapseBeforeActualLaunch()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Fixture(MechDefenseExtensionKind.ActiveProtection);var profile=assembler.RuntimeState.AssembledParts[0].part.DefenseExtension;
        profile.reactionSeconds=.08f;profile.cooldownSeconds=100;
        var radar=assembler.gameObject.AddComponent<MechRadarController>();var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        // Hold this fixture's observed contact steady; empty real scans are not part of a reaction-timer test.
        Seed(radar,Threat(radar.SimTimeSeconds));radar.enabled=false;
        var driver=assembler.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();driver.enabled=false;
        typeof(ACunicombat.Simulation.Driver.SimulationDriver).GetField("simulationTimeSource",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(driver,(Func<double>)(()=>10.0));
        Seed(radar,Threat(radar.SimTimeSeconds));weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);
        Assert.That(snapshots[0].Pending,Is.True);double ready=snapshots[0].ReadyAt;
        float deadline=Time.realtimeSinceStartup+5;
        while(Time.realtimeSinceStartup<deadline)
        {
            Seed(radar,Threat(radar.SimTimeSeconds));weapons.Actions.Tick(Time.timeAsDouble);weapons.DefenseExtensions.CopySnapshots(snapshots);
            if(Time.timeAsDouble<ready)Assert.That(snapshots[0].Ammunition,Is.EqualTo(8));
            if(snapshots[0].Ammunition<8)break;yield return null;
        }
        Assert.That(Time.timeAsDouble,Is.GreaterThanOrEqualTo(ready));Assert.That(snapshots[0].Ammunition,Is.EqualTo(7));
        // Physics can launch and finish the short fuse before this render-frame coroutine
        // resumes. Verify the actual commit time, not the lifetime of the visual object.
        var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);int commits=0;
        foreach(var entry in feedback)if(entry.Phase==MechActionPhase.Succeeded)
        {commits++;Assert.That(entry.Time,Is.GreaterThanOrEqualTo(ready),"The real launch must not precede reaction completion");}
        Assert.That(commits,Is.EqualTo(1),"Physics and render ticks must not double-fire the same action");
        foreach(var shot in UnityEngine.Object.FindObjectsByType<MechApsProjectile>())UnityEngine.Object.DestroyImmediate(shot.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator BriefPrecisionLossDoesNotRestartReactionOrSpendAmmo()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Fixture(MechDefenseExtensionKind.ActiveProtection);assembler.RuntimeState.AssembledParts[0].part.DefenseExtension.reactionSeconds=.05f;
        var radar=assembler.gameObject.AddComponent<MechRadarController>();var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        Seed(radar,Threat(radar.SimTimeSeconds));radar.enabled=false;
        var driver=assembler.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();driver.enabled=false;
        typeof(ACunicombat.Simulation.Driver.SimulationDriver).GetField("simulationTimeSource",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(driver,(Func<double>)(()=>10.0));
        Seed(radar,Threat(10));weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);double ready=snapshots[0].ReadyAt;
        var changed=Threat(10);changed.Incoming=false;Seed(radar,changed);weapons.Actions.Tick(Time.timeAsDouble);weapons.DefenseExtensions.CopySnapshots(snapshots);Assert.That(snapshots[0].Pending,Is.True);Assert.That(snapshots[0].ReadyAt,Is.EqualTo(ready));
        var imprecise=Threat(10);imprecise.PositionUncertaintyMeters=100;Seed(radar,imprecise);weapons.Actions.Tick(Time.timeAsDouble);
        float deadline=Time.realtimeSinceStartup+5;while(Time.timeAsDouble<ready+.04&&Time.realtimeSinceStartup<deadline)yield return null;
        weapons.Actions.Tick(Time.timeAsDouble);weapons.DefenseExtensions.CopySnapshots(snapshots);
        Assert.That(snapshots[0].Pending,Is.True);Assert.That(snapshots[0].ReadyAt,Is.EqualTo(ready));Assert.That(snapshots[0].Ammunition,Is.EqualTo(8));
        Seed(radar,Threat(10));weapons.Actions.Tick(Time.timeAsDouble);weapons.DefenseExtensions.CopySnapshots(snapshots);Assert.That(snapshots[0].Ammunition,Is.EqualTo(7));
        foreach(var shot in UnityEngine.Object.FindObjectsByType<MechApsProjectile>())UnityEngine.Object.DestroyImmediate(shot.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator ReactionDoesNotSpendAmmoAndUnequipCancelsPendingShot()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var assembler=Fixture(MechDefenseExtensionKind.ActiveProtection);assembler.RuntimeState.AssembledParts[0].part.DefenseExtension.reactionSeconds=.5f;
        var radar=assembler.gameObject.AddComponent<MechRadarController>();var weapons=assembler.gameObject.AddComponent<MechTestWeaponSystem>();
        Seed(radar,Threat(radar.SimTimeSeconds));weapons.DefenseExtensions.TickRequests(weapons);weapons.Actions.Tick(Time.timeAsDouble);
        var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);
        var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);Assert.That(snapshots[0].Pending,Is.True,string.Join(";",feedback.ConvertAll(e=>e.Reason)));Assert.That(snapshots[0].Ammunition,Is.EqualTo(8));Assert.That(UnityEngine.Object.FindObjectsByType<MechApsProjectile>().Length,Is.Zero);
        assembler.Loadout.TrySetPart("ext",null,out _);assembler.Assemble();weapons.Actions.Tick(Time.timeAsDouble);
        Assert.That(weapons.Actions.ActiveCount,Is.Zero);Assert.That(UnityEngine.Object.FindObjectsByType<MechApsProjectile>().Length,Is.Zero);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator CountermunitionCanMissWithoutDeletingMissile()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var attacker=Own(new GameObject("Attacker"));var target=Own(new GameObject("Missile"));target.transform.position=Vector3.right*100;
        var missile=target.AddComponent<MechTestProjectile>();missile.Configure(attacker.transform,null,default,TestWeaponKind.Missile,Vector3.zero,10,.1f,10);
        var defender=Own(new GameObject("Defender"));var shotObject=Own(new GameObject("APS miss"));var shot=shotObject.AddComponent<MechApsProjectile>();shot.Configure(defender.transform,Vector3.forward*100,.1f,8,10000);shot.Step(.1f);
        Assert.That(missile.IsInterceptableMissile,Is.True);Assert.That(shot.DamagedMissiles,Is.Zero);
        Cleanup();yield return new ExitPlayMode();
    }
}
