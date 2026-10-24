using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public sealed class MechApsCombatTests
{
    private static int kills;private static Transform observedDefender;private static float closestOwnerBurst;private static MechTestProjectile observedTarget;private static readonly List<string> bursts=new();
    private static void OnBurst(MechApsProjectile shot,int hits,int destroyed){kills+=destroyed;if(observedDefender!=null)closestOwnerBurst=Mathf.Min(closestOwnerBurst,Vector3.Distance(shot.transform.position,observedDefender.position));if(observedTarget!=null){var delta=observedTarget.PositionAtPhysicsTime(shot.BurstTimeSeconds)-shot.transform.position;bursts.Add($"burst={shot.transform.position} distance={delta.magnitude:F2} forwardDot={Vector3.Dot(shot.FragmentDirection,delta.normalized):F3} hits={hits} kills={destroyed} {shot.BlastDiagnostic}");}}
    private int? savedTargetRate;
    private float? savedCaptureDelta;
    private readonly List<UnityEngine.Object> owned=new();
    private T Own<T>(T obj)where T:UnityEngine.Object {owned.Add(obj);return obj;}
    [TearDown] public void Cleanup(){observedDefender=null;if(savedCaptureDelta.HasValue){Time.captureDeltaTime=savedCaptureDelta.Value;savedCaptureDelta=null;}if(savedTargetRate.HasValue){Application.targetFrameRate=savedTargetRate.Value;savedTargetRate=null;}MechApsProjectile.BurstResolved-=OnBurst;for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);owned.Clear();}
    private MechTestWeaponSystem Defender(bool moving=false)
    {
        var part=Own(UnityEngine.Object.Instantiate(Resources.Load<MechPartDefinition>("DefenseExtensions/APS")));
        var platform=Own(ScriptableObject.CreateInstance<MechPlatformDefinition>());platform.platformId="real.aps";
        platform.slots.Add(new MechSlotDefinition{id="aps",category=MechPartCategory.Utility,hardpointType=MechHardpointType.Utility});
        // Match the playable mech's multi-track fire-control capacity. A bare utility-only
        // fixture defaults to one track and would replace the missile with its launcher.
        var fcs=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        fcs.EditorConfigure("test.fcs","Test FCS",MechPartSlot.FireControlSystem,MechPartCategory.Internal,null,new MechPartStats{trackCapacity=16},null);
        platform.slots.Add(new MechSlotDefinition{id="fcs",legacyRole=MechPartSlot.FireControlSystem,category=MechPartCategory.Internal,hardpointType=MechSlotRules.RequiredHardpointType(MechPartSlot.FireControlSystem)});
        var loadout=Own(ScriptableObject.CreateInstance<MechLoadoutDefinition>());loadout.ConfigureRuntime("real.aps","Real APS",new[]{new MechPartAssignment{slotId="aps",part=part},new MechPartAssignment{slotId="fcs",part=fcs,slot=MechPartSlot.FireControlSystem}});loadout.ConfigureLayout(platform);
        var root=Own(new GameObject("APS platform"));root.AddComponent<AudioListener>();var assembler=root.AddComponent<MechAssembler>();assembler.Loadout=loadout;assembler.Assemble();
        if(moving)root.AddComponent<MechFlightMotionController>();
        var radar=root.AddComponent<MechRadarController>();radar.SetRadarPowerEnabled(false);
        return root.AddComponent<MechTestWeaponSystem>();
    }
    private MechTestProjectile Missile(Vector3 position,Vector3 velocity)
    {
        var launcher=Own(new GameObject("Hostile launcher"));var go=Own(new GameObject("Real incoming missile"));go.transform.position=position;go.transform.rotation=Quaternion.LookRotation(velocity.sqrMagnitude>.01f?velocity.normalized:Vector3.forward);
        var missile=go.AddComponent<MechTestProjectile>();missile.Configure(launcher.transform,null,default,TestWeaponKind.Missile,velocity,10,.1f,8,
            accelerationMetersPerSecondSquared:1,newSeekerActivationDelay:20);return missile;
    }
    [Serializable] private class CombatRecord{public string origin="real projectile -> sensor measurements -> shared tracks -> action arbiter";public List<string> cases=new();public List<string> observations=new();public List<string> actions=new();public List<string> bursts=new();}
    [UnityTest] public IEnumerator RealIncomingMissilesTriggerWithoutInjectedTracksFromFrontAndRear()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var weapons=Defender();var records=new CombatRecord();kills=0;bursts.Clear();MechApsProjectile.BurstResolved+=OnBurst;
        foreach(var approach in new[]{Vector3.forward,Vector3.back,Vector3.up,Vector3.forward*5})
        {
            int killsBefore=kills;weapons.DefenseExtensions.ResetEquipment();var missile=Missile(approach.normalized*400,-approach*120);observedTarget=missile;
            float deadline=Time.realtimeSinceStartup+12;bool fired=false;double nextSample=0;
            while(Time.realtimeSinceStartup<deadline&&missile!=null&&missile.IsInterceptableMissile)
            {
                var radar=weapons.GetComponent<MechRadarController>();
                if(radar.SimTimeSeconds>=nextSample)
                {
                    nextSample=radar.SimTimeSeconds+.08;var contacts=new ACunicombat.Sensing.Contracts.MissileTrackSnapshot[32];int count=radar.CopyMissileTracks(contacts);
                    for(int k=0;k<count;k++)records.observations.Add($"t={radar.SimTimeSeconds:F3} r={Unity.Mathematics.math.length(contacts[k].Position):F1} pSigma={contacts[k].PositionUncertaintyMeters:F2} v={Unity.Mathematics.math.length(contacts[k].Velocity):F1} age={radar.SimTimeSeconds-contacts[k].LastEvidenceTime:F3} q={contacts[k].Quality}");
                }
                var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);
                if(snapshots.Count>0&&snapshots[0].Ammunition<8)fired=true;
                if(missile.transform.position.magnitude<12)break;
                yield return null;
            }
            records.bursts=new List<string>(bursts);
            var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);
            foreach(var entry in feedback)records.actions.Add($"{entry.Time:F3} {entry.Phase} {entry.Reason}");
            var diagnostics=new List<MechDefenseExtensions.Diagnostic>();weapons.DefenseExtensions.CopyDiagnostics(diagnostics);
            string detail=string.Join(";",diagnostics.ConvertAll(d=>d.reason+" "+string.Join(",",d.rejected)+" sample="+d.planningSample+" history="+string.Join(";",d.history)));
            records.cases.Add(approach+" fired="+fired+" intercepted="+(kills>killsBefore)+" "+detail);
            var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");if(!string.IsNullOrEmpty(output))File.WriteAllText(Path.Combine(output,"aps-real-incoming.json"),JsonUtility.ToJson(records,true));
            Assert.That(fired,Is.True,detail);Assert.That(kills,Is.GreaterThan(killsBefore),"No physical APS kill: "+detail);
            foreach(var shot in UnityEngine.Object.FindObjectsByType<MechApsProjectile>())UnityEngine.Object.DestroyImmediate(shot.gameObject);
            yield return null;
        }
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator WarmSensorIntercepts1200MetersPerSecondMissile()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var weapons=Defender();var records=new CombatRecord();kills=0;bursts.Clear();MechApsProjectile.BurstResolved+=OnBurst;
        float warmUntil=Time.fixedTime+.3f;while(Time.fixedTime<warmUntil)yield return null;
        foreach(var approach in new[]{Vector3.right*10})
        {
            int killsBefore=kills;weapons.DefenseExtensions.ResetEquipment();var missile=Missile(approach.normalized*400,-approach*120);observedTarget=missile;
            float deadline=Time.realtimeSinceStartup+12;bool fired=false;double nextSample=0;
            while(Time.realtimeSinceStartup<deadline&&missile!=null&&missile.IsInterceptableMissile)
            {
                var radar=weapons.GetComponent<MechRadarController>();
                if(radar.SimTimeSeconds>=nextSample)
                {
                    nextSample=radar.SimTimeSeconds+.08;var contacts=new ACunicombat.Sensing.Contracts.MissileTrackSnapshot[32];int count=radar.CopyMissileTracks(contacts);
                    for(int k=0;k<count;k++)records.observations.Add($"t={radar.SimTimeSeconds:F3} r={Unity.Mathematics.math.length(contacts[k].Position):F1} pSigma={contacts[k].PositionUncertaintyMeters:F2} v={Unity.Mathematics.math.length(contacts[k].Velocity):F1} age={radar.SimTimeSeconds-contacts[k].LastEvidenceTime:F3} q={contacts[k].Quality}");
                }
                var snapshots=new List<MechDefenseExtensionSnapshot>();weapons.DefenseExtensions.CopySnapshots(snapshots);
                if(snapshots.Count>0&&snapshots[0].Ammunition<8)fired=true;
                if(missile.transform.position.magnitude<12)break;
                yield return null;
            }
            records.bursts=new List<string>(bursts);
            var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);
            foreach(var entry in feedback)records.actions.Add($"{entry.Time:F3} {entry.Phase} {entry.Reason}");
            var diagnostics=new List<MechDefenseExtensions.Diagnostic>();weapons.DefenseExtensions.CopyDiagnostics(diagnostics);
            string detail=string.Join(";",diagnostics.ConvertAll(d=>d.reason+" "+string.Join(",",d.rejected)+" sample="+d.planningSample+" history="+string.Join(";",d.history)));
            records.cases.Add(approach+" fired="+fired+" intercepted="+(kills>killsBefore)+" "+detail);
            var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");if(!string.IsNullOrEmpty(output))File.WriteAllText(Path.Combine(output,"aps-high-speed-incoming.json"),JsonUtility.ToJson(records,true));
            Assert.That(fired,Is.True,detail);Assert.That(kills,Is.GreaterThan(killsBefore),"No physical APS kill: "+detail);
            foreach(var shot in UnityEngine.Object.FindObjectsByType<MechApsProjectile>())UnityEngine.Object.DestroyImmediate(shot.gameObject);
            yield return null;
        }
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator MovingForwardInterceptsRealMissile()=>MovingDefense(new Vector2(0,1),false,"forward");
    [UnityTest] public IEnumerator MovingSidewaysInterceptsRealMissile()=>MovingDefense(new Vector2(1,0),false,"strafe");
    [UnityTest] public IEnumerator AcceleratingSidewaysInterceptsRealMissile()=>MovingDefense(new Vector2(1,0),true,"accelerating");
    [UnityTest] public IEnumerator MovingForwardAt60FpsInterceptsRealMissile()=>MovingDefense(new Vector2(0,1),false,"forward-60",60);
    [UnityTest] public IEnumerator MovingSidewaysAt60FpsInterceptsRealMissile()=>MovingDefense(new Vector2(1,0),false,"strafe-60",60);
    [UnityTest] public IEnumerator AcceleratingSidewaysAt60FpsInterceptsRealMissile()=>MovingDefense(new Vector2(1,0),true,"accelerating-60",60);
    [UnityTest] public IEnumerator LogFast1870Forward30Fps()=>MovingDefense(new Vector2(0,1),false,"log-1870-forward",30,1870,1400);
    [UnityTest] public IEnumerator LogFast2020Strafe30Fps()=>MovingDefense(new Vector2(1,0),false,"log-2020-strafe",30,2020,1400);
    [UnityTest] public IEnumerator LogFast2020Strafe60Fps()=>MovingDefense(new Vector2(1,0),false,"log-2020-strafe-60",60,2020,1400);
    [UnityTest] public IEnumerator LogFast2020Accelerating30Fps()=>MovingDefense(new Vector2(1,0),true,"log-2020-accelerating",30,2020,1400);
    [UnityTest] public IEnumerator LogFast2020Stationary30Fps()=>MovingDefense(Vector2.zero,false,"log-2020-stationary",30,2020,1400);
    private IEnumerator MovingDefense(Vector2 input,bool accelerate,string label,int fps=30,float closingSpeed=600,float startRange=1000)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        savedTargetRate=Application.targetFrameRate;Application.targetFrameRate=fps;
        savedCaptureDelta=Time.captureDeltaTime;Time.captureDeltaTime=1f/fps;
        var weapons=Defender(true);var root=weapons.gameObject;root.transform.position=Vector3.up*1000;
        var motion=root.GetComponent<MechFlightMotionController>();motion.ConfigureModelForwardYawOffset(0);motion.ConfigureGravity(0);motion.ConfigurePropulsionForceModel(false,false,0);motion.ConfigureTechnicalSafetySpeed(closingSpeed>1800?65:accelerate?1000:150);
        motion.ConfigureManeuverEnergy(1000,4,15,18,0,.5f,.2f);
        typeof(MechFlightMotionController).GetField("airCoastingDragAcceleration",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(motion,0f);
        yield return null; // Let the production controller build its initial nozzle layout.
        float fixtureThrustKn=motion.CurrentMassKg*50f/1000f;
        foreach(var name in new[]{"fallbackMainThrustKn","fallbackLateralThrustKn","fallbackReverseThrustKn","verticalThrustKn","auxiliaryNozzleThrustKn"})typeof(MechFlightMotionController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(motion,fixtureThrustKn);
        var channels=(List<MechThrusterChannelState>)typeof(MechFlightMotionController).GetField("thrusterChannels",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(motion);channels.Clear();
        var axes=new[]{Vector3.forward,Vector3.back,Vector3.right,Vector3.left,Vector3.up,Vector3.down};
        var roles=new[]{MechThrusterRole.Main,MechThrusterRole.Reverse,MechThrusterRole.StrafePositive,MechThrusterRole.StrafeNegative,MechThrusterRole.LegLift,MechThrusterRole.Descent};
        for(int i=0;i<axes.Length;i++){var anchor=new GameObject("Test nozzle "+i).transform;anchor.SetParent(root.transform,false);anchor.localRotation=Quaternion.LookRotation(-axes[i],i>3?Vector3.forward:Vector3.up);channels.Add(new MechThrusterChannelState("moving."+i,roles[i],MechThrusterSide.Center,anchor,Vector3.zero,false));}
        var drive=root.AddComponent<ApsPropulsionTestDriver>();drive.Motion=motion;drive.Input=new MechPlayerInputFrame{MoveLocal=input};
        var start=root.transform.position;float until=Time.fixedTime+4;float timeout=Time.realtimeSinceStartup+20;
        while(Time.fixedTime<until&&Time.realtimeSinceStartup<timeout)yield return null;
        if(input!=Vector2.zero){Assert.That(Vector3.Distance(start,root.transform.position),Is.GreaterThan(20));Assert.That(motion.Velocity.magnitude,Is.GreaterThan(30));}
        var target=root.AddComponent<CombatTestTarget>();target.Configure(root.transform,5000,false,0,0);target.ConfigureIdentity("Moving defender",1);
        // Log replay begins near the recorded 40-70 m/s carrier speed; the accelerating
        // case keeps thrust held and removes only the warm-up speed cap at missile launch.
        if(accelerate&&closingSpeed>1800)motion.ConfigureTechnicalSafetySpeed(1000);
        var initialVelocity=motion.Velocity;var launch=root.transform.position;
        var attacker=Own(new GameObject("Guided hostile launcher"));attacker.transform.position=launch+Vector3.forward*(startRange+200);attacker.AddComponent<CombatTestTarget>().ConfigureIdentity("Hostile",2);var attackerRadar=attacker.AddComponent<MechRadarController>();attackerRadar.SetRadarPowerEnabled(false);
        var missileObject=Own(new GameObject("Guided incoming"));missileObject.transform.position=launch+Vector3.forward*startRange;missileObject.transform.rotation=Quaternion.LookRotation(-Vector3.forward);
        var missile=missileObject.AddComponent<MechTestProjectile>();missile.Configure(attacker.transform,attackerRadar,default,TestWeaponKind.Missile,initialVelocity-Vector3.forward*closingSpeed,10,.1f,10,accelerationMetersPerSecondSquared:20,turnRateDegreesPerSecond:180,newSeekerActivationDelay:0,newSeekerMaximumRange:3000,newSeekerHalfAngleDegrees:80,newSeekerGimbalDegrees:85,newMaximumLateralAcceleration:400,newManeuverDeltaV:1000,hasInitialGuidanceEstimate:true,initialGuidancePosition:launch,initialGuidanceVelocity:initialVelocity,initialGuidanceUncertaintyMeters:1);observedTarget=missile;observedDefender=root.transform;closestOwnerBurst=float.PositiveInfinity;kills=0;bursts.Clear();MechApsProjectile.BurstResolved+=OnBurst;
        var samples=new List<string>();var radar=root.GetComponent<MechRadarController>();double next=0;float end=Time.fixedTime+4;timeout=Time.realtimeSinceStartup+20;
        while(Time.fixedTime<end&&Time.realtimeSinceStartup<timeout&&missile!=null&&missile.IsInterceptableMissile)
        {
            if(radar.SimTimeSeconds>=next){next=radar.SimTimeSeconds+.04;var diag=new List<MechDefenseExtensions.Diagnostic>();weapons.DefenseExtensions.CopyDiagnostics(diag);var contacts=new ACunicombat.Sensing.Contracts.MissileTrackSnapshot[32];int count=radar.CopyMissileTracks(contacts);samples.Add($"t={radar.SimTimeSeconds:F3} own={root.transform.position} v={motion.Velocity} acceleration={motion.CurrentControlAcceleration} reason="+string.Join(";",diag.ConvertAll(d=>d.reason+" "+d.planningSample)));for(int i=0;i<count;i++)samples.Add($"track={contacts[i].Position} v={contacts[i].Velocity} a={contacts[i].Acceleration} jerk={contacts[i].Jerk} age={radar.SimTimeSeconds-contacts[i].LastUpdateTime:F3} truth={(missile!=null?missile.transform.position.ToString():"gone")} sigma={contacts[i].PositionUncertaintyMeters} incoming={contacts[i].Incoming} q={contacts[i].Quality}");}
            yield return null;
        }
        var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");if(!string.IsNullOrEmpty(output))File.WriteAllLines(Path.Combine(output,"aps-moving-"+label+".txt"),samples.ToArray());if(!string.IsNullOrEmpty(output))File.WriteAllLines(Path.Combine(output,"aps-moving-"+label+"-bursts.txt"),bursts);
        Assert.That(motion.TranslationInputActive,Is.EqualTo(input!=Vector2.zero));if(input!=Vector2.zero)Assert.That(Vector3.Distance(launch,root.transform.position),Is.GreaterThan(5));
        if(accelerate)Assert.That(motion.Velocity.magnitude,Is.GreaterThan(initialVelocity.magnitude+1));
        Assert.That(kills,Is.GreaterThan(0),string.Join("\n",samples));
        Assert.That(closestOwnerBurst,Is.GreaterThanOrEqualTo(35),"Safety distance must hold during real movement");
        var actionEvents=new List<MechActionFeedback>();weapons.Actions.CopyEvents(actionEvents);
        MechActionId committedId=default;double committedAt=-1,beganAt=-1;
        foreach(var e in actionEvents)if(e.Phase==MechActionPhase.Succeeded){committedId=e.Id;committedAt=e.Time;break;}
        foreach(var e in actionEvents)if(e.Phase==MechActionPhase.Running&&e.Id.Equals(committedId)){beganAt=e.Time;break;}
        Assert.That(beganAt,Is.GreaterThanOrEqualTo(0));Assert.That(committedAt,Is.GreaterThanOrEqualTo(0));
        Assert.That(committedAt-beganAt,Is.GreaterThanOrEqualTo(.149),"Do not bypass the configured reaction time");Cleanup();yield return new ExitPlayMode();
    }
    private MechTestProjectile MissileWithPhysicsSegment(Vector3 start,Vector3 end,float dt)
    {
        var missile=Missile(end,(end-start)/dt);var sphere=missile.gameObject.AddComponent<SphereCollider>();sphere.radius=.1f;
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(MechTestProjectile).GetField("previousPhysicsPosition",flags).SetValue(missile,start);
        typeof(MechTestProjectile).GetField("previousPhysicsTime",flags).SetValue(missile,Time.fixedTimeAsDouble-dt);
        typeof(MechTestProjectile).GetField("physicsStateTime",flags).SetValue(missile,Time.fixedTimeAsDouble);
        return missile;
    }
    [UnityTest] public IEnumerator CollisionOnRealPhysicsTicksIntercepts()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var missile=Missile(Vector3.forward*100,Vector3.back*1000);missile.gameObject.AddComponent<SphereCollider>().radius=.1f;
        var owner=Own(new GameObject("Owner"));var go=Own(new GameObject("Physics contact charge"));go.transform.position=Vector3.forward*60;
        var shot=go.AddComponent<MechApsProjectile>();shot.Configure(owner.transform,Vector3.forward*900,1,8,160,Vector3.forward,60,35);
        observedTarget=missile;kills=0;bursts.Clear();MechApsProjectile.BurstResolved+=OnBurst;Physics.SyncTransforms();
        float deadline=Time.realtimeSinceStartup+5;
        while(kills==0&&Time.realtimeSinceStartup<deadline&&shot!=null)yield return null;
        Assert.That(kills,Is.EqualTo(1),string.Join(";",bursts));Assert.That(string.Join(";",bursts),Does.Contain("trigger=collision:"));
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator CollisionUsesSameTimeForSweepingAndFragmentDamage()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var missile=MissileWithPhysicsSegment(Vector3.forward*90,Vector3.forward*70,.02f);
        var owner=Own(new GameObject("Safe owner"));var go=Own(new GameObject("Contact charge"));go.transform.position=Vector3.forward*60;
        var shot=go.AddComponent<MechApsProjectile>();shot.Configure(owner.transform,Vector3.forward*900,1,8,160,Vector3.forward,60,35);
        Physics.SyncTransforms();shot.Step(.02f);
        Assert.That(shot.IsFlying,Is.False,"The two swept paths cross before the timed fuse");Assert.That(shot.DamagedMissiles,Is.EqualTo(1),shot.BlastDiagnostic);
        Assert.That(missile.IsInterceptableMissile,Is.False,shot.BlastDiagnostic);
        var delta=missile.PositionAtPhysicsTime(shot.BurstTimeSeconds)-go.transform.position;
        Assert.That(Vector3.Dot(delta,Vector3.forward),Is.GreaterThan(0));Assert.That(delta.magnitude,Is.LessThan(.5));
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator CollisionInsideSafetyDistanceDoesNotDamageMissile()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var missile=MissileWithPhysicsSegment(Vector3.forward*35,Vector3.forward*15,.02f);
        var owner=Own(new GameObject("Owner"));var go=Own(new GameObject("Unsafe contact"));go.transform.position=Vector3.forward*5;
        var shot=go.AddComponent<MechApsProjectile>();shot.Configure(owner.transform,Vector3.forward*900,1,8,160,Vector3.forward,60,35);
        Physics.SyncTransforms();shot.Step(.02f);Assert.That(shot.BlastDiagnostic,Does.Contain("SafetyInhibited"));Assert.That(shot.DamagedMissiles,Is.Zero);Assert.That(missile.IsInterceptableMissile,Is.True);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator CollisionNearMissAndEarlierWallRemainPhysical()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var missile=MissileWithPhysicsSegment(new Vector3(2,0,90),new Vector3(2,0,70),.02f);
        var owner=Own(new GameObject("Owner"));var go=Own(new GameObject("Near miss"));go.transform.position=Vector3.forward*60;
        var shot=go.AddComponent<MechApsProjectile>();shot.Configure(owner.transform,Vector3.forward*900,1,8,160,Vector3.forward,60,35);
        Physics.SyncTransforms();shot.Step(.02f);Assert.That(shot.IsFlying,Is.True);Assert.That(missile.IsInterceptableMissile,Is.True);
        UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(missile.gameObject);
        missile=MissileWithPhysicsSegment(Vector3.forward*90,Vector3.forward*70,.02f);
        var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));wall.name="Blocking wall";wall.transform.position=Vector3.forward*65;wall.transform.localScale=new Vector3(5,5,1);
        go=Own(new GameObject("Wall contact"));go.transform.position=Vector3.forward*60;shot=go.AddComponent<MechApsProjectile>();shot.Configure(owner.transform,Vector3.forward*900,1,8,160,Vector3.forward,60,35);
        Physics.SyncTransforms();shot.Step(.02f);Assert.That(shot.IsFlying,Is.False);Assert.That(go.transform.position.z,Is.LessThan(65));Assert.That(missile.IsInterceptableMissile,Is.True);
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator CountermunitionFirstStepUsesOnlyTimeSinceLaunch()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var go=Own(new GameObject("Substep charge"));var shot=go.AddComponent<MechApsProjectile>();shot.Configure(null,Vector3.right*1000,1,8,160);
        typeof(MechApsProjectile).GetField("integratedThroughTime",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(shot,Time.fixedTimeAsDouble-.005);
        typeof(MechApsProjectile).GetMethod("FixedUpdate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(shot,null);
        Assert.That(go.transform.position.x,Is.EqualTo(5).Within(.001),"A launch within a physics step must not receive 20 ms of travel");
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator DirectionalFragmentsDamageFrontButNotRear()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var front=Missile(Vector3.forward*3,Vector3.zero);var rear=Missile(Vector3.back*3,Vector3.zero);
        var source=Own(new GameObject("Source"));var go=Own(new GameObject("Directional charge"));var shot=go.AddComponent<MechApsProjectile>();
        shot.Configure(source.transform,Vector3.zero,.01f,8,160,Vector3.forward,60);shot.Step(.01f);
        Assert.That(front.IsInterceptableMissile,Is.False);Assert.That(rear.IsInterceptableMissile,Is.True);
        Cleanup();yield return new ExitPlayMode();
    }
    [Test] public void TimedWorldSnapshotsProjectOnceAndDoNotLeakAcrossGenerations()
    {
        using var world=new ACunicombat.Simulation.World.SimWorld();
        var state=new ACunicombat.Simulation.Kinematics.KinematicState{PositionWorld=new Unity.Mathematics.double3(10,0,0),VelocityWorld=new Unity.Mathematics.double3(100,0,0)};
        var id=world.Register(in state);Assert.That(world.TrySetTimedState(id,in state,0),Is.True);
        world.SwapSnapshots(.02);Assert.That(world.ReadSnapshot[id.Value.Index].PositionWorld.x,Is.EqualTo(12).Within(.0001));
        world.SwapSnapshots(.04);Assert.That(world.ReadSnapshot[id.Value.Index].PositionWorld.x,Is.EqualTo(14).Within(.0001));
        Assert.That(world.WriteBuffer[id.Value.Index].PositionWorld.x,Is.EqualTo(10));
        world.Unregister(id);var next=world.Register(in state);Assert.That(world.TrySetTimedState(id,in state,0),Is.False);
        world.SwapSnapshots(10);Assert.That(world.ReadSnapshot[next.Value.Index].PositionWorld.x,Is.EqualTo(10));
    }
    [Test] public void AuxiliaryReceiverProvenanceStaysInsideItsObserver()
    {
        using var world=new ACunicombat.Simulation.World.SimWorld();var empty=new ACunicombat.Simulation.Kinematics.KinematicState();
        var owner=world.Register(in empty);var other=world.Register(in empty);
        using var context=new ACunicombat.Simulation.Driver.PlatformTrackContext();
        var primary=ACunicombat.Sensors.MissileSeekerProfile.Create(new ACunicombat.Sensing.Contracts.SensorId(new ACunicombat.Simulation.Core.Handle(0,1)),owner,false,500,10);
        var aux=ACunicombat.Sensors.MissileSeekerProfile.Create(new ACunicombat.Sensing.Contracts.SensorId(new ACunicombat.Simulation.Core.Handle(1,1)),owner,false,500,10);
        var foreign=ACunicombat.Sensors.MissileSeekerProfile.Create(new ACunicombat.Sensing.Contracts.SensorId(new ACunicombat.Simulation.Core.Handle(2,1)),other,false,500,10);
        context.BindPrimaryRadar(in primary);context.RegisterAuxiliary(in aux,true);Assert.Throws<ArgumentException>(()=>context.RegisterAuxiliary(in foreign,true));
        using var measurements=new Unity.Collections.NativeList<ACunicombat.Sensing.Contracts.Measurement>(3,Unity.Collections.Allocator.Temp);
        using var emissions=new Unity.Collections.NativeList<ACunicombat.Sensing.Contracts.EmissionEvent>(1,Unity.Collections.Allocator.Temp);
        for(int pass=0;pass<3;pass++)
        {
            measurements.Clear();int index=0;
            foreach(var sensor in new[]{primary.SensorId,aux.SensorId,foreign.SensorId})
                measurements.Add(new ACunicombat.Sensing.Contracts.Measurement{SensorId=sensor,Timestamp=1+pass*.04,Range=100+(index++)*100,Direction=new Unity.Mathematics.float3(0,0,1),RangeVar=.1f,AngleVar=.000001f,VelocityVar=1,LosRateVar=.0001f,SnrDb=40,ObjectClass=ACunicombat.Sensing.Contracts.TrackObjectClass.Missile,ClassificationConfidence=1,Flags=ACunicombat.Sensing.Contracts.MeasurementFlags.CloseProtectionReceiver});
            context.Process(measurements,emissions,Unity.Mathematics.double3.zero,Unity.Mathematics.double3.zero,1+pass*.04);
        }
        var tracks=new ACunicombat.Sensing.Contracts.TrackSnapshot[8];int count=context.CopyTracks(tracks);Assert.That(count,Is.EqualTo(2));
        foreach(var track in tracks)if(track.TrackId.IsValid)
            Assert.That((track.SourceMask&ACunicombat.Sensing.Contracts.TrackSourceMask.CloseProtectionRadar)!=0,Is.EqualTo(track.Position.z>150));
    }
    [Test] public void FasterChargeMovesInterceptOutwardAndRejectsUnsafeBurst()
    {
        var p=new MechDefenseExtensionProfile();var target=new ACunicombat.Sensing.Contracts.MissileTrackSnapshot
        {TrackId=new ACunicombat.Sensing.Contracts.TrackId(new ACunicombat.Simulation.Core.Handle(1,1)),Quality=ACunicombat.Sensing.Contracts.MissileTrackQuality.WeaponsQuality,
            Incoming=true,Position=new Unity.Mathematics.double3(200,0,0),Velocity=new Unity.Mathematics.double3(-600,0,0),LastUpdateTime=1,LastEvidenceTime=1,TimeToClosestApproachSeconds=200f/600,PositionUncertaintyMeters=.1f};
        Assert.That(p.projectileSpeed,Is.EqualTo(900));
        Assert.That(MechDefenseExtensions.TryPlan(in target,1,Vector3.zero,Vector3.zero,Quaternion.identity,p,.15f,out _,out float flight,out _),Is.True);
        Assert.That(flight*p.projectileSpeed,Is.GreaterThan(60));
        target.Position=new Unity.Mathematics.double3(40,0,0);target.TimeToClosestApproachSeconds=40f/600;
        Assert.That(MechDefenseExtensions.TryPlan(in target,1,Vector3.zero,Vector3.zero,Quaternion.identity,p,0,out _,out _,out var reason),Is.False);
        Assert.That(reason,Is.EqualTo("BurstTooClose"));
    }
    [UnityTest] public IEnumerator OwnerApproachingBurstInhibitsDetonation()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var owner=Own(new GameObject("Owner"));var missile=Missile(Vector3.forward*13,Vector3.zero);
        var charge=Own(new GameObject("Unsafe charge"));charge.transform.position=Vector3.forward*10;
        var shot=charge.AddComponent<MechApsProjectile>();shot.Configure(owner.transform,Vector3.zero,.01f,8,160,Vector3.forward,60,35,20);shot.Step(.01f);
        Assert.That(missile.IsInterceptableMissile,Is.True);Assert.That(shot.DamagedMissiles,Is.Zero);Assert.That(shot.BlastDiagnostic,Does.Contain("SafetyInhibited"));
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator CloudScalesWithoutExtraParticlesAndRetainsDepth()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var old=UnityEngine.Random.state;UnityEngine.Random.InitState(123);
        var small=Own(MechApsEffects.Burst(Vector3.zero,Vector3.forward,60,false,1));UnityEngine.Random.InitState(123);
        var large=Own(MechApsEffects.Burst(Vector3.zero,Vector3.forward,60,false,20));UnityEngine.Random.state=old;
        var a=new ParticleSystem.Particle[256];var b=new ParticleSystem.Particle[256];
        int ac=small.transform.Find("White transverse smoke ring").GetComponent<ParticleSystem>().GetParticles(a);
        int bc=large.transform.Find("White transverse smoke ring").GetComponent<ParticleSystem>().GetParticles(b);
        Assert.That(ac,Is.EqualTo(128));Assert.That(bc,Is.EqualTo(ac));
        Assert.That(b[0].position.magnitude/a[0].position.magnitude,Is.EqualTo(20).Within(.001));
        Assert.That(b[0].velocity.magnitude/a[0].velocity.magnitude,Is.EqualTo(20).Within(.001));
        Assert.That(b[0].startSize/a[0].startSize,Is.EqualTo(20).Within(.001));
        float minDepth=float.MaxValue,maxDepth=float.MinValue,minRadius=float.MaxValue,maxRadius=0;
        for(int i=0;i<bc;i++)
        {
            minDepth=Mathf.Min(minDepth,b[i].position.z);maxDepth=Mathf.Max(maxDepth,b[i].position.z);
            float radius=new Vector2(b[i].position.x,b[i].position.y).magnitude;
            minRadius=Mathf.Min(minRadius,radius);maxRadius=Mathf.Max(maxRadius,radius);
        }
        Assert.That(maxDepth-minDepth,Is.GreaterThan(7),"Smoke wall needs axial thickness");
        Assert.That(maxRadius-minRadius,Is.GreaterThan(8),"Cloud must not collapse to a thin circular wreath");
        Assert.That(large.transform.Find("Forward dense smoke").GetComponent<ParticleSystem>().particleCount,Is.GreaterThan(0));
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator OwnLaunchHasNoDistanceAttenuation()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        MechApsEffects.Launch(Vector3.forward*200,Vector3.forward,true);
        var source=UnityEngine.Object.FindAnyObjectByType<AudioSource>();Assert.That(source,Is.Not.Null);
        Assert.That(source.spatialBlend,Is.Zero);Assert.That(source.clip.name,Does.StartWith("smoke_grenade_launcher_interior-"));
        UnityEngine.Object.DestroyImmediate(source.gameObject);Cleanup();yield return new ExitPlayMode();
    }
    [Test] public void RequestedAudioVariantsAreAvailable()
    {
        Assert.That(Resources.LoadAll<AudioClip>("Audio/APS").Length,Is.EqualTo(16));
        for(int i=1;i<=10;i++)Assert.That(Resources.Load<AudioClip>("Audio/Impacts/Missile/explosion_air_close_"+i.ToString("000")),Is.Not.Null);
    }
    [UnityTest] public IEnumerator RenderCylinderAndDirectionalBurst()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");
        var cameraObject=Own(new GameObject("APS preview camera"));var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.04f,.06f);camera.orthographic=true;camera.orthographicSize=65;
        camera.transform.position=new Vector3(160,80,-240);camera.transform.LookAt(new Vector3(0,0,2));
        var light=Own(new GameObject("Preview light")).AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(40,-30,0);
        var cylinder=Own(new GameObject("APS cylinder preview"));MechApsEffects.AttachCylinder(cylinder);cylinder.transform.position=Vector3.back*2;cylinder.transform.localScale=Vector3.one*4;
        var previewRandom=UnityEngine.Random.state;UnityEngine.Random.InitState(1742);
        var burst=Own(MechApsEffects.Burst(Vector3.zero,Vector3.forward,60,false));
        var ps=burst.GetComponentsInChildren<ParticleSystem>();Assert.That(ps.Length,Is.EqualTo(4));
        var rt=Own(new RenderTexture(768,768,24));rt.Create();camera.targetTexture=rt;var pixels=Own(new Texture2D(768,768,TextureFormat.RGB24,false));
        float previous=0;
        foreach(float time in new[]{.035f,.14f,.35f,.7f})
        {
            foreach(var system in ps)system.Simulate(time-previous,false,false,true);previous=time;
            camera.Render();var old=RenderTexture.active;RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,768,768),0,0);pixels.Apply();RenderTexture.active=old;
            if(!string.IsNullOrEmpty(output))File.WriteAllBytes(Path.Combine(output,"aps-burst-"+Mathf.RoundToInt(time*1000)+".png"),pixels.EncodeToPNG());
        }
        UnityEngine.Object.DestroyImmediate(burst);
        var side=Own(MechApsEffects.Burst(Vector3.zero,Vector3.forward,60,false));foreach(var system in side.GetComponentsInChildren<ParticleSystem>())system.Simulate(.12f,false,false,true);
        camera.transform.position=new Vector3(-260,80,-60);camera.transform.LookAt(new Vector3(0,0,2));camera.Render();
        var active=RenderTexture.active;RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,768,768),0,0);pixels.Apply();RenderTexture.active=active;
        if(!string.IsNullOrEmpty(output))File.WriteAllBytes(Path.Combine(output,"aps-burst-side.png"),pixels.EncodeToPNG());
        UnityEngine.Random.state=previewRandom;camera.targetTexture=null;rt.Release();Cleanup();yield return new ExitPlayMode();
    }
}

// Runs the same translation input and flight controller integration as MechPlayerRig.Update.
public sealed class ApsPropulsionTestDriver : MonoBehaviour
{
    public MechFlightMotionController Motion;public MechPlayerInputFrame Input;
    private void Update(){if(Motion!=null)Motion.Tick(in Input,Time.deltaTime);}
}
