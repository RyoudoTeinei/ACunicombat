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

			Assert.That(profile.RadarMaximumRangeMeters, Is.EqualTo(42000f).Within(0.01f));
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
