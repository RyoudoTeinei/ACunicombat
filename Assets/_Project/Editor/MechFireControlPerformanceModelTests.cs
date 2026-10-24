using System;
using ACunicombat.Sensors;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using ACunicombat.Simulation.Kinematics;
using Unity.Mathematics;
using UnityEngine;
using ACunicombat.Mechs;
using ACunicombat.Tracking;
using NUnit.Framework;

namespace ACunicombat.Editor.Tests
{
	public sealed class MechFireControlPerformanceModelTests
	{
		[Test]
		public void ReferenceStats_PreserveExistingRadarTimingAndNoiseScale()
		{
			var profile = MechFireControlPerformanceModel.FromStats(new MechAggregatedStats
			{
				radarRangeKm = 42f,
				sensorPrecision = MechFireControlPerformanceModel.ReferenceSensorPrecision,
				radarRefreshHz = MechFireControlPerformanceModel.ReferenceRadarRefreshHz,
				fireControlQuality = MechFireControlPerformanceModel.ReferenceFireControlQuality
			});

			Assert.That(profile.RadarMaximumRangeMeters, Is.EqualTo(new MechRadarPerformance().Physical.EstimateRangeMeters(1f,.008f)).Within(.01f));
			Assert.That(profile.MeasurementPrecisionScale, Is.EqualTo(1f).Within(0.0001f));
			Assert.That(profile.SearchIntervalSeconds, Is.EqualTo(0.04f).Within(0.0001f));
			Assert.That(profile.TwsRevisitIntervalSeconds, Is.EqualTo(0.24f).Within(0.0001f));
			Assert.That(profile.SttRevisitIntervalSeconds, Is.EqualTo(0.035f).Within(0.0001f));
			Assert.That(profile.FireSolutionCovarianceScale, Is.EqualTo(1f).Within(0.0001f));
		}

		[Test]
		public void BetterSensorAndFireControlStats_ReducePredictionError()
		{
			var weak = MechFireControlPerformanceModel.FromStats(Stats(25f, 1f, 35f));
			var strong = MechFireControlPerformanceModel.FromStats(Stats(85f, 8f, 90f));

			float weakError = MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
				weak,
				20000f,
				MechFireControlTrackMode.SingleTargetTrack);
			float strongError = MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
				strong,
				20000f,
				MechFireControlTrackMode.SingleTargetTrack);

			Assert.That(strongError, Is.LessThan(weakError));
		}

		[Test]
		public void PredictionError_GrowsWithRangeAndIsLowerInStt()
		{
			var profile = MechFireControlPerformanceModel.FromStats(
				Stats(38f, 2.5f, 65f));

			float nearStt = MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
				profile,
				5000f,
				MechFireControlTrackMode.SingleTargetTrack);
			float farStt = MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
				profile,
				30000f,
				MechFireControlTrackMode.SingleTargetTrack);
			float farTws = MechFireControlPerformanceModel.EstimatePredictionErrorMeters(
				profile,
				30000f,
				MechFireControlTrackMode.TrackWhileScan);

			Assert.That(farStt, Is.GreaterThan(nearStt));
			Assert.That(farStt, Is.LessThan(farTws));
		}

		[Test]
		public void SpecializedRadar_DoesNotOverrideFireControlComputer()
		{
			var stats = SpecializedStats();
			var originalFireControl = MechFireControlPerformanceModel.BuildFireControlProfile(
				stats,
				MechFireControlPerformanceModel.BuildRadarProfile(stats));

			stats.radarPerformance.referenceDetectionRangeKm = 120f;
			stats.radarPerformance.trackCapacity = 24;
			stats.radarPerformance.twsUpdateRateHz = 12f;
			var modifiedFireControl = MechFireControlPerformanceModel.BuildFireControlProfile(
				stats,
				MechFireControlPerformanceModel.BuildRadarProfile(stats));

			Assert.That(modifiedFireControl.GuidanceChannelCapacity, Is.EqualTo(originalFireControl.GuidanceChannelCapacity));
			Assert.That(modifiedFireControl.MaximumPredictionTimeSeconds, Is.EqualTo(originalFireControl.MaximumPredictionTimeSeconds));
			Assert.That(modifiedFireControl.NominalAngularErrorMilliradians, Is.EqualTo(originalFireControl.NominalAngularErrorMilliradians));
		}

		[Test]
		public void SpecializedFireControl_DoesNotOverrideRadar()
		{
			var stats = SpecializedStats();
			var originalRadar = MechFireControlPerformanceModel.BuildRadarProfile(stats);

			stats.fireControlPerformance.baseSolutionErrorMrad = 0.08f;
			stats.fireControlPerformance.guidanceChannelCapacity = 12;
			stats.fireControlPerformance.maximumPredictionTimeS = 10f;
			var modifiedRadar = MechFireControlPerformanceModel.BuildRadarProfile(stats);

			Assert.That(modifiedRadar.MaximumRangeMeters, Is.EqualTo(originalRadar.MaximumRangeMeters));
			Assert.That(modifiedRadar.TrackCapacity, Is.EqualTo(originalRadar.TrackCapacity));
			Assert.That(modifiedRadar.TwsRevisitIntervalSeconds, Is.EqualTo(originalRadar.TwsRevisitIntervalSeconds));
		}

		private static MechAggregatedStats SpecializedStats() => new MechAggregatedStats
		{
			radarPerformance = new MechRadarPerformance
			{
				referenceDetectionRangeKm = 42f,
				stableTrackRangeKm = 30f,
				trackCapacity = 8,
				rangeAccuracyM = 12f,
				angularAccuracyMrad = 26.17994f,
				radialVelocityAccuracyMps = 2f,
				searchDwellRateHz = 25f,
				twsUpdateRateHz = 4.166667f,
				sttUpdateRateHz = 28.57143f
			},
			fireControlPerformance = new MechFireControlPerformance
			{
				baseSolutionErrorMrad = 0.262f,
				maximumPredictionTimeS = 6f,
				simultaneousSolutionCapacity = 8,
				weaponControlChannels = 4,
				guidanceChannelCapacity = 3,
				multiTargetEngagementCapacity = 3,
				solutionUpdateRateHz = 30f,
				solutionLatencyMs = 35f
			}
		};

		private static MechAggregatedStats Stats(
			float sensorPrecision,
			float radarRefreshHz,
			float fireControlQuality) => new MechAggregatedStats
		{
			radarRangeKm = 42f,
			sensorPrecision = sensorPrecision,
			radarRefreshHz = radarRefreshHz,
			fireControlQuality = fireControlQuality
		};
	}

	public sealed class RadarTrackManagerCapacityTests
	{
		[TestCase(8, 8)]
		[TestCase(64, RadarTrackManager.MaxTracks)]
		[TestCase(0, 1)]
		public void ConfigureLimits_UsesInstalledCapacityWithinSafetyLimit(
			int requested,
			int expected)
		{
			var manager = new RadarTrackManager();
			manager.ConfigureLimits(requested, 3.0);
			Assert.That(manager.Capacity, Is.EqualTo(expected));
		}
	}
}

public sealed class MechRadarPhysicalIntegrationTests
{
    [Test] public void RadarEquation_UsesPowerApertureNoiseRcsAndIntegration()
    {
        var h=new ACunicombat.Mechs.MechRadarPerformance().Physical;
        float baseline=h.EstimateRangeMeters(1,.008f);
        var stronger=h;stronger.AverageTransmitPowerWatts*=16;
        Assert.That(stronger.EstimateRangeMeters(1,.008f)/baseline,Is.EqualTo(2).Within(.0001));
        var larger=h;larger.ApertureDiameterMeters*=2;
        Assert.That(larger.EstimateRangeMeters(1,.008f)/baseline,Is.EqualTo(2).Within(.0001));
        var noisy=h;noisy.SystemNoiseTemperatureKelvin*=16;
        Assert.That(noisy.EstimateRangeMeters(1,.008f)/baseline,Is.EqualTo(.5).Within(.0001));
        Assert.That(h.EstimateRangeMeters(16,.008f)/baseline,Is.EqualTo(2).Within(.0001));
        Assert.That(h.EstimateRangeMeters(1,.128f)/baseline,Is.EqualTo(2).Within(.0001));
        var c=h.BuildDwell(.008f,h.BeamWidthRadians);
        Assert.That(RadarMath.ReceivedPowerDb(c,0,200000)-RadarMath.ReceivedPowerDb(c,0,100000),Is.EqualTo(-12.0412).Within(.001));
    }
    [Test] public void LegacyDistanceFields_CannotImposeDetectionOrTrackingLimits()
    {
        var s=new ACunicombat.Mechs.MechAggregatedStats();
        var before=ACunicombat.Mechs.MechFireControlPerformanceModel.BuildRadarProfile(s);
        s.radarRangeKm=.01f;s.radarPerformance.referenceDetectionRangeKm=.01f;s.radarPerformance.stableTrackRangeKm=.001f;
        var after=ACunicombat.Mechs.MechFireControlPerformanceModel.BuildRadarProfile(s);
        Assert.That(after.MaximumRangeMeters,Is.EqualTo(before.MaximumRangeMeters));
        Assert.That(after.StableTrackRangeMeters,Is.EqualTo(before.StableTrackRangeMeters));
        Assert.That(after.StableTrackRangeMeters/after.MaximumRangeMeters,Is.Not.EqualTo(.75f).Within(.001));
    }
    [TestCase(50000,1,true)]
    [TestCase(200000,1,true)]
    [TestCase(2000000,10000,true)]
    [TestCase(2000000,.001f,false)]
    public void RealSensor_DetectsByReturnStrengthBeyondOldRange(double range,float rcs,bool expected)
    {
        var h=new ACunicombat.Mechs.MechRadarPerformance().Physical;
        var result=Observe(h,range,rcs,.008f,1);
        Assert.That(result.count,Is.EqualTo(expected?1:0));
        if(expected)Assert.That(result.range,Is.EqualTo(range).Within(range*.001));
    }
    [Test] public void RealSensor_DwellAndDamageChangeMeasuredSnr()
    {
        var h=new ACunicombat.Mechs.MechRadarPerformance().Physical;
        var search=Observe(h,100000,1,.008f,1);
        var track=Observe(h,100000,1,.028f,1);
        var damaged=Observe(h,100000,1,.008f,.25f);
        Assert.That(search.count+track.count+damaged.count,Is.EqualTo(3));
        Assert.That(track.snr-search.snr,Is.EqualTo(10*Math.Log10(3.5)).Within(.001));
        Assert.That(damaged.snr-search.snr,Is.EqualTo(10*Math.Log10(.25)).Within(.001));
    }
    [Test] public void LockUsesPublicTrackPrecisionAndAge_NotKilometers()
    {
        var p=ACunicombat.Mechs.MechFireControlPerformanceModel.BuildRadarProfile(new ACunicombat.Mechs.MechAggregatedStats());
        var t=new TrackSnapshot{Position=new double3(0,0,1500000),Lifecycle=TrackLifecycle.Confirmed,LastUpdateTime=10,LastPredictTime=10};
        for(int i=0;i<3;i++)t.SetCovariance(i,i,100);
        Assert.That(ACunicombat.Mechs.MechFireControlPerformanceModel.IsTrackPreciseEnoughForLock(t,Vector3.zero,10,p),Is.True);
        for(int i=0;i<3;i++)t.SetCovariance(i,i,1e12);
        Assert.That(ACunicombat.Mechs.MechFireControlPerformanceModel.IsTrackPreciseEnoughForLock(t,Vector3.zero,10,p),Is.False);
        for(int i=0;i<3;i++)t.SetCovariance(i,i,100);
        Assert.That(ACunicombat.Mechs.MechFireControlPerformanceModel.IsTrackPreciseEnoughForLock(t,Vector3.zero,20,p),Is.False);
    }
    [Test] public void PredictionUncertaintyGrowth_PreventsLockOnImpreciseFreshTrack()
    {
        var p=ACunicombat.Mechs.MechFireControlPerformanceModel.BuildRadarProfile(new ACunicombat.Mechs.MechAggregatedStats());
        var t=new TrackSnapshot{Position=new double3(0,0,1500000),Lifecycle=TrackLifecycle.Confirmed,LastUpdateTime=10.5,LastPredictTime=10};
        for(int i=0;i<3;i++){t.SetCovariance(i,i,100);t.SetCovariance(i+3,i+3,1e12);}
        Assert.That(ACunicombat.Mechs.MechFireControlPerformanceModel.IsTrackPreciseEnoughForLock(t,Vector3.zero,10.5,p),Is.False);
    }
    [Test] public void ShippedRadarAsset_LoadsPhysicalHardwareAndClonesIt()
    {
        var part=UnityEditor.AssetDatabase.LoadAssetAtPath<ACunicombat.Mechs.MechPartDefinition>("Assets/_Project/Data/Mechs/AC6ImportedSamples/AC6_RDR_Test_Array.asset");
        Assert.That(part,Is.Not.Null);
        var hardware=part.RadarPerformance.Clone().Physical;
        Assert.That(hardware.IsValid,Is.True);
        Assert.That(hardware.AverageTransmitPowerWatts,Is.EqualTo(250000));
        Assert.That(hardware.EstimateRangeMeters(1,.008f),Is.InRange(720000f,722000f));
    }
    private static (int count,float snr,float range) Observe(RadarPhysicalParameters physical,double range,float rcs,float dwell,float efficiency)
    {
        using var world=new SimWorld();using var grid=new SpatialHashGrid(SimWorld.MaxEntities*2);
        using var scheduler=new DwellScheduler(32);using var sensor=new RadarSensorSystem(world,grid,scheduler){SessionSeed=1234};
        var owner=world.Register(new KinematicState{Rotation=quaternion.identity,Mass=1});
        var target=world.Register(new KinematicState{PositionWorld=new double3(0,0,range),Rotation=quaternion.identity,Mass=1});
        var id=new SensorId(new Handle(0,1));
        sensor.RegisterSensor(new RadarSensorProfile{SensorId=id,OwnerEntityId=owner,EmitterId=new EmitterId(new Handle(0,1)),
            Physical=physical,MaxRangeMeters=42000,ClutterPowerDb=-300,Capability=SensorCapability.Range|SensorCapability.Doppler,
            DwellConfig=physical.BuildDwell(dwell,physical.BeamWidthRadians),Resolutions=new ResolutionMerge.Resolutions{RangeMeters=1,AngleRadians=.001f,VelocityMps=1}});
        sensor.SetSensorEfficiency(id,efficiency);
        sensor.RegisterTarget(new RadarTargetProfile{EntityId=target,RadarCrossSectionSquareMeters=rcs});
        sensor.ScheduleDwell(new Dwell{SensorId=id,DueTime=.04,DwellTimeSeconds=dwell,BeamDirection=new float3(0,0,1),BeamWidthRadians=physical.BeamWidthRadians,Sequence=1});
        sensor.Tick(.04);
        return sensor.Detections.Length==0?(0,0,0):(sensor.Detections.Length,sensor.Detections[0].SnrDb,sensor.Detections[0].Range);
    }
}
