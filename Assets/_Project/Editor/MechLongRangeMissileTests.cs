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

public class MechLongRangeMissileTests
{
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static MechPartDefinition Part(string number,string side="L") => Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_"+number);
    [TestCase("4310")] [TestCase("4230")] [TestCase("4340")]
    public void SavedLoadoutsHaveAnimatedLaunchersAndPassiveAmmunition(string number)
    {
        foreach(string side in new[]{"L","R"})
        {
            var p=Part(number,side);Assert.IsNotNull(p);
            var expected=AC6TestWeaponProfileFactory.Create(p.PartId);
            Assert.AreEqual(JsonUtility.ToJson(expected),JsonUtility.ToJson(p.WeaponPerformance));
            Assert.IsTrue(p.WeaponPerformance.IsConfigured);Assert.IsTrue(p.WeaponPerformance.supportsPassiveRfHoming);
            Assert.IsNotNull(Resources.Load<GameObject>(p.WeaponPerformance.missileVisualResourcePath));
            foreach(string action in new[]{"a000_800110","a000_800120","a000_800210","a000_800320","a000_800310"})
                Assert.IsTrue(p.EquipmentAnimations.Any(a=>a.ActionId==action),p.PartId+" missing "+action);
        }
    }

    [UnityTest] public IEnumerator AllEightSavedLaunchersReallyDeployAndReleaseTheirConfiguredMissile()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float previous=Time.captureDeltaTime,previousScale=Time.timeScale;Time.timeScale=1f;Time.captureDeltaTime=.04f;
        var fixture=new MechEquipmentExecutionTests();
        var assemble=typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private);
        var seed=typeof(MechEquipmentExecutionTests).GetMethod("SeedAirMeasurements",BindingFlags.NonPublic|BindingFlags.Static);
        try
        {
            foreach(string number in new[]{"4302","4310","4230","4340"})foreach(string side in new[]{"L","R"})
            {
                var part=Part(number,side);var a=(MechAssembler)assemble.Invoke(fixture,new object[]{1,part});
                var stats=a.RuntimeState.AggregatedStats;
                stats.radarRangeKm=30;stats.guidanceChannels=2;stats.trackCapacity=16;stats.sensorPrecision=38;stats.fireControlQuality=65;stats.radarRefreshHz=2.5f;
                var radar=a.gameObject.AddComponent<MechRadarController>();radar.Configure(null,null);radar.SetTrackOperationMode(true);
                var weapons=a.gameObject.AddComponent<MechTestWeaponSystem>();yield return null;
                // The three prelaunch observations span 80 ms. An isolated run
                // must advance its fresh clock before injecting that history.
                float warmDeadline=Time.realtimeSinceStartup+5f;
                while(radar.SimTimeSeconds<.12&&Time.realtimeSinceStartup<warmDeadline)yield return null;
                Assert.GreaterOrEqual(radar.SimTimeSeconds,.12,"Test simulation clock must advance.");
                seed.Invoke(null,new object[]{radar});Assert.IsTrue(radar.TryGetBestTrack(out var track));
                Assert.IsTrue(radar.TryAssignFireControlTrack(track.TrackId),"power="+radar.RadarPowerEnabled+" mode="+radar.TrackOperationMode+" track="+JsonUtility.ToJson(track));
                Assert.IsTrue(weapons.CommandFireSlot("gun.0",MechActionSource.Player,radar.Reference(track.TrackId)),part.PartId);
                MechTestProjectile shot=null;float start=Time.time;
                while(shot==null&&Time.time-start<7)
                {
                    yield return null;seed.Invoke(null,new object[]{radar});
                    shot=MechTestProjectile.Registry.FirstOrDefault(p=>p!=null&&p.IsOwnedBy(a.transform));
                }
                var feedback=new List<MechActionFeedback>();weapons.Actions.CopyEvents(feedback);
                Assert.IsNotNull(shot,part.PartId+": "+string.Join(";",feedback.Select(e=>e.Phase+":"+e.Reason)));
                Assert.Greater(Time.time-start,.5f,"Must wait for real launcher deployment/opening.");
                Assert.Greater(shot.GetComponentsInChildren<Renderer>().Length,0,"Real projectile visual must be attached.");
                Assert.AreEqual(part.WeaponPerformance.missileFlightProfile.PropulsionMode,
                    typeof(MechTestProjectile).GetField("propulsionMode",Private).GetValue(shot));
                var snapshots=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(snapshots);
                Assert.Less(snapshots[0].Rounds,part.WeaponPerformance.magazineCapacity);
                TestContext.WriteLine(part.PartId+" launched after "+(Time.time-start)+"s, rounds="+snapshots[0].Rounds);
                foreach(var p in MechTestProjectile.Registry.ToArray())if(p!=null)UnityEngine.Object.DestroyImmediate(p.gameObject);
                fixture.Cleanup();yield return null;
            }
        }
        finally{Time.captureDeltaTime=previous;Time.timeScale=previousScale;fixture.Cleanup();}
        yield return new ExitPlayMode();
    }

    // Uses the production projectile integrator, not a second spreadsheet model.
    // Straight vacuum range validates propulsion/lifetime, not moving-target hit probability.
    [TestCase("4310",30)] [TestCase("4310",60)]
    [TestCase("4230",30)] [TestCase("4230",60)]
    [TestCase("4340",30)] [TestCase("4340",60)]
    public void ProductionFlightReachesLongRangeWithFinitePropellant(string number,int hz)
    {
        AssertFlightEnvelope(Part(number).WeaponPerformance,number,hz);
    }
    public static void AssertFlightEnvelope(MechWeaponPerformance p,string number,int hz,float rangeFraction=.95f)
    {
        var profile=p.missileFlightProfile;
        // This fixture has no target. Adaptive pulses intentionally remain in
        // reserve and the ducted motor cruises at minimum throttle.
        var unobservedProfile=profile;
        if(profile.AdaptiveSecondPulse)
        {
            unobservedProfile.DryMassKg+=profile.SecondStagePropellantMassKg;
            unobservedProfile.SecondStagePropellantMassKg=0;
        }
        else if(profile.PropulsionMode==MissilePropulsionMode.VariableFlowDuctedRocket)
        {
            unobservedProfile.SecondStageThrustNewtons*=profile.SecondStageMinimumThrottle;
            unobservedProfile.SecondStageMassFlowKgPerSecond*=profile.SecondStageMinimumThrottle;
        }
        float expectedRange=unobservedProfile.ReferenceRangeMeters;
        var owner=new GameObject("Range test launcher");var body=new GameObject("Range test missile");
        float previous=Time.fixedDeltaTime;Time.fixedDeltaTime=1f/hz;
        try
        {
            var audio=body.AddComponent<MechMissileAudioController>();
            typeof(MechMissileAudioController).GetMethod("Awake",Private).Invoke(audio,null);
            var missile=body.AddComponent<MechTestProjectile>();
            missile.Configure(owner.transform,null,default,TestWeaponKind.Missile,Vector3.forward*profile.EjectionSpeedMps,1,.1f,
                profile.MaximumFlightTimeSeconds,newMaximumTravelDistance:profile.MaximumRangeMeters,missileProfile:profile);
            var step=typeof(MechTestProjectile).GetMethod("UpdateMissile",Private);
            float elapsed=0;bool reached100=false,reached200=false,secondBurn=false;float previousMass=missile.CurrentMassKg;
            while(body.transform.position.z<expectedRange*rangeFraction&&elapsed<profile.MaximumFlightTimeSeconds-.1f)
            {
                // This isolated envelope test has no seeker/target. Prevent only
                // guidance-loss disposal; propulsion, staging and fuel are real.
                typeof(MechTestProjectile).GetField("noGuidanceSeconds",Private).SetValue(missile,0f);
                typeof(MechTestProjectile).GetField("missileFlightTime",Private).SetValue(missile,elapsed);
                step.Invoke(missile,new object[]{Time.fixedDeltaTime});elapsed+=Time.fixedDeltaTime;
                body.transform.position+=missile.Velocity*Time.fixedDeltaTime;
                Assert.IsTrue(float.IsFinite(missile.Velocity.z));Assert.LessOrEqual(missile.CurrentMassKg,previousMass+.001f);previousMass=missile.CurrentMassKg;
                reached100|=body.transform.position.z>=100000;reached200|=body.transform.position.z>=200000;
                secondBurn|=(bool)typeof(MechTestProjectile).GetField("secondStageIgnited",Private).GetValue(missile);
            }
            if(expectedRange>=220000)Assert.IsTrue(reached100&&reached200);
            Assert.GreaterOrEqual(body.transform.position.z,expectedRange*rangeFraction);
            Assert.AreEqual(profile.HasSecondStage&&!profile.AdaptiveSecondPulse,secondBurn,"Adaptive missile must keep its reserve without any observed target.");Assert.GreaterOrEqual(missile.CurrentMassKg,profile.DryMassKg);
            TestContext.WriteLine(number+" @"+hz+"Hz distance="+body.transform.position.z+" time="+elapsed+" speed="+missile.Velocity.magnitude+" mass="+missile.CurrentMassKg);
        }
        finally
        {
            // EditMode-created MonoBehaviours need explicit disposal of their
            // native simulation world; Unity does not guarantee OnDestroy here.
            var missile=body.GetComponent<MechTestProjectile>();
            if(missile!=null)(typeof(MechTestProjectile).GetField("seekerRuntime",Private).GetValue(missile) as IDisposable)?.Dispose();
            Time.fixedDeltaTime=previous;UnityEngine.Object.DestroyImmediate(body);UnityEngine.Object.DestroyImmediate(owner);
        }
    }

    [UnityTest] public IEnumerator PassiveLongRangeFlightUsesRealRadiationAndTerminalSeeker()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float previous=Time.captureDeltaTime;Time.captureDeltaTime=.1f;
        try
        {
            foreach(string number in MechLongRangeMissileSetup.Numbers)
            {
                var owner=new GameObject("Silent long-range launcher");var emitter=new GameObject("Long-range emitting target");GameObject body=null;
                try
                {
                    float range=number=="4310"?220000:200000;
                    owner.AddComponent<CombatTestTarget>().ConfigureIdentity("OWN",1);
                    var radar=owner.AddComponent<MechRadarController>();
                    emitter.transform.position=Vector3.forward*range;emitter.transform.rotation=Quaternion.LookRotation(Vector3.back);
                    emitter.AddComponent<SphereCollider>().radius=10;
                    var victim=emitter.AddComponent<CombatTestTarget>();victim.ConfigureIdentity("EMITTER",2);victim.Configure(null,100000,false,0,0);victim.ConfigureSignatures(100,1,1);
                    var source=emitter.AddComponent<MechRadarController>();source.SetTrackOperationMode(true);
                    double warm=radar.SimTimeSeconds;
                    while(radar.SimTimeSeconds-warm<6){yield return null;if(source.TryGetBestTrack(out var observed))source.TryAssignFireControlTrack(observed.TrackId);}
                    // One precise independent prelaunch 3D estimate. No target truth
                    // or synthetic measurements are fed to the missile after launch.
                    var driver=owner.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();
                    var context=driver.GetType().GetField("trackContext",Private).GetValue(driver);
                    var manager=(ACunicombat.Tracking.RadarTrackManager)context.GetType().GetField("tracks",Private).GetValue(context);
                    using(var measurements=new Unity.Collections.NativeList<ACunicombat.Sensing.Contracts.Measurement>(1,Unity.Collections.Allocator.Temp))
                    for(int i=0;i<3;i++)
                    {
                        double epoch=radar.SimTimeSeconds-(2-i)*.04;measurements.Clear();
                        measurements.Add(new ACunicombat.Sensing.Contracts.Measurement{SensorId=driver.PrimaryRadarIdentity,Timestamp=epoch,Range=range,
                            Direction=new Unity.Mathematics.float3(0,0,1),RangeVar=4,AngleVar=1e-10f,VelocityVar=1,LosRateVar=1e-10f,SnrDb=40,
                            ObjectClass=ACunicombat.Sensing.Contracts.TrackObjectClass.AirVehicle,ClassificationConfidence=1});
                        manager.Process(measurements,Unity.Mathematics.double3.zero,epoch);
                    }
                    Assert.IsTrue(radar.TryGetBestTrack(out var track));radar.SetRadarPowerEnabled(false);
                    while(radar.DataLinkPolicy!=MechDataLinkPolicy.Silent)radar.CycleDataLinkPolicy();
                    Assert.IsTrue(radar.TryAssociatePassiveTarget(track,out _),number+" must receive real radiation at launch range");
                    var p=Part(number).WeaponPerformance;var profile=p.missileFlightProfile;
                    body=new GameObject("Long-range "+number);body.transform.position=Vector3.forward*20;
                    var missile=body.AddComponent<MechTestProjectile>();
                    missile.Configure(owner.transform,radar,track.TrackId,TestWeaponKind.Missile,Vector3.forward*profile.EjectionSpeedMps,p.directHitDamage,.2f,
                        profile.MaximumFlightTimeSeconds,missileProfile:profile,hasInitialGuidanceEstimate:true,initialGuidancePosition:emitter.transform.position,initialGuidanceUncertaintyMeters:2);
                    missile.EnablePassiveRfHoming();Assert.IsTrue(missile.PassiveRfHomingEnabled);
                    double start=radar.SimTimeSeconds;bool active=false,locked=false;float minRange=range;double nextLog=start;float pulseTime=-1;string pulseReason=null;
                    while(missile!=null&&victim.Health>=victim.MaxHealth&&radar.SimTimeSeconds-start<profile.MaximumFlightTimeSeconds)
                    {
                        yield return null;if(missile==null)break;
                        active|=missile.ActiveSeekerEnabled;locked|=missile.SeekerLocked;
                        pulseTime=missile.SecondPulseIgnitionTime;pulseReason=missile.SecondPulseIgnitionReason;
                        minRange=Mathf.Min(minRange,Vector3.Distance(missile.transform.position,emitter.transform.position));
                        if(radar.SimTimeSeconds>=nextLog){nextLog+=5;TestContext.WriteLine(number+" t="+(radar.SimTimeSeconds-start)+" range="+minRange+" rcs="+missile.RcsPropellantMassRemainingKg+" passiveSeen="+typeof(MechTestProjectile).GetField("passiveLastSeen",Private).GetValue(missile)+" seeker="+active+" sourceSTT="+source.HasStt);}
                    }
                    TestContext.WriteLine(number+" range="+range+" time="+(radar.SimTimeSeconds-start)+" minRange="+minRange+" active="+active+" lock="+locked+" damage="+(victim.MaxHealth-victim.Health));
                    Assert.IsFalse(radar.RadarPowerEnabled);Assert.IsTrue(active&&locked,"Must autonomously acquire at terminal range.");
                    Assert.Less(victim.Health,victim.MaxHealth,"Real terminal detonation must damage the target.");
                    if(profile.AdaptiveSecondPulse){Assert.Greater(pulseTime,profile.MainBurnDurationSeconds+profile.SecondStageIgnitionDelaySeconds);Assert.IsNotNull(pulseReason);TestContext.WriteLine("adaptive pulse at="+pulseTime+" reason="+pulseReason);}
                }
                finally{if(body!=null)UnityEngine.Object.DestroyImmediate(body);UnityEngine.Object.DestroyImmediate(emitter);UnityEngine.Object.DestroyImmediate(owner);}
                yield return null;
            }
        }
        finally{Time.captureDeltaTime=previous;}
        yield return new ExitPlayMode();
    }
}
