using System;
using System.Linq;
using ACunicombat.Mechs;
using ACunicombat.Sensors;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Mathematics;

public sealed class MechRadarSignalSchedulingTests
{
    private SimWorld world;private SpatialHashGrid grid;private DwellScheduler scheduler;private RadarSensorSystem sensor;
    private RadarPhysicalParameters hardware;private SensorId id;private SimEntityId target;
    [SetUp] public void Setup()
    {
        world=new SimWorld();grid=new SpatialHashGrid(SimWorld.MaxEntities*2);scheduler=new DwellScheduler(64);
        sensor=new RadarSensorSystem(world,grid,scheduler){SessionSeed=1234};
        hardware=new MechRadarPerformance().Physical;hardware.AntennaDutyCycle=1;
        id=AddSensor(0,1);
        target=world.Register(State(new double3(0,0,100000)));
        sensor.RegisterTarget(new RadarTargetProfile{EntityId=target,RadarCrossSectionSquareMeters=1});
    }
    [TearDown] public void Cleanup(){sensor.Dispose();scheduler.Dispose();grid.Dispose();world.Dispose();}
    private static KinematicState State(double3 p)=>new KinematicState{PositionWorld=p,Rotation=quaternion.identity,Mass=1};
    private SensorId AddSensor(int index,byte generation)
    {
        var value=new SensorId(new Handle(index,generation));var owner=world.Register(State(double3.zero));
        sensor.RegisterSensor(new RadarSensorProfile{SensorId=value,OwnerEntityId=owner,EmitterId=new EmitterId(new Handle(index,generation)),
            Physical=hardware,DwellConfig=hardware.BuildDwell(.008f,hardware.BeamWidthRadians),ClutterPowerDb=-300,
            SidelobeLevelDb=-25,FrequencyBand=1,MaxRangeMeters=1,Capability=SensorCapability.Range|SensorCapability.Doppler,
            Resolutions=new ResolutionMerge.Resolutions{RangeMeters=1,AngleRadians=.001f,VelocityMps=1}});
        return value;
    }
    private Dwell Work(ulong sequence,float duration=.008f,DwellPriority priority=DwellPriority.WideSearch,double due=0)
        =>new Dwell{SensorId=id,DueTime=due,DwellTimeSeconds=duration,BeamDirection=new float3(0,0,1),
            BeamWidthRadians=hardware.BeamWidthRadians,Sequence=sequence,Priority=priority};
    [Test] public void ReceiverNoiseUsesKelvinBandwidthAndDbw()
    {
        var c=hardware.BuildDwell(.008f,hardware.BeamWidthRadians);
        double expected=10*Math.Log10(1.380649e-23*600*20e6);
        Assert.That(c.NoisePowerDb,Is.EqualTo(expected).Within(.0001));
        var wider=hardware;wider.SignalBandwidthHz*=2;var d=wider.BuildDwell(.008f,wider.BeamWidthRadians);
        Assert.That(d.NoisePowerDb-c.NoisePowerDb,Is.EqualTo(3.0103).Within(.001));
        Assert.That((d.IntegrationGainDb-d.NoisePowerDb)-(c.IntegrationGainDb-c.NoisePowerDb),Is.Zero.Within(.001));
    }
    private float Sample(double now,float beamAngle=0,JamContribution? jam=null)
    {
        var d=Work((ulong)(now*1000),due:now);d.BeamDirection=new float3(math.sin(beamAngle),0,math.cos(beamAngle));
        sensor.ScheduleDwell(d);if(jam.HasValue)sensor.JamInboxForEw.Add(jam.Value);sensor.Tick(now);
        return sensor.SignalDiagnostics.Last().SnrDb;
    }
    [Test] public void RealEchoHasTwoWayMainlobeAttenuationAndBoundedSidelobes()
    {
        float center=Sample(.04);float edge=Sample(.08,hardware.BeamWidthRadians*.5f);float side=Sample(.12,1f);
        Assert.That(edge-center,Is.EqualTo(-6).Within(.03));
        Assert.That(side-center,Is.EqualTo(-50).Within(.03));
    }
    private JamContribution Jam(float3 direction)=>new JamContribution{VictimSensorId=id,FrequencyBand=1,
        ArrivalDirection=direction,PowerIsDbw=true,PowerDb=hardware.ThermalNoisePowerDbw+20,
        CenterFrequencyHz=hardware.FrequencyHz,BandwidthHz=hardware.SignalBandwidthHz};
    [Test] public void RealReceiverCouplesJammerByDirectionAndFrequency()
    {
        float baseline=Sample(.04);var main=Jam(new float3(0,0,1));float jammed=Sample(.08,jam:main);
        var side=Jam(new float3(1,0,0));float sidelobe=Sample(.12,jam:side);
        var wrong=main;wrong.CenterFrequencyHz+=hardware.SignalBandwidthHz*2;float outside=Sample(.16,jam:wrong);
        Assert.That(baseline-jammed,Is.EqualTo(10*Math.Log10(101)).Within(.01));
        Assert.That(sidelobe,Is.GreaterThan(jammed+15));Assert.That(outside,Is.EqualTo(baseline).Within(.001));
    }
    [Test] public void LegacyNoiseRatioHasExplicitPhysicalConversion_AndOtherObserversAreUnaffected()
    {
        var physical=Jam(new float3(0,0,1));float a=Sample(.04,jam:physical);
        var legacy=physical;legacy.PowerIsDbw=false;legacy.PowerDb=20;float b=Sample(.08,jam:legacy);
        Assert.That(a,Is.EqualTo(b).Within(.001));
        var other=physical;other.VictimSensorId=new SensorId(new Handle(5,1));float unaffected=Sample(.12,jam:other);
        Assert.That(unaffected,Is.EqualTo(Sample(.16)).Within(.001));
    }
    [Test] public void OverlapUsesIncidentBandwidth_AndPowersAddLinearly()
    {
        Assert.That(RadarMath.SpectralOverlap(100,20,100,40),Is.EqualTo(.5));
        var j=Jam(new float3(0,0,1));float single=Sample(.04,jam:j);
        sensor.JamInboxForEw.Add(j);float doubled=Sample(.08,jam:j);
        Assert.That(single-doubled,Is.EqualTo(10*Math.Log10(201.0/101)).Within(.01));
        Assert.That(Sample(.12),Is.GreaterThan(single+19));
    }
    [Test] public void SensorCannotSpendMoreDwellTimeThanElapsedTime()
    {
        for(ulong i=1;i<=10;i++)sensor.ScheduleDwell(Work(i));
        sensor.Tick(.02);Assert.That(sensor.ExecutedPhysicalDwells,Is.EqualTo(2));Assert.That(sensor.TotalPhysicalDwellSeconds,Is.LessThanOrEqualTo(.020001));
        sensor.Tick(.04);Assert.That(sensor.ExecutedPhysicalDwells,Is.EqualTo(5));Assert.That(sensor.TotalPhysicalDwellSeconds,Is.LessThanOrEqualTo(.040001));
        Assert.That(sensor.BudgetDeferredCount,Is.GreaterThan(0));
    }
    [Test] public void HighPriorityWaitReservesBudgetInsteadOfBeingStarvedBySmallSearches()
    {
        sensor.ScheduleDwell(Work(1));sensor.ScheduleDwell(Work(2,.028f,DwellPriority.SingleTargetTrack));
        sensor.Tick(.02);Assert.That(sensor.ExecutedPhysicalDwells,Is.Zero);
        sensor.Tick(.04);var executed=sensor.Diagnostics.Where(x=>x.Result==RadarSensorSystem.DwellResult.Executed).ToArray();
        Assert.That(executed[0].Sequence,Is.EqualTo(2));Assert.That(executed[1].Sequence,Is.EqualTo(1));
    }
    [Test] public void EachRadarHasIndependentAntennaBudget()
    {
        var second=AddSensor(1,1);
        sensor.ScheduleDwell(Work(1,.028f,DwellPriority.SingleTargetTrack));var other=Work(2);other.SensorId=second;sensor.ScheduleDwell(other);
        sensor.Tick(.02);
        Assert.That(sensor.Diagnostics.Any(x=>x.Sensor.Equals(second)&&x.Result==RadarSensorSystem.DwellResult.Executed),Is.True);
        Assert.That(sensor.Diagnostics.Any(x=>x.Sensor.Equals(id)&&x.Result==RadarSensorSystem.DwellResult.Executed),Is.False);
    }
    [Test] public void QueueIsBoundedAndUrgentWorkCanReplaceSearch()
    {
        for(ulong i=1;i<=20;i++)sensor.ScheduleDwell(Work(i));
        Assert.That(scheduler.Count,Is.EqualTo(16));Assert.That(sensor.RejectedDwellCount,Is.EqualTo(4));
        sensor.ScheduleDwell(Work(21,.028f,DwellPriority.SingleTargetTrack));Assert.That(scheduler.Count,Is.EqualTo(16));
        sensor.Tick(.04);Assert.That(sensor.Diagnostics.First(x=>x.Result==RadarSensorSystem.DwellResult.Executed).Sequence,Is.EqualTo(21));
    }
    [Test] public void StaleRequestsExpireWithoutCreatingFreshMeasurements()
    {
        sensor.ScheduleDwell(Work(1));sensor.Tick(1);
        Assert.That(sensor.Detections.Length,Is.Zero);Assert.That(sensor.ExpiredDwellCount,Is.EqualTo(1));Assert.That(scheduler.Count,Is.Zero);
    }
    [Test] public void IdleTimeCannotAccumulateAnUnlimitedBurst()
    {
        for(ulong i=1;i<=16;i++)sensor.ScheduleDwell(Work(i,due:10));sensor.Tick(10);
        Assert.That(sensor.TotalPhysicalDwellSeconds,Is.LessThanOrEqualTo(.100001));Assert.That(sensor.ExecutedPhysicalDwells,Is.EqualTo(12));
    }
    [Test] public void OldGenerationDoesNotUseNewDeviceBudget()
    {
        sensor.ScheduleDwell(Work(1));sensor.UnregisterSensor(id);id=AddSensor(0,2);sensor.ScheduleDwell(Work(2));sensor.Tick(.04);
        Assert.That(sensor.ExecutedPhysicalDwells,Is.EqualTo(1));Assert.That(sensor.Diagnostics.Any(x=>x.Sequence==1&&x.Result==RadarSensorSystem.DwellResult.Unavailable),Is.True);
    }
    [Test] public void RequestedBeamCannotBeNarrowerThanPhysicalAperture()
    {
        var d=Work(1);d.BeamWidthRadians=hardware.BeamWidthRadians*.1f;sensor.ScheduleDwell(d);sensor.Tick(.04);
        Assert.That(sensor.Emissions[0].BeamWidth,Is.EqualTo(hardware.BeamWidthRadians));
    }
    [Test] public void StrongSidelobeReturnDoesNotGiveMainlobeAngularPrecision()
    {
        sensor.RegisterTarget(new RadarTargetProfile{EntityId=target,RadarCrossSectionSquareMeters=1000000});
        Sample(.04);Assert.That(sensor.Detections.Length,Is.EqualTo(1));float mainVariance=sensor.Detections[0].AngleVar;
        Sample(.08,1f);Assert.That(sensor.Detections.Length,Is.EqualTo(1));
        Assert.That(sensor.Detections[0].AngleVar,Is.GreaterThan(mainVariance*100));
        Assert.That(sensor.Detections[0].AngleVar,Is.GreaterThanOrEqualTo(hardware.BeamWidthRadians*hardware.BeamWidthRadians));
    }
    [Test] public void DiagnosticRingsRemainBoundedAndChronologicalAfterWrapping()
    {
        for(ulong i=1;i<=600;i++){sensor.ScheduleDwell(Work(i,due:i*.04));sensor.Tick(i*.04);}
        Assert.That(sensor.Diagnostics.Count,Is.EqualTo(512));Assert.That(sensor.SignalDiagnostics.Count,Is.EqualTo(512));
        Assert.That(sensor.Diagnostics[0].Sequence,Is.EqualTo(89));Assert.That(sensor.Diagnostics[511].Sequence,Is.EqualTo(600));
        Assert.That(sensor.SignalDiagnostics[0].Sequence,Is.EqualTo(89));Assert.That(sensor.SignalDiagnostics[511].Sequence,Is.EqualTo(600));
    }
    [Test] public void InvalidDwellIsRejectedBeforeQueueing()
    {
        sensor.ScheduleDwell(Work(1,float.NaN));sensor.ScheduleDwell(Work(2,.2f));sensor.ScheduleDwell(Work(3,-1));
        Assert.That(scheduler.Count,Is.Zero);Assert.That(sensor.RejectedDwellCount,Is.EqualTo(3));
    }
}
