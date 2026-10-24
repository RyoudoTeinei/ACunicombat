using System;
using System.Collections.Generic;
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
using UnityEngine;
public sealed class MechLongRangeCombatStressTests
{
    private static KinematicState State(double3 p,double3 v)=>new KinematicState{PositionWorld=p,VelocityWorld=v,Rotation=quaternion.identity,Mass=10000};
    [TestCase(100000,false,42)] [TestCase(200000,false,42)] [TestCase(300000,false,42)] [TestCase(200000,true,42)] [TestCase(300000,true,42)]
    [TestCase(100000,false,7)] [TestCase(200000,false,7)] [TestCase(300000,false,7)] [TestCase(200000,true,7)] [TestCase(300000,true,7)]
    public void LongManeuveringMultiPlatformAndIncomingSalvos(int range,bool enhanced,int seed)
    {
        int aircraft=enhanced?8:4,rounds=aircraft*2;
        using var world=new SimWorld();using var grid=new SpatialHashGrid(SimWorld.MaxEntities*2);using var scheduler=new DwellScheduler(512);
        using var sensor=new RadarSensorSystem(world,grid,scheduler){SessionSeed=(uint)seed};var tracker=new RadarTrackManager();tracker.ConfigureLimits(enhanced?32:16,3);
        var h=new MechRadarPerformance().Physical;if(enhanced){h.AverageTransmitPowerWatts*=4;h.ApertureDiameterMeters*=2;}
        var owner=world.Register(State(double3.zero,new double3(100,0,0)));var id=new SensorId(new Handle(0,1));
        sensor.RegisterSensor(new RadarSensorProfile{SensorId=id,OwnerEntityId=owner,EmitterId=new EmitterId(new Handle(0,1)),Physical=h,
            DwellConfig=h.BuildDwell(.008f,h.BeamWidthRadians),MaxRangeMeters=h.EstimateRangeMeters(1,.008f),ClutterPowerDb=-300,SidelobeLevelDb=-25,
            Capability=SensorCapability.Range|SensorCapability.Doppler,Resolutions=new ResolutionMerge.Resolutions{RangeMeters=7.5f,AngleRadians=.001f,VelocityMps=2}});
        var actors=new SimEntityId[aircraft];var missiles=new SimEntityId[rounds];var shotPositions=new double3[rounds];var shotVelocities=new double3[rounds];var alive=new bool[rounds];
        var positions=new double3[aircraft];var velocities=new double3[aircraft];
        for(int i=0;i<aircraft;i++){actors[i]=world.Register(State(double3.zero,double3.zero));sensor.RegisterTarget(new RadarTargetProfile{EntityId=actors[i],RadarCrossSectionSquareMeters=100,ObjectClass=TrackObjectClass.AirVehicle,ClassificationSignature=1});}
        var aircraftIds=new HashSet<TrackId>(); var history=new Queue<string>(); tracker.Diagnostic+=d=>{if(d.Kind==RadarTrackDiagnosticKind.TrackAllocated||d.Kind==RadarTrackDiagnosticKind.TrackReleased||d.Kind==RadarTrackDiagnosticKind.TrackInitiationDeferred){history.Enqueue($"{d.Timestamp:F2} {d.Kind} {d.ObjectClass} {d.TrackId} nis={d.NormalizedDistanceSquared} pos={d.MeasurementPosition} {d.Reason}");while(history.Count>24)history.Dequeue();}}; var tracks=new TrackSnapshot[32];ulong seq=0;int launched=0,maxAircraft=0,maxMissiles=0;double maxAircraftSpeed=0;int detections=0;
        for(int tick=1;tick<=3750;tick++)
        {
            double now=tick*.04;var observer=new double3(100*now,0,0);world.TrySetState(owner,State(observer,new double3(100,0,0)));
            for(int i=0;i<aircraft;i++)
            {
                double phase=i*2*Math.PI/aircraft;
                positions[i]=new double3(Math.Cos(phase)*range*.35+2500*Math.Sin(now*.1+i),Math.Sin(phase)*range*.2+1000*Math.Sin(now*.13+i),range*(1+i*.03)-100*now);
                velocities[i]=new double3(250*Math.Cos(now*.1+i),130*Math.Cos(now*.13+i),-100);
                world.TrySetState(actors[i],State(positions[i],velocities[i]));
            }
            for(int m=0;m<rounds;m++)
            {
                double launch=20+(m/aircraft)*2+(m%aircraft)*.12;
                if(!missiles[m].IsValid&&now>=launch)
                {
                    shotPositions[m]=positions[m%aircraft];shotVelocities[m]=math.normalize(observer-shotPositions[m])*2200;
                    missiles[m]=world.Register(State(shotPositions[m],shotVelocities[m]));alive[m]=true;launched++;
                    sensor.RegisterTarget(new RadarTargetProfile{EntityId=missiles[m],RadarCrossSectionSquareMeters=.06f,ObjectClass=TrackObjectClass.Missile,ClassificationSignature=1});
                }
                if(alive[m])
                {
                    var p=shotPositions[m]+shotVelocities[m]*(now-launch);
                    if(p.z<0){sensor.UnregisterTarget(missiles[m]);world.Unregister(missiles[m]);alive[m]=false;}
                    else world.TrySetState(missiles[m],State(p,shotVelocities[m]));
                }
            }
            for(int beam=0;beam<2;beam++)
            {
                int target=(tick*2+beam)%(aircraft+rounds);double3 point;
                if(target<aircraft)
                {
                    // A 1.4-second aircraft revisit gap every 20 seconds while fire control is busy.
                    if(now>20&&now%20<1.4)continue;point=positions[target];
                }
                else {int m=target-aircraft;if(!alive[m])continue;double launch=20+(m/aircraft)*2+(m%aircraft)*.12;point=shotPositions[m]+shotVelocities[m]*(now-launch);}
                var direction=(float3)math.normalize(point-observer);if(tick%5==0)direction=math.mul(quaternion.RotateY(.12f),direction);
                sensor.ScheduleDwell(new Dwell{SensorId=id,DueTime=now,DwellTimeSeconds=.008f,BeamDirection=direction,BeamWidthRadians=h.BeamWidthRadians,Sequence=++seq});
            }
            sensor.Tick(now);detections+=sensor.Detections.Length;tracker.Process(sensor.Detections,observer,new double3(100,0,0),now);
            int count=tracker.CopyTracks(tracks),air=0,miss=0;
            for(int i=0;i<count;i++)
            {
                Assert.That(math.all(math.isfinite(tracks[i].Position))&&math.all(math.isfinite(tracks[i].Velocity)),Is.True);
                for(int axis=0;axis<6;axis++)Assert.That(tracks[i].GetCovariance(axis,axis),Is.GreaterThanOrEqualTo(0));
                if(tracks[i].ObjectClass==TrackObjectClass.AirVehicle){aircraftIds.Add(tracks[i].TrackId);air++;maxAircraftSpeed=math.max(maxAircraftSpeed,math.length(tracks[i].Velocity));Assert.That(math.length(tracks[i].Velocity),Is.LessThan(1200),$"aircraft velocity, range={range} t={now}");}
                if(tracks[i].ObjectClass==TrackObjectClass.Missile)miss++;
            }
            maxAircraft=Math.Max(maxAircraft,air);maxMissiles=Math.Max(maxMissiles,miss);
            Assert.That(air,Is.LessThanOrEqualTo(aircraft),$"duplicate aircraft range={range} enhanced={enhanced} t={now}\n"+string.Join("\n",history)+"\n"+string.Join("\n",tracks.Take(count).Where(t=>t.ObjectClass==TrackObjectClass.AirVehicle).Select(t=>$"{t.TrackId} pos={t.Position} vel={t.Velocity} nearestError={positions.Min(p=>math.distance(p,t.Position))} sigma={Math.Sqrt(t.GetCovariance(0,0)+t.GetCovariance(1,1)+t.GetCovariance(2,2))}")));
            Assert.That(miss,Is.LessThanOrEqualTo(launched),$"duplicate missiles t={now}\n"+string.Join("\n",history));
        }
        Assert.That(aircraftIds.Count,Is.EqualTo(aircraft),"Aircraft identities must survive the entire run without replacement.");Assert.That(maxAircraft,Is.EqualTo(aircraft));Assert.That(maxMissiles,Is.GreaterThan(0));
        TestContext.WriteLine($"range={range} enhanced={enhanced} seconds=150 aircraft={maxAircraft}/{aircraft} missileTracksPeak={maxMissiles}/{rounds} maxAirSpeed={maxAircraftSpeed:F1} detections={detections}");
    }
}
