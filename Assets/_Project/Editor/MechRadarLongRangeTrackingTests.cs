using System;
using System.Linq;
using ACunicombat.Mechs;
using ACunicombat.Sensors;
using ACunicombat.Tracking;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Mathematics;
public sealed class MechRadarLongRangeTrackingTests
{
    [Test] public void FloatLosNoLongerCreatesNegativeVarianceOrDivergence()
    {
        var d=new float3(.7071068286895752f,.7071068286895752f,0);var n=math.normalize((double3)d);
        Cmkf.ConvertMeasurement(double3.zero,100000,d,.5625f,.0008f,out var z,out var r);
        double v=0;for(int i=0;i<3;i++)for(int j=0;j<3;j++)v+=n[i]*r[i,j]*n[j];
        Assert.That(v,Is.GreaterThan(0));
        var state=new FilterState{Position=z};
        for(int i=0;i<3;i++)for(int j=0;j<3;j++)state.P[i,j]=100*n[i]*n[j]+.01*((i==j?1:0)-n[i]*n[j]);
        for(int i=3;i<6;i++)state.P[i,i]=100;
        for(int i=0;i<100;i++){Assert.That(Cmkf.TryUpdatePosition(ref state,z,r),Is.True);Assert.That(Cmkf.Healthy(state),Is.True);}
    }
    [Test] public void InvalidMeasurementCannotMutateHealthyFilter()
    {
        var state=new FilterState{Position=new double3(1,2,3)};for(int i=0;i<6;i++)state.P[i,i]=1;
        var bad=new Matrix6();bad[0,0]=-2;bad[1,1]=1;bad[2,2]=1;
        Assert.That(Cmkf.TryUpdatePosition(ref state,new double3(1e9),bad),Is.False);
        Assert.That(state.Position,Is.EqualTo(new double3(1,2,3)));Assert.That(Cmkf.Healthy(state),Is.True);
    }
    [Test] public void CorrelatedInnovationGateUsesNarrowRadialAxis()
    {
        var state=new FilterState();for(int i=0;i<6;i++)state.P[i,i]=1;
        var d=new float3(.7071068286895752f,.7071068286895752f,0);
        Cmkf.ConvertMeasurement(double3.zero,100000,d,1,.0008f,out _,out var r);
        Assert.That(Cmkf.TryCalculatePositionInnovationNis(state,new double3(1000,1000,0),r,out var nis),Is.True);
        Assert.That(nis,Is.GreaterThan(25));
    }
    [UnityEngine.TestTools.UnityTest]
    public System.Collections.IEnumerator LiveUnityFourMechsUseProductionRadarAndDriver()
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);
        yield return new UnityEngine.TestTools.EnterPlayMode();
        float oldCapture=UnityEngine.Time.captureDeltaTime;UnityEngine.Time.captureDeltaTime=.04f;
        var objects=new System.Collections.Generic.List<UnityEngine.GameObject>();
        try
        {
            var positions=new[]{new UnityEngine.Vector3(-12000,-12000,40000),new UnityEngine.Vector3(-20000,15000,60000),new UnityEngine.Vector3(25000,-20000,60000),new UnityEngine.Vector3(35000,27000,90000)};
            var targets=new CombatTestTarget[4];
            for(int i=0;i<4;i++)
            {
                var go=new UnityEngine.GameObject("Long range mech "+i);objects.Add(go);go.transform.position=positions[i];
                targets[i]=go.AddComponent<CombatTestTarget>();targets[i].ConfigureLinearFlight(go.transform,5000,UnityEngine.Vector3.right,i%2==0?100:70,false);
                targets[i].ConfigureIdentity("Long range "+i,i<2?1:2);targets[i].ConfigureSignatures(100,1,1);
            }
            var observer=new UnityEngine.GameObject("Regression observer");objects.Add(observer);
            var radar=observer.AddComponent<MechRadarController>();radar.SetTrackOperationMode(true);
            var seen=new System.Collections.Generic.HashSet<TrackId>();
            var tracks=new TrackSnapshot[32];float start=UnityEngine.Time.time;int maxCount=0;double maxSpeed=0;ulong seq=100000;
            while(UnityEngine.Time.time-start<30)
            {
                yield return new UnityEngine.WaitForFixedUpdate();
                var driver=observer.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();
                if(driver==null)continue;
                int phase=(int)((UnityEngine.Time.time-start)/.04f);
                // Controlled pointing commands; all observations still come from actual scene entities.
                // No measurements, associations or public tracks are injected by this fixture.
                if(phase%2==0)
                {
                    int target=phase/2%4;var direction=(float3)targets[target].transform.position.normalized;
                    if((phase/8)%2==0)direction=math.mul(quaternion.RotateY(.2f),direction);
                    driver.ScheduleRadarDwell(new Dwell{SensorId=driver.PrimaryRadarIdentity,DueTime=radar.SimTimeSeconds,
                        DwellTimeSeconds=.008f,BeamDirection=direction,BeamWidthRadians=new MechRadarPerformance().Physical.BeamWidthRadians,
                        Priority=DwellPriority.TrackWhileScan,Sequence=seq++});
                }
                int count=radar.CopyTracks(tracks);maxCount=Math.Max(maxCount,count);
                Assert.That(count,Is.LessThanOrEqualTo(4),$"live scene t={UnityEngine.Time.time-start} tracks={count}");
                for(int i=0;i<count;i++)
                {
                    seen.Add(tracks[i].TrackId);
                    maxSpeed=math.max(maxSpeed,math.length(tracks[i].Velocity));
                    seen.Add(tracks[i].TrackId);
                Assert.That(math.all(math.isfinite(tracks[i].Velocity)),Is.True);
                    Assert.That(math.length(tracks[i].Velocity),Is.LessThan(1000));
                    for(int axis=0;axis<6;axis++)Assert.That(tracks[i].GetCovariance(axis,axis),Is.GreaterThanOrEqualTo(0));
                }
            }
            Assert.That(radar.CopyTracks(tracks),Is.EqualTo(4));
            Assert.That(seen.Count,Is.EqualTo(4),"Live scene must retain four identities, not silently replace tracks.");
            TestContext.WriteLine($"Live Unity four mech scene: maxTracks={maxCount}, maxSpeed={maxSpeed:F3} m/s");
        }
        finally {UnityEngine.Time.captureDeltaTime=oldCapture;foreach(var go in objects)if(go!=null)UnityEngine.Object.DestroyImmediate(go);}
        yield return new UnityEngine.TestTools.ExitPlayMode();
    }

    private static KinematicState State(double3 p,double3 v)=>new KinematicState{PositionWorld=p,VelocityWorld=v,Rotation=quaternion.identity,Mass=10000};
    [TestCase(1)] [TestCase(7)] [TestCase(42)] [TestCase(1234)]
    public void FourMovingPlatformsAt40To100Km_AlternatingMainAndSideLobes(int seed)
    {
        using var world=new SimWorld();using var grid=new SpatialHashGrid(SimWorld.MaxEntities*2);using var scheduler=new DwellScheduler(512);
        using var sensor=new RadarSensorSystem(world,grid,scheduler){SessionSeed=(uint)seed};var tracker=new RadarTrackManager();var diagnostics=new System.Collections.Generic.List<string>();
        tracker.Diagnostic+=d=>{if(d.Kind==RadarTrackDiagnosticKind.TrackAllocated||d.Kind==RadarTrackDiagnosticKind.AssociationAccepted)diagnostics.Add($"{d.Timestamp} {d.Kind} m={d.MeasurementIndex} id={d.TrackId} pos={d.MeasurementPosition} nis={d.NormalizedDistanceSquared}");};
        var h=new MechRadarPerformance().Physical;h.AntennaDutyCycle=1;
        var observerVelocity=new double3(30,5,50);var owner=world.Register(State(double3.zero,observerVelocity));
        var id=new SensorId(new Handle(0,1));
        sensor.RegisterSensor(new RadarSensorProfile{SensorId=id,OwnerEntityId=owner,EmitterId=new EmitterId(new Handle(0,1)),Physical=h,
            DwellConfig=h.BuildDwell(.008f,h.BeamWidthRadians),MaxRangeMeters=h.EstimateRangeMeters(1,.008f),ClutterPowerDb=-300,SidelobeLevelDb=-25,
            Capability=SensorCapability.Range|SensorCapability.Doppler,Resolutions=new ResolutionMerge.Resolutions{RangeMeters=7.5f,AngleRadians=.001f,VelocityMps=2}});
        var initial=new[]{new double3(-12000,-12000,40000),new double3(-20000,15000,60000),new double3(25000,-20000,60000),new double3(35000,27000,90000)};
        var velocities=new[]{new double3(100,15,-60),new double3(-70,5,90),new double3(120,-20,30),new double3(-80,25,-100)};
        var entities=new SimEntityId[4];for(int i=0;i<4;i++){entities[i]=world.Register(State(initial[i],velocities[i]));sensor.RegisterTarget(new RadarTargetProfile{EntityId=entities[i],RadarCrossSectionSquareMeters=100,ObjectClass=TrackObjectClass.AirVehicle,ClassificationSignature=1});}
        var seen=new System.Collections.Generic.HashSet<TrackId>();
        var tracks=new TrackSnapshot[32];float maxSpeed=0;int maxCount=0;ulong sequence=0;
        for(int tick=1;tick<=1000;tick++)
        {
            double time=tick*.04;var observer=observerVelocity*time;world.TrySetState(owner,State(observer,observerVelocity));
            for(int i=0;i<4;i++)world.TrySetState(entities[i],State(initial[i]+velocities[i]*time,velocities[i]));
            for(int i=0;i<4;i++)
            {
                var direction=(float3)math.normalize(initial[i]+velocities[i]*time-observer);
                if(tick%2==0)direction=math.mul(quaternion.RotateY(.2f),direction);
                sensor.ScheduleDwell(new Dwell{SensorId=id,DueTime=time,DwellTimeSeconds=.008f,BeamDirection=direction,BeamWidthRadians=h.BeamWidthRadians,Sequence=++sequence});
            }
            sensor.Tick(time);tracker.Process(sensor.Detections,observer,observerVelocity,time);
            int count=tracker.CopyTracks(tracks);maxCount=Math.Max(maxCount,count);
            Assert.That(count,Is.LessThanOrEqualTo(4),$"seed={seed} tick={tick} duplicate tracks\n"+string.Join("\n",diagnostics.TakeLast(30)));
            for(int i=0;i<count;i++)
            {
                seen.Add(tracks[i].TrackId);
                Assert.That(math.all(math.isfinite(tracks[i].Velocity)),Is.True);
                float speed=(float)math.length(tracks[i].Velocity);maxSpeed=math.max(maxSpeed,speed);
                Assert.That(speed,Is.LessThan(1000),$"seed={seed} tick={tick} unphysical speed");
                for(int axis=0;axis<6;axis++)Assert.That(tracks[i].GetCovariance(axis,axis),Is.GreaterThanOrEqualTo(0));
            }
        }
        Assert.That(seen.Count,Is.EqualTo(4),"Must not recreate targets under new TrackIds.");
        Assert.That(tracker.Count,Is.EqualTo(4));int finalCount=tracker.CopyTracks(tracks);
        for(int target=0;target<4;target++)
        {
            var truth=initial[target]+velocities[target]*40;int nearest=Enumerable.Range(0,finalCount).OrderBy(i=>math.distance(tracks[i].Position,truth)).First();
            Assert.That(math.distance(tracks[nearest].Position,truth),Is.LessThan(100));
            Assert.That(math.distance(tracks[nearest].Velocity,velocities[target]),Is.LessThan(50));
        }
        TestContext.WriteLine($"seed={seed} maxTracks={maxCount} maxSpeed={maxSpeed} finalTracks={finalCount}");
    }
}
