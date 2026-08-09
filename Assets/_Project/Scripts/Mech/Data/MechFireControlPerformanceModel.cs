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

	public readonly struct MechRadarPerformanceProfile
	{
		public MechRadarPerformanceProfile(
			float maximumRangeMeters,
			float stableTrackRangeMeters,
			float searchAzimuthDegrees,
			float searchElevationDegrees,
			int trackCapacity,
			int sttChannelCapacity,
			float maximumTrackingRateDegreesPerSecond,
			float trackMemorySeconds,
			float rangeAccuracyMeters,
			float angularAccuracyMilliradians,
			float radialVelocityAccuracyMetersPerSecond,
			float searchIntervalSeconds,
			float twsRevisitIntervalSeconds,
			float sttRevisitIntervalSeconds,
			float measurementPrecisionScale,
			float lockMaintenanceRating)
		{
			MaximumRangeMeters = maximumRangeMeters;
			StableTrackRangeMeters = stableTrackRangeMeters;
			SearchAzimuthDegrees = searchAzimuthDegrees;
			SearchElevationDegrees = searchElevationDegrees;
			TrackCapacity = trackCapacity;
			SttChannelCapacity = sttChannelCapacity;
			MaximumTrackingRateDegreesPerSecond = maximumTrackingRateDegreesPerSecond;
			TrackMemorySeconds = trackMemorySeconds;
			RangeAccuracyMeters = rangeAccuracyMeters;
			AngularAccuracyMilliradians = angularAccuracyMilliradians;
			RadialVelocityAccuracyMetersPerSecond = radialVelocityAccuracyMetersPerSecond;
			SearchIntervalSeconds = searchIntervalSeconds;
			TwsRevisitIntervalSeconds = twsRevisitIntervalSeconds;
			SttRevisitIntervalSeconds = sttRevisitIntervalSeconds;
			MeasurementPrecisionScale = measurementPrecisionScale;
			LockMaintenanceRating = lockMaintenanceRating;
		}

		public float MaximumRangeMeters { get; }
		public float StableTrackRangeMeters { get; }
		public float SearchAzimuthDegrees { get; }
		public float SearchElevationDegrees { get; }
		public int TrackCapacity { get; }
		public int SttChannelCapacity { get; }
		public float MaximumTrackingRateDegreesPerSecond { get; }
		public float TrackMemorySeconds { get; }
		public float RangeAccuracyMeters { get; }
		public float AngularAccuracyMilliradians { get; }
		public float RadialVelocityAccuracyMetersPerSecond { get; }
		public float SearchIntervalSeconds { get; }
		public float TwsRevisitIntervalSeconds { get; }
		public float SttRevisitIntervalSeconds { get; }
		public float MeasurementPrecisionScale { get; }
		public float LockMaintenanceRating { get; }
	}

	public readonly struct MechFireControlComputerProfile
	{
		public MechFireControlComputerProfile(
			float covarianceScale,
			float nominalAngularErrorMilliradians,
			float nominalLateralVelocityUncertaintyMetersPerSecond,
			float maximumPredictionTimeSeconds,
			int simultaneousSolutionCapacity,
			int weaponControlChannels,
			int guidanceChannelCapacity,
			int multiTargetEngagementCapacity,
			float pointDefenseRating,
			float trackReacquisitionTimeSeconds,
			float solutionUpdateRateHz,
			float solutionLatencyMilliseconds)
		{
			CovarianceScale = covarianceScale;
			NominalAngularErrorMilliradians = nominalAngularErrorMilliradians;
			NominalLateralVelocityUncertaintyMetersPerSecond = nominalLateralVelocityUncertaintyMetersPerSecond;
			MaximumPredictionTimeSeconds = maximumPredictionTimeSeconds;
			SimultaneousSolutionCapacity = simultaneousSolutionCapacity;
			WeaponControlChannels = weaponControlChannels;
			GuidanceChannelCapacity = guidanceChannelCapacity;
			MultiTargetEngagementCapacity = multiTargetEngagementCapacity;
			PointDefenseRating = pointDefenseRating;
			TrackReacquisitionTimeSeconds = trackReacquisitionTimeSeconds;
			SolutionUpdateRateHz = solutionUpdateRateHz;
			SolutionLatencyMilliseconds = solutionLatencyMilliseconds;
		}

		public float CovarianceScale { get; }
		public float NominalAngularErrorMilliradians { get; }
		public float NominalLateralVelocityUncertaintyMetersPerSecond { get; }
		public float MaximumPredictionTimeSeconds { get; }
		public int SimultaneousSolutionCapacity { get; }
		public int WeaponControlChannels { get; }
		public int GuidanceChannelCapacity { get; }
		public int MultiTargetEngagementCapacity { get; }
		public float PointDefenseRating { get; }
		public float TrackReacquisitionTimeSeconds { get; }
		public float SolutionUpdateRateHz { get; }
		public float SolutionLatencyMilliseconds { get; }
	}

	/// <summary>Compatibility view for the existing prediction chart and callers.</summary>
	public readonly struct MechFireControlPerformanceProfile
	{
		public MechFireControlPerformanceProfile(
			in MechRadarPerformanceProfile radar,
			in MechFireControlComputerProfile fireControl)
		{
			Radar = radar;
			FireControl = fireControl;
		}

		public MechRadarPerformanceProfile Radar { get; }
		public MechFireControlComputerProfile FireControl { get; }
		public float RadarMaximumRangeMeters => Radar.MaximumRangeMeters;
		public float MeasurementPrecisionScale => Radar.MeasurementPrecisionScale;
		public float SearchIntervalSeconds => Radar.SearchIntervalSeconds;
		public float TwsRevisitIntervalSeconds => Radar.TwsRevisitIntervalSeconds;
		public float SttRevisitIntervalSeconds => Radar.SttRevisitIntervalSeconds;
		public float FireSolutionCovarianceScale => FireControl.CovarianceScale;
		public float NominalAngularErrorMilliradians => FireControl.NominalAngularErrorMilliradians;
		public float NominalLateralVelocityUncertaintyMetersPerSecond =>
			FireControl.NominalLateralVelocityUncertaintyMetersPerSecond;
	}

	/// <summary>
	/// Builds independent radar and fire-control profiles. Legacy aggregate fields are used only
	/// when an old part asset has not yet received specialized data.
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
		private const float ReferenceAngularAccuracyMilliradians = 26.17994f;
		private const float ReferenceFireControlErrorMilliradians = 0.262f;
		private const float BestFireControlErrorMilliradians = 0.08f;
		private const float WorstFireControlErrorMilliradians = 0.6f;
		private const float BestLateralVelocityUncertaintyMetersPerSecond = 0.15f;
		private const float WorstLateralVelocityUncertaintyMetersPerSecond = 0.5f;
		private const float StandardSnrLinear = 100f;
		private const float StandardPredictionSeconds = 1f;

		public static MechFireControlPerformanceProfile FromStats(MechAggregatedStats stats)
		{
			var radar = BuildRadarProfile(stats);
			var fireControl = BuildFireControlProfile(stats, radar);
			return new MechFireControlPerformanceProfile(radar, fireControl);
		}

		public static MechRadarPerformanceProfile BuildRadarProfile(MechAggregatedStats stats)
		{
			var source = stats?.radarPerformance;
			if (source != null && source.HasData)
			{
				float angularAccuracy = PositiveOrFallback(
					source.angularAccuracyMrad,
					ReferenceAngularAccuracyMilliradians);
				return new MechRadarPerformanceProfile(
					PositiveOrFallback(source.referenceDetectionRangeKm, 100f) * 1000f,
					PositiveOrFallback(source.stableTrackRangeKm, source.referenceDetectionRangeKm) * 1000f,
					PositiveOrFallback(source.searchAzimuthDeg, 100f),
					PositiveOrFallback(source.searchElevationDeg, 60f),
					Mathf.Clamp(source.trackCapacity, 1, MechRadarPerformance.RadarTrackManagerSafetyLimit),
					Mathf.Max(1, source.sttChannelCapacity),
					PositiveOrFallback(source.maximumTrackingRateDegPerSecond, 120f),
					PositiveOrFallback(source.trackMemorySeconds, 3f),
					PositiveOrFallback(source.rangeAccuracyM, 12f),
					angularAccuracy,
					PositiveOrFallback(source.radialVelocityAccuracyMps, 2f),
					ReciprocalOrFallback(source.searchDwellRateHz, ReferenceSearchIntervalSeconds),
					ReciprocalOrFallback(source.twsUpdateRateHz, ReferenceTwsRevisitIntervalSeconds),
					ReciprocalOrFallback(source.sttUpdateRateHz, ReferenceSttRevisitIntervalSeconds),
					Mathf.Clamp(angularAccuracy / ReferenceAngularAccuracyMilliradians, 0.35f, 2.5f),
					source.LockMaintenanceRating);
			}

			float precision = PositiveOrFallback(stats?.sensorPrecision ?? 0f, ReferenceSensorPrecision);
			float refreshHz = PositiveOrFallback(stats?.radarRefreshHz ?? 0f, ReferenceRadarRefreshHz);
			float rawScale = Mathf.Lerp(1.6f, 0.55f, Mathf.Clamp01(precision / 100f));
			float referenceScale = Mathf.Lerp(1.6f, 0.55f, ReferenceSensorPrecision / 100f);
			float measurementScale = Mathf.Clamp(rawScale / referenceScale, 0.45f, 1.8f);
			float revisitScale = ReferenceRadarRefreshHz / Mathf.Max(0.25f, refreshHz);
			float range = stats != null && stats.radarRangeKm > 0f
				? stats.radarRangeKm * 1000f
				: DefaultRadarMaximumRangeMeters;
			return new MechRadarPerformanceProfile(
				range,
				range * 0.75f,
				100f,
				60f,
				Mathf.Clamp(stats?.trackCapacity ?? 8, 1, MechRadarPerformance.RadarTrackManagerSafetyLimit),
				1,
				120f,
				3f,
				12f * measurementScale,
				ReferenceAngularAccuracyMilliradians * measurementScale,
				2f * measurementScale,
				Mathf.Clamp(ReferenceSearchIntervalSeconds * revisitScale, 0.02f, 0.2f),
				Mathf.Clamp(ReferenceTwsRevisitIntervalSeconds * revisitScale, 0.08f, 0.8f),
				Mathf.Clamp(ReferenceSttRevisitIntervalSeconds * Mathf.Sqrt(revisitScale), 0.02f, 0.12f),
				measurementScale,
				Mathf.Clamp01(refreshHz / 5f) * 100f);
		}

		public static MechFireControlComputerProfile BuildFireControlProfile(
			MechAggregatedStats stats,
			in MechRadarPerformanceProfile radar)
		{
			var source = stats?.fireControlPerformance;
			if (source != null && source.HasData)
			{
				float angularError = PositiveOrFallback(
					source.baseSolutionErrorMrad,
					ReferenceFireControlErrorMilliradians);
				float updateRate = PositiveOrFallback(source.solutionUpdateRateHz, 30f);
				float latencySeconds = Mathf.Max(0f, source.solutionLatencyMs) * 0.001f;
				return new MechFireControlComputerProfile(
					Mathf.Clamp(angularError / ReferenceFireControlErrorMilliradians, 0.35f, 2.5f),
					angularError,
					Mathf.Max(0.1f, radar.RadialVelocityAccuracyMetersPerSecond * 0.2f + latencySeconds * 4f + 1f / updateRate),
					PositiveOrFallback(source.maximumPredictionTimeS, 6f),
					Mathf.Max(1, source.simultaneousSolutionCapacity),
					Mathf.Max(1, source.weaponControlChannels),
					Mathf.Max(0, source.guidanceChannelCapacity),
					Mathf.Max(1, source.multiTargetEngagementCapacity),
					Mathf.Clamp(source.pointDefenseRating, 0f, 100f),
					Mathf.Max(0f, source.trackReacquisitionTimeS),
					updateRate,
					Mathf.Max(0f, source.solutionLatencyMs));
			}

			float quality = PositiveOrFallback(stats?.fireControlQuality ?? 0f, ReferenceFireControlQuality);
			float quality01 = Mathf.Clamp01(quality / 100f);
			float referenceScale = Mathf.Lerp(1.8f, 0.65f, ReferenceFireControlQuality / 100f);
			return new MechFireControlComputerProfile(
				Mathf.Clamp(Mathf.Lerp(1.8f, 0.65f, quality01) / referenceScale, 0.55f, 1.75f),
				Mathf.Lerp(WorstFireControlErrorMilliradians, BestFireControlErrorMilliradians, quality01),
				Mathf.Lerp(WorstLateralVelocityUncertaintyMetersPerSecond, BestLateralVelocityUncertaintyMetersPerSecond, quality01),
				6f,
				Mathf.Max(1, stats?.trackCapacity ?? 8),
				4,
				Mathf.Max(0, stats?.guidanceChannels ?? 2),
				Mathf.Max(1, stats?.guidanceChannels ?? 2),
				quality,
				0.6f,
				30f,
				35f);
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
					revisitSeconds = profile.Radar.SttRevisitIntervalSeconds;
					break;
				case MechFireControlTrackMode.Coasting:
					beamWidthDegrees = 8f;
					revisitSeconds = profile.Radar.TwsRevisitIntervalSeconds;
					predictionSeconds = Mathf.Min(2f, profile.FireControl.MaximumPredictionTimeSeconds);
					break;
				default:
					beamWidthDegrees = 8f;
					revisitSeconds = profile.Radar.TwsRevisitIntervalSeconds;
					break;
			}

			float updateRateHz = 1f / Mathf.Max(0.01f, revisitSeconds);
			float independentSampleCount = Mathf.Max(1f, updateRateHz * StandardPredictionSeconds);
			float angularSigma = beamWidthDegrees * Mathf.Deg2Rad
				/ (1.6f * Mathf.Sqrt(2f * StandardSnrLinear))
				* profile.Radar.MeasurementPrecisionScale
				/ Mathf.Sqrt(independentSampleCount);
			float rangeSigma = Mathf.Max(1.2f, profile.Radar.RangeAccuracyMeters / Mathf.Sqrt(2f * StandardSnrLinear));
			float crossRangeSigma = Mathf.Max(0f, rangeMeters) * angularSigma;
			float positionSigma = Mathf.Sqrt(rangeSigma * rangeSigma + crossRangeSigma * crossRangeSigma);
			float velocitySigma = Mathf.Max(
				profile.FireControl.NominalLateralVelocityUncertaintyMetersPerSecond,
				positionSigma / Mathf.Max(0.35f, Mathf.Sqrt(updateRateHz)));
			float predictedSigma = Mathf.Sqrt(positionSigma * positionSigma
				+ velocitySigma * velocitySigma * predictionSeconds * predictionSeconds);
			return predictedSigma * Mathf.Sqrt(profile.FireControl.CovarianceScale);
		}

		public static TrackSnapshot ScaleTrackForFireSolution(
			in TrackSnapshot source,
			in MechFireControlPerformanceProfile profile) =>
			ScaleTrackForFireSolution(source, profile.FireControl);

		public static TrackSnapshot ScaleTrackForFireSolution(
			in TrackSnapshot source,
			in MechFireControlComputerProfile fireControl)
		{
			TrackSnapshot scaled = source;
			float scale = Mathf.Max(0.01f, fireControl.CovarianceScale);
			for (int row = 0; row < 6; row++)
			{
				for (int column = row; column < 6; column++)
				{
					scaled.SetCovariance(row, column, source.GetCovariance(row, column) * scale);
				}
			}
			return scaled;
		}

		private static float ReciprocalOrFallback(float rateHz, float fallbackSeconds) =>
			rateHz > 0f ? 1f / rateHz : fallbackSeconds;

		private static float PositiveOrFallback(float value, float fallback) =>
			value > 0f ? value : fallback;
	}
}
