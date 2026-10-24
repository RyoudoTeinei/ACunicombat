using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using ACunicombat.Simulation.Kinematics;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.SensorBench
{
    public sealed class SensorTargetGenerationTests
    {
        [Test] public void StaleUnregisterCannotHideReplacementFromRadarOrOptical()
        {
            using var world = new SimWorld(); using var grid = new SpatialHashGrid(512);
            using var scheduler = new DwellScheduler(32); using var radar = new RadarSensorSystem(world,grid,scheduler);
            using var optical = new ElectroOpticalSensorSystem(world);
            var observer = world.Register(new KinematicState());
            var old = world.Register(new KinematicState()); world.Unregister(old);
            var current = world.Register(new KinematicState { PositionWorld = new double3(0,0,1000) });
            Assert.That(current.Value.Index,Is.EqualTo(old.Value.Index));
            var profile = SensorBenchTests.Profile(0,observer); radar.RegisterSensor(profile);
            radar.RegisterTarget(new RadarTargetProfile { EntityId=current, RadarCrossSectionSquareMeters=18 });
            optical.RegisterTarget(new ElectroOpticalTargetProfile { EntityId=current, SignatureDb=80,
                ReferenceDiameterMeters=12, ObjectClass=TrackObjectClass.AirVehicle, ClassificationSignature=1 });
            radar.UnregisterTarget(old); optical.UnregisterTarget(old);
            radar.ScheduleDwell(new Dwell { SensorId=profile.SensorId, DueTime=.04, DwellTimeSeconds=.008f,
                BeamDirection=new float3(0,0,1), BeamWidthRadians=.2f, Priority=DwellPriority.TrackWhileScan, Sequence=1 });
            radar.Tick(.04);
            optical.Tick(new ElectroOpticalReceiverProfile { SensorId=profile.SensorId,OwnerEntityId=observer,
                Forward=new float3(0,0,1),FieldOfViewRadians=1,MaximumRangeMeters=10000,SensitivityFloorDb=-60,
                BearingSigmaRadians=.001f,TrackCapacity=4,ClassificationScale=1 },.04);
            Assert.That(radar.Detections.Length,Is.EqualTo(1)); Assert.That(optical.Observations.Length,Is.EqualTo(1));
        }
        [Test] public void SensorEfficiencyChangesActualRangeAndStopsQueuedEmissions()
        {
            using var world=new SimWorld();using var grid=new SpatialHashGrid(512);using var scheduler=new DwellScheduler(32);
            using var radar=new RadarSensorSystem(world,grid,scheduler);
            var observer=world.Register(new KinematicState());var target=world.Register(new KinematicState{PositionWorld=new double3(0,0,1500)});
            var profile=SensorBenchTests.Profile(0,observer);profile.MaxRangeMeters=2000;radar.RegisterSensor(profile);
            radar.RegisterTarget(new RadarTargetProfile{EntityId=target,RadarCrossSectionSquareMeters=18});
            void Scan(double time,uint sequence)
            {radar.ScheduleDwell(new Dwell{SensorId=profile.SensorId,DueTime=time,DwellTimeSeconds=.008f,BeamDirection=new float3(0,0,1),BeamWidthRadians=.2f,Priority=DwellPriority.TrackWhileScan,Sequence=sequence});radar.Tick(time);}
            Scan(.04,1);Assert.That(radar.Detections.Length,Is.EqualTo(1));
            Assert.That(radar.SetSensorEfficiency(profile.SensorId,.25f),Is.True);Scan(.08,2);Assert.That(radar.Detections.Length,Is.Zero);
            radar.ScheduleDwell(new Dwell{SensorId=profile.SensorId,DueTime=.12,DwellTimeSeconds=.008f,BeamDirection=new float3(0,0,1),BeamWidthRadians=.2f,Priority=DwellPriority.TrackWhileScan,Sequence=3});
            radar.SetSensorEfficiency(profile.SensorId,0);radar.Tick(.12);Assert.That(radar.Detections.Length,Is.Zero);Assert.That(radar.Emissions.Length,Is.Zero);
            radar.SetSensorEfficiency(profile.SensorId,1);Scan(.16,4);Assert.That(radar.Detections.Length,Is.EqualTo(1));
            var stale=profile.SensorId;profile.SensorId=new SensorId(new Handle(stale.Value.Index,(byte)(stale.Value.Generation+1)));radar.RegisterSensor(profile);
            Assert.That(radar.SetSensorEfficiency(stale,0),Is.False);Scan(.20,5);Assert.That(radar.Detections.Length,Is.EqualTo(1));
        }
        [Test] public void FailedLaserDiscardsQueuedPulseAndRepairRestoresEmission()
        {
            using var world=new SimWorld();using var laser=new LaserRangefinderSystem(world);
            var owner=world.Register(new KinematicState());var sensor=new SensorId(new Handle(0,1));
            laser.RegisterSensor(new LaserRangefinderProfile{SensorId=sensor,OwnerEntityId=owner,EmitterId=new EmitterId(new Handle(0,1)),MaximumRangeMeters=2000,BeamWidthRadians=.01f});
            laser.SchedulePulse(new LaserPulse{SensorId=sensor,Direction=new float3(0,0,1),DueTime=.04,Sequence=1});
            Assert.That(laser.SetSensorEfficiency(sensor,0),Is.True);laser.Tick(.04);Assert.That(laser.Emissions.Length,Is.Zero);
            laser.SetSensorEfficiency(sensor,1);laser.SchedulePulse(new LaserPulse{SensorId=sensor,Direction=new float3(0,0,1),DueTime=.08,Sequence=2});
            laser.Tick(.08);Assert.That(laser.Emissions.Length,Is.EqualTo(1));
        }
    }
}