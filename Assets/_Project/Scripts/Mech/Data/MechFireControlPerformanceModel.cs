using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
	public enum MechFireControlTrackMode : byte
	{
		TrackWhileScan,
		SingleTargetTrack,
		Coasting
	}

	public readonly struct MechFireControlPerformanceProfile
	{
		public MechFireControlPerformanceProfile(
			float radarMaximumRangeMeters,
			float measurementPrecisionScale,
			float searchIntervalSeconds,
			float twsRevisitIntervalSeconds,
			float sttRevisitIntervalSeconds,
			float fireSolutionCovarianceScale,
			float nominalAngularErrorMilliradians,
			float nominalLateralVelocityUncertaintyMetersPerSecond)
		{
			RadarMaximumRangeMeters = radarMaximumRangeMeters;
			MeasurementPrecisionScale = measurementPrecisionScale;
			SearchIntervalSeconds = searchIntervalSeconds;
			TwsRevisitIntervalSeconds = twsRevisitIntervalSeconds;
			SttRevisitIntervalSeconds = sttRevisitIntervalSeconds;
			FireSolutionCovarianceScale = fireSolutionCovarianceScale;
			NominalAngularErrorMilliradians = nominalAngularErrorMilliradians;
			NominalLateralVelocityUncertaintyMetersPerSecond = nominalLateralVelocityUncertaintyMetersPerSecond;
		}

		public float RadarMaximumRangeMeters { get; }
		public float MeasurementPrecisionScale { get; }
		public float SearchIntervalSeconds { get; }
		public float TwsRevisitIntervalSeconds { get; }
		public float SttRevisitIntervalSeconds { get; }
		public float FireSolutionCovarianceScale { get; }
		public float NominalAngularErrorMilliradians { get; }
		public float NominalLateralVelocityUncertaintyMetersPerSecond { get; }
	}

	/// <summary>
	/// Converts assembled sensor/fire-control attributes into both runtime radar parameters and
	/// the standardized performance estimate shown in the assembly screen.
	/// </summary>
	public static class MechFireControlPerformanceModel
	{
		public const float ReferenceSensorPrecision = 38f;
		public const float ReferenceRadarRefreshHz = 2.5f;
		public const float ReferenceFireControlQuality = 65f;
		public const float ReferenceSearchIntervalSeconds = 0.04f;
		public const float ReferenceTwsRevisitIntervalSeconds = 0.24f;
		public const float ReferenceSttRevisitIntervalSeconds = 0.035f;

		private const float DefaultRadarMaximumRangeMeters = 100000f;
		private const float BestFireControlErrorMilliradians = 0.08f;
		private const float WorstFireControlErrorMilliradians = 0.6f;
		private const float BestLateralVelocityUncertaintyMetersPerSecond = 0.15f;
		private const float WorstLateralVelocityUncertaintyMetersPerSecond = 0.5f;
		private const float StandardSnrLinear = 100f;
		private const float StandardPredictionSeconds = 1f;

		public static MechFireControlPerformanceProfile FromStats(MechAggregatedStats stats)
		{
			float sensorPrecision = PositiveOrFallback(stats?.sensorPrecision ?? 0f, ReferenceSensorPrecision);
			float radarRefreshHz = PositiveOrFallback(stats?.radarRefreshHz ?? 0f, ReferenceRadarRefreshHz);
			float fireControlQuality = PositiveOrFallback(stats?.fireControlQuality ?? 0f, ReferenceFireControlQuality);
			float radarMaximumRangeMeters = (stats != null && stats.radarRangeKm > 0f)
				? stats.radarRangeKm * 1000f
				: DefaultRadarMaximumRangeMeters;

			float sensorQuality01 = Mathf.Clamp01(sensorPrecision / 100f);
			float referenceSensorQuality01 = ReferenceSensorPrecision / 100f;
			float rawMeasurementScale = Mathf.Lerp(1.6f, 0.55f, sensorQuality01);
			float referenceMeasurementScale = Mathf.Lerp(1.6f, 0.55f, referenceSensorQuality01);
			float measurementPrecisionScale = Mathf.Clamp(
				rawMeasurementScale / referenceMeasurementScale,
				0.45f,
				1.8f);

			float revisitScale = ReferenceRadarRefreshHz / Mathf.Max(0.25f, radarRefreshHz);
			float searchIntervalSeconds = Mathf.Clamp(
				ReferenceSearchIntervalSeconds * revisitScale,
				0.02f,
				0.2f);
			float twsRevisitIntervalSeconds = Mathf.Clamp(
				ReferenceTwsRevisitIntervalSeconds * revisitScale,
				0.08f,
				0.8f);
			float sttRevisitIntervalSeconds = Mathf.Clamp(
				ReferenceSttRevisitIntervalSeconds * Mathf.Sqrt(revisitScale),
				0.02f,
				0.12f);

			float fireControlQuality01 = Mathf.Clamp01(fireControlQuality / 100f);
			float referenceFireControlScale = Mathf.Lerp(1.8f, 0.65f, ReferenceFireControlQuality / 100f);
			float fireSolutionCovarianceScale = Mathf.Clamp(
				Mathf.Lerp(1.8f, 0.65f, fireControlQuality01) / referenceFireControlScale,
				0.55f,
				1.75f);
			float nominalAngularErrorMilliradians = Mathf.Lerp(
				WorstFireControlErrorMilliradians,
				BestFireControlErrorMilliradians,
				fireControlQuality01);
			float nominalLateralVelocityUncertainty = Mathf.Lerp(
				WorstLateralVelocityUncertaintyMetersPerSecond,
				BestLateralVelocityUncertaintyMetersPerSecond,
				fireControlQuality01) * Mathf.Sqrt(Mathf.Clamp(revisitScale, 0.4f, 3f));

			return new MechFireControlPerformanceProfile(
				radarMaximumRangeMeters,
				measurementPrecisionScale,
				searchIntervalSeconds,
				twsRevisitIntervalSeconds,
				sttRevisitIntervalSeconds,
				fireSolutionCovarianceScale,
				nominalAngularErrorMilliradians,
				nominalLateralVelocityUncertainty);
		}

		public static float EstimatePredictionErrorMeters(
			in MechFireControlPerformanceProfile profile,
			float rangeMeters,
			MechFireControlTrackMode mode)
		{
			float beamWidthDegrees;
			float revisitSeconds;
			float predictionSeconds = StandardPredictionSeconds;
			switch (mode)
			{
				case MechFireControlTrackMode.SingleTargetTrack:
					beamWidthDegrees = 6f;
					revisitSeconds = profile.SttRevisitIntervalSeconds;
					break;
				case MechFireControlTrackMode.Coasting:
					beamWidthDegrees = 8f;
					revisitSeconds = profile.TwsRevisitIntervalSeconds;
					predictionSeconds = 2f;
					break;
				default:
					beamWidthDegrees = 8f;
					revisitSeconds = profile.TwsRevisitIntervalSeconds;
					break;
			}

			float updateRateHz = 1f / Mathf.Max(0.01f, revisitSeconds);
			float independentSampleCount = Mathf.Max(1f, updateRateHz * StandardPredictionSeconds);
			float singleMeasurementAngularSigma = beamWidthDegrees * Mathf.Deg2Rad
				/ (1.6f * Mathf.Sqrt(2f * StandardSnrLinear))
				* profile.MeasurementPrecisionScale;
			float filteredAngularSigma = singleMeasurementAngularSigma
				/ Mathf.Sqrt(independentSampleCount);
			float rangeSigma = Mathf.Max(1.2f, 12f / Mathf.Sqrt(2f * StandardSnrLinear))
				* profile.MeasurementPrecisionScale;
			float crossRangeSigma = Mathf.Max(0f, rangeMeters) * filteredAngularSigma;
			float positionSigma = Mathf.Sqrt(rangeSigma * rangeSigma + crossRangeSigma * crossRangeSigma);
			float velocitySigma = positionSigma / Mathf.Max(0.35f, Mathf.Sqrt(updateRateHz));
			float predictedSigma = Mathf.Sqrt(
				positionSigma * positionSigma
				+ velocitySigma * velocitySigma * predictionSeconds * predictionSeconds);
			return predictedSigma * Mathf.Sqrt(profile.FireSolutionCovarianceScale);
		}

		public static TrackSnapshot ScaleTrackForFireSolution(
			in TrackSnapshot source,
			in MechFireControlPerformanceProfile profile)
		{
			TrackSnapshot scaled = source;
			float scale = Mathf.Max(0.01f, profile.FireSolutionCovarianceScale);
			for (int row = 0; row < 6; row++)
			{
				for (int column = row; column < 6; column++)
				{
					scaled.SetCovariance(row, column, source.GetCovariance(row, column) * scale);
				}
			}
			return scaled;
		}

		private static float PositiveOrFallback(float value, float fallback) =>
			value > 0f ? value : fallback;
	}
}
