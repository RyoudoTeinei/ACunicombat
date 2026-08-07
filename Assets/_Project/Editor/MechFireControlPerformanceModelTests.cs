using ACunicombat.Mechs;
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
}
